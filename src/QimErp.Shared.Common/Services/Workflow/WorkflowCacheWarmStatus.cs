namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowCacheWarmStatus
{
    /// <summary>The tenant warm marker could not be read.</summary>
    Unknown = 0,

    /// <summary>A fresh version 2 marker is present and no Bound entry names the entity type: nothing is bound.</summary>
    Warm = 1,

    /// <summary>No marker, an empty tenant, a stale or future-dated marker, or a version other than 2.</summary>
    NotWarm = 2,

    /// <summary>The entity type was bound (under any module) when the cache was warmed, so its missing keys prove nothing.</summary>
    BoundAtWarm = 3,
}
