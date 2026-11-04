using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QimErp.Shared.Common.Filters;

public static class InternalServiceSignature
{
    public const string TenantHeader = "X-Internal-Tenant";
    public const string TimestampHeader = "X-Internal-Timestamp";
    public const string SignatureHeader = "X-Internal-Signature";

    public static string Sign(string key, string tenantId, string method, string path, DateTimeOffset timestamp) =>
        Convert.ToHexString(Compute(key, tenantId, method, path, timestamp.ToUnixTimeSeconds()));

    public static bool Verify(string key, string tenantId, string method, string path,
        string timestampHeader, string signatureHeader, TimeSpan validity, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(tenantId) || validity <= TimeSpan.Zero
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            || unix < DateTimeOffset.MinValue.ToUnixTimeSeconds() || unix > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || Math.Abs(now.ToUnixTimeSeconds() - unix) > validity.TotalSeconds)
            return false;

        byte[] provided;
        try { provided = Convert.FromHexString(signatureHeader); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(provided, Compute(key, tenantId, method, path, unix));
    }

    private static byte[] Compute(string key, string tenantId, string method, string path, long unix) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(string.Join('\n',
            tenantId, method.ToUpperInvariant(), path.ToLowerInvariant(), unix.ToString(CultureInfo.InvariantCulture))));
}
