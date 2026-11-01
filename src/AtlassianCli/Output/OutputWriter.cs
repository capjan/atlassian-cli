using System.Globalization;
using System.Text;
using System.Text.Json;
using AtlassianCli.Serialization;

namespace AtlassianCli.Output;

public enum OutputFormat { Table, Json, Markdown }
public enum OutputKind { General, JiraIssue, JiraUser, JiraProject, JiraComment, Transition, ConfluenceContent, Space, Label, Restriction, JiraSprint }

public sealed class OutputWriter(OutputFormat format, TextWriter writer)
{
    public Task WriteAsync(ListResult value, OutputKind kind, CancellationToken cancellationToken, bool includeBody = true)
        => WriteAsync(JsonSerializer.SerializeToElement(value, ApiJsonContext.Default.ListResult), kind, cancellationToken, includeBody);

    public Task MessageAsync(string message, CancellationToken cancellationToken)
        => WriteAsync(JsonSerializer.SerializeToElement(new OperationMessage(message), ApiJsonContext.Default.OperationMessage), OutputKind.General, cancellationToken);

    public async Task WriteAsync(JsonElement value, OutputKind kind, CancellationToken cancellationToken, bool includeBody = true)
    {
        if (format == OutputFormat.Json)
        {
            using var buffer = new MemoryStream();
            using (var jsonWriter = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                value.WriteTo(jsonWriter);
            }

            await writer.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()).AsMemory(), cancellationToken);
        }
        else
        {
            string text = TerminalSafe(Render(value, kind, includeBody));
            await writer.WriteLineAsync(text.AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
    }

    private string Render(JsonElement value, OutputKind kind, bool includeBody)
    {
        bool isList = value.TryGetProperty("results", out JsonElement items) && items.ValueKind == JsonValueKind.Array;
        JsonElement[] values = isList ? items.EnumerateArray().ToArray() : [value];
        if (values.Length == 0)
        {
            return "No results.";
        }

        var result = new StringBuilder();
        string[] headings = Headings(kind);
        if (format == OutputFormat.Markdown)
        {
            result.Append("| ").Append(string.Join(" | ", headings)).AppendLine(" |");
            result.Append("| ").Append(string.Join(" | ", headings.Select(_ => "---"))).AppendLine(" |");
            foreach (JsonElement item in values)
            {
                result.Append("| ").Append(string.Join(" | ", Cells(item, kind).Select(cell => StorageMarkdownConverter.EscapeCell(StorageMarkdownConverter.EscapeInline(cell))))).AppendLine(" |");
            }
        }
        else
        {
            AppendTable(result, headings, values.Select(item => Cells(item, kind).Select(SingleLine).ToArray()).ToArray());
        }

        foreach (JsonElement item in values)
        {
            string body = kind switch
            {
                OutputKind.JiraIssue when includeBody => JsonValues.Text(item, "fields", "description"),
                OutputKind.JiraComment => JsonValues.Text(item, "body"),
                OutputKind.ConfluenceContent => JsonValues.Text(item, "body", "storage", "value"),
                _ => ""
            };
            if (body.Length > 0)
            {
                string id = JsonValues.Text(item, "key");
                if (id.Length == 0)
                {
                    id = JsonValues.Text(item, "id");
                }

                result.AppendLine().AppendLine($"### Content: {StorageMarkdownConverter.EscapeInline(id)}").AppendLine();
                result.AppendLine(kind == OutputKind.ConfluenceContent
                    ? StorageMarkdownConverter.Convert(body)
                    : JiraWikiMarkdownConverter.Convert(body));
            }

            if (kind == OutputKind.Restriction)
            {
                result.AppendLine(item.GetRawText());
            }
        }

        if (isList && value.TryGetProperty("hasMore", out JsonElement hasMore) && hasMore.ValueKind == JsonValueKind.True)
        {
            result.AppendLine().AppendLine("More results exist. Use --all or increase --limit.");
        }

        return result.ToString().TrimEnd();
    }

    private static void AppendTable(StringBuilder result, string[] headings, string[][] rows)
    {
        int[] widths = headings.Select(DisplayWidth).ToArray();
        foreach (string[] row in rows)
        {
            for (int index = 0; index < widths.Length; index++)
            {
                widths[index] = Math.Max(widths[index], DisplayWidth(row[index]));
            }
        }

        AppendBorder(result, widths, '┌', '┬', '┐');
        AppendRow(result, headings, widths);
        AppendBorder(result, widths, '├', '┼', '┤');
        foreach (string[] row in rows)
        {
            AppendRow(result, row, widths);
        }

        AppendBorder(result, widths, '└', '┴', '┘');
    }

    private static void AppendBorder(StringBuilder result, int[] widths, char left, char middle, char right)
    {
        result.Append(left);
        for (int index = 0; index < widths.Length; index++)
        {
            if (index > 0)
            {
                result.Append(middle);
            }

            result.Append('─', widths[index] + 2);
        }

        result.Append(right).AppendLine();
    }

    private static void AppendRow(StringBuilder result, string[] cells, int[] widths)
    {
        result.Append('│');
        for (int index = 0; index < widths.Length; index++)
        {
            string cell = cells[index];
            result.Append(' ').Append(cell).Append(' ', widths[index] - DisplayWidth(cell) + 1).Append('│');
        }

        result.AppendLine();
    }

    private static int DisplayWidth(string value)
    {
        int width = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            int elementWidth = 0;
            bool emojiPresentation = false;
            foreach (Rune rune in elements.GetTextElement().EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (rune.Value == 0xFE0F)
                {
                    emojiPresentation = true;
                }

                if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                {
                    continue;
                }

                elementWidth = Math.Max(elementWidth, IsWide(rune.Value) ? 2 : 1);
            }

            width += emojiPresentation ? 2 : elementWidth;
        }

        return width;
    }

    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115F
            or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F000 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD
            or >= 0x2600 and <= 0x27BF;

    private static string[] Headings(OutputKind kind) => kind switch
    {
        OutputKind.JiraIssue => ["KEY", "SUMMARY", "STATUS", "ASSIGNEE"],
        OutputKind.JiraUser => ["NAME", "DISPLAY NAME", "ACTIVE"],
        OutputKind.JiraProject => ["KEY", "NAME", "TYPE", "LEAD"],
        OutputKind.JiraSprint => ["ID", "NAME", "STATE", "START", "END"],
        OutputKind.JiraComment => ["ID", "AUTHOR", "CREATED"],
        OutputKind.Transition => ["ID", "NAME", "TARGET STATUS"],
        OutputKind.Space => ["KEY", "NAME", "TYPE", "STATUS"],
        OutputKind.Label => ["NAME", "PREFIX"],
        OutputKind.Restriction => ["OPERATION"],
        OutputKind.ConfluenceContent => ["ID", "TITLE", "TYPE", "VERSION", "PARENT"],
        _ => ["MESSAGE"]
    };

    private static string[] Cells(JsonElement item, OutputKind kind)
    {
        string T(params string[] path) => JsonValues.Text(item, path);
        return kind switch
        {
            OutputKind.JiraIssue => [T("key"), T("fields", "summary"), T("fields", "status", "name"), T("fields", "assignee", "displayName")],
            OutputKind.JiraUser => [T("name"), T("displayName"), T("active")],
            OutputKind.JiraProject => [T("key"), T("name"), T("projectTypeKey"), T("lead", "displayName")],
            OutputKind.JiraSprint => [T("id"), T("name"), T("state"), SprintDate(item, "startDate"), SprintDate(item, "endDate")],
            OutputKind.JiraComment => [T("id"), T("author", "displayName"), T("created")],
            OutputKind.Transition => [T("id"), T("name"), T("to", "name")],
            OutputKind.Space => [T("key"), T("name"), T("type"), T("status")],
            OutputKind.Label => [T("name"), T("prefix")],
            OutputKind.Restriction => [T("operation")],
            OutputKind.ConfluenceContent => [T("id"), T("title"), T("type"), T("version", "number"), Parent(item)],
            _ => [T("message").Length > 0 ? T("message") : T("name") + " " + T("version")]
        };
    }

    private static string SprintDate(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Jira sprint '{name}' must be an ISO timestamp. Use --output json to inspect the original value.");
        }

        string timestamp = value.GetString()!;
        if (timestamp.Length >= 5 && timestamp[^5] is '+' or '-' && timestamp[^3] != ':')
        {
            timestamp = timestamp.Insert(timestamp.Length - 2, ":");
        }

        if (!DateTimeOffset.TryParseExact(timestamp,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
             "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
        {
            throw new InvalidDataException($"Jira sprint '{name}' is not a valid ISO timestamp. Use --output json to inspect the original value.");
        }

        return date.ToString("dd.MM.yy HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Parent(JsonElement item)
    {
        string container = JsonValues.Text(item, "container", "id");
        if (container.Length > 0)
        {
            return container;
        }

        return item.TryGetProperty("ancestors", out JsonElement ancestors) && ancestors.ValueKind == JsonValueKind.Array
            && ancestors.GetArrayLength() > 0 ? JsonValues.Text(ancestors[ancestors.GetArrayLength() - 1], "id") : "";
    }

    private static string SingleLine(string value) => new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static string TerminalSafe(string value) => new(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
}
