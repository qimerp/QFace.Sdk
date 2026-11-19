using System.Collections.Immutable;

namespace QimErp.Shared.Common.Integrations.Microsoft365;

public sealed record Microsoft365ConnectionCatalog
{
    public string TenantId { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public bool IsComplete { get; init; }
    public DateTimeOffset ValidUntilUtc { get; init; }
    public Microsoft365ConnectionEntry? DefaultConnection { get; init; }
    public ImmutableDictionary<string, Microsoft365ConnectionEntry> CompanyConnections { get; init; }
        = ImmutableDictionary<string, Microsoft365ConnectionEntry>.Empty;
}

public sealed record Microsoft365ConnectionEntry
{
    public TenantMicrosoft365ResolvedOptions? Options { get; init; }
    public string? Error { get; init; }
}

public enum Microsoft365ConnectionStatus
{
    Ready,
    NotConfigured,
    Unavailable,
    Invalid
}

public sealed record Microsoft365ConnectionResolution
{
    public Microsoft365ConnectionStatus Status { get; init; }
    public TenantMicrosoft365ResolvedOptions? Options { get; init; }
    public string? SourceCompanyId { get; init; }
    public string? Revision { get; init; }
    public string? Error { get; init; }
}

public sealed class Microsoft365CatalogOptions
{
    public const string SectionName = "Microsoft365Catalog";
    public const string HydrationPath = "/internal/integrations/microsoft365/catalog/hydrate";

    public string PlatformOrigin { get; set; } = string.Empty;
    public string HydrationKey { get; set; } = string.Empty;
    public string ProtectionKey { get; set; } = string.Empty;
}
