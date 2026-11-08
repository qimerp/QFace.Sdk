namespace QimErp.Shared.Common.Services.Notifications.Registry;

public enum RegistryFailure
{
    None,
    ReleasePointerMissing,
    ReleasePointerInvalid,
    ManifestMissing,
    ManifestInvalid,
    ManifestHashMismatch,
    TemplateMissing,
    TemplateIntentMismatch,
    AssetMissing,
    AssetHashMismatch,
    ContractMissing,
    ContractHashMismatch,
}

public sealed record RegistryResult<T>(T? Value, RegistryFailure Failure)
{
    public bool Succeeded => Failure == RegistryFailure.None;

    public static RegistryResult<T> Ok(T value) => new(value, RegistryFailure.None);

    public static RegistryResult<T> Fail(RegistryFailure failure) => new(default, failure);
}
