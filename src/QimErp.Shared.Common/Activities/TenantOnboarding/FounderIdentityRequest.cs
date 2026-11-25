namespace QimErp.Shared.Common.Activities.TenantOnboarding;

public sealed class FounderIdentityRequest
{
    public Guid OperationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public Guid CompanyId { get; set; }
    public long EmployeeRevision { get; set; }
}
