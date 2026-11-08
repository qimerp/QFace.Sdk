namespace QimErp.Shared.Common.Services.Notifications.Rendering;

internal sealed record HtmlLinks(IReadOnlyList<string> AnchorHrefs, IReadOnlyList<string> VmlHrefs, bool HasVmlLink);

/// <summary>Anchors count only outside every comment; VML hrefs come from <c>&lt;v:*&gt;</c> tags inside an <c>[if … mso …]</c> comment other than <c>!mso</c>. Only the first href of a tag is read. Must match Stage 2 <c>intents.py</c> <c>scan_links</c>.</summary>
internal static class HtmlLinkScanner
{
    private static readonly Regex MsoCondition = new(
        @"^\[if\s(?![^\]]*!\s*mso)[^\]]*\bmso\b[^\]]*\]>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    private static readonly char[] Space = [' ', '\t', '\n', '\f', '\r'];

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private static List<(string Name, string? Href)>? Tags(string html)
    {
        var tags = new List<(string Name, string? Href)>();
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<' || i + 1 >= html.Length || !char.IsAsciiLetter(html[i + 1]))
            {
                i++;
                continue;
            }

            var nameStart = ++i;
            while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is ':' or '-'))
                i++;
            var name = html[nameStart..i];

            string? href = null;
            var sawHref = false;
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

                if (!sawHref && attrName.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    sawHref = true;
                    href = value?.Trim(Space);
                }
            }

            if (!closed)
                return null;
            tags.Add((name, href));
        }

        return tags;
    }
}
