using System.Text;
using System.Text.Json;
using AtlassianCli.Serialization;

namespace AtlassianCli.Output;

public enum OutputFormat { Table, Json, Markdown }
public enum OutputKind { General, JiraIssue, JiraUser, JiraComment, Transition, ConfluenceContent, Space, Label, Restriction }

public sealed class OutputWriter(OutputFormat format, TextWriter writer)
{
    public Task WriteAsync(ListResult value, OutputKind kind, CancellationToken cancellationToken)
        => WriteAsync(JsonSerializer.SerializeToElement(value, ApiJsonContext.Default.ListResult), kind, cancellationToken);

    public Task MessageAsync(string message, CancellationToken cancellationToken)
        => WriteAsync(JsonSerializer.SerializeToElement(new OperationMessage(message), ApiJsonContext.Default.OperationMessage), OutputKind.General, cancellationToken);

    public async Task WriteAsync(JsonElement value, OutputKind kind, CancellationToken cancellationToken)
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
            string text = TerminalSafe(Render(value, kind));
            await writer.WriteLineAsync(text.AsMemory(), cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
    }

    private string Render(JsonElement value, OutputKind kind)
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
            result.AppendLine(string.Join("\t", headings));
            foreach (JsonElement item in values)
            {
                result.AppendLine(string.Join("\t", Cells(item, kind).Select(SingleLine)));
            }
        }

        foreach (JsonElement item in values)
        {
            string body = kind switch
            {
                OutputKind.JiraIssue => JsonValues.Text(item, "fields", "description"),
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

    private static string[] Headings(OutputKind kind) => kind switch
    {
        OutputKind.JiraIssue => ["KEY", "SUMMARY", "STATUS", "ASSIGNEE"],
        OutputKind.JiraUser => ["NAME", "DISPLAY NAME", "ACTIVE"],
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
            OutputKind.JiraComment => [T("id"), T("author", "displayName"), T("created")],
            OutputKind.Transition => [T("id"), T("name"), T("to", "name")],
            OutputKind.Space => [T("key"), T("name"), T("type"), T("status")],
            OutputKind.Label => [T("name"), T("prefix")],
            OutputKind.Restriction => [T("operation")],
            OutputKind.ConfluenceContent => [T("id"), T("title"), T("type"), T("version", "number"), Parent(item)],
            _ => [T("message").Length > 0 ? T("message") : T("name") + " " + T("version")]
        };
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
