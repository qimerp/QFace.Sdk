using QimErp.Shared.Common.Services.Notifications.Rendering;

namespace QimErp.Shared.Common.Services.Notifications.Registry;

public sealed record ReleasePointer(
    int SchemaVersion,
    string ReleaseId,
    string ManifestKey,
    string ManifestSha256,
    long Sequence,
    long Generation,
    long HighWater,
    string Action,
    string? PriorReleaseId);

public sealed record ReleaseManifest(
    int SchemaVersion,
    string ReleaseId,
    long Sequence,
    ManifestSource Source,
    string? ContractRevision,
    IReadOnlyList<ManifestObject> Objects,
    IReadOnlyList<ManifestAsset> Assets,
    IReadOnlyList<ManifestContract> Contracts,
    IReadOnlyList<ManifestTemplate> Templates,
    IReadOnlyList<string> Partials,
    string ManifestSha256)
{
    /// <summary>Codes whose entries passed structural validation but could not be fully read; loading one fails with ManifestInvalid.</summary>
    public IReadOnlySet<string> MalformedTemplateCodes { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

public sealed record ManifestSource(string Commit, string? CommittedAt);

public sealed record ManifestObject(string Key, string Path, long Length, string Sha256, string ContentType);

public sealed record ManifestAsset(string Key, string Path, long Length, string Sha256, string ContentType, string Url);

public sealed record ManifestContract(string Key, int Version, string Sha256, string ObjectKey);

public sealed record ManifestSender(string Name, string Address);

public sealed record ManifestTemplate(
    string Code,
    string Id,
    string Subject,
    string Preheader,
    ManifestSender Sender,
    string HtmlKey,
    string TextKey,
    IReadOnlyList<ManifestIntentDeclaration> Intents)
{
    /// <summary>Null means the template is not eligible for tenant branding; always null in a schema version 1 manifest.</summary>
    public ManifestBranding? Branding { get; init; }
}

/// <summary><paramref name="Tokens"/> holds the defaults for exactly BrandBand, BrandAccent and BrandLink; the only variant key is <see cref="ManifestBranding.LogoVariant"/>.</summary>
public sealed record ManifestBranding(
    IReadOnlyList<string> Audiences,
    IReadOnlyDictionary<string, string> Tokens,
    string LogoHost,
    IReadOnlyDictionary<string, ManifestTemplateVariant> Variants)
{
    public const string LogoVariant = "logo";
}

public sealed record ManifestTemplateVariant(string HtmlKey);

public sealed record ManifestIntentDeclaration(
    string Key,
    int Version,
    string Audience,
    IReadOnlyDictionary<string, string> Fallbacks,
    ManifestFieldGroups Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PlaceholderContexts);

public sealed record ManifestFieldGroups(
    IReadOnlyList<string> Producer,
    IReadOnlyList<string> PlatformPrecheck,
    IReadOnlyList<string> PlatformAfterPrecheck,
    IReadOnlyList<string> Infra);

public sealed record PinnedTemplate(
    ManifestTemplate Template,
    ManifestIntentDeclaration Declaration,
    string Html,
    string Text,
    IntentContract Contract,
    string ContractSha256)
{
    /// <summary>Null for the standard HTML; otherwise the key under <see cref="ManifestBranding.Variants"/>.</summary>
    public string? Variant { get; init; }

    /// <summary>SHA-256 of the HTML object loaded into <see cref="Html"/>; set by <see cref="ReleaseManifestReader"/>.</summary>
    public string VariantHtmlSha256 { get; init; } = string.Empty;
}
