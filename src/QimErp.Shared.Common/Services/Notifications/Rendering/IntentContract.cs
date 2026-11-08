using QimErp.Shared.Common.Security;

namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public enum IntentFieldType
{
    Text,
    Url,
    Hostname,
    Datetime,
    Date,
    Money,
    Integer,
    HtmlFragment,
}

public enum IntentResolutionPhase
{
    Precheck,
    AfterPrecheck,
}

public static class IntentFieldSources
{
    public const string Producer = "producer";
    public const string Platform = "platform";
}

public sealed record IntentContract(
    string Key,
    int Version,
    IReadOnlyList<IntentAudience> Audiences,
    IReadOnlyList<IntentReference> References,
    IReadOnlyList<IntentField> Fields);

public sealed record IntentAudience(string Key, string RecipientSource, string RecipientFrom);

public sealed record IntentReference(string Name, string Type, bool Required, DataClassification Sensitivity);

/// <summary><paramref name="Source"/> is <see cref="IntentFieldSources.Producer"/> or <see cref="IntentFieldSources.Platform"/>.</summary>
public sealed record IntentField(
    string Name,
    IntentFieldType Type,
    bool Required,
    string Source,
    DataClassification Sensitivity,
    IntentResolutionPhase ResolvedAt,
    string? ResolvedFrom,
    IntentUrlRule? Url);

public sealed record IntentUrlRule(string Host, string? Purpose, string? Reference, bool SingleUse);
