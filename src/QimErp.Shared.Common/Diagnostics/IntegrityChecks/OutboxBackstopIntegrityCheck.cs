using QimErp.Shared.Common.Outbox;

namespace QimErp.Shared.Common.Diagnostics.IntegrityChecks;

/// <summary>
/// DATA-06 sample check: counts <see cref="OutboxMessage"/> rows of this service's origin left
/// unprocessed past <see cref="_staleAfter"/>, i.e. the backstop for the relay itself having stalled
/// (crashed, deadlocked, or never started). Stuck rows of other origins are reported in
/// <see cref="IntegrityCheckResult.NotOwnedCounts"/>, not in the count.
/// </summary>
public sealed class OutboxBackstopIntegrityCheck<TContext> : IIntegrityCheck
    where TContext : DbContext
{
    public const string CheckName = "outbox.backstop.stuck_messages";
    private const int SampleSize = 10;

    private readonly TContext _context;
    private readonly TimeSpan _staleAfter;
    private readonly OutboxOrigin _origin;

    public OutboxBackstopIntegrityCheck(TContext context, TimeSpan? staleAfter = null, OutboxOrigin? origin = null)
    {
        _context = context;
        _staleAfter = staleAfter ?? TimeSpan.FromHours(1);
        _origin = origin ?? OutboxOrigin.Deployed;
    }

    public string Name => CheckName;

    public async Task<IntegrityCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - _staleAfter;
        var allStuck = _context.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null && m.DeadAt == null && m.OccurredAt < cutoff);

        var perOrigin = await allStuck
            .GroupBy(m => m.Origin ?? string.Empty)
            .Select(g => new { Origin = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);

        var notOwned = perOrigin
            .Where(o => o.Origin != _origin.Value)
            .ToDictionary(o => o.Origin, o => o.Count);
        var count = perOrigin.Where(o => o.Origin == _origin.Value).Sum(o => o.Count);

        IReadOnlyList<string>? sampleIds = null;
        if (count > 0)
        {
            sampleIds = await allStuck.OwnedBy(_origin)
                .OrderBy(m => m.OccurredAt)
                .Take(SampleSize)
                .Select(m => m.Id.ToString())
                .ToListAsync(cancellationToken);
        }

        return new IntegrityCheckResult(Name, count, sampleIds)
        {
            NotOwnedCounts = notOwned.Count > 0 ? notOwned : null,
        };
    }
}
