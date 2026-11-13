using System.Security.Cryptography;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.People;

[Workflow(PeopleEmailIntents.WorkflowType)]
public interface IPeopleEmailIntentWorkflow
{
    [WorkflowRun]
    Task RunAsync(PeopleEmailIntentRequest request);
}

public static class PeopleEmailIntents
{
    public const string WorkflowType = "PeopleEmailIntentWorkflow";
    public const string TaskQueue = "qimerp-platform-people-email";
    public const string Audience = "employee";
    public const string RecipientReference = "recipientId";

    public static string WorkflowId(string intentKey, int intentVersion, string occurrenceId, Guid recipientId) =>
        $"people-email-{Hash($"{intentKey}@{intentVersion}|{occurrenceId}|{recipientId:N}")[..32]}";

    /// <summary>Stable recipient id for a mailbox that is not an employee, such as the HR general address.</summary>
    public static Guid MailboxRecipientId(string address)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("people-email-mailbox|" + address.Trim().ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Core HR → Platform request to send one People email intent to one recipient. The tenant travels in the
/// Temporal tenant header, never in the payload. Values are plain text; Platform validates them against the intent contract.
/// </summary>
public class PeopleEmailIntentRequest
{
    public string IntentKey { get; set; } = string.Empty;
    public int IntentVersion { get; set; }

    /// <summary>Opaque id of the occurrence; the same occurrence and recipient is delivered once.</summary>
    public string OccurrenceId { get; set; } = string.Empty;

    /// <summary>The employee id, or <see cref="PeopleEmailIntents.MailboxRecipientId"/> of <see cref="RecipientEmail"/> when the recipient is not an employee.</summary>
    public Guid RecipientId { get; set; }

    public Guid? RecipientEmployeeId { get; set; }

    /// <summary>Used only when Platform has no active user linked to the employee.</summary>
    public string? RecipientEmail { get; set; }

    public string? RecipientFirstName { get; set; }

    /// <summary>People app path without the workspace segment, for example /hr/onboarding/{id}.</summary>
    public string LinkPath { get; set; } = "/";

    /// <summary>Producer fields by contract field name.</summary>
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    public DateTime OccurredAtUtc { get; set; }
}
