using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlassianCli.Confluence;
using AtlassianCli.Http;
using AtlassianCli.Jira;
using AtlassianCli.Serialization;
using static AtlassianCli.Tests.TestSupport;

namespace AtlassianCli.Tests;

internal static class ClientTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Jira pagination uses returned count and exact limit", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                int max = int.Parse(Parameter(request.Uri, "maxResults"), System.Globalization.CultureInfo.InvariantCulture);
                string[] issues = Enumerable.Range(start, Math.Min(2, max)).Select(i => $"{{\"key\":\"TEST-{i}\"}}").ToArray();
                return Json($"{{\"startAt\":{start},\"total\":5,\"issues\":[{string.Join(",", issues)}]}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new JiraClient(http).SearchAsync("project = TEST", new PageRequest(0, 3), default);
            Equal(3, result.Size);
            Equal("TEST-2", JsonValues.Text(result.Results[2], "key"));
            True(result.HasMore);
            Equal(2, handler.Requests.Count);
        }
        );
        yield return ("Jira comments retrieve all pages", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                return Json($"{{\"startAt\":{start},\"total\":3,\"comments\":[{{\"id\":\"{start}\"}}]}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new JiraClient(http).CommentsAsync("TEST-1", new PageRequest(All: true), default);
            Equal(3, result.Size);
            True(!result.HasMore);
        }
        );
        yield return ("Jira inconsistent pagination fails explicitly", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{\"startAt\":0,\"total\":10,\"issues\":[]}")));
            await ThrowsAsync<InvalidDataException>(() => new JiraClient(http).SearchAsync("x", new PageRequest(All: true), default));
        }
        );
        yield return ("Confluence pagination follows context-aware links", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                int start = int.Parse(Parameter(request.Uri, "start"), System.Globalization.CultureInfo.InvariantCulture);
                return start == 0
                    ? Json("{\"start\":0,\"results\":[{\"key\":\"A\",\"name\":\"Space A\"}],\"_links\":{\"next\":\"/confluence/rest/api/space?start=1&limit=100\"}}")
                    : Json("{\"start\":1,\"results\":[{\"key\":\"B\",\"name\":\"Space B\"}],\"_links\":{}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new ConfluenceClient(http).SpacesAsync(new PageRequest(All: true), default);
            Equal(2, result.Size);
            Equal("Space B", JsonValues.Text(result.Results[1], "name"));
            True(!result.HasMore);
        }
        );
        yield return ("Confluence results never exceed requested limit", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{\"start\":0,\"results\":[{\"id\":\"1\"},{\"id\":\"2\"}],\"_links\":{}}")));
            ListResult result = await new ConfluenceClient(http).LabelsAsync("1", new PageRequest(Limit: 1), default);
            Equal(1, result.Size);
            True(result.HasMore);
        }
        );
        yield return ("Confluence malicious/cyclic continuation fails", async () =>
        {
            foreach (string link in new[] { "https://evil.test/confluence/rest/api/space", "/confluence/rest/api/space?start=0&limit=100" })
            {
                using AtlassianHttpClient http = Client(new FakeHandler(_ => Json($"{{\"start\":0,\"results\":[{{}}],\"_links\":{{\"next\":\"{link}\"}}}}")));
                await ThrowsAsync<InvalidDataException>(() => new ConfluenceClient(http).SpacesAsync(new PageRequest(All: true), default));
            }
        }
        );
        yield return ("Content list type/body and CQL request contracts", async () =>
        {
            var handler = new FakeHandler(_ => Json("{\"start\":0,\"results\":[],\"_links\":{}}"));
            using AtlassianHttpClient http = Client(handler);
            var client = new ConfluenceClient(http);
            await client.PagesAsync("DOCS", "blogpost", true, new PageRequest(), default);
            Equal("DOCS", Parameter(handler.Requests[0].Uri, "spaceKey"));
            Equal("blogpost", Parameter(handler.Requests[0].Uri, "type"));
            Contains("body.storage", Parameter(handler.Requests[0].Uri, "expand"));
            await client.SearchAsync("space = DOCS AND type = page", false, new PageRequest(), default);
            Equal("space = DOCS AND type = page", Parameter(handler.Requests[1].Uri, "cql"));
        }
        );
        yield return ("Comments include reply depth, inline/resolution and parent metadata", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("all", Parameter(request.Uri, "depth"));
                Contains("location=footer&location=inline&location=resolved", request.Uri.Query);
                Contains("extensions.resolution", Parameter(request.Uri, "expand"));
                Contains("ancestors", Parameter(request.Uri, "expand"));
                return Json("{\"start\":0,\"results\":[{\"id\":\"7\",\"type\":\"comment\",\"container\":{\"id\":\"3\"},\"extensions\":{\"inlineProperties\":{\"originalSelection\":\"text\"},\"resolution\":{\"status\":\"open\"}}}],\"_links\":{}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new ConfluenceClient(http).CommentsAsync("3", "all", true, new PageRequest(All: true), default);
            Equal("text", JsonValues.Text(result.Results[0], "extensions", "inlineProperties", "originalSelection"));
            Equal("3", JsonValues.Text(result.Results[0], "container", "id"));
        }
        );
        yield return ("Restriction subjects paginated independently", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                string operation = request.Uri.AbsolutePath.Split('/')[^1];
                string subject = Parameter(request.Uri, "expand").Split('.')[^1];
                int start = int.Parse(Parameter(request.Uri, "start"), System.Globalization.CultureInfo.InvariantCulture);
                string items = start == 0 ? "{\"username\":\"alice\"}" : "";
                return Json($"{{\"operation\":\"{operation}\",\"restrictions\":{{\"{subject}\":{{\"results\":[{items}],\"start\":{start},\"limit\":1}}}}}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new ConfluenceClient(http).RestrictionsAsync("3", default);
            Equal(2, result.Size);
            Equal(8, handler.Requests.Count);
            Equal(1, result.Results[0].GetProperty("restrictions").GetProperty("user").GetProperty("results").GetArrayLength());
            True(handler.Requests.All(request => request.Method == HttpMethod.Get));
        }
        );
        yield return ("Confluence version increment and original storage payload", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Json("{\"version\":{\"number\":3}}");
                }

                JsonElement body = Parse(request.Body);
                Equal(4, body.GetProperty("version").GetProperty("number").GetInt32());
                Equal("<p>new</p>", JsonValues.Text(body, "body", "storage", "value"));
                return Json("{\"id\":\"3\",\"version\":{\"number\":4}}");
            });
            using AtlassianHttpClient http = Client(handler);
            await new ConfluenceClient(http).UpdatePageAsync("3", "New", "<p>new</p>", null, "change", default);
            Equal(2, handler.Requests.Count);
        }
        );
        yield return ("Version conflict is not retried or overwritten", async () =>
        {
            var handler = new FakeHandler(_ => Json("{\"message\":\"version conflict\"}", HttpStatusCode.Conflict));
            using AtlassianHttpClient http = Client(handler);
            await ThrowsAsync<AtlassianApiException>(() => new ConfluenceClient(http).UpdatePageAsync("3", "New", "<p/>", 4, null, default));
            Equal(1, handler.Requests.Count);
        }
        );
        yield return ("Jira ambiguous transition rejected before POST", async () =>
        {
            var handler = new FakeHandler(_ => Json("{\"transitions\":[{\"id\":\"1\",\"name\":\"Done\"},{\"id\":\"2\",\"name\":\"Close\",\"to\":{\"name\":\"Done\"}}]}"));
            using AtlassianHttpClient http = Client(handler);
            await ThrowsAsync<ArgumentException>(() => new JiraClient(http).TransitionAsync("TEST-1", "Done", default));
            Equal(1, handler.Requests.Count);
        }
        );
        yield return ("Field arrays/objects retain commas and JSON types", () =>
        {
            JsonObject fields = JsonValues.Fields(["labels=[\"a\",\"b\"]", "priority={\"name\":\"High\"}", "estimate=3.5", "assignee=null"], null);
            Equal(2, fields["labels"]!.AsArray().Count);
            Equal("High", fields["priority"]!["name"]!.GetValue<string>());
            Equal(3.5, fields["estimate"]!.GetValue<double>());
            True(fields["assignee"] is null);
            Throws<JsonException>(() => JsonValues.Fields(["labels=[invalid"], null));
            Throws<ArgumentException>(() => JsonValues.Fields(["summary=\"a\"", "summary=\"b\""], null));
            return Task.CompletedTask;
        }
        );
        yield return ("Page bounds invalid inputs rejected", () =>
        {
            foreach (PageRequest request in new[] { new PageRequest(-1), new PageRequest(Limit: 0), new PageRequest(Limit: 10001) })
            {
                Throws<ArgumentException>(request.Validate);
            }

            return Task.CompletedTask;
        }
        );
        yield return ("Transition by ID sends correct POST payload", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Json("{\"transitions\":[{\"id\":\"11\",\"name\":\"Start\"}]}");
                }

                Equal(HttpMethod.Post, request.Method);
                Equal("11", JsonValues.Text(Parse(request.Body), "transition", "id"));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            using AtlassianHttpClient http = Client(handler);
            await new JiraClient(http).TransitionAsync("TEST-1", "11", default);
            Equal(2, handler.Requests.Count);
        }
        );
        yield return ("Accepted creation without identity fails explicitly", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{}")));
            await ThrowsAsync<InvalidDataException>(() => new JiraClient(http).CreateAsync(new JsonObject(), default));
            await ThrowsAsync<InvalidDataException>(() => new ConfluenceClient(http).CreatePageAsync("DOCS", "Test", "<p/>", null, default));
        }
        );
        yield return ("Missing pagination metadata rejected", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{\"start\":0,\"results\":[]}")));
            await ThrowsAsync<InvalidDataException>(() => new ConfluenceClient(http).SpacesAsync(new PageRequest(), default));
        }
        );
        yield return ("Dot-segment identifiers rejected before HTTP", async () =>
        {
            var handler = new FakeHandler(_ => Json("{}"));
            using AtlassianHttpClient http = Client(handler);
            await ThrowsAsync<ArgumentException>(() => new JiraClient(http).GetIssueAsync("..", default));
            await ThrowsAsync<ArgumentException>(() => new ConfluenceClient(http).PageAsync(".", default));
            Equal(0, handler.Requests.Count);
        }
        );
    }
}
