using QimErp.Shared.Common.Security;
using QimErp.Shared.Common.Services.Notifications.Registry;

namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public enum RenderFailure
{
    None,
    UnknownPlaceholder,
    RequiredFieldMissing,
    ContextViolation,
    HeaderViolation,
    SenderNotAllowed,
    RenderResidue,
    UnsupportedFieldType,
    UnsupportedInfraToken,
    RequiredLinkMissing,
    InvalidBrandValue,
}

/// <summary>Colours are <c>#RRGGBB</c> upper-case; an invalid colour falls back to the manifest default. A null <paramref name="Link"/> follows a valid <paramref name="Accent"/>.</summary>
public sealed record BrandValues(string? Band, string? Accent, string? Link, string? LogoUrl);

public sealed record RenderedEmail(string Subject, string Preheader, string Html, string Text, string SenderName, string SenderAddress);

public sealed record RenderResult(RenderedEmail? Email, RenderFailure Failure, IReadOnlyList<string> FieldNames)
{
    public bool Succeeded => Failure == RenderFailure.None;

    public static RenderResult Ok(RenderedEmail email) => new(email, RenderFailure.None, []);

    public static RenderResult Fail(RenderFailure failure, IEnumerable<string> fieldNames) =>
        new(null, failure, fieldNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
}

/// <summary>Values must already be validated display strings from <see cref="IntentValueValidator"/>.</summary>
public sealed class IntentTemplateRenderer
{
    private const string HtmlHref = "htmlHref";
    private const string TextUrl = "textUrl";

    private const string BrandBand = "BrandBand";
    private const string BrandAccent = "BrandAccent";
    private const string BrandLink = "BrandLink";
    private const string BrandLogoUrl = "BrandLogoUrl";

