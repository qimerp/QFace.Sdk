namespace QimErp.Shared.Common.TenantSetup;

public sealed class CountrySetupProfileResolver(
    IEnumerable<ICountrySetupProfile> profiles,
    ILogger<CountrySetupProfileResolver> logger) : ICountrySetupProfileResolver
{
    private readonly Dictionary<string, ICountrySetupProfile> _byCode =
        profiles.ToDictionary(p => p.CountryCode.ToUpperInvariant(), p => p);

    public ICountrySetupProfile Resolve(string? countryCode)
    {
        var code = CountryCodeNormalizer.Normalize(countryCode);
        if (code is not null && _byCode.TryGetValue(code, out var profile) && profile.SupportsOperationalSetup)
            return profile;

        logger.LogWarning(
            "[CountrySetup] Country '{Code}' has no supported setup profile; country setup is required.",
            code);
        throw new InvalidOperationException("Country setup is required before this workspace can be initialized.");
    }
}
