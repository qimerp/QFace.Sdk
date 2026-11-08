namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public enum IntentValueFailure
{
    None,
    InvalidFieldValue,
    UrlPolicyViolation,
    UnsupportedFieldType,
}

public sealed record IntentValueResult(bool Ok, string? DisplayValue, IntentValueFailure Failure, string FieldName)
{
    public static IntentValueResult Valid(string fieldName, string displayValue) => new(true, displayValue, IntentValueFailure.None, fieldName);

    public static IntentValueResult Invalid(string fieldName, IntentValueFailure failure) => new(false, null, failure, fieldName);
}
