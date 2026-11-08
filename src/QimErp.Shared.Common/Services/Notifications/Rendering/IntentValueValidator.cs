namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public sealed class IntentValueValidator
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly Regex Hostname = new(@"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex IntegerPattern = new(@"^-?[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex MoneyPattern = new(@"^[A-Z]{3} -?[0-9]+(?:\.[0-9]{2})?\z", RegexOptions.CultureInvariant);

    private readonly IntentUrlPolicy _policy;

    public IntentValueValidator(IntentUrlPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public static string FormatDateTime(DateTimeOffset value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.Day} {Months[value.Month - 1]} {value.ToString("yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC");

    /// <summary>Datetime takes a <see cref="DateTimeOffset"/> with offset zero; date takes <see cref="DateOnly"/> or yyyy-MM-dd; other types take a string.</summary>
    public IntentValueResult Validate(IntentField field, object raw)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Type == IntentFieldType.HtmlFragment)
            return IntentValueResult.Invalid(field.Name, IntentValueFailure.UnsupportedFieldType);

        if (field.Type == IntentFieldType.Datetime)
        {
            return raw is DateTimeOffset instant && instant.Offset == TimeSpan.Zero
                ? IntentValueResult.Valid(field.Name, FormatDateTime(instant))
                : IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);
        }

        if (field.Type == IntentFieldType.Date && raw is DateOnly day)
            return IntentValueResult.Valid(field.Name, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (raw is not string value)
            return IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);

        switch (field.Type)
        {
            case IntentFieldType.Text:
                var normalized = IntentText.Normalize(value);
                return IntentText.IsClean(normalized)
                    ? IntentValueResult.Valid(field.Name, normalized)
                    : IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);

            case IntentFieldType.Url:
                if (!IntentText.IsClean(value))
                    return IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);
                return field.Url is not null && _policy.IsAllowed(field.Url, value)
                    ? IntentValueResult.Valid(field.Name, value)
                    : IntentValueResult.Invalid(field.Name, IntentValueFailure.UrlPolicyViolation);

            case IntentFieldType.Hostname:
                return Matches(field, value, Hostname);

            case IntentFieldType.Integer:
                return Matches(field, value, IntegerPattern);

            case IntentFieldType.Money:
                return Matches(field, value, MoneyPattern);

            case IntentFieldType.Date:
                return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? IntentValueResult.Valid(field.Name, parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);

            default:
                return IntentValueResult.Invalid(field.Name, IntentValueFailure.UnsupportedFieldType);
        }
    }

    private static IntentValueResult Matches(IntentField field, string value, Regex pattern) =>
        IntentText.IsClean(value) && pattern.IsMatch(value)
            ? IntentValueResult.Valid(field.Name, value)
            : IntentValueResult.Invalid(field.Name, IntentValueFailure.InvalidFieldValue);
}
