namespace QimErp.Shared.Common.Services.Notifications.Intents;

public sealed record NotificationIntentRequest
{
    public required string IntentKey { get; init; }
    public required int IntentVersion { get; init; }
    public required string Channel { get; init; }
    public required string Audience { get; init; }
    public required string OccurrenceId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required IReadOnlyDictionary<string, string> References { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record NotificationIntentAccepted(Guid DeliveryId, bool Duplicate);
