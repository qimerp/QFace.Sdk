using Microsoft.Extensions.Options;
using QFace.Sdk.Temporal.Options;

namespace QimErp.Shared.Common.Outbox;

/// <summary>
/// The writing and relaying process's effective <c>Temporal:TaskQueueSuffix</c>; empty for deployed services.
/// A relay claims only rows whose <see cref="OutboxMessage.Origin"/> matches its own, and the deployed
/// relay also claims rows with a null origin.
/// </summary>
public sealed class OutboxOrigin
{
    public static readonly OutboxOrigin Deployed = new(string.Empty);

    public OutboxOrigin(string? value) => Value = value ?? string.Empty;

    public string Value { get; }

    public bool IsDeployed => Value.Length == 0;

    public static OutboxOrigin FromServices(IServiceProvider services)
    {
        var fromOptions = services.GetService<IOptions<TemporalOptions>>()?.Value.TaskQueueSuffix;
        if (!string.IsNullOrEmpty(fromOptions))
            return new OutboxOrigin(fromOptions);

        var configuration = services.GetService<IConfiguration>();
        return new OutboxOrigin(
            configuration?[$"{TemporalOptions.SectionName}:{nameof(TemporalOptions.TaskQueueSuffix)}"]);
    }
}

internal static class OutboxOriginQueryExtensions
{
    public static IQueryable<OutboxMessage> OwnedBy(this IQueryable<OutboxMessage> rows, OutboxOrigin origin)
    {
        var value = origin.Value;
        return origin.IsDeployed
            ? rows.Where(m => m.Origin == value || m.Origin == null)
            : rows.Where(m => m.Origin == value);
    }
}
