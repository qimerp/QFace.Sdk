using System.Collections.ObjectModel;
using System.Security.Cryptography;
using QimErp.Shared.Common.Services.Notifications.Registry;

namespace QimErp.Shared.Common.Services.Notifications.Intents;

public static class NotificationIntentLock
{
    public const string ResourceName = "QimErp.Shared.Common.NotificationIntents.lock.json";

    private static readonly Regex LockedIdentity = new(@"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){2}@[1-9][0-9]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    static NotificationIntentLock()
    {
        var assembly = typeof(NotificationIntentLock).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        (Revision, Contracts) = Load(buffer.ToArray(), NotificationIntents.ContractRevision);
    }

    internal static (string Revision, IReadOnlyDictionary<string, string> Contracts) Load(byte[] body, string generatedRevision)
    {
        JsonElement root;
        try
        {
            root = StrictJson.Parse(body);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Embedded intent lock is malformed.", error);
        }

        if (root.ValueKind != JsonValueKind.Object
            || !StrictJson.HasExactProperties(root, "contracts", "revision")
            || root.GetProperty("contracts").ValueKind != JsonValueKind.Object
            || root.GetProperty("revision").ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Embedded intent lock is malformed.");

        var revision = root.GetProperty("revision").GetString()!;
        if (!Hex64.IsMatch(revision))
            throw new InvalidOperationException("Embedded intent lock has an invalid revision.");

        var contracts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("contracts").EnumerateObject())
        {
            if (!LockedIdentity.IsMatch(entry.Name)
                || entry.Value.ValueKind != JsonValueKind.String
                || !Hex64.IsMatch(entry.Value.GetString()!))
                throw new InvalidOperationException("Embedded intent lock has an invalid contract entry.");
            contracts.Add(entry.Name, entry.Value.GetString()!);
        }

        var compact = "{" + string.Join(",", contracts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"\"{c.Key}\":\"{c.Value}\"")) + "}";
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compact))), revision, StringComparison.Ordinal)
            || !string.Equals(revision, generatedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Embedded intent lock revision does not match its contracts or the generated intents.");

        return (revision, new ReadOnlyDictionary<string, string>(contracts));
    }

    public static string Revision { get; }

    /// <summary>"key@version" to the lowercase sha256 hex of the locked contract file.</summary>
    public static IReadOnlyDictionary<string, string> Contracts { get; }

    public static bool TryGetContractSha256(string key, int version, out string sha256)
    {
        if (Contracts.TryGetValue(Identity(key, version), out var found))
        {
            sha256 = found;
            return true;
        }

        sha256 = string.Empty;
        return false;
    }

    public static string Identity(string key, int version) =>
        string.Create(CultureInfo.InvariantCulture, $"{key}@{version}");
}
