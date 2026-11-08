using Microsoft.Extensions.Logging;
using QimErp.Shared.Common.Outbox.Transports;

namespace QimErp.Shared.Common.Outbox;

/// <summary>
/// Claims, completes, and retries outbox rows. Uses <c>FOR UPDATE SKIP LOCKED</c> on
/// PostgreSQL; falls back to a tracked LINQ claim for InMemory / non-relational providers.
/// </summary>
public sealed class OutboxStore
{
    private readonly DbContext _db;
    private readonly ILogger<OutboxStore> _logger;
    private readonly InProcessEventTypeCatalog? _inProcessTypes;

    public OutboxStore(DbContext db, ILogger<OutboxStore> logger)
        : this(db, logger, null)
    {
    }

    internal OutboxStore(DbContext db, ILogger<OutboxStore> logger, InProcessEventTypeCatalog? inProcessTypes)
    {
        _db = db;
        _logger = logger;
        _inProcessTypes = inProcessTypes;
    }

    public async Task<List<OutboxMessage>> ClaimAsync(
        string node,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now + lease;
        var known = _inProcessTypes?.KnownEventTypes();

        if (CanUsePostgresClaim())
        {
            try
            {
                return await ClaimWithSqlAsync(node, batchSize, lease, known, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgreSQL outbox claim failed. Node={Node}", node);
                throw;
            }
        }

        return await ClaimWithLinqAsync(node, batchSize, now, lockedUntil, known, cancellationToken);
    }

    /// <summary>Pending in-process EventTypes this process cannot deserialize and therefore never claims, with row counts; at most 100 types.</summary>
    public async Task<IReadOnlyList<OutboxUnclaimableEventType>> ListUnclaimableEventTypesAsync(CancellationToken cancellationToken)
    {
        var known = _inProcessTypes?.KnownEventTypes();
        if (known is null)
            return [];

        var rows = await _db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null
                        && m.DeadAt == null
                        && m.Transport == OutboxTransports.InProcess
                        && !known.Contains(m.EventType.ToLower()))
            .GroupBy(m => m.EventType)
            .Select(g => new { EventType = g.Key, Count = g.Count() })
            .OrderBy(t => t.EventType)
            .Take(100)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new OutboxUnclaimableEventType(r.EventType, r.Count)).ToList();
    }

    public async Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (CanUseExecuteUpdate())
        {
            await _db.Set<OutboxMessage>()
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ProcessedAt, DateTimeOffset.UtcNow)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null),
                    cancellationToken);
            return;
        }

        var row = await _db.Set<OutboxMessage>().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (row is null) return;
        row.ProcessedAt = DateTimeOffset.UtcNow;
        row.LockedUntil = null;
        row.LockedBy = null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(
        Guid id,
        int attempts,
        string error,
        DateTimeOffset next,
        bool dead,
        CancellationToken cancellationToken)
    {
        var truncated = Truncate(error, 2000);
        if (CanUseExecuteUpdate())
        {
            await _db.Set<OutboxMessage>()
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Attempts, attempts)
                    .SetProperty(m => m.LastError, truncated)
                    .SetProperty(m => m.NextAttemptAt, next)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null)
                    .SetProperty(m => m.DeadAt, dead ? DateTimeOffset.UtcNow : (DateTimeOffset?)null),
                    cancellationToken);
            return;
        }

        var row = await _db.Set<OutboxMessage>().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (row is null) return;
        row.Attempts = attempts;
        row.LastError = truncated;
        row.NextAttemptAt = next;
        row.LockedUntil = null;
        row.LockedBy = null;
        row.DeadAt = dead ? DateTimeOffset.UtcNow : null;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private bool CanUsePostgresClaim()
    {
        var provider = _db.Database.ProviderName ?? string.Empty;
        return _db.Database.IsRelational()
               && provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanUseExecuteUpdate() => _db.Database.IsRelational();

    private async Task<List<OutboxMessage>> ClaimWithSqlAsync(
        string node,
        int batchSize,
        TimeSpan lease,
        IReadOnlyList<string>? known,
        CancellationToken cancellationToken)
    {
        var filterInProcess = known is not null;
        var knownTypes = known?.ToArray() ?? [];
        FormattableString sql = $"""
            UPDATE "OutboxMessages" m
               SET "LockedUntil" = now() + ({lease.TotalSeconds} * interval '1 second'),
                   "LockedBy" = {node}
             WHERE m."Id" IN (
                   SELECT "Id" FROM "OutboxMessages"
                    WHERE "ProcessedAt" IS NULL AND "DeadAt" IS NULL
                      AND "NextAttemptAt" <= now()
                      AND ("LockedUntil" IS NULL OR "LockedUntil" < now())
                      AND (NOT {filterInProcess} OR "Transport" <> {OutboxTransports.InProcess}
                           OR lower("EventType") = ANY({knownTypes}))
                    ORDER BY "OccurredAt"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED)
            RETURNING m.*
            """;

        return await _db.Set<OutboxMessage>()
            .FromSql(sql)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private async Task<List<OutboxMessage>> ClaimWithLinqAsync(
        string node,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset lockedUntil,
        IReadOnlyList<string>? known,
        CancellationToken cancellationToken)
    {
        var query = _db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null
                        && m.DeadAt == null
                        && m.NextAttemptAt <= now
                        && (m.LockedUntil == null || m.LockedUntil < now));
        if (known is not null)
            query = query.Where(m => m.Transport != OutboxTransports.InProcess || known.Contains(m.EventType.ToLower()));

        var candidates = await query
            .OrderBy(m => m.OccurredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var msg in candidates)
        {
            msg.LockedBy = node;
            msg.LockedUntil = lockedUntil;
        }

        if (candidates.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return candidates;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}

public sealed record OutboxUnclaimableEventType(string EventType, int Count);
