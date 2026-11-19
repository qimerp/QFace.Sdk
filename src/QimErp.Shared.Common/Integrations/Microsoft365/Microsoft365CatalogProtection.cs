using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QimErp.Shared.Common.Integrations.Microsoft365;

public static class Microsoft365CatalogProtection
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const byte FormatVersion = 1;

    public static string Protect(Microsoft365ConnectionCatalog catalog, string protectionKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog.TenantId);
        var key = ReadKey(protectionKey);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var payload = new byte[1 + NonceLength + TagLength + plaintext.Length];
        payload[0] = FormatVersion;
        var nonce = payload.AsSpan(1, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        var tag = payload.AsSpan(1 + NonceLength, TagLength);
        var ciphertext = payload.AsSpan(1 + NonceLength + TagLength);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(catalog.TenantId));
            return Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static Microsoft365ConnectionCatalog Unprotect(string payload, string tenantId, string protectionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var encrypted = Convert.FromBase64String(payload);
        if (encrypted.Length <= 1 + NonceLength + TagLength || encrypted[0] != FormatVersion)
            throw new CryptographicException("The Microsoft 365 connection catalog format is invalid.");
        var key = ReadKey(protectionKey);
        var plaintext = new byte[encrypted.Length - 1 - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(encrypted.AsSpan(1, NonceLength), encrypted.AsSpan(1 + NonceLength + TagLength),
                encrypted.AsSpan(1 + NonceLength, TagLength), plaintext, AssociatedData(tenantId));
            var catalog = JsonSerializer.Deserialize<Microsoft365ConnectionCatalog>(plaintext)
                ?? throw new CryptographicException("The Microsoft 365 connection catalog is empty.");
            if (!string.Equals(catalog.TenantId, tenantId, StringComparison.Ordinal))
                throw new CryptographicException("The Microsoft 365 connection catalog tenant does not match.");
            return catalog;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] ReadKey(string protectionKey)
    {
        var key = Convert.FromBase64String(protectionKey);
        if (key.Length == 32)
            return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("Microsoft 365 catalog protection requires a 256-bit key.");
    }

    private static byte[] AssociatedData(string tenantId) =>
        Encoding.UTF8.GetBytes($"QimErp.Microsoft365.Catalog:{FormatVersion}:{tenantId}");
}
