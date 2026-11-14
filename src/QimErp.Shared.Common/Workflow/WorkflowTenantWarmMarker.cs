namespace QimErp.Shared.Common.Workflow;

/// <summary>
/// Value stored at <see cref="WorkflowDefinitionCacheKeys.TenantWarmMarker"/>:
/// <c>{ "warmedAt": ..., "version": 2, "bound": ["{module}:{entityType}", ...] }</c>.
/// </summary>
public sealed class WorkflowTenantWarmMarker
{
    public const int CurrentVersion = 2;

    /// <summary>Markers older than this (default 48h) prove nothing.</summary>
    public const string MaxAgeSettingKey = "Workflow:WarmMarkerMaxAge";

    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(48);

    public DateTimeOffset WarmedAt { get; set; }

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Pairs with an enabled workflow config or an active published definition at warm time; null on version 1 markers.</summary>
    public List<string>? Bound { get; set; }

    public static string BoundPair(string module, string entityType) => $"{module}:{entityType}";

    public bool IsBoundSetPresent => Version == CurrentVersion && Bound is not null;

    public bool IsBound(string module, string entityType) =>
        Bound?.Contains(BoundPair(module, entityType), StringComparer.OrdinalIgnoreCase) == true;
}
