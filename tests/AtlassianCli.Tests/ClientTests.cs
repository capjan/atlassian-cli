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
        yield return ("Sprint status inclusion and exclusion build safe JQL on every page", async () =>
        {
            foreach ((string[] statuses, string jql) in new (string[], string)[]
            {
                (["+Open", "+In Progress", "+open"], "status IN (\"Open\", \"In Progress\")"),
                (["Open", "In Progress"], "status IN (\"Open\", \"In Progress\")"),
                (["-Done,+Ready,+ReadyForTest"], "status IN (\"Ready\", \"ReadyForTest\") AND status NOT IN (\"Done\")"),
                ([" -Done , Ready , +ReadyForTest ", "ready"], "status IN (\"Ready\", \"ReadyForTest\") AND status NOT IN (\"Done\")"),
                (["-Done", "-Cancelled"], "status NOT IN (\"Done\", \"Cancelled\")"),
                (["+ In Progress ", "-Done"], "status IN (\"In Progress\") AND status NOT IN (\"Done\")"),
                (["+A\" OR status != \"B", "-C\\D", "+A,B"], "status IN (\"A\\\" OR status != \\\"B\", \"A\", \"B\") AND status NOT IN (\"C\\\\D\")")
            })
            {
                var handler = new FakeHandler(request =>
                {
                    Equal(jql, Parameter(request.Uri, "jql"));
                    Equal("*all", Parameter(request.Uri, "fields"));
                    int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                    return Json($"{{\"startAt\":{start},\"total\":2,\"issues\":[{{\"key\":\"DRB-{start}\"}}]}}");
                });
                using AtlassianHttpClient http = Client(handler);
                ListResult result = await new JiraClient(http).SprintIssuesAsync(42, new PageRequest(All: true), default, true, statuses);
                Equal(2, result.Size);
                Equal(2L, result.Total);
                True(!result.HasMore);
                Equal(2, handler.Requests.Count);
            }
        }
        );
        yield return ("Sprint status malformed and conflicting filters fail before HTTP", async () =>
        {
            var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP."));
            using AtlassianHttpClient http = Client(handler);
            foreach (string[] statuses in new[]
            {
                new[] { "" }, ["+"], ["-  "], ["+Open\n"], ["+Open", "-open"],
                ["+ Open ", "-OPEN"], ["Ready,"], [",Ready"], ["Ready,,Done"], ["Ready,-ready"]
            })
            {
                await ThrowsAsync<ArgumentException>(() => new JiraClient(http).SprintIssuesAsync(42, new PageRequest(), default, statuses: statuses));
            }

            Equal(0, handler.Requests.Count);
        }
        );
        yield return ("Sprint issues preserve original fields, server order and pagination limits", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/confluence/rest/agile/1.0/sprint/42/issue", request.Uri.AbsolutePath);
                Equal("", Parameter(request.Uri, "jql"));
                Equal("summary,status,assignee", Parameter(request.Uri, "fields"));
                int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                int requested = int.Parse(Parameter(request.Uri, "maxResults"), System.Globalization.CultureInfo.InvariantCulture);
                int count = Math.Min(2, Math.Min(requested, Math.Max(0, 5 - start)));
                string issues = string.Join(",", Enumerable.Range(start, count)
                    .Select(id => $"{{\"key\":\"DRB-{5 - id}\",\"fields\":{{\"summary\":\"Ticket\",\"custom\":true}}}}"));
                return Json($"{{\"startAt\":{start},\"total\":5,\"issues\":[{issues}]}}");
            });
            using AtlassianHttpClient http = Client(handler);
            var client = new JiraClient(http);
            ListResult result = await client.SprintIssuesAsync(42, new PageRequest(Start: 1, Limit: 3), default);
            Equal(1, result.Start);
            Equal(3, result.Size);
            Equal(5L, result.Total);
            True(result.HasMore);
            Equal("DRB-4", JsonValues.Text(result.Results[0], "key"));
            Equal("DRB-2", JsonValues.Text(result.Results[2], "key"));
            True(result.Results[0].GetProperty("fields").GetProperty("custom").GetBoolean());
            Equal(2, handler.Requests.Count);
            Equal("3", Parameter(handler.Requests[0].Uri, "maxResults"));
            Equal("1", Parameter(handler.Requests[1].Uri, "maxResults"));
            result = await client.SprintIssuesAsync(42, new PageRequest(All: true), default);
            Equal(5, result.Size);
            True(!result.HasMore);
            Equal("DRB-1", JsonValues.Text(result.Results[4], "key"));
        }
        );
        yield return ("Sprint issues handle empty lists and reject invalid pagination responses", async () =>
        {
            using AtlassianHttpClient empty = Client(new FakeHandler(_ => Json("{\"startAt\":0,\"total\":0,\"issues\":[]}")));
            ListResult result = await new JiraClient(empty).SprintIssuesAsync(42, new PageRequest(), default);
            Equal(0, result.Size);
            Equal(0L, result.Total);
            True(!result.HasMore);
            foreach (string response in new[]
            {
                "{\"startAt\":0,\"total\":5,\"issues\":[]}",
                "{\"startAt\":1,\"total\":1,\"issues\":[{}]}",
                "{\"startAt\":0,\"issues\":[]}"
            })
            {
                using AtlassianHttpClient http = Client(new FakeHandler(_ => Json(response)));
                await ThrowsAsync<InvalidDataException>(() => new JiraClient(http).SprintIssuesAsync(42, new PageRequest(), default));
            }
        }
        );
        yield return ("Sprint issues request all fields on every page only with include-body", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("*all", Parameter(request.Uri, "fields"));
                int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                return Json($"{{\"startAt\":{start},\"total\":2,\"issues\":[{{\"key\":\"DRB-{start}\",\"fields\":{{\"description\":\"Details\",\"custom\":true}}}}]}}");
            });
            using AtlassianHttpClient http = Client(handler);
            ListResult result = await new JiraClient(http).SprintIssuesAsync(42, new PageRequest(All: true), default, includeBody: true);
            Equal(2, handler.Requests.Count);
            Equal("Details", JsonValues.Text(result.Results[1], "fields", "description"));
            True(result.Results[1].GetProperty("fields").GetProperty("custom").GetBoolean());
        }
        );
        yield return ("Sprint issues reject invalid IDs before HTTP", async () =>
        {
            var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP."));
            using AtlassianHttpClient http = Client(handler);
            foreach (int id in new[] { 0, -1 })
            {
                await ThrowsAsync<ArgumentException>(() => new JiraClient(http).SprintIssuesAsync(id, new PageRequest(), default));
            }

            Equal(0, handler.Requests.Count);
        }
        );
        yield return ("Jira sprints paginate all boards, deduplicate and order by descending numeric ID", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("100", Parameter(request.Uri, "maxResults"));
                int start = int.Parse(Parameter(request.Uri, "startAt"), System.Globalization.CultureInfo.InvariantCulture);
                if (request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal))
                {
                    Equal("DRB", Parameter(request.Uri, "projectKeyOrId"));
                    Equal("scrum", Parameter(request.Uri, "type"));
                    return start == 0
                        ? Json("{\"startAt\":0,\"isLast\":false,\"values\":[{\"id\":1}]}")
                        : Json("{\"startAt\":1,\"isLast\":true,\"values\":[{\"id\":2},{\"id\":1}]}");
                }

                Equal("", Parameter(request.Uri, "state"));
                if (request.Uri.AbsolutePath.EndsWith("/board/1/sprint", StringComparison.Ordinal))
                {
                    return start == 0
                        ? Json("{\"startAt\":0,\"isLast\":false,\"values\":[{\"id\":10,\"name\":\"Ten\",\"goal\":\"kept\"}]}")
                        : Json("{\"startAt\":1,\"isLast\":true,\"values\":[{\"id\":2,\"name\":\"Two\"}]}");
                }

                Equal("/confluence/rest/agile/1.0/board/2/sprint", request.Uri.AbsolutePath);
                Equal(0, start);
                return Json("{\"startAt\":0,\"total\":2,\"values\":[{\"id\":10,\"name\":\"Ten\"},{\"id\":3,\"name\":\"Three\"}]}");
            });
            using AtlassianHttpClient http = Client(handler);
            var client = new JiraClient(http);
            ListResult result = await client.SprintsAsync("DRB", null, new PageRequest(All: true), default);
            Equal(3, result.Size);
            Equal(3L, result.Total);
            Equal("10", JsonValues.Text(result.Results[0], "id"));
            Equal("3", JsonValues.Text(result.Results[1], "id"));
            Equal("2", JsonValues.Text(result.Results[2], "id"));
            Equal("kept", JsonValues.Text(result.Results[0], "goal"));
            True(!result.HasMore);
            Equal(5, handler.Requests.Count);

            result = await client.SprintsAsync("DRB", null, new PageRequest(Limit: 1), default);
            Equal(1, result.Size);
            Equal("10", JsonValues.Text(result.Results[0], "id"));
            True(result.HasMore);

            result = await client.SprintsAsync("DRB", null, new PageRequest(Start: 1, Limit: 1), default);
            Equal(1, result.Start);
            Equal(1, result.Limit);
            Equal(1, result.Size);
            Equal("3", JsonValues.Text(result.Results[0], "id"));
            Equal(3L, result.Total);
            True(result.HasMore);

            result = await client.SprintsAsync("DRB", null, new PageRequest(Start: 1, Limit: 1, All: true), default);
            Equal(2, result.Size);
            Equal("3", JsonValues.Text(result.Results[0], "id"));
            Equal("2", JsonValues.Text(result.Results[1], "id"));
            True(!result.HasMore);
            result = await client.SprintsAsync("DRB", null, new PageRequest(Start: 4), default);
            Equal(0, result.Size);
            Equal(3L, result.Total);
            True(!result.HasMore);
        }
        );
        yield return ("Jira sprint state filters are forwarded on every sprint page", async () =>
        {
            foreach (string state in new[] { "active", "future", "closed", "active,future" })
            {
                var handler = new FakeHandler(request =>
                {
                    if (request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal))
                    {
                        return Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":1}]}");
                    }

                    Equal(state, Parameter(request.Uri, "state"));
                    return Parameter(request.Uri, "startAt") == "0"
                        ? Json("{\"startAt\":0,\"isLast\":false,\"values\":[{\"id\":1}]}")
                        : Json("{\"startAt\":1,\"isLast\":true,\"values\":[{\"id\":2}]}");
                });
                using AtlassianHttpClient http = Client(handler);
                ListResult result = await new JiraClient(http).SprintsAsync("DRB", state, new PageRequest(), default);
                Equal(2, result.Size);
                Equal(3, handler.Requests.Count);
            }
        }
        );
        yield return ("Jira sprint empty boards and empty sprint lists return an explicit empty list", async () =>
        {
            foreach (bool hasBoard in new[] { false, true })
            {
                var handler = new FakeHandler(request => hasBoard && request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal)
                    ? Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":1}]}")
                    : Json("{\"startAt\":0,\"isLast\":true,\"values\":[]}"));
                using AtlassianHttpClient http = Client(handler);
                ListResult result = await new JiraClient(http).SprintsAsync("DRB", null, new PageRequest(), default);
                Equal(0, result.Size);
                Equal(0L, result.Total);
                True(!result.HasMore);
                Equal(hasBoard ? 2 : 1, handler.Requests.Count);
            }
        }
        );
        yield return ("Jira Agile rejects malformed pagination and identities on boards and sprints", async () =>
        {
            foreach (string response in new[]
            {
                "{\"startAt\":0,\"values\":[]}",
                "{\"startAt\":0,\"isLast\":\"true\",\"values\":[]}",
                "{\"startAt\":0,\"isLast\":false,\"values\":[]}",
                "{\"startAt\":1,\"isLast\":true,\"values\":[{\"id\":1}]}",
                "{\"startAt\":0,\"isLast\":true,\"total\":2,\"values\":[{\"id\":1}]}",
                "{\"startAt\":0,\"isLast\":false,\"total\":1,\"values\":[{\"id\":1}]}",
                "{\"startAt\":0,\"isLast\":true,\"total\":-1,\"values\":[]}",
                "{\"startAt\":0,\"isLast\":true,\"values\":[{}]}",
                "{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":0}]}",
                "{\"startAt\":0,\"isLast\":true,\"values\":[null]}"
            })
            {
                foreach (bool failOnBoard in new[] { false, true })
                {
                    var handler = new FakeHandler(request =>
                        !failOnBoard && request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal)
                            ? Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":1}]}")
                            : Json(response));
                    using AtlassianHttpClient http = Client(handler);
                    await ThrowsAsync<InvalidDataException>(() => new JiraClient(http).SprintsAsync("DRB", null, new PageRequest(), default));
                }
            }
        }
        );
        yield return ("Jira Agile rejects repeating offsets and oversized pages", async () =>
        {
            foreach (bool repeatOffset in new[] { false, true })
            {
                string values = string.Join(",", Enumerable.Range(1, 101).Select(id => $"{{\"id\":{id}}}"));
                var handler = new FakeHandler(_ => repeatOffset
                    ? Json("{\"startAt\":0,\"isLast\":false,\"values\":[{\"id\":1}]}")
                    : Json($"{{\"startAt\":0,\"isLast\":true,\"values\":[{values}]}}"));
                using AtlassianHttpClient http = Client(handler);
                await ThrowsAsync<InvalidDataException>(() => new JiraClient(http).SprintsAsync("DRB", null, new PageRequest(), default));
                Equal(repeatOffset ? 2 : 1, handler.Requests.Count);
            }
        }
        );
        yield return ("Jira sprint API failures do not return partial results", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                if (request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal))
                {
                    return Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":1},{\"id\":2}]}");
                }

                return request.Uri.AbsolutePath.EndsWith("/board/1/sprint", StringComparison.Ordinal)
                    ? Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":10}]}")
                    : Json("{\"message\":\"Forbidden\"}", HttpStatusCode.Forbidden);
            });
            using AtlassianHttpClient http = Client(handler);
            await ThrowsAsync<AtlassianApiException>(() => new JiraClient(http).SprintsAsync("DRB", null, new PageRequest(Limit: 1), default));
            Equal(3, handler.Requests.Count);
        }
        );
        yield return ("Jira project list uses array endpoint and applies pagination locally", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("/confluence/rest/api/2/project", request.Uri.AbsolutePath);
                True(string.IsNullOrEmpty(request.Uri.Query));
                return Json("[{\"key\":\"ONE\",\"name\":\"One\"},{\"key\":\"TWO\",\"name\":\"Two\"},{\"key\":\"THREE\",\"name\":\"Three\"}]");
            });
            using AtlassianHttpClient http = Client(handler);
            var client = new JiraClient(http);
            ListResult result = await client.ProjectsAsync(new PageRequest(Start: 1, Limit: 1), default);
            Equal(1, result.Size);
            Equal("TWO", JsonValues.Text(result.Results[0], "key"));
            Equal(3, result.Total);
            True(result.HasMore);
            result = await client.ProjectsAsync(new PageRequest(Start: 1, All: true), default);
            Equal(2, result.Size);
            Equal("THREE", JsonValues.Text(result.Results[1], "key"));
            True(!result.HasMore);
            Equal(2, handler.Requests.Count);
        }
        );
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
