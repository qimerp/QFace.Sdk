namespace QimErp.Shared.Common.Activities.TenantOnboarding;

public sealed class FounderIdentityResult
{
    public Guid OperationId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid EmployeeId { get; set; }
    public Guid CompanyId { get; set; }
    public long EmployeeRevision { get; set; }
    public string? UserId { get; set; }
    public FounderIdentityState State { get; set; } = FounderIdentityState.Pending;
    public long StatusRevision { get; set; }
}
