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
