using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using QimErp.Shared.Common.Services.Notifications.Registry;
using QimErp.Shared.Common.Services.Notifications.Rendering;

namespace QimErp.Shared.Common.Tests.Notifications;

internal static class NotificationFixtures
{
    public const string ReleaseId = "rel-1111111111111111111111111111111111111111";
    public const string ReleasePrefix = "email-templates/releases/" + ReleaseId;
    public const string ResetCode = "pl-08-reset-password";
    public const string ResetKey = "auth.password_reset.requested";
    public const string ResetAudience = "account_holder";
    public const string ApprovalCode = "wf-02-approval-needed";
    public const string ApprovalKey = "workflow.approval.requested";
    public const string ApprovalAudience = "assigned_approver";
    public const string ExampleOrigin = "https://app.example.qimerp.com";

    public static readonly ManifestSender AllowedSender = new("QIM ERP", "no-reply@qimerp.com");

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Notifications", "Fixtures");

    public static string RegistryRoot => Path.Combine(Root, "registry");

    public static string PreviewPath(string name) => Path.Combine(Root, "preview", name);

    public static string IntentPath(string key, int version) => Path.Combine(Root, "intents", $"{key}.v{version}.json");

    public static ReleaseManifestReader Reader(INotificationRegistryStore? store = null) =>
        new(store ?? new LocalDirectoryRegistryStore(RegistryRoot), new RegistryLayout(), new ReleaseManifestReaderOptions { PointerCacheSeconds = 0 });

    public static async Task<ReleaseManifest> ManifestAsync(ReleaseManifestReader reader)
    {
        var pointer = await reader.ReadPointerAsync();
        pointer.Succeeded.Should().BeTrue();
        var manifest = await reader.ReadManifestAsync(pointer.Value!.ReleaseId, pointer.Value.ManifestSha256);
        manifest.Failure.Should().Be(RegistryFailure.None);
        return manifest.Value!;
    }

    public static async Task<PinnedTemplate> PinnedAsync(string code, string key, string audience)
    {
        using var reader = Reader();
        var manifest = await ManifestAsync(reader);
        var pinned = await reader.LoadPinnedTemplateAsync(manifest, code, key, 1, audience);
        pinned.Failure.Should().Be(RegistryFailure.None);
        return pinned.Value!;
    }

    public static IntentUrlPolicy ExamplePolicy() => new(
        new Dictionary<string, Uri> { ["platform-frontend"] = new(ExampleOrigin) },
        new Dictionary<string, Func<Uri, bool>>
        {
            ["password_reset"] = uri => uri.AbsolutePath == "/reset-password" && Regex.IsMatch(uri.Query, @"^\?token=[A-Za-z0-9_-]+\z"),
            ["approval_review"] = uri => Regex.IsMatch(uri.AbsolutePath, @"^/workflow/entity/[A-Za-z0-9_-]+/[A-Za-z0-9_-]+/review\z") && uri.Query.Length == 0,
        });

    public static IReadOnlyDictionary<string, string> ExampleValues(IntentContract contract, string key, bool requiredOnly = false)
    {
        var examples = ReadExamples(key);
        var validator = new IntentValueValidator(ExamplePolicy());
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in contract.Fields.Where(f => !requiredOnly || f.Required))
        {
            object raw = field.Type == IntentFieldType.Datetime
                ? DateTimeOffset.Parse(examples[field.Name], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                : examples[field.Name];
            var result = validator.Validate(field, raw);
            result.Ok.Should().BeTrue($"the {field.Name} example must validate");
            values[field.Name] = result.DisplayValue!;
        }

        return values;
    }

    public static Dictionary<string, string> ReadExamples(string key)
    {
        var node = JsonNode.Parse(File.ReadAllText(IntentPath(key, 1)))!;
        return node["fields"]!.AsArray().ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!["example"]!.GetValue<string>(), StringComparer.Ordinal);
    }
}

internal sealed class TempRegistry : IDisposable
{
    public TempRegistry()
    {
        Root = Path.Combine(Path.GetTempPath(), "qimerp-registry-" + Guid.NewGuid().ToString("N"));
        Copy(NotificationFixtures.RegistryRoot, Root);
    }

    public string Root { get; }

    public string PointerPath => Path.Combine(Root, "email-templates", "releases", "latest.json");

    public string ManifestPath => PathFor(NotificationFixtures.ReleasePrefix + "/manifest.json");

    public string PathFor(string key) => Path.Combine([Root, .. key.Split('/')]);

    public ReleaseManifestReader Reader() => NotificationFixtures.Reader(new LocalDirectoryRegistryStore(Root));

    public JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public string WriteManifest(JsonObject manifest) => WriteManifestBytes(Encoding.UTF8.GetBytes(manifest.ToJsonString()));

    public string WriteManifestBytes(byte[] body)
    {
        File.WriteAllBytes(ManifestPath, body);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    public static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target, StringComparison.Ordinal));
    }
}
