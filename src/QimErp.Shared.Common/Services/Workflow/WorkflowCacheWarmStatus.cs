namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowCacheWarmStatus
{
    /// <summary>The tenant warm marker could not be read.</summary>
    Unknown = 0,

    /// <summary>A version 2 marker is present and the pair is not in its Bound set: nothing is bound.</summary>
    Warm = 1,

    /// <summary>No marker, or a version 1 marker without a Bound set.</summary>
    NotWarm = 2,

    /// <summary>The pair was bound when the cache was warmed, so its missing keys are not proof of anything.</summary>
    BoundAtWarm = 3,
}
