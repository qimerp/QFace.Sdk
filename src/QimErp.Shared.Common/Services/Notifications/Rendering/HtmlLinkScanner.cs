namespace QimErp.Shared.Common.Services.Notifications.Rendering;

internal sealed record HtmlLinks(IReadOnlyList<string> AnchorHrefs, IReadOnlyList<string> VmlHrefs, bool HasVmlLink);

/// <summary>A placeholder that is the complete value of a typed position, at its offset in the original HTML.</summary>
internal sealed record TypedPlacement(string Name, string Context, int Offset);

/// <summary>Offsets are into the original HTML. <paramref name="CommentTaggedSpans"/> are tags with a comment delimiter inside a quoted attribute value.</summary>
internal sealed record HtmlPlacements(
    IReadOnlyList<TypedPlacement> Placements,
    IReadOnlyList<(string Name, int Offset)> SensitiveAttributeTokens,
    IReadOnlyList<(int Start, int End)> CommentTaggedSpans);

/// <summary>Anchors count only outside every comment; VML hrefs come from <c>&lt;v:*&gt;</c> tags inside an <c>[if … mso …]</c> comment other than <c>!mso</c>. Only the first href of a tag is read. Must match Stage 2 <c>intents.py</c> <c>scan_links</c>.</summary>
internal static class HtmlLinkScanner
{
    public const string CssColor = "cssColor";
    public const string AttrColor = "attrColor";
    public const string ImgSrc = "imgSrc";

