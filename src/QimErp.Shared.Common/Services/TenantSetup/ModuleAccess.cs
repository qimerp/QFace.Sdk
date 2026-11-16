namespace QimErp.Shared.Common.Services.TenantSetup;

public enum ModuleAccess
{
    /// <summary>The tenant module snapshot is missing or could not be read.</summary>
    Unknown = 0,
    Enabled = 1,
    NotInstalled = 2,
}
