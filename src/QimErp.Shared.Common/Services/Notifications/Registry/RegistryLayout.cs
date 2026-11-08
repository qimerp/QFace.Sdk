namespace QimErp.Shared.Common.Services.Notifications.Registry;

public sealed record RegistryLayout(string TemplatesPrefix = "email-templates", string ReleasesPrefix = "email-templates/releases")
{
    public static readonly Regex ReleaseIdPattern = new(@"^rel-(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant);

    public string PointerKey => $"{ReleasesPrefix}/latest.json";

    public string ManifestKey(string releaseId) => $"{ReleasesPrefix}/{releaseId}/manifest.json";

    public static bool IsReleaseId(string? releaseId) => releaseId is not null && ReleaseIdPattern.IsMatch(releaseId);
}
