using Microsoft.Extensions.Options;
using QFace.Sdk.Temporal.Options;

namespace QimErp.Shared.Common.Outbox;

public sealed class OutboxOrigin
{
    public static readonly OutboxOrigin Deployed = new(string.Empty);

    public OutboxOrigin(string? value) => Value = value ?? string.Empty;

    public string Value { get; }

    public bool IsDeployed => Value.Length == 0;

    /// <summary>The only resolver of the effective task queue suffix; the outbox Temporal transport uses it too.</summary>
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