    private static readonly Regex Token = new(@"\{\{([A-Z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex LogoPath = new(@"^[0-9a-f]{32}/[0-9a-f]{64}\.png\z", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string[]> BrandContexts = new(StringComparer.Ordinal)
    {
        [BrandBand] = [HtmlLinkScanner.CssColor, HtmlLinkScanner.AttrColor],
        [BrandAccent] = [HtmlLinkScanner.CssColor, HtmlLinkScanner.AttrColor],
        [BrandLink] = [HtmlLinkScanner.CssColor, HtmlLinkScanner.AttrColor],
        [BrandLogoUrl] = [HtmlLinkScanner.ImgSrc],
    };

    private static readonly Regex HeaderRaw = new(
        @"[A-Za-z][A-Za-z0-9+.-]*:(?=\S)|www\.|[@<>]|[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}(?![A-Za-z0-9-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabelledLink = new(
        @"^[^{}\r\n]+: \{\{([A-Z][A-Za-z0-9]*)\}\}\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DeclaredHeaderContexts = new(StringComparer.Ordinal)
    {
        "subject", "preheader", "senderName", "senderAddress", "htmlTitle", "htmlAriaLabel",
    };

    private readonly ManifestSender _allowedSender;

    public IntentTemplateRenderer(ManifestSender allowedSender)
    {
        _allowedSender = allowedSender ?? throw new ArgumentNullException(nameof(allowedSender));
    }

    /// <summary><paramref name="brand"/> is used only when the declaration lists brand infra tokens; null renders the manifest defaults.</summary>
    public RenderResult RenderPrecheck(PinnedTemplate t, IReadOnlyDictionary<string, string> values, BrandValues? brand = null) =>
        Render(t, values, brand, final: false);

    /// <summary><paramref name="brand"/> is used only when the declaration lists brand infra tokens; null renders the manifest defaults.</summary>
    public RenderResult RenderFinal(PinnedTemplate t, IReadOnlyDictionary<string, string> values, BrandValues? brand = null) =>
        Render(t, values, brand, final: true);

    private RenderResult Render(PinnedTemplate t, IReadOnlyDictionary<string, string> values, BrandValues? brand, bool final)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(values);

        var template = t.Template;
        var declaration = t.Declaration;
        var fields = t.Contract.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var infra = declaration.Fields.Infra;
        if (infra.Count > 0
            && (infra.Any(n => !BrandContexts.ContainsKey(n))
                || template.Branding is null
                || !template.Branding.Audiences.Contains(declaration.Audience, StringComparer.Ordinal)))
            return RenderResult.Fail(RenderFailure.UnsupportedInfraToken, infra);

        var headers = new[] { template.Subject, template.Preheader, template.Sender.Name, template.Sender.Address };
        var allUsed = headers.Concat([t.Html, t.Text]).SelectMany(TokenNames).ToHashSet(StringComparer.Ordinal);

        var groupFailure = CheckGroups(declaration.Fields, fields, allUsed);
        if (groupFailure is not null)
            return groupFailure;

        var brandUsed = allUsed.Where(n => infra.Contains(n, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var used = allUsed.Where(n => !brandUsed.Contains(n)).ToHashSet(StringComparer.Ordinal);

        var unsupported = used.Where(n => fields[n].Type == IntentFieldType.HtmlFragment).ToList();
        if (unsupported.Count > 0)
            return RenderResult.Fail(RenderFailure.UnsupportedFieldType, unsupported);

        if (!string.Equals(template.Sender.Name, _allowedSender.Name, StringComparison.Ordinal)
            || !string.Equals(template.Sender.Address, _allowedSender.Address, StringComparison.Ordinal))
            return RenderResult.Fail(RenderFailure.SenderNotAllowed, []);

        var contextViolations = used.Where(n => !ContextsAllowed(fields[n], declaration)).ToList();
        if (contextViolations.Count > 0)
            return RenderResult.Fail(RenderFailure.ContextViolation, contextViolations);

        var brandResolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (brandUsed.Count > 0)
        {
            var brandViolations = BrandContextViolations(t, headers, brandUsed);
            if (brandViolations.Count > 0)
                return RenderResult.Fail(RenderFailure.ContextViolation, brandViolations);

            var invalidBrand = ResolveBrand(template.Branding!, brand, brandUsed, brandResolved);
            if (invalidBrand.Count > 0)
                return RenderResult.Fail(RenderFailure.InvalidBrandValue, invalidBrand);
        }

        var headerNames = headers.SelectMany(TokenNames)
            .Concat(used.Where(n => declaration.PlaceholderContexts[n].Any(DeclaredHeaderContexts.Contains)));
        var headerViolations = headerNames.Where(n => !AllowedInHeader(fields[n])).ToList();
        if (headerViolations.Count > 0)
            return RenderResult.Fail(RenderFailure.HeaderViolation, headerViolations);

        var missingLinks = MissingRequiredLinks(t);
        if (missingLinks.Count > 0)
            return RenderResult.Fail(RenderFailure.RequiredLinkMissing, missingLinks);

        var missing = t.Contract.Fields
            .Where(f => f.Required && IsResolved(f, final) && !HasValue(values, f.Name))
            .Select(f => f.Name)
            .ToList();
        if (missing.Count > 0)
            return RenderResult.Fail(RenderFailure.RequiredFieldMissing, missing);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingFallbacks = new List<string>();
        foreach (var name in used.Where(n => IsResolved(fields[n], final)))
        {
            if (HasValue(values, name))
                resolved[name] = IntentText.Normalize(values[name]);
            else if (declaration.Fallbacks.TryGetValue(name, out var fallback) && !string.IsNullOrWhiteSpace(IntentText.Normalize(fallback)))
                resolved[name] = IntentText.Normalize(fallback);
            else
                missingFallbacks.Add(name);
        }

        if (missingFallbacks.Count > 0)
            return RenderResult.Fail(RenderFailure.RequiredFieldMissing, missingFallbacks);

        var subject = Substitute(template.Subject, resolved, html: false);
        var preheader = Substitute(template.Preheader, resolved, html: false);
        var senderName = Substitute(template.Sender.Name, resolved, html: false);
        var senderAddress = Substitute(template.Sender.Address, resolved, html: false);

        var headerFailed = false;
        var headerFields = new List<string>();
        foreach (var (rendered, source, isAddress, mayBeEmpty) in new[]
                 {
                     (subject, template.Subject, false, false), (preheader, template.Preheader, false, true),
                     (senderName, template.Sender.Name, false, false), (senderAddress, template.Sender.Address, true, false),
                 })
        {
            if (mayBeEmpty && source.Length == 0)
                continue;

            if (IntentText.IsClean(rendered) && !Token.IsMatch(rendered) && (isAddress || !HeaderRaw.IsMatch(rendered)))
                continue;
            headerFailed = true;
            headerFields.AddRange(TokenNames(source));
        }

        if (headerFailed)
            return RenderResult.Fail(RenderFailure.HeaderViolation, headerFields);

        var htmlResolved = brandResolved.Count == 0
            ? resolved
            : resolved.Concat(brandResolved).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var html = Substitute(t.Html, htmlResolved, html: true);
        var text = Substitute(t.Text, resolved, html: false);

        var residue = Residue(t.Html, html, htmlResolved, final).Concat(Residue(t.Text, text, resolved, final)).ToList();
        if (residue.Count > 0 || HasStrayMarker(html) || HasStrayMarker(text))
            return RenderResult.Fail(RenderFailure.RenderResidue, residue);

        return RenderResult.Ok(new RenderedEmail(subject, preheader, html, text, senderName, senderAddress));
    }

    private static RenderResult? CheckGroups(ManifestFieldGroups groups, IReadOnlyDictionary<string, IntentField> fields, IReadOnlySet<string> used)
    {
        var membership = new Dictionary<string, int>(StringComparer.Ordinal);
        var invalid = new List<string>();

        void Check(IEnumerable<string> names, Func<IntentField, bool> matches)
        {
            foreach (var name in names)
            {
                membership[name] = membership.GetValueOrDefault(name) + 1;
                if (!fields.TryGetValue(name, out var field) || !matches(field))
                    invalid.Add(name);
            }
        }

        Check(groups.Producer, f => f.Source == IntentFieldSources.Producer);
        Check(groups.PlatformPrecheck, f => f.Source == IntentFieldSources.Platform && f.ResolvedAt == IntentResolutionPhase.Precheck);
        Check(groups.PlatformAfterPrecheck, f => f.Source == IntentFieldSources.Platform && f.ResolvedAt == IntentResolutionPhase.AfterPrecheck);

        foreach (var name in groups.Infra)
        {
            membership[name] = membership.GetValueOrDefault(name) + 1;
            if (fields.ContainsKey(name))
                invalid.Add(name);
        }

        invalid.AddRange(membership.Where(m => m.Value > 1).Select(m => m.Key));
        invalid.AddRange(used.Where(n => !membership.ContainsKey(n)));

        return invalid.Count > 0 ? RenderResult.Fail(RenderFailure.UnknownPlaceholder, invalid) : null;
    }

    private static List<string> MissingRequiredLinks(PinnedTemplate t)
    {
        var required = t.Contract.Fields
            .Where(f => f.Required && f.Type == IntentFieldType.Url)
            .Select(f => f.Name)
            .ToList();
        if (required.Count == 0)
            return required;

        var links = HtmlLinkScanner.Scan(t.Html);
        if (links is null)
            return required;

        var textTokens = LabelledLink.Matches(t.Text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return required
            .Where(name =>
            {
                var token = "{{" + name + "}}";
                return !links.AnchorHrefs.Contains(token, StringComparer.Ordinal)
                       || (links.HasVmlLink && !links.VmlHrefs.Contains(token, StringComparer.Ordinal))
                       || !textTokens.Contains(name);
            })
            .ToList();
    }

    private static List<string> BrandContextViolations(PinnedTemplate t, IEnumerable<string> headers, IReadOnlySet<string> brandUsed)
    {
        var violations = headers.Append(t.Text).SelectMany(TokenNames).Where(brandUsed.Contains).ToHashSet(StringComparer.Ordinal);

        var placements = HtmlLinkScanner.Scan(t.Html)?.TypedPlacements;
        var occurrences = TokenNames(t.Html).Where(brandUsed.Contains).GroupBy(n => n, StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            var name = occurrence.Key;
            if (placements is null
                || !t.Declaration.PlaceholderContexts.TryGetValue(name, out var declared) || declared.Count == 0
                || declared.Any(c => !BrandContexts[name].Contains(c, StringComparer.Ordinal)))
            {
                violations.Add(name);
                continue;
            }

            var placed = placements.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)).ToList();
            if (placed.Count != occurrence.Count() || placed.Any(p => !declared.Contains(p.Context, StringComparer.Ordinal)))
                violations.Add(name);
        }

        return violations.ToList();
    }

    private static List<string> ResolveBrand(ManifestBranding branding, BrandValues? brand, IReadOnlySet<string> brandUsed, Dictionary<string, string> resolved)
    {
        var invalid = new List<string>();

        string? Colour(string name, string? supplied)
        {
            if (ReleaseManifestParser.IsBrandColour(supplied))
                return supplied;
            return branding.Tokens.TryGetValue(name, out var fallback) && ReleaseManifestParser.IsBrandColour(fallback) ? fallback : null;
        }

        var accentSupplied = ReleaseManifestParser.IsBrandColour(brand?.Accent);
        var colours = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BrandBand] = Colour(BrandBand, brand?.Band),
            [BrandAccent] = Colour(BrandAccent, brand?.Accent),
            [BrandLink] = brand?.Link is null && accentSupplied ? brand!.Accent : Colour(BrandLink, brand?.Link),
        };

        foreach (var name in brandUsed)
        {
            if (name == BrandLogoUrl)
            {
                if (IsLogoUrl(branding.LogoHost, brand?.LogoUrl))
                    resolved[name] = brand!.LogoUrl!;
                else
                    invalid.Add(name);
            }
            else if (colours[name] is { } colour)
                resolved[name] = colour;
            else
                invalid.Add(name);
        }

        return invalid;
    }

