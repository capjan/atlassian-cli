using System.Net;
using System.Text.Json;
using AtlassianCli.Cli;
using AtlassianCli.Http;
using AtlassianCli.Output;
using AtlassianCli.Serialization;
using static AtlassianCli.Tests.TestSupport;

namespace AtlassianCli.Tests;

internal static class CliTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("CLI help/version work without credentials", async () =>
        {
            using var output = new StringWriter();
            var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => throw new InvalidOperationException("Unexpected network client."));
            Equal(0, await app.RunAsync(["--help"]));
            Contains("confluence", output.ToString());
            output.GetStringBuilder().Clear();
            Equal(0, await app.RunAsync(["jira", "project", "--help"]));
            Contains("list", output.ToString());
            output.GetStringBuilder().Clear();
            Equal(0, await app.RunAsync(["jira", "sprint", "list", "--help"]));
            Contains("--project", output.ToString());
            Contains("--state", output.ToString());
            Contains("--all", output.ToString());
            output.GetStringBuilder().Clear();
            Equal(0, await app.RunAsync(["jira", "sprint", "issues", "--help"]));
            Contains("--sprint", output.ToString());
            Contains("--include-body", output.ToString());
            Contains("--status", output.ToString());
            Contains("--all", output.ToString());
            output.GetStringBuilder().Clear();
            Equal(0, await app.RunAsync(["version", "-o", "json"]));
            Equal("0.1.0", Parse(output.ToString()).GetProperty("version").GetString());
        }
        );
        yield return ("CLI invalid format/options/missing arguments rejected", async () =>
        {
            foreach (string[] args in new[] { new[] { "jira", "issue", "get" }, ["version", "-o", "csv"], ["version", "-o", "99"], ["version", "--timeout", "0"], ["conf", "restriction", "add", "1"], ["jira", "issue", "get", "TEST-1", "--unknown"], ["jira", "issue", "update", "TEST-1", "--field", "--allow-write"] })
            {
                using var output = new StringWriter();
                var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => throw new InvalidOperationException("Unexpected network."));
                Equal(2, await app.RunAsync(args));
            }
        }
        );
        foreach (string[] args in MutationCommands())
        {
            yield return ("Write gate: " + string.Join(" ", args.Take(3)), async () =>
            {
                var handler = new FakeHandler(_ => Json("{}"));
                using var errors = new StringWriter();
                var app = new CliApplication(new StringWriter(), errors, clientFactory: (_, _) => Client(handler));
                Equal(2, await app.RunAsync(args));
                Equal(0, handler.Requests.Count);
                Contains("--allow-write", errors.ToString());
            }
            );
        }
        yield return ("Delete additionally requires --yes", async () =>
        {
            var handler = new FakeHandler(_ => Json("{}"));
            var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(2, await app.RunAsync(["jira", "issue", "delete", "TEST-1", "--allow-write"]));
            Equal(0, handler.Requests.Count);
        }
        );
        foreach ((string[] args, string path, HttpMethod method) in Contracts())
        {
            yield return ("CLI contract: " + string.Join(" ", args.Take(3)), async () =>
            {
                var handler = new FakeHandler(request =>
                {
                    Equal("/confluence" + path, request.Uri.AbsolutePath);
                    Equal(method, request.Method);
                    if (request.Method != HttpMethod.Get && args.Contains("update", StringComparer.Ordinal) && args[0] == "jira")
                    {
                        Equal(2, Parse(request.Body).GetProperty("fields").GetProperty("labels").GetArrayLength());
                    }

                    if (args.Contains("--assignee", StringComparer.Ordinal))
                    {
                        Equal("alice", JsonValues.Text(Parse(request.Body), "fields", "assignee", "name"));
                        True(!request.Body.Contains("accountId", StringComparison.Ordinal));
                    }

                    return Response(request);
                });
                using var output = new StringWriter();
                using var errors = new StringWriter();
                var app = new CliApplication(output, errors, clientFactory: (_, _) => Client(handler));
                int code = await app.RunAsync([.. args, "--output", "json"]);
                Equal(0, code);
                True(handler.Requests.Count > 0);
                True(Parse(output.ToString()).ValueKind == JsonValueKind.Object);
            }
            );
        }
        yield return ("Confluence body input conflicts fail before network", async () =>
        {
            var handler = new FakeHandler(_ => Json("{}"));
            var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(2, await app.RunAsync(["conf", "page", "create", "--space", "DOCS", "--title", "Test", "--body", "<p/>", "--body-stdin", "--allow-write"]));
            Equal(0, handler.Requests.Count);
        }
        );
        yield return ("Confluence stdin body supported", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("<p>stdin</p>", JsonValues.Text(Parse(request.Body), "body", "storage", "value"));
                return Json("{\"id\":\"1\",\"type\":\"page\"}");
            });
            var app = new CliApplication(new StringWriter(), new StringWriter(), new StringReader("<p>stdin</p>"), (_, _) => Client(handler));
            Equal(0, await app.RunAsync(["conf", "page", "create", "--space", "DOCS", "--title", "Test", "--body-stdin", "--allow-write"]));
        }
        );
        foreach ((HttpStatusCode status, int expected) in new[] { (HttpStatusCode.Unauthorized, 3), (HttpStatusCode.Forbidden, 3), (HttpStatusCode.NotFound, 5), (HttpStatusCode.Conflict, 4), (HttpStatusCode.TooManyRequests, 5) })
        {
            yield return ($"CLI exit code for HTTP {(int)status}", async () =>
            {
                var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(new FakeHandler(_ => Json("{}", status))));
                Equal(expected, await app.RunAsync(["jira", "issue", "get", "TEST-1"]));
            }
            );
        }
        yield return ("Browser url-only requires no external handler", async () =>
        {
            var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected request."));
            using var output = new StringWriter();
            var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(0, await app.RunAsync(["jira", "issue", "open", "TEST-1", "--url-only", "-o", "json"]));
            Equal("https://example.test/confluence/browse/TEST-1", Parse(output.ToString()).GetProperty("message").GetString());
            Equal(0, handler.Requests.Count);
        }
        );
        yield return ("CLI transition GET/POST and confirmation", async () =>
        {
            var handler = new FakeHandler(request => request.Method == HttpMethod.Get
                ? Json("{\"transitions\":[{\"id\":\"11\",\"name\":\"Start\"}]}")
                : new HttpResponseMessage(HttpStatusCode.NoContent));
            var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(0, await app.RunAsync(["jira", "issue", "transition", "TEST-1", "11", "--allow-write"]));
            Equal(2, handler.Requests.Count);
            Equal(HttpMethod.Post, handler.Requests[1].Method);
        }
        );
        yield return ("CLI restriction list has only GET requests", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                string operation = request.Uri.AbsolutePath.Split('/')[^1];
                string subject = Parameter(request.Uri, "expand").Split('.')[^1];
                return Json($"{{\"operation\":\"{operation}\",\"restrictions\":{{\"{subject}\":{{\"results\":[],\"start\":0,\"limit\":100}}}}}}");
            });
            using var output = new StringWriter();
            var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(0, await app.RunAsync(["conf", "restriction", "list", "1", "-o", "json"]));
            Equal(2, Parse(output.ToString()).GetProperty("results").GetArrayLength());
            True(handler.Requests.All(request => request.Method == HttpMethod.Get));
        }
        );
        yield return ("CLI attachment download and UTF-8 body/field files", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "atlassian-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string bodyFile = Path.Combine(directory, "body.xml");
            string fieldsFile = Path.Combine(directory, "fields.json");
            string destination = Path.Combine(directory, "download.txt");
            try
            {
                await File.WriteAllTextAsync(bodyFile, "<p>UTF-8: \u00e4</p>");
                await File.WriteAllTextAsync(fieldsFile, "{\"labels\":[\"a\",\"b\"]}");
                var handler = new FakeHandler(request =>
                {
                    if (request.Uri.AbsolutePath.Contains("/download/", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("file") };
                    }

                    if (request.Method == HttpMethod.Post)
                    {
                        Equal("<p>UTF-8: \u00e4</p>", JsonValues.Text(Parse(request.Body), "body", "storage", "value"));
                        Equal("99", JsonValues.Text(Parse(request.Body).GetProperty("ancestors")[0], "id"));
                        return Json("{\"id\":\"1\"}");
                    }

                    if (request.Method == HttpMethod.Put)
                    {
                        Equal(2, Parse(request.Body).GetProperty("fields").GetProperty("labels").GetArrayLength());
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    }

                    return Json("{\"id\":\"5\",\"type\":\"attachment\",\"_links\":{\"download\":\"/confluence/download/file\"}}");
                });
                var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
                Equal(0, await app.RunAsync(["conf", "page", "create", "--space", "DOCS", "--title", "Title", "--parent", "99", "--body-file", bodyFile, "--allow-write"]));
                Equal(0, await app.RunAsync(["jira", "issue", "update", "TEST-1", "--fields-file", fieldsFile, "--allow-write"]));
                Equal(0, await app.RunAsync(["conf", "attachment", "download", "5", "--file", destination]));
                Equal("file", await File.ReadAllTextAsync(destination));
            }
            finally
            {
                File.Delete(bodyFile);
                File.Delete(fieldsFile);
                File.Delete(destination);
                Directory.Delete(directory);
            }
        }
        );
        yield return ("CLI invalid content/list limits rejected before HTTP", async () =>
        {
            foreach (string[] args in new[]
            {
                new[] { "conf", "page", "create", "--space", "DOCS", "--title", "Title", "--body", "<p>", "--allow-write" },
                ["conf", "page", "list", "--space", "DOCS", "--type", "invalid"],
                ["conf", "comment", "list", "1", "--location", "invalid"],
                ["conf", "space", "list", "--limit", "0"]
            })
            {
                var handler = new FakeHandler(_ => Json("{}"));
                var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
                int expected = args.Contains("<p>", StringComparer.Ordinal) ? 6 : 2;
                Equal(expected, await app.RunAsync(args));
                Equal(0, handler.Requests.Count);
            }
        }
        );
        yield return ("CLI sprint list validates project, state and pagination before HTTP", async () =>
        {
            foreach (string[] args in new[]
            {
                new[] { "jira", "sprint", "list" },
                ["jira", "sprint", "list", "--project", ""],
                ["jira", "sprint", "list", "--project", "DRB\n"],
                ["jira", "sprint", "list", "--project", "DRB", "--state", ""],
                ["jira", "sprint", "list", "--project", "DRB", "--state", "active,invalid"],
                ["jira", "sprint", "list", "--project", "DRB", "--state", "active,"],
                ["jira", "sprint", "list", "--project", "DRB", "--limit", "0"],
                ["jira", "sprint", "list", "--project", "DRB", "--start", "-1"]
            })
            {
                var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP request."));
                using var errors = new StringWriter();
                var app = new CliApplication(new StringWriter(), errors, clientFactory: (_, _) => Client(handler));
                Equal(2, await app.RunAsync(args));
                Equal(0, handler.Requests.Count);
                True(errors.ToString().Length > 0);
            }
        }
        );
        yield return ("CLI sprint issues validates required ID and pagination before HTTP", async () =>
        {
            foreach (string[] args in new[]
            {
                new[] { "jira", "sprint", "issues" },
                ["jira", "sprint", "issues", "--sprint", "0"],
                ["jira", "sprint", "issues", "--sprint", "-1"],
                ["jira", "sprint", "issues", "--sprint", "DRB"],
                ["jira", "sprint", "issues", "--sprint", "42", "--limit", "0"],
                ["jira", "sprint", "issues", "--sprint", "42", "--start", "-1"]
            })
            {
                var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP."));
                using var errors = new StringWriter();
                var app = new CliApplication(new StringWriter(), errors, clientFactory: (_, _) => Client(handler));
                Equal(2, await app.RunAsync(args));
                Equal(0, handler.Requests.Count);
                True(errors.ToString().Length > 0);
            }
        }
        );
        yield return ("CLI sprint issues wires ID, pagination and existing issue output formats", async () =>
        {
            foreach (OutputFormat format in Enum.GetValues<OutputFormat>())
            {
                var handler = new FakeHandler(request =>
                {
                    Equal(HttpMethod.Get, request.Method);
                    Equal("/confluence/rest/agile/1.0/sprint/42/issue", request.Uri.AbsolutePath);
                    Equal("1", Parameter(request.Uri, "startAt"));
                    Equal("100", Parameter(request.Uri, "maxResults"));
                    return Json("{\"startAt\":1,\"total\":2,\"issues\":[{\"key\":\"DRB-7\",\"fields\":{\"summary\":\"Sprint ticket\",\"status\":{\"name\":\"Open\"},\"assignee\":{\"displayName\":\"Alex\"},\"custom\":true}}]}");
                });
                using var output = new StringWriter();
                var app = new CliApplication(output, new StringWriter(), clientFactory: (service, _) =>
                {
                    Equal("JIRA", service);
                    return Client(handler);
                });
                Equal(0, await app.RunAsync(["jira", "sprint", "issues", "--sprint", "42", "--start", "1", "--limit", "1", "--all", "-o", format.ToString()]));
                Contains("DRB-7", output.ToString());
                Contains("Sprint ticket", output.ToString());
                Contains("Open", output.ToString());
                Contains("Alex", output.ToString());
                if (format == OutputFormat.Json)
                {
                    JsonElement result = Parse(output.ToString());
                    Equal(1, result.GetProperty("size").GetInt32());
                    Equal(1, result.GetProperty("start").GetInt32());
                    Equal(1, result.GetProperty("limit").GetInt32());
                    True(!result.GetProperty("hasMore").GetBoolean());
                    True(result.GetProperty("results")[0].GetProperty("fields").GetProperty("custom").GetBoolean());
                }
            }
        }
        );
        yield return ("CLI sprint issues reports API errors without partial results", async () =>
        {
            foreach (HttpStatusCode status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            {
                var handler = new FakeHandler(_ => Json("{\"message\":\"Sprint unavailable\"}", status));
                using var output = new StringWriter();
                using var errors = new StringWriter();
                var app = new CliApplication(output, errors, clientFactory: (_, _) => Client(handler));
                Equal(status == HttpStatusCode.Forbidden ? 3 : 5,
                    await app.RunAsync(["jira", "sprint", "issues", "--sprint", "42"]));
                Equal("", output.ToString());
                Contains("Sprint unavailable", errors.ToString());
            }
        }
        );
        yield return ("CLI sprint issue descriptions are opt-in for retrieval and human output", async () =>
        {
            foreach (bool includeBody in new[] { false, true })
            {
                foreach (OutputFormat format in Enum.GetValues<OutputFormat>())
                {
                    var handler = new FakeHandler(request =>
                    {
                        Equal(includeBody ? "*all" : "summary,status,assignee", Parameter(request.Uri, "fields"));
                        string description = includeBody ? ",\"description\":\"Detailed content\",\"custom\":true" : "";
                        return Json($"{{\"startAt\":0,\"total\":1,\"issues\":[{{\"key\":\"DRB-7\",\"fields\":{{\"summary\":\"Overview\",\"status\":{{\"name\":\"Open\"}},\"assignee\":{{\"displayName\":\"Alex\"}}{description}}}}}]}}");
                    });
                    using var output = new StringWriter();
                    var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => Client(handler));
                    string[] args = ["jira", "sprint", "issues", "--sprint", "42", "-o", format.ToString()];
                    Equal(0, await app.RunAsync(includeBody ? [.. args, "--include-body"] : args));
                    Contains("Overview", output.ToString());
                    Equal(includeBody, output.ToString().Contains("Detailed content", StringComparison.Ordinal));
                    if (format == OutputFormat.Json)
                    {
                        JsonElement fields = Parse(output.ToString()).GetProperty("results")[0].GetProperty("fields");
                        Equal(includeBody, fields.TryGetProperty("description", out _));
                        Equal(includeBody, fields.TryGetProperty("custom", out _));
                    }
                    else
                    {
                        Equal(includeBody, output.ToString().Contains("### Content:", StringComparison.Ordinal));
                    }
                }
            }
        }
        );
        yield return ("CLI sprint status filters combine with pagination and include-body", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("status IN (\"Open\", \"In Progress\") AND status NOT IN (\"Done\")", Parameter(request.Uri, "jql"));
                Equal("*all", Parameter(request.Uri, "fields"));
                Equal("1", Parameter(request.Uri, "startAt"));
                Equal("1", Parameter(request.Uri, "maxResults"));
                return Json("{\"startAt\":1,\"total\":3,\"issues\":[{\"key\":\"DRB-7\",\"fields\":{\"summary\":\"Filtered\",\"description\":\"Details\"}}]}");
            });
            using var output = new StringWriter();
            var app = new CliApplication(output, new StringWriter(), clientFactory: (_, _) => Client(handler));
            Equal(0, await app.RunAsync(["jira", "sprint", "issues", "--sprint", "42",
                "--status", "Open,+In Progress", "--status=-Done",
                "--include-body", "--start", "1", "--limit", "1", "-o", "json"]));
            JsonElement result = Parse(output.ToString());
            Equal(1, result.GetProperty("size").GetInt32());
            Equal(3, result.GetProperty("total").GetInt32());
            True(result.GetProperty("hasMore").GetBoolean());
            Contains("Details", output.ToString());
        }
        );
        yield return ("CLI invalid sprint status filters produce explicit input errors", async () =>
        {
            foreach (string[] options in new[]
            {
                new[] { "--status" }, ["--status", "+"],
                ["--status="], ["--status=-"], ["--status", "+Open", "--status=-open"],
                ["--status=Ready,"], ["--status=Ready,,Done"]
            })
            {
                var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected HTTP."));
                using var errors = new StringWriter();
                var app = new CliApplication(new StringWriter(), errors, clientFactory: (_, _) => Client(handler));
                Equal(2, await app.RunAsync(["jira", "sprint", "issues", "--sprint", "42", .. options]));
                Equal(0, handler.Requests.Count);
                True(errors.ToString().Length > 0);
            }
        }
        );
        yield return ("CLI accepts comma-separated signed and unsigned sprint statuses", async () =>
        {
            foreach (string statuses in new[] { "-Done,+Ready,+ReadyForTest", "-Done,Ready,ReadyForTest" })
            {
                var handler = new FakeHandler(request =>
                {
                    Equal("status IN (\"Ready\", \"ReadyForTest\") AND status NOT IN (\"Done\")", Parameter(request.Uri, "jql"));
                    return Json("{\"startAt\":0,\"total\":0,\"issues\":[]}");
                });
                var app = new CliApplication(new StringWriter(), new StringWriter(), clientFactory: (_, _) => Client(handler));
                Equal(0, await app.RunAsync(["jira", "sprint", "issues", "--sprint", "42", "--status=" + statuses]));
                Equal(1, handler.Requests.Count);
            }
        }
        );
        yield return ("CLI sprint list wires project, state, combined pagination and JSON output", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal(HttpMethod.Get, request.Method);
                if (request.Uri.AbsolutePath == "/confluence/rest/agile/1.0/board")
                {
                    Equal("DRB", Parameter(request.Uri, "projectKeyOrId"));
                    Equal("scrum", Parameter(request.Uri, "type"));
                    return Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":7}]}");
                }

                Equal("/confluence/rest/agile/1.0/board/7/sprint", request.Uri.AbsolutePath);
                Equal("active,future", Parameter(request.Uri, "state"));
                return Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":12,\"name\":\"Twelve\",\"goal\":\"original\"},{\"id\":2,\"name\":\"Two\",\"goal\":\"second\"}]}");
            });
            using var output = new StringWriter();
            var app = new CliApplication(output, new StringWriter(), clientFactory: (service, _) =>
            {
                Equal("JIRA", service);
                return Client(handler);
            });
            Equal(0, await app.RunAsync(["jira", "sprint", "list", "--project", "DRB", "--state", "active,future", "--start", "1", "--limit", "1", "-o", "json"]));
            JsonElement result = Parse(output.ToString());
            Equal(1, result.GetProperty("start").GetInt32());
            Equal(1, result.GetProperty("size").GetInt32());
            Equal(1, result.GetProperty("limit").GetInt32());
            Equal(2, result.GetProperty("total").GetInt32());
            True(!result.GetProperty("hasMore").GetBoolean());
            Equal(2, result.GetProperty("results")[0].GetProperty("id").GetInt32());
            Equal("second", JsonValues.Text(result.GetProperty("results")[0], "goal"));
            Equal(2, handler.Requests.Count);
        }
        );
        yield return ("CLI sprint API failure reports error without partial output", async () =>
        {
            var handler = new FakeHandler(request =>
                request.Uri.AbsolutePath.EndsWith("/board", StringComparison.Ordinal)
                    ? Json("{\"startAt\":0,\"isLast\":true,\"values\":[{\"id\":1}]}")
                    : Json("{\"message\":\"Forbidden\"}", HttpStatusCode.Forbidden));
            using var output = new StringWriter();
            using var errors = new StringWriter();
            var app = new CliApplication(output, errors, clientFactory: (_, _) => Client(handler));
            Equal(3, await app.RunAsync(["jira", "sprint", "list", "--project", "DRB"]));
            Equal("", output.ToString());
            Contains("403", errors.ToString());
        }
        );
    }

    private static IEnumerable<string[]> MutationCommands()
    {
        yield return ["jira", "issue", "create", "--summary", "Test"];
        yield return ["jira", "issue", "update", "TEST-1"];
        yield return ["jira", "issue", "delete", "TEST-1"];
        yield return ["jira", "issue", "transition", "TEST-1", "Done"];
        yield return ["jira", "issue", "comment", "add", "TEST-1", "--body", "hello"];
        yield return ["conf", "page", "create", "--space", "DOCS", "--title", "Test"];
        yield return ["conf", "page", "update", "1", "--title", "Test"];
        yield return ["conf", "label", "add", "1", "test"];
        yield return ["conf", "label", "remove", "1", "test"];
    }

    private static IEnumerable<(string[], string, HttpMethod)> Contracts()
    {
        yield return (["jira", "myself"], "/rest/api/2/myself", HttpMethod.Get);
        yield return (["jira", "project", "list"], "/rest/api/2/project", HttpMethod.Get);
        yield return (["jira", "issue", "get", "TEST-1"], "/rest/api/2/issue/TEST-1", HttpMethod.Get);
        yield return (["jira", "issue", "search", "project = TEST"], "/rest/api/2/search", HttpMethod.Get);
        yield return (["jira", "issue", "create", "--project", "TEST", "--summary", "Title", "--assignee", "alice", "--allow-write"], "/rest/api/2/issue", HttpMethod.Post);
        yield return (["jira", "issue", "update", "TEST-1", "--field", "labels=[\"a\",\"b\"]", "--allow-write"], "/rest/api/2/issue/TEST-1", HttpMethod.Put);
        yield return (["jira", "issue", "delete", "TEST-1", "--allow-write", "--yes"], "/rest/api/2/issue/TEST-1", HttpMethod.Delete);
        yield return (["jira", "issue", "subtasks", "TEST-1"], "/rest/api/2/issue/TEST-1", HttpMethod.Get);
        yield return (["jira", "issue", "transitions", "TEST-1"], "/rest/api/2/issue/TEST-1/transitions", HttpMethod.Get);
        // Transition's GET and POST share the same endpoint; the dedicated client test checks the POST.
        yield return (["jira", "issue", "comment", "list", "TEST-1"], "/rest/api/2/issue/TEST-1/comment", HttpMethod.Get);
        yield return (["jira", "issue", "comment", "add", "TEST-1", "--body", "comment", "--allow-write"], "/rest/api/2/issue/TEST-1/comment", HttpMethod.Post);
        yield return (["conf", "space", "list"], "/rest/api/space", HttpMethod.Get);
        yield return (["conf", "space", "get", "DOCS"], "/rest/api/space/DOCS", HttpMethod.Get);
        yield return (["conf", "page", "get", "1"], "/rest/api/content/1", HttpMethod.Get);
        yield return (["conf", "page", "list", "--space", "DOCS"], "/rest/api/content", HttpMethod.Get);
        yield return (["conf", "search", "space = DOCS"], "/rest/api/content/search", HttpMethod.Get);
        yield return (["conf", "page", "create", "--space", "DOCS", "--title", "Test", "--body", "<p>body</p>", "--allow-write"], "/rest/api/content", HttpMethod.Post);
        yield return (["conf", "page", "update", "1", "--title", "Test", "--body", "<p/>", "--version", "2", "--allow-write"], "/rest/api/content/1", HttpMethod.Put);
        yield return (["conf", "comment", "list", "1"], "/rest/api/content/1/child/comment", HttpMethod.Get);
        yield return (["conf", "label", "list", "1"], "/rest/api/content/1/label", HttpMethod.Get);
        yield return (["conf", "label", "add", "1", "a", "b", "--allow-write"], "/rest/api/content/1/label", HttpMethod.Post);
        yield return (["conf", "label", "remove", "1", "a/b", "--allow-write"], "/rest/api/content/1/label", HttpMethod.Delete);
        yield return (["conf", "attachment", "list", "1"], "/rest/api/content/1/child/attachment", HttpMethod.Get);
    }

    private static HttpResponseMessage Response(Request request)
    {
        if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete)
        {
            return request.Method == HttpMethod.Put && request.Uri.AbsolutePath.Contains("/rest/api/content/", StringComparison.Ordinal)
                ? Json("{\"id\":\"1\",\"type\":\"page\",\"version\":{\"number\":2}}")
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        string path = request.Uri.AbsolutePath;
        if (path.Contains("/rest/api/2/", StringComparison.Ordinal))
        {
            if (path.EndsWith("/project", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                return Json("{\"startAt\":0,\"total\":0,\"issues\":[]}");
            }

            if (path.EndsWith("/comment", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return Json("{\"startAt\":0,\"total\":0,\"comments\":[]}");
            }

            if (path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                return Json("{\"transitions\":[]}");
            }

            return Json("{\"id\":\"1\",\"key\":\"TEST-1\",\"fields\":{\"subtasks\":[]},\"name\":\"alice\"}");
        }

        return Json("{\"id\":\"1\",\"type\":\"page\",\"title\":\"Test\",\"start\":0,\"results\":[],\"_links\":{}}");
    }
}
