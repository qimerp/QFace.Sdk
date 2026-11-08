namespace QimErp.Shared.Common.Outbox.Transports;

internal sealed class InProcessEventTypeCatalog
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<string>? _registered;
    private readonly object _gate = new();
    private int _assemblyCount = -1;
    private string[] _known = [];
    private long _nextProbeTicks;

    public InProcessEventTypeCatalog(
        IOutboxEventTypeResolver resolver,
        IEnumerable<OutboxEventTypeRegistration> registrations)
    {
        _registered = resolver is OutboxEventTypeResolver
            ? registrations.Select(r => r.EventType).ToList()
            : null;
    }

    /// <summary>Lower-cased invariant names; null when a custom resolver makes the set unknowable, which disables filtering.</summary>
    public IReadOnlyList<string>? KnownEventTypes()
    {
        if (_registered is null)
            return null;

        var assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
        lock (_gate)
        {
            if (assemblyCount != _assemblyCount)
            {
                _known = _registered
                    .Concat(InProcessEventTransport.NotificationTypes().Select(t => t.Name))
                    .Select(n => n.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                _assemblyCount = assemblyCount;
            }

            return _known;
        }
    }

    public bool TryBeginProbe(DateTimeOffset now)
    {
        var next = Interlocked.Read(ref _nextProbeTicks);
        if (now.UtcTicks < next)
            return false;

        return Interlocked.CompareExchange(ref _nextProbeTicks, (now + ProbeInterval).UtcTicks, next) == next;
    }
}
