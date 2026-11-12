namespace QimErp.Shared.Common.Services.Notifications.Rendering;

internal sealed record HtmlLinks(IReadOnlyList<string> AnchorHrefs, IReadOnlyList<string> VmlHrefs, bool HasVmlLink)
{
    /// <summary>Placeholders that are the complete value of a typed position, as (token name, context) pairs; one entry per occurrence.</summary>
    public IReadOnlyList<(string Name, string Context)> TypedPlacements { get; init; } = [];
}

/// <summary>Anchors count only outside every comment; VML hrefs come from <c>&lt;v:*&gt;</c> tags inside an <c>[if … mso …]</c> comment other than <c>!mso</c>. Only the first href of a tag is read. Must match Stage 2 <c>intents.py</c> <c>scan_links</c>.</summary>
internal static class HtmlLinkScanner
{
    public const string CssColor = "cssColor";
    public const string AttrColor = "attrColor";
    public const string ImgSrc = "imgSrc";

    private static readonly Regex MsoCondition = new(
        @"^\[if\s(?![^\]]*!\s*mso)[^\]]*\bmso\b[^\]]*\]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WholePlaceholder = new(@"^\{\{([A-Z][A-Za-z0-9]*)\}\}\z", RegexOptions.CultureInvariant);

    private static readonly string[] RawTextElements = ["style", "script", "title", "textarea", "xmp", "noscript", "iframe", "noembed", "noframes", "plaintext"];

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

        var visibleHtml = visible.ToString();
        var visibleTags = Tags(visibleHtml);
        if (visibleTags is null)
            return null;

        var placements = new List<(string Name, string Context)>();
        AddTypedPlacements(visibleHtml, visibleTags, placements);

        var vmlHrefs = new List<string>();
        foreach (var vmlBody in vmlBodies)
        {
            var tags = Tags(vmlBody);
            if (tags is null)
                return null;
            vmlHrefs.AddRange(tags
                .Where(t => t.Href is not null && t.Name.StartsWith("v:", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Href!));
            AddTypedPlacements(vmlBody, tags, placements);
        }

        var anchors = visibleTags
            .Where(t => t.Href is not null && t.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Href!)
            .ToList();

        return new HtmlLinks(anchors, vmlHrefs, vmlHrefs.Count > 0) { TypedPlacements = placements };
    }

    private static void AddTypedPlacements(string html, List<HtmlTag> tags, List<(string Name, string Context)> placements)
    {
        var rawTextEnd = -1;
        foreach (var tag in tags)
        {
            if (tag.Start < rawTextEnd)
                continue;

            if (RawTextElements.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
            {
                var close = html.IndexOf("</" + tag.Name, tag.End, StringComparison.OrdinalIgnoreCase);
                rawTextEnd = close < 0 ? html.Length : close;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value, quoted) in tag.Attributes)
            {
                if (!seen.Add(name) || !quoted)
                    continue;

                if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    AddCssColorPlacements(value, placements);
                    continue;
                }

                var whole = WholePlaceholder.Match(value);
                if (!whole.Success)
                    continue;

                if (name.Equals("bgcolor", StringComparison.OrdinalIgnoreCase) || name.Equals("fillcolor", StringComparison.OrdinalIgnoreCase))
                    placements.Add((whole.Groups[1].Value, AttrColor));
                else if (name.Equals("src", StringComparison.OrdinalIgnoreCase) && tag.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
                    placements.Add((whole.Groups[1].Value, ImgSrc));
            }
        }
    }

    /// <summary>Any escape, entity, comment or unbalanced quote or parenthesis makes the whole declaration list ineligible.</summary>
    private static void AddCssColorPlacements(string style, List<(string Name, string Context)> placements)
    {
        if (!style.Contains("{{", StringComparison.Ordinal)
            || style.Contains('\\') || style.Contains('&') || style.Contains("/*", StringComparison.Ordinal))
            return;

        var declarations = new List<string>();
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
                declarations.Add(style[start..i]);
                start = i + 1;
            }
        }

        if (quote is not null || depth != 0)
            return;
        declarations.Add(style[start..]);

        foreach (var declaration in declarations)
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                continue;

            var property = declaration[..colon].Trim(Space);
            if (!property.Equals("color", StringComparison.OrdinalIgnoreCase) && !property.Equals("background-color", StringComparison.OrdinalIgnoreCase))
                continue;

            var whole = WholePlaceholder.Match(declaration[(colon + 1)..].Trim(Space));
            if (whole.Success)
                placements.Add((whole.Groups[1].Value, CssColor));
        }
    }

    private static readonly char[] Space = [' ', '\t', '\n', '\f', '\r'];

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private sealed record HtmlTag(string Name, string? Href, List<(string Name, string Value, bool Quoted)> Attributes, int Start, int End);

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

            var tagStart = i;
            var nameStart = ++i;
            while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is ':' or '-'))
                i++;
            var name = html[nameStart..i];

            string? href = null;
            var sawHref = false;
            var attributes = new List<(string Name, string Value, bool Quoted)>();
            var closed = false;
            while (i < html.Length)
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
                while (i < html.Length && !IsSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
                    i++;
                var attrName = html[attrStart..i];

                var j = i;
                while (j < html.Length && IsSpace(html[j]))
                    j++;

                string? value = null;
                var quoted = false;
                if (j < html.Length && html[j] == '=')
                {
                    j++;
                    while (j < html.Length && IsSpace(html[j]))
                        j++;
                    if (j >= html.Length)
                        return null;

                    if (html[j] is '"' or '\'')
                    {
                        var end = html.IndexOf(html[j], j + 1);
                        if (end < 0)
                            return null;
                        value = html[(j + 1)..end];
                        quoted = true;
                        j = end + 1;
                    }
                    else
                    {
                        var start = j;
                        while (j < html.Length && !IsSpace(html[j]) && html[j] != '>')
                            j++;
                        value = html[start..j];
                    }

                    i = j;
                }

                attributes.Add((attrName, value ?? string.Empty, quoted));

                if (!sawHref && attrName.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    sawHref = true;
                    href = value?.Trim(Space);
                }
            }

            if (!closed)
                return null;
            tags.Add(new HtmlTag(name, href, attributes, tagStart, i));
        }

        return tags;
    }
}
