namespace QimErp.Shared.Common.Workflow;

/// <summary>Value stored at <see cref="WorkflowDefinitionCacheKeys.TenantWarmMarker"/>: <c>{ "warmedAt": ..., "version": 1 }</c>.</summary>
public sealed class WorkflowTenantWarmMarker
{
    public const int CurrentVersion = 1;

    public DateTimeOffset WarmedAt { get; set; }

    public int Version { get; set; } = CurrentVersion;
}
