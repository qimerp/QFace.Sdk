using Microsoft.Extensions.Logging;

namespace QimErp.Shared.Common.Outbox;

internal static class OutboxUnclaimableReporter
{
    public static async Task<long> ReportAsync(OutboxStore store, ILogger logger, string node, CancellationToken cancellationToken)
    {
        var unclaimable = await store.ListUnclaimableEventTypesAsync(cancellationToken);
        long total = 0;
        foreach (var type in unclaimable)
        {
            total += type.Count;
            logger.LogWarning(
                "Outbox relay on node {Node} is not claiming {Count} pending in-process row(s) of EventType {EventType}: no CLR type is registered in this process. They stay pending until a relay that registers it claims them.",
                node, type.Count, type.EventType);
        }

        OutboxMetrics.RecordUnclaimable(total);
        return total;
    }
}
