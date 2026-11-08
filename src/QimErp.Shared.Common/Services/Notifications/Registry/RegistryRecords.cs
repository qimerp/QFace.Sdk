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
    IReadOnlyList<ManifestIntentDeclaration> Intents);

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
    string ContractSha256);