    private static bool IsLogoUrl(string logoHost, string? value)
    {
        if (value is null || !ReleaseManifestParser.IsLogoHost(logoHost))
            return false;

        var prefix = "https://" + logoHost + "/brand/";
        return value.StartsWith(prefix, StringComparison.Ordinal) && LogoPath.IsMatch(value[prefix.Length..]);
    }

    private static bool ContextsAllowed(IntentField field, ManifestIntentDeclaration declaration)
    {
        if (!declaration.PlaceholderContexts.TryGetValue(field.Name, out var contexts) || contexts.Count == 0)
            return false;

        return field.Type == IntentFieldType.Url
            ? contexts.All(c => c is HtmlHref or TextUrl)
            : !contexts.Contains(HtmlHref);
    }

    private static bool AllowedInHeader(IntentField field) =>
        field.Sensitivity is not (DataClassification.Confidential or DataClassification.Pii or DataClassification.Sensitive)
        && field.Type is not (IntentFieldType.Url or IntentFieldType.Hostname or IntentFieldType.HtmlFragment)
        && field.ResolvedAt != IntentResolutionPhase.AfterPrecheck;

    private static bool IsResolved(IntentField field, bool final) =>
        final || field.Source == IntentFieldSources.Producer || field.ResolvedAt == IntentResolutionPhase.Precheck;

    private static bool HasValue(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && value is not null && !string.IsNullOrWhiteSpace(IntentText.Normalize(value));

    private static IEnumerable<string> TokenNames(string content) =>
        Token.Matches(content).Select(m => m.Groups[1].Value);

    private static string Substitute(string content, IReadOnlyDictionary<string, string> resolved, bool html) =>
        Token.Replace(content, match =>
            resolved.TryGetValue(match.Groups[1].Value, out var value) ? (html ? EscapeHtml(value) : value) : match.Value);

    private static IEnumerable<string> Residue(string source, string rendered, IReadOnlyDictionary<string, string> resolved, bool final)
    {
        var expected = final
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : TokenNames(source).Where(n => !resolved.ContainsKey(n)).GroupBy(n => n, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var actual = TokenNames(rendered).GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return expected.Keys.Union(actual.Keys, StringComparer.Ordinal)
            .Where(n => expected.GetValueOrDefault(n) != actual.GetValueOrDefault(n));
    }

    private static bool HasStrayMarker(string rendered) => IntentText.ContainsMarker(Token.Replace(rendered, string.Empty));

    /// <summary>Byte-equal to Python <c>html.escape(value, quote=True)</c>.</summary>
    public static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#x27;", StringComparison.Ordinal);
}
