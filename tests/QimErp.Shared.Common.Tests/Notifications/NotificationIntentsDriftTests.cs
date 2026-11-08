using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using QimErp.Shared.Common.Services.Notifications;
using QimErp.Shared.Common.Services.Notifications.Intents;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class NotificationIntentsDriftTests
{
    [Fact]
    public void RecomputedLockRevision_EqualsGeneratedContractRevision()
    {
        var compact = "{" + string.Join(",", NotificationIntentLock.Contracts
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => $"\"{c.Key}\":\"{c.Value}\"")) + "}";

        var recomputed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compact)));

        recomputed.Should().Be(NotificationIntentLock.Revision);
        NotificationIntents.ContractRevision.Should().Be(NotificationIntentLock.Revision);
    }

    [Fact]
    public void GeneratedIntents_AndLockEntries_MatchExactly()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in typeof(NotificationIntents).GetNestedTypes())
        foreach (var intent in module.GetNestedTypes())
        {
            var key = (string)intent.GetField("Key", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;
            foreach (var version in intent.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.Name.StartsWith('V')))
                generated.Add(NotificationIntentLock.Identity(key, (int)version.GetRawConstantValue()!));
        }

        generated.Should().NotBeEmpty();
        generated.Should().BeEquivalentTo(NotificationIntentLock.Contracts.Keys);
    }

    [Fact]
    public void FixtureIntentFiles_HashToTheirLockEntries()
    {
        var files = Directory.GetFiles(Path.Combine(NotificationFixtures.Root, "intents"), "*.json");
        files.Should().HaveCount(NotificationIntentLock.Contracts.Count);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var split = name.LastIndexOf(".v", StringComparison.Ordinal);
            var key = name[..split];
            var version = int.Parse(name[(split + 2)..], System.Globalization.CultureInfo.InvariantCulture);

            NotificationIntentLock.TryGetContractSha256(key, version, out var locked).Should().BeTrue(name);
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))).Should().Be(locked, name);
        }
    }

    [Fact]
    public void Lock_IsLoadedFromTheEmbeddedResource()
    {
        typeof(NotificationIntentLock).Assembly.GetManifestResourceNames().Should().Contain(NotificationIntentLock.ResourceName);
        NotificationIntentLock.TryGetContractSha256(NotificationIntents.Auth.PasswordResetRequested.Key, NotificationIntents.Auth.PasswordResetRequested.V1, out _)
            .Should().BeTrue();
        NotificationIntentLock.TryGetContractSha256("auth.password_reset.requested", 2, out _).Should().BeFalse();
    }

    [Fact]
    public void LockLoad_FailsFast_WhenRevisionDoesNotMatchContracts()
    {
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(LockBytes()).Replace(NotificationIntentLock.Revision, new string('0', 64), StringComparison.Ordinal));

        var act = () => NotificationIntentLock.Load(body, new string('0', 64));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LockLoad_FailsFast_WhenGeneratedRevisionDiffers()
    {
        var act = () => NotificationIntentLock.Load(LockBytes(), new string('0', 64));

        act.Should().Throw<InvalidOperationException>();
        NotificationIntentLock.Load(LockBytes(), NotificationIntents.ContractRevision).Revision.Should().Be(NotificationIntentLock.Revision);
    }

    private static byte[] LockBytes()
    {
        using var stream = typeof(NotificationIntentLock).Assembly.GetManifestResourceStream(NotificationIntentLock.ResourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
