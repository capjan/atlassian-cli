using System.Text.Json;
using System.Xml;
using AtlassianCli.Output;
using AtlassianCli.Serialization;
using static AtlassianCli.Tests.TestSupport;

namespace AtlassianCli.Tests;

internal static class OutputTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Issue overview rendering suppresses returned descriptions without changing detail defaults", async () =>
        {
            foreach (OutputFormat format in new[] { OutputFormat.Table, OutputFormat.Markdown })
            {
                JsonElement issue = Parse("{\"key\":\"DRB-7\",\"fields\":{\"summary\":\"Overview\",\"description\":\"Detailed content\"}}");
                using var output = new StringWriter();
                await new OutputWriter(format, output).WriteAsync(issue, OutputKind.JiraIssue, default, includeBody: false);
                Contains("Overview", output.ToString());
                True(!output.ToString().Contains("Detailed content", StringComparison.Ordinal));
                output.GetStringBuilder().Clear();
                await new OutputWriter(format, output).WriteAsync(issue, OutputKind.JiraIssue, default);
                Contains("Detailed content", output.ToString());
            }
        }
        );
        yield return ("JSON preserves raw unknown fields and storage", async () =>
        {
            JsonElement input = Parse("{\"id\":\"1\",\"custom\":{\"array\":[1,true,null]},\"body\":{\"storage\":{\"value\":\"<p>Hello</p>\"}}}");
            using var output = new StringWriter();
            await new OutputWriter(OutputFormat.Json, output).WriteAsync(input, OutputKind.ConfluenceContent, default);
            Equal("<p>Hello</p>", JsonValues.Text(Parse(output.ToString()), "body", "storage", "value"));
            Equal(3, Parse(output.ToString()).GetProperty("custom").GetProperty("array").GetArrayLength());
        }
        );
        yield return ("List output shape and exact measured size", async () =>
        {
            using var output = new StringWriter();
            var result = new ListResult([Parse("{\"id\":\"1\"}")], 10, 1, 1, 100, true);
            await new OutputWriter(OutputFormat.Json, output).WriteAsync(result, OutputKind.ConfluenceContent, default);
            JsonElement json = Parse(output.ToString());
            Equal(1, json.GetProperty("size").GetInt32());
            Equal(1, json.GetProperty("results").GetArrayLength());
            Equal(10, json.GetProperty("start").GetInt32());
            True(json.GetProperty("hasMore").GetBoolean());
        }
        );
        yield return ("Markdown table escapes pipes and newlines", async () =>
        {
            using var output = new StringWriter();
            await new OutputWriter(OutputFormat.Markdown, output).WriteAsync(Parse("{\"key\":\"A\",\"name\":\"a|b\\nc\"}"), OutputKind.Space, default);
            Contains("a\\|b<br>c", output.ToString());
        }
        );
        yield return ("Table output aligns Unicode columns", async () =>
        {
            using var output = new StringWriter();
            await new OutputWriter(OutputFormat.Table, output).WriteAsync(
                Parse("{\"results\":[{\"key\":\"A\",\"name\":\"🦄 A\",\"type\":\"global\",\"status\":\"current\"},{\"key\":\"LONGKEY\",\"name\":\"e\\u0301\",\"type\":\"personal\",\"status\":\"current\"}]}"),
                OutputKind.Space,
                default);

            Contains("┌─────────┬──────┬──────────┬─────────┐", output.ToString());
            Contains("│ A       │ 🦄 A │ global   │ current │", output.ToString());
            Contains("│ LONGKEY │ é    │ personal │ current │", output.ToString());
        }
        );
        yield return ("Jira project output shows project key, name, type and lead", async () =>
        {
            using var output = new StringWriter();
            await new OutputWriter(OutputFormat.Table, output).WriteAsync(
                Parse("{\"results\":[{\"key\":\"APP\",\"name\":\"App Platform\",\"projectTypeKey\":\"software\",\"lead\":{\"displayName\":\"Alex\"}}]}"),
                OutputKind.JiraProject,
                default);
            Contains("KEY", output.ToString());
            Contains("App Platform", output.ToString());
            Contains("software", output.ToString());
            Contains("Alex", output.ToString());
        }
        );
        yield return ("Jira sprint table and Markdown render dates and future sprints without dates", async () =>
        {
            foreach (OutputFormat format in new[] { OutputFormat.Table, OutputFormat.Markdown })
            {
                using var output = new StringWriter();
                var result = new ListResult(
                    [Parse("{\"id\":12,\"name\":\"Sprint Twelve\",\"state\":\"active\",\"startDate\":\"2026-11-01T09:00:00Z\",\"endDate\":\"2026-11-14T17:00:00Z\"}"),
                     Parse("{\"id\":13,\"name\":\"Sprint Thirteen\",\"state\":\"future\"}")],
                    0, 100, 2, 2, false);
                await new OutputWriter(format, output).WriteAsync(result, OutputKind.JiraSprint, default);
                Contains("ID", output.ToString());
                Contains("STATE", output.ToString());
                Contains("START", output.ToString());
                Contains("END", output.ToString());
                Contains("Sprint Twelve", output.ToString());
                Contains("active", output.ToString());
                Contains("01.11.26 09:00", output.ToString());
                Contains("14.11.26 17:00", output.ToString());
                Contains("Sprint Thirteen", output.ToString());
                Contains("future", output.ToString());
            }
        }
        );
        yield return ("Sprint dates preserve source offset and use compact invariant formatting", async () =>
        {
            foreach (string timestamp in new[]
            {
                "2026-11-01T00:30:45.123+0100",
                "2026-11-01T00:30:45+01:00",
                "2026-11-01T00:30:45.1234567-0530",
                "2026-11-01T00:30:45Z"
            })
            {
                foreach (OutputFormat format in new[] { OutputFormat.Table, OutputFormat.Markdown })
                {
                    using var output = new StringWriter();
                    await new OutputWriter(format, output).WriteAsync(
                        Parse($"{{\"id\":1,\"startDate\":\"{timestamp}\",\"endDate\":null}}"), OutputKind.JiraSprint, default);
                    Contains("01.11.26 00:30", output.ToString());
                    True(!output.ToString().Contains(timestamp, StringComparison.Ordinal));
                    if (format == OutputFormat.Markdown)
                    {
                        Contains("| 1 |  |  | 01.11.26 00:30 |  |", output.ToString());
                    }
                }
            }
        }
        );
        yield return ("Sprint invalid dates fail explicitly in human output but remain unchanged in JSON", async () =>
        {
            foreach (string value in new[] { "\"invalid\"", "\"2026-02-30T09:00:00Z\"", "\"\"", "42" })
            {
                JsonElement sprint = Parse($"{{\"id\":1,\"endDate\":{value}}}");
                foreach (OutputFormat format in new[] { OutputFormat.Table, OutputFormat.Markdown })
                {
                    using var output = new StringWriter();
                    await ThrowsAsync<InvalidDataException>(() => new OutputWriter(format, output).WriteAsync(sprint, OutputKind.JiraSprint, default));
                    Equal("", output.ToString());
                }

                using var jsonOutput = new StringWriter();
                await new OutputWriter(OutputFormat.Json, jsonOutput).WriteAsync(sprint, OutputKind.JiraSprint, default);
                Equal(value, Parse(jsonOutput.ToString()).GetProperty("endDate").GetRawText());
            }
        }
        );
        yield return ("Sprint JSON dates retain their original precision and offset", async () =>
        {
            using var output = new StringWriter();
            const string timestamp = "2026-11-01T09:00:45.123+0100";
            await new OutputWriter(OutputFormat.Json, output).WriteAsync(
                Parse($"{{\"id\":1,\"startDate\":\"{timestamp}\"}}"), OutputKind.JiraSprint, default);
            Equal(timestamp, JsonValues.Text(Parse(output.ToString()), "startDate"));
        }
        );
        yield return ("Storage headings, entities and nested lists", () =>
        {
            string markdown = StorageMarkdownConverter.Convert("<h2>A&nbsp;B</h2><ul><li>one<ul><li>two</li></ul></li></ul>");
            Contains("## A B", markdown);
            Contains("  - two", markdown);
            return Task.CompletedTask;
        }
        );
        yield return ("Confluence code macro preserves CDATA and nested fences", () =>
        {
            string markdown = StorageMarkdownConverter.Convert("<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">csharp</ac:parameter><ac:plain-text-body><![CDATA[a < b\n```]]></ac:plain-text-body></ac:structured-macro>");
            Contains("````csharp", markdown);
            Contains("a < b", markdown);
            return Task.CompletedTask;
        }
        );
        yield return ("Unsupported macro visibly preserves original XML", () =>
        {
            string markdown = StorageMarkdownConverter.Convert("<ac:structured-macro ac:name=\"custom\"><ac:parameter ac:name=\"key\">value</ac:parameter></ac:structured-macro>");
            Contains("Unsupported Confluence macro: custom", markdown);
            Contains("value", markdown);
            Contains("```xml", markdown);
            return Task.CompletedTask;
        }
        );
        yield return ("Storage malformed XML/DTD explicitly rejected", () =>
        {
            Throws<XmlException>(() => StorageMarkdownConverter.Convert("<p>"));
            Throws<XmlException>(() => StorageMarkdownConverter.Convert("<!DOCTYPE root [<!ENTITY secret SYSTEM \"file:///etc/passwd\">]><p>&secret;</p>"));
            return Task.CompletedTask;
        }
        );
        yield return ("Storage table escapes content and preserves merged cells", () =>
        {
            Contains("a\\|b", StorageMarkdownConverter.Convert("<table><tr><td>a|b</td></tr></table>"));
            Contains("```xml", StorageMarkdownConverter.Convert("<table><tr><td colspan=\"2\">merged</td></tr></table>"));
            return Task.CompletedTask;
        }
        );
        yield return ("Jira wiki not processed as HTML", () =>
        {
            string output = JiraWikiMarkdownConverter.Convert("h2. Heading\nx < y\n{code}sample{code}");
            Contains("## Heading", output);
            Contains("x \\< y", output);
            Contains("{code}sample{code}", output);
            return Task.CompletedTask;
        }
        );
        yield return ("Human output strips terminal controls", async () =>
        {
            using var output = new StringWriter();
            await new OutputWriter(OutputFormat.Table, output).WriteAsync(Parse("{\"name\":\"a\\u001bb\"}"), OutputKind.Space, default);
            True(!output.ToString().Contains('\u001b', StringComparison.Ordinal));
        }
        );
        yield return ("Writer failures propagate", async () =>
        {
            using var output = new FailingWriter();
            await ThrowsAsync<IOException>(() => new OutputWriter(OutputFormat.Markdown, output).MessageAsync("test", default));
        }
        );
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => Task.FromException(new IOException("broken pipe"));
    }
}
