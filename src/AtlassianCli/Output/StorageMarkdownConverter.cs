using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AtlassianCli.Output;

public static partial class StorageMarkdownConverter
{
    private static readonly XNamespace Ac = "urn:confluence:ac";
    private static readonly XNamespace Ri = "urn:confluence:ri";

    public static string Convert(string storage)
    {
        if (storage.Length == 0)
        {
            return "";
        }

        string normalized = NamedEntity().Replace(storage, match =>
        {
            if (match.Value is "&amp;" or "&lt;" or "&gt;" or "&quot;" or "&apos;")
            {
                return match.Value;
            }

            string decoded = WebUtility.HtmlDecode(match.Value);
            return decoded == match.Value
                ? match.Value
                : string.Concat(decoded.EnumerateRunes().Select(rune => $"&#{rune.Value};"));
        });
        using var reader = XmlReader.Create(
            new StringReader($"<root xmlns:ac=\"{Ac}\" xmlns:ri=\"{Ri}\">{normalized}</root>"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        XElement root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        return Children(root).Trim();
    }

    private static string Children(XElement element) => string.Concat(element.Nodes().Select(Render));

    private static string Render(XNode node)
    {
        if (node is XText text)
        {
            return EscapeInline(text.Value.Replace('\u00a0', ' '));
        }

        if (node is not XElement element)
        {
            return "";
        }

        if (element.Name.Namespace == Ac)
        {
            return element.Name.LocalName switch
            {
                "structured-macro" => Macro(element),
                "link" => ConfluenceLink(element),
                "image" => $"[Attachment image: {EscapeInline(element.Descendants(Ri + "attachment").FirstOrDefault()?.Attribute(Ri + "filename")?.Value ?? "unknown")}]\n",
                "rich-text-body" or "plain-text-link-body" or "link-body" or "inline-comment-marker" or "placeholder" => Children(element),
                "emoticon" => EscapeInline(element.Attribute(Ac + "name")?.Value ?? ""),
                _ => Preserve(element)
            };
        }

        if (element.Name.Namespace == Ri)
        {
            return Preserve(element);
        }

        string tag = element.Name.LocalName.ToLowerInvariant();
        return tag switch
        {
            "root" or "div" or "span" or "tbody" or "thead" or "tfoot" => Children(element),
            "p" => Children(element).Trim() + "\n\n",
            "br" => "\n",
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => $"\n{new string('#', tag[1] - '0')} {Children(element).Trim()}\n\n",
            "strong" or "b" => $"**{Children(element)}**",
            "em" or "i" => $"*{Children(element)}*",
            "s" or "del" => $"~~{Children(element)}~~",
            "code" => InlineCode(element.Value),
            "pre" => Fence(element.Value, ""),
            "a" => $"[{Children(element)}](<{LinkTarget(element.Attribute("href")?.Value ?? "")}>)",
            "img" => $"![{EscapeInline(element.Attribute("alt")?.Value ?? "")}](<{LinkTarget(element.Attribute("src")?.Value ?? "")}>)",
            "ul" or "ol" => List(element),
            "li" => Children(element),
            "blockquote" => Quote(Children(element)),
            "table" => Table(element),
            "hr" => "\n---\n\n",
            _ => Preserve(element)
        };
    }

    private static string Macro(XElement macro)
    {
        string name = macro.Attribute(Ac + "name")?.Value ?? "unknown";
        string Parameter(string key) => macro.Elements(Ac + "parameter")
            .FirstOrDefault(item => item.Attribute(Ac + "name")?.Value == key)?.Value ?? "";
        XElement? body = macro.Element(Ac + "rich-text-body");
        return name switch
        {
            "code" => Fence(macro.Element(Ac + "plain-text-body")?.Value ?? "", SafeLanguage(Parameter("language"))),
            "info" or "note" or "warning" or "tip" => Quote($"**{name}:**\n\n{(body is null ? "" : Children(body))}"),
            "expand" => $"\n**{EscapeInline(Parameter("title"))}**\n\n{(body is null ? "" : Children(body))}\n",
            "status" => EscapeInline(Parameter("title")),
            "toc" => "\n[Confluence table of contents]\n\n",
            _ => $"\n**Unsupported Confluence macro: {EscapeInline(name)}**\n{Preserve(macro)}"
        };
    }

    private static string ConfluenceLink(XElement link)
    {
        XElement? page = link.Element(Ri + "page");
        XElement? attachment = link.Element(Ri + "attachment");
        XElement? body = link.Element(Ac + "plain-text-link-body") ?? link.Element(Ac + "link-body");
        string label = body is null ? "" : Children(body);
        if (page is not null)
        {
            string reference = page.Attribute(Ri + "content-title")?.Value ?? "";
            string space = page.Attribute(Ri + "space-key")?.Value ?? "";
            return $"[{(label.Length > 0 ? label : EscapeInline(reference))}; Confluence page: {EscapeInline(space.Length > 0 ? space + ":" + reference : reference)}]";
        }

        if (attachment is not null)
        {
            return $"[Attachment: {EscapeInline(attachment.Attribute(Ri + "filename")?.Value ?? "")}]";
        }

        return Preserve(link);
    }

    private static string List(XElement element)
    {
        var result = new StringBuilder("\n");
        int index = 1;
        foreach (XElement item in element.Elements("li"))
        {
            string marker = element.Name.LocalName == "ol" ? $"{index++}. " : "- ";
            string content = Children(item).Trim();
            string[] lines = content.Split('\n');
            result.Append(marker).AppendLine(lines[0]);
            foreach (string line in lines.Skip(1))
            {
                result.Append(' ', marker.Length).AppendLine(line);
            }
        }

        return result.AppendLine().ToString();
    }

    private static string Table(XElement table)
    {
        if (table.Descendants("td").Concat(table.Descendants("th")).Any(cell =>
            cell.Attribute("rowspan")?.Value is not (null or "1") || cell.Attribute("colspan")?.Value is not (null or "1")))
        {
            return Preserve(table);
        }

        string[][] rows = table.Descendants("tr")
            .Where(row => row.Ancestors("table").First() == table)
            .Select(row => row.Elements().Where(cell => cell.Name.LocalName is "td" or "th")
                .Select(cell => EscapeCell(Children(cell).Trim())).ToArray()).ToArray();
        if (rows.Length == 0)
        {
            return "";
        }

        int columns = rows.Max(row => row.Length);
        var result = new StringBuilder("\n");
        for (int row = 0; row < rows.Length; row++)
        {
            result.Append("| ");
            result.Append(string.Join(" | ", Enumerable.Range(0, columns).Select(column => column < rows[row].Length ? rows[row][column] : "")));
            result.AppendLine(" |");
            if (row == 0)
            {
                result.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columns))).AppendLine(" |");
            }
        }

