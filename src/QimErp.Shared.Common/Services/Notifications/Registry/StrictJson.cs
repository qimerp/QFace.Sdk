namespace QimErp.Shared.Common.Services.Notifications.Registry;

public static class StrictJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Throws <see cref="JsonException"/> on invalid UTF-8, a byte order mark, an undecodable string such as a lone surrogate escape, or a duplicate key at any depth.</summary>
    public static JsonElement Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
            throw new JsonException("JSON must not start with a byte order mark.");

        try
        {
            StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException error)
        {
            throw new JsonException("JSON is not valid UTF-8.", error);
        }

        RejectDuplicateKeys(utf8);

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out JsonElement element)
    {
        try
        {
            element = Parse(utf8);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    public static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
                return false;
            count++;
        }

        return count == expected.Count;
    }

    public static bool HasOnlyProperties(JsonElement element, IReadOnlyCollection<string> required, IReadOnlyCollection<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name) && !optional.Contains(property.Name))
                return false;
            seen.Add(property.Name);
        }

        return required.All(seen.Contains);
    }

    private static string Decode(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString()!;
        }
        catch (InvalidOperationException error)
        {
            throw new JsonException("JSON contains an invalid string.", error);
        }
    }

    private static void RejectDuplicateKeys(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var scopes = new Stack<HashSet<string>?>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek()!.Add(Decode(ref reader)))
                        throw new JsonException("JSON contains a duplicate key.");
                    break;
                case JsonTokenType.String:
                    Decode(ref reader);
                    break;
            }
        }
    }
}
