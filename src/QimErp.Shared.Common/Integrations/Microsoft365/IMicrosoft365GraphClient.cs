namespace QimErp.Shared.Common.Integrations.Microsoft365;

/// <summary>
/// Cached tenant Graph credentials + feature toggles. Written by IAM on save;
/// read by Leave OOF and Company Events Teams meeting flows. Never log secrets.
/// </summary>
public sealed class TenantMicrosoft365ResolvedOptions
{
    public string EntraTenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public bool EnableOutlookOofOnLeave { get; set; }
    public bool HideLeaveTypeInOutlookOof { get; set; }
    public bool EnableCalendarBlockOnLeave { get; set; }
    public bool EnableTeamsPresenceOnLeave { get; set; }
    public bool EnableTeamsMeetingOnCompanyEvents { get; set; }
    public bool EnableSyncProfilePictureFromMicrosoft { get; set; }
    public List<string> ConsentedScopes { get; set; } = [];
}

public sealed class Microsoft365ProfilePhotoResult
{
    public bool Succeeded { get; init; }
    public bool NotFound { get; init; }
    public byte[]? Content { get; init; }
    public string? ContentType { get; init; }
    public string? Etag { get; init; }
    public string? Error { get; init; }
}

public sealed class Microsoft365OnlineMeetingResult
{
    public bool Succeeded { get; init; }
    public string? JoinUrl { get; init; }
    public string? Error { get; init; }
}

public sealed class Microsoft365OofResult
{
    public bool Succeeded { get; init; }
    public bool Skipped { get; init; }
    public string? Error { get; init; }
}

public interface IMicrosoft365GraphClient
{
    Task<Microsoft365ConnectionResolution> ResolveConnectionAsync(
        string tenantId,
        string companyId,
        CancellationToken cancellationToken = default);

    Task<Microsoft365OnlineMeetingResult> TryCreateOnlineMeetingAsync(
        string tenantId,
        string companyId,
        string subject,
        DateTime startUtc,
        DateTime endUtc,
        string organizerUserPrincipalName,
        CancellationToken cancellationToken = default);

    Task<Microsoft365OofResult> TrySetAutomaticRepliesAsync(
        string tenantId,
        string companyId,
        string mailboxUserPrincipalName,
        DateTime startUtc,
        DateTime endUtc,
        string externalMessage,
        string internalMessage,
        bool clear,
        CancellationToken cancellationToken = default);

    Task<Microsoft365OofResult> TryUpsertLeaveCalendarEventAsync(
        string tenantId,
        string companyId,
        string mailboxUserPrincipalName,
        string subject,
        DateTime startUtc,
        DateTime endUtcExclusive,
        string? transactionId,
        bool clear,
        CancellationToken cancellationToken = default);

    Task<Microsoft365OofResult> TrySetLeavePresenceAsync(
        string tenantId,
        string companyId,
        string mailboxUserPrincipalName,
        DateTime startUtc,
        DateTime endUtcExclusive,
        bool clear,
        CancellationToken cancellationToken = default);

    Task<Microsoft365ProfilePhotoResult> TryGetUserProfilePhotoAsync(
        string tenantId,
        string userPrincipalName,
        CancellationToken cancellationToken = default);
}
