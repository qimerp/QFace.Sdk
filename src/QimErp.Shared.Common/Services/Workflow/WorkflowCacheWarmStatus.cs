namespace QimErp.Shared.Common.Services.Workflow;

public enum WorkflowCacheWarmStatus
{
    /// <summary>The tenant warm marker could not be read.</summary>
    Unknown = 0,
    Warm = 1,
    NotWarm = 2,
}
