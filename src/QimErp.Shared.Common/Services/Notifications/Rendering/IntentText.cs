using System.Buffers;

namespace QimErp.Shared.Common.Services.Notifications.Rendering;

public static class IntentText
{
    private static readonly string[] Markers = ["{{", "}}", "[[", "]]"];

    /// <summary>Replaces CR, LF and tab with a space and drops control, format, line and paragraph separator code points and unpaired surrogates.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            remaining = remaining[consumed..];
            if (status != OperationStatus.Done)
                continue;

            if (rune.Value is '\r' or '\n' or '\t')
                builder.Append(' ');
            else if (!IsControlOrFormat(rune))
                builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    public static bool IsControlOrFormat(char c) => char.IsSurrogate(c) || IsControlOrFormat(new Rune(c));

    public static bool IsControlOrFormat(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    public static bool ContainsMarker(string value) => Markers.Any(m => value.Contains(m, StringComparison.Ordinal));

    public static bool IsClean(string value) =>
        !string.IsNullOrWhiteSpace(value) && !ContainsMarker(value) && !HasControlFormatOrInvalid(value);

    private static bool HasControlFormatOrInvalid(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || IsControlOrFormat(rune))
                return true;
            remaining = remaining[consumed..];
        }

        return false;
    }
}
