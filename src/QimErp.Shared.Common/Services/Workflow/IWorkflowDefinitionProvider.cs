namespace QimErp.Shared.Common.Services.Workflow;

/// <summary>
/// Single read path for published workflow definitions (Redis cache → authoritative store).
/// </summary>
public interface IWorkflowDefinitionProvider
{
    /// <remarks>Returns null on a read failure; approval decisions must use <see cref="LookupPublishedDefinitionAsync"/>.</remarks>
    Task<PublishedWorkflowDefinition?> GetPublishedDefinitionAsync(
        string tenantId,
        string workflowCode,
        string entityType,
        CancellationToken cancellationToken = default);

    /// <remarks>Returns null on a read failure; approval decisions must use <see cref="LookupPublishedDefinitionByEntityTypeAsync"/>.</remarks>
    Task<PublishedWorkflowDefinition?> GetPublishedDefinitionByEntityTypeAsync(
        string tenantId,
        string entityType,
        CancellationToken cancellationToken = default);

    /// <remarks>The default implementation cannot report <see cref="WorkflowDefinitionLookupStatus.Unknown"/>; override it.</remarks>
    async Task<WorkflowDefinitionLookup> LookupPublishedDefinitionAsync(
        string tenantId,
        string workflowCode,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        var definition = await GetPublishedDefinitionAsync(tenantId, workflowCode, entityType, cancellationToken);
        return definition is null ? WorkflowDefinitionLookup.Absent : WorkflowDefinitionLookup.Found(definition);
    }

    /// <remarks>The default implementation cannot report <see cref="WorkflowDefinitionLookupStatus.Unknown"/>; override it.</remarks>
    async Task<WorkflowDefinitionLookup> LookupPublishedDefinitionByEntityTypeAsync(
        string tenantId,
        string entityType,
        CancellationToken cancellationToken = default)
    {
        var definition = await GetPublishedDefinitionByEntityTypeAsync(tenantId, entityType, cancellationToken);
        return definition is null ? WorkflowDefinitionLookup.Absent : WorkflowDefinitionLookup.Found(definition);
    }
}
