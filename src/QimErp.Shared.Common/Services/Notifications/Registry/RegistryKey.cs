namespace QimErp.Shared.Common.Services.Notifications.Registry;

public static class RegistryKey
{
    public const int MaxObjectBytes = 16 * 1024 * 1024;

    private static readonly Regex Segment = new(@"^[A-Za-z0-9_@=+-][A-Za-z0-9._@=+-]*\z", RegexOptions.CultureInvariant);

    public static bool IsValid(string? key) => key is not null && Segments(key) is not null;

    public static string[] SplitOrThrow(string? key) =>
        (key is null ? null : Segments(key)) ?? throw new RegistryKeyRejectedException("Registry key is not a plain key.");

    private static string[]? Segments(string key)
    {
        if (key.Length == 0)
            return null;

        var segments = key.Split('/');
        return segments.All(s => Segment.IsMatch(s)) ? segments : null;
    }
}