    private static readonly Regex MsoCondition = new(
        @"^\[if\s(?![^\]]*!\s*mso)[^\]]*\bmso\b[^\]]*\]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WholePlaceholder = new(@"^\{\{([A-Z][A-Za-z0-9]*)\}\}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{\{([A-Z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant);

    private static readonly string[] RawTextElements = ["style", "script", "title", "textarea", "xmp", "noscript", "iframe", "noembed", "noframes", "plaintext"];
    private static readonly string[] SensitiveAttributes = ["style", "bgcolor", "fillcolor", "src", "srcset", "background"];

    /// <summary>Returns null when the markup is malformed (unterminated comment, tag or quoted attribute).</summary>
    public static HtmlLinks? Scan(string html)
    {
        var visible = new StringBuilder(html.Length);
        var vmlBodies = new List<string>();
        var position = 0;
        while (true)
        {
            var open = html.IndexOf("<!--", position, StringComparison.Ordinal);
            if (open < 0)
            {
                visible.Append(html, position, html.Length - position);
                break;
            }

            visible.Append(html, position, open - position);
            var close = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
            if (close < 0)
                return null;

            var body = html[(open + 4)..close];
            var condition = MsoCondition.Match(body);
            if (condition.Success)
                vmlBodies.Add(body[condition.Length..]);
            position = close + 3;
        }

        var visibleTags = Tags(visible.ToString());
        if (visibleTags is null)
            return null;

        var vmlHrefs = new List<string>();
        foreach (var vmlBody in vmlBodies)
        {
            var tags = Tags(vmlBody);
            if (tags is null)
                return null;
            vmlHrefs.AddRange(tags
                .Where(t => t.Href is not null && t.Name.StartsWith("v:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Href!));
        }

        var anchors = visibleTags
            .Where(t => t.Href is not null && t.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Href!)
            .ToList();

        return new HtmlLinks(anchors, vmlHrefs, vmlHrefs.Count > 0);
    }

    /// <summary>Walks the original HTML; comments are recognised only outside tags, and only <c>[if … mso …]</c> comment bodies are parsed as markup. Returns null when malformed.</summary>
    public static HtmlPlacements? ScanPlacements(string html)
    {
        var placements = new List<TypedPlacement>();
        var sensitive = new List<(string Name, int Offset)>();
        var commentTagged = new List<(int Start, int End)>();
        return Walk(html, 0, html.Length, topLevel: true, placements, sensitive, commentTagged)
            ? new HtmlPlacements(placements, sensitive, commentTagged)
            : null;
    }

    private static bool Walk(
        string html, int from, int limit, bool topLevel,
        List<TypedPlacement> placements, List<(string Name, int Offset)> sensitive, List<(int Start, int End)> commentTagged)
    {
        var i = from;
        while (i < limit)
        {
            if (topLevel && string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, limit - (i + 4), StringComparison.Ordinal);
                if (close < 0)
                    return false;

                var condition = MsoCondition.Match(html, i + 4, close - (i + 4));
                if (condition.Success && condition.Index == i + 4
                    && !Walk(html, condition.Index + condition.Length, close, topLevel: false, placements, sensitive, commentTagged))
                    return false;
                i = close + 3;
                continue;
            }

            if (html[i] != '<' || i + 1 >= limit || !char.IsAsciiLetter(html[i + 1]))
            {
                i++;
                continue;
            }

            var tag = ReadTag(html, ref i, limit);
            if (tag is null)
                return false;
            Classify(html, tag, placements, sensitive, commentTagged);

            if (RawTextElements.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
            {
                var end = html.IndexOf("</" + tag.Name, i, limit - i, StringComparison.OrdinalIgnoreCase);
                i = end < 0 ? limit : end;
            }
        }

        return true;
    }

    private static void Classify(
        string html, HtmlTag tag,
        List<TypedPlacement> placements, List<(string Name, int Offset)> sensitive, List<(int Start, int End)> commentTagged)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in tag.Attributes)
        {
            if (attribute.Quoted
                && (attribute.Value.Contains("<!--", StringComparison.Ordinal) || attribute.Value.Contains("-->", StringComparison.Ordinal)))
                commentTagged.Add((tag.Start, tag.End));

            if (SensitiveAttributes.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (Match match in Placeholder.Matches(attribute.Value))
                    sensitive.Add((match.Groups[1].Value, attribute.ValueOffset + match.Index));
            }

            if (!seen.Add(attribute.Name) || !attribute.Quoted)
                continue;

            if (attribute.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                AddCssColorPlacements(attribute, placements);
                continue;
            }

            var whole = WholePlaceholder.Match(attribute.Value);
            if (!whole.Success)
                continue;

            if (attribute.Name.Equals("bgcolor", StringComparison.OrdinalIgnoreCase) || attribute.Name.Equals("fillcolor", StringComparison.OrdinalIgnoreCase))
                placements.Add(new TypedPlacement(whole.Groups[1].Value, AttrColor, attribute.ValueOffset));
            else if (attribute.Name.Equals("src", StringComparison.OrdinalIgnoreCase) && tag.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
                placements.Add(new TypedPlacement(whole.Groups[1].Value, ImgSrc, attribute.ValueOffset));
        }
    }

    /// <summary>Any escape, entity, comment or unbalanced quote or parenthesis makes the whole declaration list ineligible.</summary>
    private static void AddCssColorPlacements(HtmlAttribute attribute, List<TypedPlacement> placements)
    {
        var style = attribute.Value;
        if (!style.Contains("{{", StringComparison.Ordinal)
            || style.Contains('\\') || style.Contains('&') || style.Contains("/*", StringComparison.Ordinal))
            return;

        var declarations = new List<(int Start, int End)>();
        var start = 0;
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < style.Length; i++)
        {
            var c = style[i];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '(')
                depth++;
            else if (c == ')')
            {
                if (--depth < 0)
                    return;
            }
            else if (c == ';' && depth == 0)
            {
                declarations.Add((start, i));
                start = i + 1;
            }
        }

        if (quote is not null || depth != 0)
            return;
        declarations.Add((start, style.Length));

        foreach (var (declarationStart, declarationEnd) in declarations)
        {
            var colon = style.IndexOf(':', declarationStart, declarationEnd - declarationStart);
            if (colon < 0)
                continue;

            var property = style[declarationStart..colon].Trim(Space);
            if (!property.Equals("color", StringComparison.OrdinalIgnoreCase) && !property.Equals("background-color", StringComparison.OrdinalIgnoreCase))
                continue;

            var valueStart = colon + 1;
            var valueEnd = declarationEnd;
            while (valueStart < valueEnd && IsSpace(style[valueStart]))
                valueStart++;
            while (valueEnd > valueStart && IsSpace(style[valueEnd - 1]))
                valueEnd--;

            var whole = WholePlaceholder.Match(style[valueStart..valueEnd]);
            if (whole.Success)
                placements.Add(new TypedPlacement(whole.Groups[1].Value, CssColor, attribute.ValueOffset + valueStart));
        }
    }

    private static readonly char[] Space = [' ', '\t', '\n', '\f', '\r'];

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private sealed record HtmlAttribute(string Name, string Value, bool Quoted, int ValueOffset);

    private sealed record HtmlTag(string Name, string? Href, List<HtmlAttribute> Attributes, int Start, int End);

    private static List<HtmlTag>? Tags(string html)
    {
        var tags = new List<HtmlTag>();
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<' || i + 1 >= html.Length || !char.IsAsciiLetter(html[i + 1]))
            {
                i++;
                continue;
            }

            var tag = ReadTag(html, ref i, html.Length);
            if (tag is null)
                return null;
            tags.Add(tag);
        }

        return tags;
    }

    /// <summary><paramref name="i"/> must point at a <c>&lt;</c> followed by an ASCII letter; nothing at or past <paramref name="limit"/> is read.</summary>
    private static HtmlTag? ReadTag(string html, ref int i, int limit)
    {
        var tagStart = i;
        var nameStart = ++i;
        while (i < limit && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is ':' or '-'))
            i++;
        var name = html[nameStart..i];

        string? href = null;
        var sawHref = false;
        var attributes = new List<HtmlAttribute>();
        var closed = false;
        while (i < limit)
        {
            if (IsSpace(html[i]) || html[i] == '/')
            {
                i++;
                continue;
            }

            if (html[i] == '>')
            {
                i++;
                closed = true;
                break;
            }

            var attrStart = i;
            while (i < limit && !IsSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
                i++;
            var attrName = html[attrStart..i];

            var j = i;
            while (j < limit && IsSpace(html[j]))
                j++;

            string? value = null;
            var quoted = false;
            var valueOffset = j;
            if (j < limit && html[j] == '=')
            {
                j++;
                while (j < limit && IsSpace(html[j]))
                    j++;
                if (j >= limit)
                    return null;

                if (html[j] is '"' or '\'')
                {
                    var end = html.IndexOf(html[j], j + 1, limit - (j + 1));
                    if (end < 0)
                        return null;
                    valueOffset = j + 1;
                    value = html[(j + 1)..end];
                    quoted = true;
                    j = end + 1;
                }
                else
                {
                    var start = j;
                    while (j < limit && !IsSpace(html[j]) && html[j] != '>')
                        j++;
                    valueOffset = start;
                    value = html[start..j];
                }

                i = j;
            }

            attributes.Add(new HtmlAttribute(attrName, value ?? string.Empty, quoted, valueOffset));

            if (!sawHref && attrName.Equals("href", StringComparison.OrdinalIgnoreCase))
            {
                sawHref = true;
                href = value?.Trim(Space);
            }
        }

        return closed ? new HtmlTag(name, href, attributes, tagStart, i) : null;
    }
}
