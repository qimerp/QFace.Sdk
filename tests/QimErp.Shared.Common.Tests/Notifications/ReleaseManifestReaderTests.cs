using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using QFace.Sdk.BlobStorage.Services;
using QimErp.Shared.Common.Services.Notifications.Intents;
using QimErp.Shared.Common.Services.Notifications.Registry;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class ReleaseManifestReaderTests
{
    private const string ResetHtmlKey = NotificationFixtures.ReleasePrefix + "/templates/pl-08-reset-password.html";

    [Fact]
    public async Task ValidRead_PinsBothPilotTemplates()
    {
        using var reader = NotificationFixtures.Reader();

        var pointer = await reader.ReadPointerAsync();
        pointer.Failure.Should().Be(RegistryFailure.None);
        pointer.Value!.ReleaseId.Should().Be(NotificationFixtures.ReleaseId);
        pointer.Value.Action.Should().Be("activate");

        var manifest = await reader.ReadManifestAsync(pointer.Value.ReleaseId, pointer.Value.ManifestSha256);
        manifest.Failure.Should().Be(RegistryFailure.None);

        var reset = await reader.LoadPinnedTemplateAsync(manifest.Value!, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);
        reset.Failure.Should().Be(RegistryFailure.None);
        reset.Value!.Contract.Key.Should().Be(NotificationFixtures.ResetKey);
        reset.Value.Declaration.Fields.PlatformAfterPrecheck.Should().Equal("ActionUrl", "LinkExpiresAt");
        NotificationIntentLock.TryGetContractSha256(NotificationFixtures.ResetKey, 1, out var resetSha).Should().BeTrue();
        reset.Value.ContractSha256.Should().Be(resetSha);
        reset.Value.Html.Should().Be(File.ReadAllText(Path.Combine(NotificationFixtures.RegistryRoot, ResetHtmlKey)));

        var approval = await reader.LoadPinnedTemplateAsync(manifest.Value!, NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, 1, NotificationFixtures.ApprovalAudience);
        approval.Failure.Should().Be(RegistryFailure.None);
        approval.Value!.Template.Sender.Should().Be(NotificationFixtures.AllowedSender);
    }

    [Fact]
    public async Task PointerMissing_ReturnsReleasePointerMissing()
    {
        using var registry = new TempRegistry();
        File.Delete(registry.PointerPath);
        using var reader = registry.Reader();

        (await reader.ReadPointerAsync()).Failure.Should().Be(RegistryFailure.ReleasePointerMissing);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("badReleaseId")]
    [InlineData("manifestKey")]
    [InlineData("generation")]
    [InlineData("action")]
    [InlineData("duplicate")]
    public async Task MalformedPointer_ReturnsReleasePointerInvalid(string mutation)
    {
        using var registry = new TempRegistry();
        var pointer = registry.ReadJson(registry.PointerPath);
        switch (mutation)
        {
            case "extra":
                pointer["note"] = "x";
                break;
            case "missing":
                pointer.Remove("highWater");
                break;
            case "badReleaseId":
                pointer["releaseId"] = "rel-XYZ";
                break;
            case "manifestKey":
                pointer["manifestKey"] = "email-templates/releases/rel-2222222222222222222222222222222222222222/manifest.json";
                break;
            case "generation":
                pointer["generation"] = 0;
                break;
            case "action":
                pointer["action"] = "promote";
                break;
        }

        var body = pointer.ToJsonString();
        if (mutation == "duplicate")
            body = body.Replace("{", "{\"action\":\"activate\",", StringComparison.Ordinal);
        File.WriteAllText(registry.PointerPath, body);
        using var reader = registry.Reader();

        (await reader.ReadPointerAsync()).Failure.Should().Be(RegistryFailure.ReleasePointerInvalid);
    }

    [Fact]
    public async Task ManifestBytesMutated_ReturnsManifestHashMismatch()
    {
        using var registry = new TempRegistry();
        File.AppendAllText(registry.ManifestPath, " ");
        using var reader = registry.Reader();
        var pointer = (await reader.ReadPointerAsync()).Value!;

        (await reader.ReadManifestAsync(pointer.ReleaseId, pointer.ManifestSha256)).Failure.Should().Be(RegistryFailure.ManifestHashMismatch);
    }

    [Fact]
    public async Task ManifestMissing_ReturnsManifestMissing()
    {
        using var registry = new TempRegistry();
        File.Delete(registry.ManifestPath);
        using var reader = registry.Reader();
        var pointer = (await reader.ReadPointerAsync()).Value!;

        (await reader.ReadManifestAsync(pointer.ReleaseId, pointer.ManifestSha256)).Failure.Should().Be(RegistryFailure.ManifestMissing);
    }

    [Fact]
    public async Task DuplicateManifestKey_ReturnsManifestInvalid()
    {
        using var registry = new TempRegistry();
        var original = File.ReadAllText(registry.ManifestPath);
        var duplicated = original.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal);
        duplicated.Should().NotBe(original);
        var sha = registry.WriteManifestBytes(Encoding.UTF8.GetBytes(duplicated));
        using var reader = registry.Reader();

        (await reader.ReadManifestAsync(NotificationFixtures.ReleaseId, sha)).Failure.Should().Be(RegistryFailure.ManifestInvalid);
    }

    [Theory]
    [InlineData("releaseId")]
    [InlineData("sequence")]
    [InlineData("objectsNotList")]
    [InlineData("traversalKey")]
    [InlineData("templateCode")]
    [InlineData("contractKey")]
    [InlineData("foreignObject")]
    [InlineData("objectOutsideReleases")]
    [InlineData("manifestAsObject")]
    public async Task ManifestFailingValidation_ReturnsManifestInvalid(string mutation)
    {
        using var registry = new TempRegistry();
        var manifest = registry.ReadJson(registry.ManifestPath);
        switch (mutation)
        {
            case "releaseId":
                manifest["releaseId"] = "rel-2222222222222222222222222222222222222222";
                break;
            case "sequence":
                manifest["sequence"] = 0;
                break;
            case "objectsNotList":
                manifest["objects"] = new JsonObject();
                break;
            case "traversalKey":
                manifest["objects"]![0]!["key"] = "email-templates/../secrets.json";
                break;
            case "templateCode":
                manifest["templates"]![0]!["code"] = "Bad_Code";
                break;
            case "contractKey":
                manifest["contracts"]![0]!["key"] = "auth.password_reset";
                break;
            case "foreignObject":
                manifest["objects"]![0]!["key"] = "email-templates/releases/rel-2222222222222222222222222222222222222222/intents/x.json";
                break;
            case "objectOutsideReleases":
                manifest["objects"]![0]!["key"] = "email-templates/intents/x.json";
                break;
            case "manifestAsObject":
                manifest["objects"]![0]!["key"] = NotificationFixtures.ReleasePrefix + "/manifest.json";
                break;
        }

        var sha = registry.WriteManifest(manifest);
        using var reader = registry.Reader();

        (await reader.ReadManifestAsync(NotificationFixtures.ReleaseId, sha)).Failure.Should().Be(RegistryFailure.ManifestInvalid);
    }

    [Fact]
    public async Task HtmlByteFlipped_ReturnsAssetHashMismatch()
    {
        using var registry = new TempRegistry();
        var path = registry.PathFor(ResetHtmlKey);
        var bytes = File.ReadAllBytes(path);
        bytes[100] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        (await LoadResetAsync(registry)).Should().Be(RegistryFailure.AssetHashMismatch);
    }

    [Fact]
    public async Task HtmlDeleted_ReturnsAssetMissing()
    {
        using var registry = new TempRegistry();
        File.Delete(registry.PathFor(ResetHtmlKey));

        (await LoadResetAsync(registry)).Should().Be(RegistryFailure.AssetMissing);
    }

    [Fact]
    public async Task ManifestContractShaDiffersFromLock_ReturnsContractHashMismatch()
    {
        using var registry = new TempRegistry();
        var manifest = registry.ReadJson(registry.ManifestPath);
        var contract = manifest["contracts"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == NotificationFixtures.ResetKey)!;
        contract["sha256"] = new string('a', 64);
        var sha = registry.WriteManifest(manifest);

        (await LoadResetAsync(registry, sha)).Should().Be(RegistryFailure.ContractHashMismatch);
    }

    [Fact]
    public async Task ConsistentlyRepublishedContract_StillFailsAgainstTheLock()
    {
        using var registry = new TempRegistry();
        var contractKey = NotificationFixtures.ReleasePrefix + "/intents/auth.password_reset.requested.v1.json";
        var contractPath = registry.PathFor(contractKey);
        var changed = Encoding.UTF8.GetBytes(File.ReadAllText(contractPath).Replace("\"example\": \"Sample\"", "\"example\": \"Changed\"", StringComparison.Ordinal));
        File.WriteAllBytes(contractPath, changed);
        var changedSha = Convert.ToHexStringLower(SHA256.HashData(changed));

        var manifest = registry.ReadJson(registry.ManifestPath);
        var entry = manifest["objects"]!.AsArray().Single(o => o!["key"]!.GetValue<string>() == contractKey)!;
        entry["sha256"] = changedSha;
        entry["length"] = changed.Length;
        manifest["contracts"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == NotificationFixtures.ResetKey)!["sha256"] = changedSha;
        var sha = registry.WriteManifest(manifest);

        (await LoadResetAsync(registry, sha)).Should().Be(RegistryFailure.ContractHashMismatch);
    }

    [Fact]
    public async Task ContractObjectDeleted_ReturnsContractMissing()
    {
        using var registry = new TempRegistry();
        File.Delete(registry.PathFor(NotificationFixtures.ReleasePrefix + "/intents/auth.password_reset.requested.v1.json"));

        (await LoadResetAsync(registry)).Should().Be(RegistryFailure.ContractMissing);
    }

    [Fact]
    public async Task UnknownTemplate_ReturnsTemplateMissing()
    {
        using var reader = NotificationFixtures.Reader();
        var manifest = await NotificationFixtures.ManifestAsync(reader);

        var result = await reader.LoadPinnedTemplateAsync(manifest, "pl-99-missing", NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);

        result.Failure.Should().Be(RegistryFailure.TemplateMissing);
    }

    [Theory]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, "assigned_approver")]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 2, NotificationFixtures.ResetAudience)]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ApprovalKey, 1, NotificationFixtures.ResetAudience)]
    public async Task DeclarationMismatch_ReturnsTemplateIntentMismatch(string code, string key, int version, string audience)
    {
        using var reader = NotificationFixtures.Reader();
        var manifest = await NotificationFixtures.ManifestAsync(reader);

        var result = await reader.LoadPinnedTemplateAsync(manifest, code, key, version, audience);

        result.Failure.Should().Be(RegistryFailure.TemplateIntentMismatch);
    }

    [Theory]
    [InlineData("email-templates/../secrets.json")]
    [InlineData("email-templates/releases%2Flatest.json")]
    [InlineData(".meta/latest.json")]
    [InlineData("email-templates/.hidden")]
    [InlineData("email-templates//latest.json")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task LocalStore_RejectsKeysThatAreNotPlain(string key)
    {
        var store = new LocalDirectoryRegistryStore(NotificationFixtures.RegistryRoot);

        var act = () => store.GetAsync(key, CancellationToken.None);

        await act.Should().ThrowAsync<RegistryKeyRejectedException>();
    }

    [Fact]
    public async Task LocalStore_ReturnsNullForMissingObject()
    {
        var store = new LocalDirectoryRegistryStore(NotificationFixtures.RegistryRoot);

        (await store.GetAsync("email-templates/releases/absent.json", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LocalStore_RejectsObjectsOverTheSizeLimit()
    {
        using var registry = new TempRegistry();
        File.WriteAllBytes(registry.PathFor("email-templates/large.bin"), new byte[RegistryKey.MaxObjectBytes + 1]);
        var store = new LocalDirectoryRegistryStore(registry.Root);

        var act = () => store.GetAsync("email-templates/large.bin", CancellationToken.None);

        await act.Should().ThrowAsync<RegistryObjectTooLargeException>();
    }

    [Fact]
    public async Task FileSymlinkEscapingRoot_IsRejected()
    {
        using var registry = new TempRegistry();
        using var outside = new TempDirectory();
        var htmlPath = registry.PathFor(ResetHtmlKey);
        var escaped = Path.Combine(outside.Root, "copy.html");
        File.Copy(htmlPath, escaped);
        File.Delete(htmlPath);
        File.CreateSymbolicLink(htmlPath, escaped);
        var store = new LocalDirectoryRegistryStore(registry.Root);

        var act = () => store.GetAsync(ResetHtmlKey, CancellationToken.None);

        await act.Should().ThrowAsync<RegistryKeyRejectedException>();
        (await LoadResetAsync(registry)).Should().Be(RegistryFailure.AssetMissing);
    }

    [Fact]
    public async Task DirectorySymlinkEscapingRoot_IsRejected()
    {
        using var registry = new TempRegistry();
        using var outside = new TempDirectory();
        var templates = registry.PathFor(NotificationFixtures.ReleasePrefix + "/templates");
        var escaped = Path.Combine(outside.Root, "templates");
        TempRegistry.Copy(templates, escaped);
        Directory.Delete(templates, recursive: true);
        Directory.CreateSymbolicLink(templates, escaped);
        var store = new LocalDirectoryRegistryStore(registry.Root);

        var act = () => store.GetAsync(ResetHtmlKey, CancellationToken.None);

        await act.Should().ThrowAsync<RegistryKeyRejectedException>();
    }

    [Fact]
    public async Task SymlinkInsideRoot_IsFollowed()
    {
        using var registry = new TempRegistry();
        var htmlPath = registry.PathFor(ResetHtmlKey);
        var inside = registry.PathFor("email-templates/inside.html");
        File.Move(htmlPath, inside);
        File.CreateSymbolicLink(htmlPath, inside);

        (await LoadResetAsync(registry)).Should().Be(RegistryFailure.None);
    }

    [Fact]
    public async Task BlobStore_NullObject_ReturnsAssetMissing()
    {
        var files = new Mock<IFileUploadService>();
        files.Setup(f => f.GetObjectBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                key == ResetHtmlKey ? null : ReadFixture(key));
        using var reader = NotificationFixtures.Reader(new BlobRegistryStore(files.Object));
        var manifest = await NotificationFixtures.ManifestAsync(reader);

        var result = await reader.LoadPinnedTemplateAsync(manifest, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);

        result.Failure.Should().Be(RegistryFailure.AssetMissing);
    }

    [Fact]
    public async Task BlobStore_Throwing_RaisesStorageUnavailable()
    {
        var files = new Mock<IFileUploadService>();
        files.Setup(f => f.GetObjectBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("S3 down"));
        var store = new BlobRegistryStore(files.Object);
        using var reader = NotificationFixtures.Reader(store);

        await store.Invoking(s => s.GetAsync("email-templates/releases/latest.json", CancellationToken.None))
            .Should().ThrowAsync<RegistryStorageUnavailableException>();
        await reader.Invoking(r => r.ReadPointerAsync())
            .Should().ThrowAsync<RegistryStorageUnavailableException>();
    }

    [Fact]
    public async Task VerifiedObjects_AreServedFromCacheAfterFirstRead()
    {
        var files = new Mock<IFileUploadService>();
        files.Setup(f => f.GetObjectBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => ReadFixture(key));
        using var reader = NotificationFixtures.Reader(new BlobRegistryStore(files.Object));
        var manifest = await NotificationFixtures.ManifestAsync(reader);

        await reader.LoadPinnedTemplateAsync(manifest, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);
        await reader.LoadPinnedTemplateAsync(manifest, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);

        files.Verify(f => f.GetObjectBytesAsync(ResetHtmlKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("key")]
    public async Task LoneSurrogateInPointer_ReturnsReleasePointerInvalid(string where)
    {
        using var registry = new TempRegistry();
        var body = File.ReadAllText(registry.PointerPath);
        body = where == "value"
            ? body.Replace("\"activate\"", "\"\\ud800\"", StringComparison.Ordinal)
            : body.Replace("\"action\"", "\"\\ud800\"", StringComparison.Ordinal);
        body.Should().Contain("\\ud800");
        File.WriteAllText(registry.PointerPath, body);
        using var reader = registry.Reader();

        (await reader.ReadPointerAsync()).Failure.Should().Be(RegistryFailure.ReleasePointerInvalid);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("key")]
    public async Task LoneSurrogateInManifest_ReturnsManifestInvalid(string where)
    {
        using var registry = new TempRegistry();
        var original = File.ReadAllText(registry.ManifestPath);
        var mutated = where == "value"
            ? original.Replace("\"id\": \"FIN-01\"", "\"id\": \"\\udc00\"", StringComparison.Ordinal)
            : original.Replace("\"id\": \"FIN-01\"", "\"\\udc00\": \"FIN-01\"", StringComparison.Ordinal);
        mutated.Should().NotBe(original);
        var sha = registry.WriteManifestBytes(Encoding.UTF8.GetBytes(mutated));
        using var reader = registry.Reader();

        (await reader.ReadManifestAsync(NotificationFixtures.ReleaseId, sha)).Failure.Should().Be(RegistryFailure.ManifestInvalid);
    }

    [Fact]
    public async Task ReturnedObjectBytes_AreDefensiveCopies()
    {
        using var reader = NotificationFixtures.Reader();
        var manifest = await NotificationFixtures.ManifestAsync(reader);
        var original = File.ReadAllBytes(Path.Combine(NotificationFixtures.RegistryRoot, ResetHtmlKey));

        var first = await reader.ReadVerifiedObjectAsync(manifest, ResetHtmlKey);
        first.Value![0] ^= 0xFF;
        var second = await reader.ReadVerifiedObjectAsync(manifest, ResetHtmlKey);
        second.Value.Should().Equal(original);
        second.Value![1] ^= 0xFF;

        (await reader.ReadVerifiedObjectAsync(manifest, ResetHtmlKey)).Value.Should().Equal(original);
        var pinned = await reader.LoadPinnedTemplateAsync(manifest, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);
        pinned.Failure.Should().Be(RegistryFailure.None);
        Encoding.UTF8.GetBytes(pinned.Value!.Html).Should().Equal(original);
    }

    [Fact]
    public async Task ContractObjectAndBytesChanged_WhileContractsEntryMatchesLock_ReturnsContractHashMismatch()
    {
        using var registry = new TempRegistry();
        var contractKey = NotificationFixtures.ReleasePrefix + "/intents/auth.password_reset.requested.v1.json";
        var contractPath = registry.PathFor(contractKey);
        var changed = Encoding.UTF8.GetBytes(File.ReadAllText(contractPath).Replace("\"example\": \"Sample\"", "\"example\": \"Changed\"", StringComparison.Ordinal));
        File.WriteAllBytes(contractPath, changed);

        var manifest = registry.ReadJson(registry.ManifestPath);
        var entry = manifest["objects"]!.AsArray().Single(o => o!["key"]!.GetValue<string>() == contractKey)!;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(changed));
        entry["length"] = changed.Length;
        NotificationIntentLock.TryGetContractSha256(NotificationFixtures.ResetKey, 1, out var locked).Should().BeTrue();
        manifest["contracts"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == NotificationFixtures.ResetKey)!["sha256"]!
            .GetValue<string>().Should().Be(locked);
        var sha = registry.WriteManifest(manifest);

        (await LoadResetAsync(registry, sha)).Should().Be(RegistryFailure.ContractHashMismatch);
    }

    [Fact]
    public async Task MalformedUnrelatedTemplate_DoesNotBlockPilots()
    {
        using var registry = new TempRegistry();
        var manifest = registry.ReadJson(registry.ManifestPath);
        var unrelated = manifest["templates"]!.AsArray().Single(t => t!["code"]!.GetValue<string>() == "fin-01-invoice")!.AsObject();
        unrelated.Remove("subject");
        var sha = registry.WriteManifest(manifest);
        using var reader = registry.Reader();

        var read = await reader.ReadManifestAsync(NotificationFixtures.ReleaseId, sha);

        read.Failure.Should().Be(RegistryFailure.None);
        read.Value!.MalformedTemplateCodes.Should().BeEquivalentTo(["fin-01-invoice"]);
        (await reader.LoadPinnedTemplateAsync(read.Value, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience))
            .Failure.Should().Be(RegistryFailure.None);
        (await reader.LoadPinnedTemplateAsync(read.Value, NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, 1, NotificationFixtures.ApprovalAudience))
            .Failure.Should().Be(RegistryFailure.None);
        (await reader.LoadPinnedTemplateAsync(read.Value, "fin-01-invoice", NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience))
            .Failure.Should().Be(RegistryFailure.ManifestInvalid);
    }

    private static byte[]? ReadFixture(string key)
    {
        var path = Path.Combine([NotificationFixtures.RegistryRoot, .. key.Split('/')]);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static async Task<RegistryFailure> LoadResetAsync(TempRegistry registry, string? manifestSha = null)
    {
        using var reader = registry.Reader();
        var pointer = (await reader.ReadPointerAsync()).Value!;
        var manifest = await reader.ReadManifestAsync(pointer.ReleaseId, manifestSha ?? pointer.ManifestSha256);
        manifest.Failure.Should().Be(RegistryFailure.None);
        var result = await reader.LoadPinnedTemplateAsync(manifest.Value!, NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, 1, NotificationFixtures.ResetAudience);
        return result.Failure;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qimerp-outside-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
