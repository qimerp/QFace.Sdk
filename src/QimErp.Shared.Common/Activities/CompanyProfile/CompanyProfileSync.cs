using Temporalio.Activities;
using Temporalio.Workflows;

namespace QimErp.Shared.Common.Activities.CompanyProfile;

public static class CompanyProfileSync
{
    public const string WorkflowType = "CompanyProfileSyncWorkflow";
    public const string TaskQueue = "qimerp-finance-company-profile-sync";
    public const string ActivityName = "SyncCompanyProfile:Accounting";

    public static string WorkflowId(string tenantId, Guid companyId, long sourceVersion) =>
        $"company-profile-sync-{tenantId}-{companyId:N}-{sourceVersion}";
}

/// <summary>
/// Platform → Accounting snapshot of one company's document header (legal name, tax ids, address, contact, logo).
/// Accounting drops a snapshot whose <see cref="SourceVersion"/> is not newer than the one it holds.
/// The top-level <see cref="CompanyId"/> makes the Temporal interceptor run the consuming activity company-scoped.
/// </summary>
public class CompanyProfileChanged
{
    public string TenantId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }

    /// <summary>False when the company was deleted in Platform; the other fields still carry its last values.</summary>
    public bool IsActive { get; set; } = true;

    public bool IsDefault { get; set; }
    public string Code { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string TradingName { get; set; } = string.Empty;
    public string? Tin { get; set; }

    /// <summary>Same as <see cref="Tin"/> until Platform holds a separate VAT number (Ghana VAT registration uses the TIN).</summary>
    public string? VatNumber { get; set; }

    public string? RegistrationNumber { get; set; }
    public List<string> AddressLines { get; set; } = [];
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    /// <summary>Permanent public https URL of the logo (PNG, JPEG or WebP); never a presigned link. Null when Platform could not publish one.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Platform's stored logo file; the bytes are not readable from Accounting.</summary>
    public Guid? LogoFileId { get; set; }

    public long SourceVersion { get; set; }
}

/// <summary>Started by Platform on <see cref="CompanyProfileSync.TaskQueue"/>, once per company version, only for tenants with Core Accounting.</summary>
[Workflow(CompanyProfileSync.WorkflowType)]
public interface ICompanyProfileSyncWorkflow
{
    [WorkflowRun]
    Task RunAsync(CompanyProfileChanged change);
}

/// <summary>Hosted by Accounting on <see cref="CompanyProfileSync.TaskQueue"/>.</summary>
public interface ICompanyProfileSyncActivity
{
    [Activity(CompanyProfileSync.ActivityName)]
    Task SyncAsync(CompanyProfileChanged change);
}
