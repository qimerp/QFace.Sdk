namespace QimErp.Shared.Common.Services.Notifications.Registry;

internal static class ReleaseManifestParser
{
    private static readonly Regex Hex64 = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant);
    private static readonly Regex TemplateCode = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex IntentKey = new(@"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex BrandColour = new(@"^#[0-9A-F]{6}\z", RegexOptions.CultureInvariant);
    private static readonly Regex LowerHostname = new(
        @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z", RegexOptions.CultureInvariant);

    public static readonly string[] BrandColourTokens = ["BrandBand", "BrandAccent", "BrandLink"];

    private static readonly string[] PointerProperties =
    [
        "schemaVersion", "releaseId", "manifestKey", "manifestSha256", "sequence", "generation", "highWater", "action", "priorReleaseId",
    ];

    public static bool IsHex64(string? value) => value is not null && Hex64.IsMatch(value);

    public static ReleasePointer? ParsePointer(byte[] body, RegistryLayout layout)
    {
        if (!StrictJson.TryParse(body, out var root) || !StrictJson.HasExactProperties(root, PointerProperties))
            return null;

        if (!TryInt(root.GetProperty("schemaVersion"), out var schemaVersion) || schemaVersion != 1
            || !TryInt(root.GetProperty("generation"), out var generation) || generation <= 0
            || !TryInt(root.GetProperty("highWater"), out var highWater)
            || !TryInt(root.GetProperty("sequence"), out var sequence)
            || !TryString(root.GetProperty("releaseId"), out var releaseId) || !RegistryLayout.IsReleaseId(releaseId)
            || !TryString(root.GetProperty("manifestKey"), out var manifestKey) || manifestKey != layout.ManifestKey(releaseId)
            || !TryString(root.GetProperty("manifestSha256"), out var manifestSha256) || !IsHex64(manifestSha256)
            || !TryString(root.GetProperty("action"), out var action) || action is not ("activate" or "rollback"))
            return null;

        var prior = root.GetProperty("priorReleaseId");
        string? priorReleaseId = null;
        if (prior.ValueKind != JsonValueKind.Null)
        {
            if (!TryString(prior, out var priorValue) || !RegistryLayout.IsReleaseId(priorValue))
                return null;
            priorReleaseId = priorValue;
        }

        return new ReleasePointer(1, releaseId, manifestKey, manifestSha256, sequence, generation, highWater, action, priorReleaseId);
    }

    public static ReleaseManifest? ParseManifest(byte[] body, RegistryLayout layout, string releaseId, string manifestSha256)
    {
        if (!StrictJson.TryParse(body, out var root) || root.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGet(root, "schemaVersion", out var schemaElement) || !TryInt(schemaElement, out var schemaVersion) || schemaVersion is not (1 or 2)
            || !TryGet(root, "releaseId", out var releaseElement) || !TryString(releaseElement, out var manifestReleaseId) || manifestReleaseId != releaseId
            || !TryGet(root, "sequence", out var sequenceElement) || !TryInt(sequenceElement, out var sequence) || sequence <= 0)
            return null;

        var lists = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in new[] { "objects", "assets", "contracts", "templates", "partials" })
        {
            if (!TryGet(root, name, out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            lists[name] = list;
        }

        if (!TryGet(root, "source", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.Object
            || !TryGet(sourceElement, "commit", out var commitElement) || !TryString(commitElement, out var commit)
            || !Commit.IsMatch(commit))
            return null;
        string? committedAt = TryGet(sourceElement, "committedAt", out var committedElement) && TryString(committedElement, out var committedValue)
            ? committedValue
            : null;

        string? contractRevision = TryGet(root, "contractRevision", out var revisionElement) && TryString(revisionElement, out var revisionValue)
            ? revisionValue
            : null;

        var releasePrefix = layout.ReleasesPrefix + "/" + releaseId + "/";
        var manifestKey = layout.ManifestKey(releaseId);
        var objects = new List<ManifestObject>();
        foreach (var entry in lists["objects"].EnumerateArray())
        {
            if (!TryObjectFields(entry, out var key, out var path, out var length, out var sha, out var contentType)
                || !RegistryKey.IsValid(key) || !RegistryKey.IsValid(path)
                || !key.StartsWith(releasePrefix, StringComparison.Ordinal) || key == manifestKey)
                return null;
            objects.Add(new ManifestObject(key, path, length, sha, contentType));
        }

        var assets = new List<ManifestAsset>();
        foreach (var entry in lists["assets"].EnumerateArray())
        {
            if (!TryObjectFields(entry, out var key, out var path, out var length, out var sha, out var contentType)
                || !TryGet(entry, "url", out var urlElement) || !TryString(urlElement, out var url)
                || !RegistryKey.IsValid(key) || !RegistryKey.IsValid(path))
                return null;
            assets.Add(new ManifestAsset(key, path, length, sha, contentType, url));
        }

        var contracts = new List<ManifestContract>();
        foreach (var entry in lists["contracts"].EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryGet(entry, "key", out var keyElement) || !TryString(keyElement, out var key)
                || !TryGet(entry, "version", out var versionElement) || !TryInt(versionElement, out var version) || version < 0 || version > int.MaxValue
                || !TryGet(entry, "sha256", out var shaElement) || !TryString(shaElement, out var sha) || !IsHex64(sha)
                || !TryGet(entry, "object", out var objectElement) || !TryString(objectElement, out var objectKey)
                || !IntentKey.IsMatch(key) || !RegistryKey.IsValid(objectKey))
                return null;
            contracts.Add(new ManifestContract(key, (int)version, sha, objectKey));
        }

        var templates = new List<ManifestTemplate>();
        var malformed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in lists["templates"].EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryGet(entry, "code", out var codeElement) || !TryString(codeElement, out var code) || !TemplateCode.IsMatch(code)
                || !TryGet(entry, "html", out var htmlElement) || !TryString(htmlElement, out var html) || !RegistryKey.IsValid(html)
                || !TryGet(entry, "text", out var textElement) || !TryString(textElement, out var text) || !RegistryKey.IsValid(text))
                return null;

            var template = ParseTemplate(entry, code, html, text, schemaVersion, objects);
            if (template is null)
                malformed.Add(code);
            else
                templates.Add(template);
        }

        var partials = new List<string>();
        foreach (var entry in lists["partials"].EnumerateArray())
        {
            if (!TryString(entry, out var partial))
                return null;
            partials.Add(partial);
        }

        return new ReleaseManifest((int)schemaVersion, releaseId, sequence, new ManifestSource(commit, committedAt), contractRevision,
            objects, assets, contracts, templates, partials, manifestSha256)
        {
            MalformedTemplateCodes = malformed,
        };
    }

    public static bool IsBrandColour(string? value) => value is not null && BrandColour.IsMatch(value);

    public static bool IsLogoHost(string? value) => value is not null && value.Length <= 253 && LowerHostname.IsMatch(value);

    private static ManifestTemplate? ParseTemplate(
        JsonElement entry, string code, string html, string text, long schemaVersion, IReadOnlyList<ManifestObject> objects)
    {
        if (!TryGet(entry, "id", out var idElement) || !TryString(idElement, out var id)
            || !TryGet(entry, "subject", out var subjectElement) || !TryString(subjectElement, out var subject)
            || !TryGet(entry, "preheader", out var preheaderElement) || !TryString(preheaderElement, out var preheader)
            || !TryGet(entry, "sender", out var senderElement) || senderElement.ValueKind != JsonValueKind.Object
            || !TryGet(senderElement, "name", out var senderNameElement) || !TryString(senderNameElement, out var senderName)
            || !TryGet(senderElement, "address", out var senderAddressElement) || !TryString(senderAddressElement, out var senderAddress)
            || !TryGet(entry, "intents", out var intentsElement) || intentsElement.ValueKind != JsonValueKind.Array)
            return null;

        var intents = new List<ManifestIntentDeclaration>();
        foreach (var declaration in intentsElement.EnumerateArray())
        {
            var parsed = ParseDeclaration(declaration);
            if (parsed is null)
                return null;
            intents.Add(parsed);
        }

        ManifestBranding? branding = null;
        if (schemaVersion >= 2 && TryGet(entry, "branding", out var brandingElement))
        {
            branding = ParseBranding(brandingElement, objects);
            if (branding is null)
                return null;
        }

        return new ManifestTemplate(code, id, subject, preheader, new ManifestSender(senderName, senderAddress), html, text, intents)
        {
            Branding = branding,
        };
    }

    private static ManifestBranding? ParseBranding(JsonElement entry, IReadOnlyList<ManifestObject> objects)
    {
        if (!StrictJson.HasExactProperties(entry, "audiences", "tokens", "logoHost", "variants")
            || !TryString(entry.GetProperty("logoHost"), out var logoHost) || !IsLogoHost(logoHost))
            return null;

        var audiences = StringList(entry.GetProperty("audiences"));
        if (audiences is null || audiences.Count == 0
            || audiences.Any(string.IsNullOrWhiteSpace)
            || audiences.Distinct(StringComparer.Ordinal).Count() != audiences.Count)
            return null;

        var tokensElement = entry.GetProperty("tokens");
        if (!StrictJson.HasExactProperties(tokensElement, BrandColourTokens))
            return null;
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in BrandColourTokens)
        {
            if (!TryString(tokensElement.GetProperty(name), out var colour) || !IsBrandColour(colour))
                return null;
            tokens[name] = colour;
        }

        var variantsElement = entry.GetProperty("variants");
        if (!StrictJson.HasOnlyProperties(variantsElement, [], [ManifestBranding.LogoVariant]))
            return null;
        var variants = new Dictionary<string, ManifestTemplateVariant>(StringComparer.Ordinal);
        foreach (var variant in variantsElement.EnumerateObject())
        {
            if (!StrictJson.HasExactProperties(variant.Value, "html")
                || !TryString(variant.Value.GetProperty("html"), out var variantHtml) || !RegistryKey.IsValid(variantHtml)
                || !objects.Any(o => string.Equals(o.Key, variantHtml, StringComparison.Ordinal)))
                return null;
            variants[variant.Name] = new ManifestTemplateVariant(variantHtml);
        }

        return new ManifestBranding(audiences, tokens, logoHost, variants);
    }

    private static ManifestIntentDeclaration? ParseDeclaration(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !TryGet(entry, "key", out var keyElement) || !TryString(keyElement, out var key) || !IntentKey.IsMatch(key)
            || !TryGet(entry, "version", out var versionElement) || !TryInt(versionElement, out var version) || version <= 0 || version > int.MaxValue
            || !TryGet(entry, "audience", out var audienceElement) || !TryString(audienceElement, out var audience)
            || !TryGet(entry, "fallbacks", out var fallbacksElement) || fallbacksElement.ValueKind != JsonValueKind.Object
            || !TryGet(entry, "fields", out var fieldsElement)
            || !StrictJson.HasExactProperties(fieldsElement, "producer", "platformPrecheck", "platformAfterPrecheck", "infra")
            || !TryGet(entry, "placeholderContexts", out var contextsElement) || contextsElement.ValueKind != JsonValueKind.Object)
            return null;

        var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in fallbacksElement.EnumerateObject())
        {
            if (!TryString(property.Value, out var value))
                return null;
            fallbacks[property.Name] = value;
        }

        var contexts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in contextsElement.EnumerateObject())
        {
            var list = StringList(property.Value);
            if (list is null)
                return null;
            contexts[property.Name] = list;
        }

        var producer = StringList(fieldsElement.GetProperty("producer"));
        var precheck = StringList(fieldsElement.GetProperty("platformPrecheck"));
        var afterPrecheck = StringList(fieldsElement.GetProperty("platformAfterPrecheck"));
        var infra = StringList(fieldsElement.GetProperty("infra"));
        if (producer is null || precheck is null || afterPrecheck is null || infra is null)
            return null;

        return new ManifestIntentDeclaration(key, (int)version, audience, fallbacks,
            new ManifestFieldGroups(producer, precheck, afterPrecheck, infra), contexts);
    }

    private static bool TryObjectFields(JsonElement entry, out string key, out string path, out long length, out string sha, out string contentType)
    {
        key = path = sha = contentType = string.Empty;
        length = 0;
        return entry.ValueKind == JsonValueKind.Object
            && TryGet(entry, "key", out var keyElement) && TryString(keyElement, out key)
            && TryGet(entry, "path", out var pathElement) && TryString(pathElement, out path)
            && TryGet(entry, "length", out var lengthElement) && TryInt(lengthElement, out length) && length >= 0
            && TryGet(entry, "sha256", out var shaElement) && TryString(shaElement, out sha) && IsHex64(sha)
            && TryGet(entry, "contentType", out var typeElement) && TryString(typeElement, out contentType);
    }

    private static IReadOnlyList<string>? StringList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryString(item, out var value))
                return null;
            items.Add(value);
        }

        return items;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static bool TryString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()!;
        return true;
    }

    private static bool TryInt(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }
}
