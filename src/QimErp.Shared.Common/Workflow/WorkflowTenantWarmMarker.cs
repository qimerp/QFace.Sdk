namespace QimErp.Shared.Common.Workflow;

/// <summary>
/// Value stored at <see cref="WorkflowDefinitionCacheKeys.TenantWarmMarker"/>:
/// <c>{ "warmedAt": ..., "version": 2, "bound": ["{module}:{entityType}", ...] }</c>.
/// </summary>
public sealed class WorkflowTenantWarmMarker
{
    public const int CurrentVersion = 2;

    /// <summary>Markers older than this prove nothing; hh:mm:ss, at most <see cref="DefaultMaxAge"/>.</summary>
    public const string MaxAgeSettingKey = "Workflow:WarmMarkerMaxAge";

    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(48);

    public DateTimeOffset WarmedAt { get; set; }

    /// <summary>JSON without a version reads as 0, which is never accepted.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Pairs with an enabled workflow config or an active published definition at warm time; null on version 1 markers.
    /// Matched by entity type only: Platform's module part can be a template category that differs from Workflow:Module.
    /// </summary>
    public List<string>? Bound { get; set; }

    public static string BoundPair(string module, string entityType) => $"{module}:{entityType}";

    public bool IsBoundSetPresent => Version == CurrentVersion && Bound is not null;

    public bool IsBound(string module, string entityType) =>
        Bound?.Any(pair => string.Equals(BoundEntityType(pair), entityType, StringComparison.OrdinalIgnoreCase)) == true;

    private static string BoundEntityType(string pair) => pair[(pair.LastIndexOf(':') + 1)..];
}
