namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public sealed class IntentUrlPolicy
{
    private static readonly Regex EncodedControl = new("%(?:0[0-9a-f]|1[0-9a-f]|7f)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, Uri> _hostOrigins;
    private readonly IReadOnlyDictionary<string, Func<Uri, bool>> _purposeRules;

    /// <summary>
    /// A URL rule whose purpose has no entry in <paramref name="purposeRules"/> rejects every URL.
    /// Each origin must be canonical (https, lowercase host, no default port, no path or trailing slash); see <see cref="NormalizeOrigin"/>.
    /// </summary>
    public IntentUrlPolicy(IReadOnlyDictionary<string, Uri> hostOrigins, IReadOnlyDictionary<string, Func<Uri, bool>> purposeRules)
    {
        _hostOrigins = hostOrigins ?? throw new ArgumentNullException(nameof(hostOrigins));
        _purposeRules = purposeRules ?? throw new ArgumentNullException(nameof(purposeRules));

        foreach (var origin in hostOrigins.Values)
        {
            if (origin is null || !IsCanonicalOrigin(origin))
                throw new ArgumentException("URL policy origins must be canonical https origins.", nameof(hostOrigins));
        }
    }

    /// <summary>Converts an https origin such as "https://App.Example.com:443/" to its canonical form; throws <see cref="ArgumentException"/> if it has a path, query, fragment or userinfo.</summary>
    public static Uri NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || origin.Contains('#'))
            throw new ArgumentException("Origin must be an https origin without path, query, fragment or userinfo.", nameof(origin));

        return new Uri(CanonicalOrigin(uri));
    }

    private static bool IsCanonicalOrigin(Uri origin) =>
        origin.IsAbsoluteUri
        && string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        && string.Equals(origin.OriginalString, CanonicalOrigin(origin), StringComparison.Ordinal);

    private static string CanonicalOrigin(Uri uri) =>
        uri.IsDefaultPort
            ? $"https://{uri.Host}"
            : string.Create(CultureInfo.InvariantCulture, $"https://{uri.Host}:{uri.Port}");

    public bool IsAllowed(IntentUrlRule rule, string value)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrEmpty(value) || !IntentText.IsClean(value))
            return false;
        if (value.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'' or '\\' or '#'))
            return false;
        if (EncodedControl.IsMatch(value))
            return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || Authority(value).Contains('@') || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (!string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal))
            return false;

        if (!_hostOrigins.TryGetValue(rule.Host, out var origin)
            || !string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != origin.Port)
            return false;

        return rule.Purpose is not null
            && _purposeRules.TryGetValue(rule.Purpose, out var purposeRule)
            && purposeRule(uri);
    }

    private static string Authority(string value)
    {
        var start = value.IndexOf("//", StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        start += 2;
        var end = value.IndexOfAny(['/', '?', '#'], start);
        return end < 0 ? value[start..] : value[start..end];
    }
}
