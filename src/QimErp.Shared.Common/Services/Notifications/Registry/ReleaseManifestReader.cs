using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using QimErp.Shared.Common.Services.Notifications.Intents;
using QimErp.Shared.Common.Services.Notifications.Rendering;

namespace QimErp.Shared.Common.Services.Notifications.Registry;

public sealed record ReleaseManifestReaderOptions
{
    /// <summary>A rollback or activation becomes visible only after this many seconds; 0 disables pointer caching.</summary>
    public int PointerCacheSeconds { get; init; } = 30;
    public long CacheSizeLimitBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>Store exceptions other than missing or rejected keys propagate; <see cref="RegistryStorageUnavailableException"/> is transient.</summary>
public sealed class ReleaseManifestReader : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly INotificationRegistryStore _store;
    private readonly RegistryLayout _layout;
    private readonly TimeSpan _pointerCacheDuration;
    private readonly MemoryCache _cache;

    public ReleaseManifestReader(INotificationRegistryStore store, RegistryLayout layout, ReleaseManifestReaderOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        options ??= new ReleaseManifestReaderOptions();
        _pointerCacheDuration = TimeSpan.FromSeconds(Math.Max(0, options.PointerCacheSeconds));
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, options.CacheSizeLimitBytes) });
    }

    public async Task<RegistryResult<ReleasePointer>> ReadPointerAsync(CancellationToken ct = default)
    {
        var cacheKey = ("pointer", _layout.PointerKey);
        if (_cache.TryGetValue(cacheKey, out ReleasePointer? cached) && cached is not null)
            return RegistryResult<ReleasePointer>.Ok(cached);

        byte[]? body;
        try
        {
            body = await _store.GetAsync(_layout.PointerKey, ct);
        }
        catch (RegistryKeyRejectedException)
        {
            return RegistryResult<ReleasePointer>.Fail(RegistryFailure.ReleasePointerInvalid);
        }
        catch (RegistryObjectTooLargeException)
        {
            return RegistryResult<ReleasePointer>.Fail(RegistryFailure.ReleasePointerInvalid);
        }

        if (body is null)
            return RegistryResult<ReleasePointer>.Fail(RegistryFailure.ReleasePointerMissing);

        var pointer = ReleaseManifestParser.ParsePointer(body, _layout);
        if (pointer is null)
            return RegistryResult<ReleasePointer>.Fail(RegistryFailure.ReleasePointerInvalid);

        if (_pointerCacheDuration > TimeSpan.Zero)
            _cache.Set(cacheKey, pointer, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _pointerCacheDuration });

        return RegistryResult<ReleasePointer>.Ok(pointer);
    }

    public async Task<RegistryResult<ReleaseManifest>> ReadManifestAsync(string releaseId, string expectedManifestSha256, CancellationToken ct = default)
    {
        if (!RegistryLayout.IsReleaseId(releaseId) || !ReleaseManifestParser.IsHex64(expectedManifestSha256))
            return RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestInvalid);

        var key = _layout.ManifestKey(releaseId);
        var cacheKey = (releaseId, key, expectedManifestSha256);
        if (!_cache.TryGetValue(cacheKey, out byte[]? body) || body is null)
        {
            try
            {
                body = await _store.GetAsync(key, ct);
            }
            catch (RegistryKeyRejectedException)
            {
                return RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestMissing);
            }
            catch (RegistryObjectTooLargeException)
            {
                return RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestInvalid);
            }

            if (body is null)
                return RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestMissing);

            if (!Sha256Equals(body, expectedManifestSha256))
                return RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestHashMismatch);

            Remember(cacheKey, body);
        }

        var manifest = ReleaseManifestParser.ParseManifest(body, _layout, releaseId, expectedManifestSha256);
        return manifest is null
            ? RegistryResult<ReleaseManifest>.Fail(RegistryFailure.ManifestInvalid)
            : RegistryResult<ReleaseManifest>.Ok(manifest);
    }

    public async Task<RegistryResult<byte[]>> ReadVerifiedObjectAsync(ReleaseManifest manifest, string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var entry = manifest.Objects.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
        if (entry is null)
            return RegistryResult<byte[]>.Fail(RegistryFailure.AssetMissing);

        var cacheKey = (manifest.ReleaseId, entry.Key, entry.Sha256);
        if (_cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null)
            return RegistryResult<byte[]>.Ok((byte[])cached.Clone());

        byte[]? body;
        try
        {
            body = await _store.GetAsync(entry.Key, ct);
        }
        catch (RegistryKeyRejectedException)
        {
            return RegistryResult<byte[]>.Fail(RegistryFailure.AssetMissing);
        }
        catch (RegistryObjectTooLargeException)
        {
            return RegistryResult<byte[]>.Fail(RegistryFailure.AssetHashMismatch);
        }

        if (body is null)
            return RegistryResult<byte[]>.Fail(RegistryFailure.AssetMissing);

        if (body.LongLength != entry.Length || !Sha256Equals(body, entry.Sha256))
            return RegistryResult<byte[]>.Fail(RegistryFailure.AssetHashMismatch);

        Remember(cacheKey, (byte[])body.Clone());
        return RegistryResult<byte[]>.Ok(body);
    }

    public async Task<RegistryResult<PinnedTemplate>> LoadPinnedTemplateAsync(
        ReleaseManifest manifest,
        string templateCode,
        string intentKey,
        int intentVersion,
        string audience,
        CancellationToken ct = default,
        string? variant = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.MalformedTemplateCodes.Contains(templateCode))
            return Fail(RegistryFailure.ManifestInvalid);

        var template = manifest.Templates.FirstOrDefault(t => string.Equals(t.Code, templateCode, StringComparison.Ordinal));
        if (template is null)
            return Fail(RegistryFailure.TemplateMissing);

        var htmlKey = template.HtmlKey;
        if (variant is not null)
        {
            if (template.Branding is null || !template.Branding.Variants.TryGetValue(variant, out var declaredVariant))
                return Fail(RegistryFailure.TemplateMissing);
            htmlKey = declaredVariant.HtmlKey;
        }

        var declaration = template.Intents.FirstOrDefault(d =>
            string.Equals(d.Key, intentKey, StringComparison.Ordinal)
            && d.Version == intentVersion
            && string.Equals(d.Audience, audience, StringComparison.Ordinal));
        if (declaration is null)
            return Fail(RegistryFailure.TemplateIntentMismatch);

        var contractEntry = manifest.Contracts.FirstOrDefault(c =>
            string.Equals(c.Key, intentKey, StringComparison.Ordinal) && c.Version == intentVersion);
        if (contractEntry is null || !NotificationIntentLock.TryGetContractSha256(intentKey, intentVersion, out var lockedSha256))
            return Fail(RegistryFailure.ContractMissing);

        if (!string.Equals(contractEntry.Sha256, lockedSha256, StringComparison.Ordinal))
            return Fail(RegistryFailure.ContractHashMismatch);

        var contractObject = manifest.Objects.FirstOrDefault(o => string.Equals(o.Key, contractEntry.ObjectKey, StringComparison.Ordinal));
        if (contractObject is null)
            return Fail(RegistryFailure.ContractMissing);
        if (!string.Equals(contractObject.Sha256, lockedSha256, StringComparison.Ordinal))
            return Fail(RegistryFailure.ContractHashMismatch);

        var contractBytes = await ReadVerifiedObjectAsync(manifest, contractEntry.ObjectKey, ct);
        if (!contractBytes.Succeeded)
            return Fail(contractBytes.Failure == RegistryFailure.AssetMissing ? RegistryFailure.ContractMissing : RegistryFailure.ContractHashMismatch);
        if (!Sha256Equals(contractBytes.Value!, lockedSha256))
            return Fail(RegistryFailure.ContractHashMismatch);

        IntentContract contract;
        try
        {
            contract = IntentContractParser.Parse(contractBytes.Value!);
        }
        catch (JsonException)
        {
            return Fail(RegistryFailure.ContractMissing);
        }

        if (!string.Equals(contract.Key, intentKey, StringComparison.Ordinal) || contract.Version != intentVersion)
            return Fail(RegistryFailure.ContractHashMismatch);
        if (!contract.Audiences.Any(a => string.Equals(a.Key, audience, StringComparison.Ordinal)))
            return Fail(RegistryFailure.TemplateIntentMismatch);

        var html = await ReadVerifiedObjectAsync(manifest, htmlKey, ct);
        if (!html.Succeeded)
            return Fail(html.Failure);

        var text = await ReadVerifiedObjectAsync(manifest, template.TextKey, ct);
        if (!text.Succeeded)
            return Fail(text.Failure);

        string htmlContent, textContent;
        try
        {
            htmlContent = StrictUtf8.GetString(html.Value!);
            textContent = StrictUtf8.GetString(text.Value!);
        }
        catch (DecoderFallbackException)
        {
            return Fail(RegistryFailure.ManifestInvalid);
        }

        var htmlSha256 = manifest.Objects.First(o => string.Equals(o.Key, htmlKey, StringComparison.Ordinal)).Sha256;
        return RegistryResult<PinnedTemplate>.Ok(new PinnedTemplate(template, declaration, htmlContent, textContent, contract, lockedSha256)
        {
            Variant = variant,
            VariantHtmlSha256 = htmlSha256,
        });
    }

    public void Dispose() => _cache.Dispose();

    private static RegistryResult<PinnedTemplate> Fail(RegistryFailure failure) => RegistryResult<PinnedTemplate>.Fail(failure);

    private void Remember(object cacheKey, byte[] body) =>
        _cache.Set(cacheKey, body, new MemoryCacheEntryOptions { Size = Math.Max(1, body.LongLength) });

    private static bool Sha256Equals(byte[] body, string expectedHex) =>
        string.Equals(Convert.ToHexStringLower(SHA256.HashData(body)), expectedHex, StringComparison.Ordinal);
}
