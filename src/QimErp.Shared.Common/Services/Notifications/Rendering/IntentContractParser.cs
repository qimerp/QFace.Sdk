using QimErp.Shared.Common.Security;
using QimErp.Shared.Common.Services.Notifications.Registry;

namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public static class IntentContractParser
{
    private static readonly string[] ContractProperties =
        ["schemaVersion", "key", "version", "owner", "summary", "channels", "audiences", "references", "fields"];
    private static readonly string[] FieldRequired = ["name", "type", "required", "source", "sensitivity", "resolvedAt", "example"];
    private static readonly string[] FieldOptional = ["resolvedFrom", "url"];
    private static readonly string[] UrlRequired = ["host"];
    private static readonly string[] UrlOptional = ["purpose", "reference", "singleUse"];

    private static readonly Dictionary<string, IntentFieldType> Types = new(StringComparer.Ordinal)
    {
        ["text"] = IntentFieldType.Text,
        ["url"] = IntentFieldType.Url,
        ["hostname"] = IntentFieldType.Hostname,
        ["datetime"] = IntentFieldType.Datetime,
        ["date"] = IntentFieldType.Date,
        ["money"] = IntentFieldType.Money,
        ["integer"] = IntentFieldType.Integer,
        ["html-fragment"] = IntentFieldType.HtmlFragment,
    };

    private static readonly Dictionary<string, DataClassification> Classifications = new(StringComparer.Ordinal)
    {
        ["Public"] = DataClassification.Public,
        ["Internal"] = DataClassification.Internal,
        ["Confidential"] = DataClassification.Confidential,
        ["Pii"] = DataClassification.Pii,
        ["Sensitive"] = DataClassification.Sensitive,
    };

    private static readonly Dictionary<string, IntentResolutionPhase> Phases = new(StringComparer.Ordinal)
    {
        ["precheck"] = IntentResolutionPhase.Precheck,
        ["afterPrecheck"] = IntentResolutionPhase.AfterPrecheck,
    };

    /// <summary>Throws <see cref="JsonException"/> for unknown or missing properties, duplicate keys, or schemaVersion other than 1.</summary>
    public static IntentContract Parse(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        var root = StrictJson.Parse(utf8);
        Require(StrictJson.HasExactProperties(root, ContractProperties), "Intent contract has unknown or missing properties.");
        Require(ReadInteger(root.GetProperty("schemaVersion")) == 1, "Intent contract schemaVersion is not supported.");

        var key = ReadString(root.GetProperty("key"));
        var version = ReadInteger(root.GetProperty("version"));
        Require(version is > 0 and <= int.MaxValue, "Intent contract version is invalid.");

        var owner = root.GetProperty("owner");
        Require(StrictJson.HasExactProperties(owner, "domain", "service"), "Intent contract owner is invalid.");
        ReadString(owner.GetProperty("domain"));
        ReadString(owner.GetProperty("service"));
        ReadString(root.GetProperty("summary"));
        Require(ReadArray(root.GetProperty("channels")).All(c => c.ValueKind == JsonValueKind.String), "Intent contract channels are invalid.");

        var audiences = ReadArray(root.GetProperty("audiences")).Select(ParseAudience).ToList();
        var references = ReadArray(root.GetProperty("references")).Select(ParseReference).ToList();
        var fields = ReadArray(root.GetProperty("fields")).Select(ParseField).ToList();

        Require(Unique(audiences.Select(a => a.Key)) && Unique(references.Select(r => r.Name)) && Unique(fields.Select(f => f.Name)),
            "Intent contract has duplicate entries.");

        return new IntentContract(key, (int)version, audiences, references, fields);
    }

    private static IntentAudience ParseAudience(JsonElement element)
    {
        Require(StrictJson.HasExactProperties(element, "key", "recipient"), "Intent audience is invalid.");
        var recipient = element.GetProperty("recipient");
        Require(StrictJson.HasExactProperties(recipient, "source", "from"), "Intent audience recipient is invalid.");
        return new IntentAudience(ReadString(element.GetProperty("key")), ReadString(recipient.GetProperty("source")), ReadString(recipient.GetProperty("from")));
    }

    private static IntentReference ParseReference(JsonElement element)
    {
        Require(StrictJson.HasExactProperties(element, "name", "type", "required", "sensitivity"), "Intent reference is invalid.");
        return new IntentReference(
            ReadString(element.GetProperty("name")),
            ReadString(element.GetProperty("type")),
            ReadBoolean(element.GetProperty("required")),
            Lookup(Classifications, element.GetProperty("sensitivity")));
    }

    private static IntentField ParseField(JsonElement element)
    {
        Require(StrictJson.HasOnlyProperties(element, FieldRequired, FieldOptional), "Intent field has unknown or missing properties.");

        var source = ReadString(element.GetProperty("source"));
        Require(source is IntentFieldSources.Producer or IntentFieldSources.Platform, "Intent field source is invalid.");

        string? resolvedFrom = element.TryGetProperty("resolvedFrom", out var resolvedElement) ? ReadString(resolvedElement) : null;
        IntentUrlRule? url = null;
        if (element.TryGetProperty("url", out var urlElement))
        {
            Require(StrictJson.HasOnlyProperties(urlElement, UrlRequired, UrlOptional), "Intent URL rule has unknown or missing properties.");
            url = new IntentUrlRule(
                ReadString(urlElement.GetProperty("host")),
                urlElement.TryGetProperty("purpose", out var purpose) ? ReadString(purpose) : null,
                urlElement.TryGetProperty("reference", out var reference) ? ReadString(reference) : null,
                urlElement.TryGetProperty("singleUse", out var singleUse) && ReadBoolean(singleUse));
        }

        var type = Lookup(Types, element.GetProperty("type"));
        Require((url is not null) == (type == IntentFieldType.Url), "Intent URL rule is required only on URL fields.");

        return new IntentField(
            ReadString(element.GetProperty("name")),
            type,
            ReadBoolean(element.GetProperty("required")),
            source,
            Lookup(Classifications, element.GetProperty("sensitivity")),
            Lookup(Phases, element.GetProperty("resolvedAt")),
            resolvedFrom,
            url);
    }

    private static T Lookup<T>(Dictionary<string, T> values, JsonElement element)
    {
        Require(values.TryGetValue(ReadString(element), out var found), "Intent contract has an unsupported value.");
        return found!;
    }

    private static string ReadString(JsonElement element)
    {
        Require(element.ValueKind == JsonValueKind.String, "Intent contract expected a string.");
        return element.GetString()!;
    }

    private static bool ReadBoolean(JsonElement element)
    {
        Require(element.ValueKind is JsonValueKind.True or JsonValueKind.False, "Intent contract expected a boolean.");
        return element.GetBoolean();
    }

    private static long ReadInteger(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw new JsonException("Intent contract expected an integer.");
        return value;
    }

    private static JsonElement.ArrayEnumerator ReadArray(JsonElement element)
    {
        Require(element.ValueKind == JsonValueKind.Array, "Intent contract expected a list.");
        return element.EnumerateArray();
    }

    private static bool Unique(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return values.All(seen.Add);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new JsonException(message);
    }
}
