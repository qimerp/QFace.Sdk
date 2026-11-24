using Microsoft.Extensions.Options;
using QFace.Sdk.Temporal.Interceptors;
using QFace.Sdk.Temporal.Options;
using QimErp.Shared.Common.Activities.People;
using QimErp.Shared.Common.Services.MultiTenancy;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace QimErp.Shared.Common.Services.Notifications.Intents;

/// <param name="EmployeeId">Null for a mailbox that is not an employee, such as a vendor contact or the HR general address.</param>
public sealed record EmailIntentRecipient(Guid? EmployeeId, string? Email, string? FirstName)
{
    public Guid RecipientId => EmployeeId ?? PeopleEmailIntents.MailboxRecipientId(Email ?? string.Empty);

    public static EmailIntentRecipient Mailbox(string email, string? firstName = null) => new(null, email.Trim(), firstName);
}

/// <param name="LinkPath">App path without the workspace segment; Platform resolves it against the intent's app URL.</param>
/// <param name="Fields">Plain text by contract field name; Platform escapes and validates them.</param>
public sealed record EmailIntentMessage(
    string IntentKey,
    int IntentVersion,
    string OccurrenceId,
    EmailIntentRecipient Recipient,
    string LinkPath,
    IReadOnlyDictionary<string, string> Fields,
    DateTime OccurredAtUtc);

public interface IEmailIntentSender
{
    /// <summary>Hands one intent for one recipient to Platform; throws when it cannot be handed over so the caller can retry.</summary>
    Task SendAsync(EmailIntentMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Starts <see cref="IPeopleEmailIntentWorkflow"/> with the current tenant in the Temporal tenant header, never in the payload.</summary>
public sealed class TemporalEmailIntentSender(
    ITemporalClient client,
    IOptions<TemporalOptions> options,
    ITenantContext tenantContext) : IEmailIntentSender
{
    public async Task SendAsync(EmailIntentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var tenantId = tenantContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("An email intent can only be sent inside a workspace.");
        if (client is not TemporalClient concrete)
            throw new InvalidOperationException("The Temporal client is unavailable.");

        var request = EmailIntentRequests.From(message);
        var clientOptions = (TemporalClientOptions)concrete.Options.Clone();
        clientOptions.Interceptors = (clientOptions.Interceptors ?? [])
            .Append(new TenantHeaderClientInterceptor(tenantId, PeopleEmailIntents.WorkflowType)).ToArray();
        var tenantClient = new TemporalClient(concrete.Connection, clientOptions);

        await tenantClient.StartWorkflowAsync(
            (IPeopleEmailIntentWorkflow workflow) => workflow.RunAsync(request),
            new WorkflowOptions(
                PeopleEmailIntents.WorkflowId(request.IntentKey, request.IntentVersion, request.OccurrenceId, request.RecipientId),
                options.Value.WithTaskQueueSuffix(PeopleEmailIntents.TaskQueue))
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                Rpc = new RpcOptions { CancellationToken = cancellationToken }
            });
    }
}

public static class EmailIntentRequests
{
    public static PeopleEmailIntentRequest From(EmailIntentMessage message) => new()
    {
        IntentKey = message.IntentKey,
        IntentVersion = message.IntentVersion,
        OccurrenceId = message.OccurrenceId,
        RecipientId = message.Recipient.RecipientId,
        RecipientEmployeeId = message.Recipient.EmployeeId,
        RecipientEmail = string.IsNullOrWhiteSpace(message.Recipient.Email) ? null : message.Recipient.Email.Trim(),
        RecipientFirstName = string.IsNullOrWhiteSpace(message.Recipient.FirstName) ? null : message.Recipient.FirstName.Trim(),
        LinkPath = LinkPath(message.LinkPath),
        Fields = message.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .ToDictionary(field => field.Key, field => field.Value.Trim(), StringComparer.Ordinal),
        OccurredAtUtc = DateTime.SpecifyKind(message.OccurredAtUtc, DateTimeKind.Utc)
    };

    /// <summary>Platform accepts only a plain app path, so a query string is dropped.</summary>
    public static string LinkPath(string? path)
    {
        var trimmed = (path ?? string.Empty).Split('?', '#')[0].Trim();
        return trimmed.StartsWith('/') ? trimmed : "/";
    }
}