        return result.AppendLine().ToString();
    }

    public static string EscapeInline(string value)
    {
        var result = new StringBuilder();
        foreach (char character in value)
        {
            if ("\\`*_[]<>#".Contains(character, StringComparison.Ordinal))
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    public static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    private static string LinkTarget(string value) => value.Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static string InlineCode(string value)
    {
        string delimiter = new('`', LongestBackticks(value) + 1);
        return $"{delimiter} {value} {delimiter}";
    }

    public static string Fence(string content, string language)
    {
        string delimiter = new('`', Math.Max(3, LongestBackticks(content) + 1));
        return $"\n{delimiter}{language}\n{content}{(content.EndsWith('\n') ? "" : "\n")}{delimiter}\n\n";
    }

    private static int LongestBackticks(string value)
    {
        int longest = 0;
        int current = 0;
        foreach (char character in value)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string SafeLanguage(string language) => new(language.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '+').ToArray());
    private static string Quote(string value) => "\n" + string.Join("\n", value.Trim().Split('\n').Select(line => "> " + line)) + "\n\n";
    private static string Preserve(XElement element) => Fence(element.ToString(SaveOptions.DisableFormatting), "xml");

    [GeneratedRegex("&[a-zA-Z][a-zA-Z0-9]+;")]
    private static partial Regex NamedEntity();
}
