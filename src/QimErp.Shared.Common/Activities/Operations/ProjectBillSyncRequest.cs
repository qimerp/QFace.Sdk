using QimErp.Shared.Common.Events;

namespace QimErp.Shared.Common.Activities.Operations;

public class ProjectBillSyncRequest
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Top level so the activity interceptor scopes the consumer to this company; empty means tenant-wide.</summary>
    public string CompanyId { get; set; } = string.Empty;

    public ProjectBillGeneratedEvent Generated { get; set; } = null!;
}
