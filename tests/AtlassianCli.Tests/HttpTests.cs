using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlassianCli.Configuration;
using AtlassianCli.Http;
using static AtlassianCli.Tests.TestSupport;

namespace AtlassianCli.Tests;

internal static class HttpTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("HTTPS/PAT configuration validation", () =>
        {
            foreach (string url in new[] { "", "http://example.test", "https://user:password@example.test", "https://example.test?q=x", "https://example.test#x" })
            {
                Throws<ArgumentException>(() => ConnectionSettings.FromValues(url, "token", 30, "TEST"));
            }

            Throws<ArgumentException>(() => ConnectionSettings.FromValues("https://example.test", "tok\n", 30, "TEST"));
            Throws<ArgumentException>(() => ConnectionSettings.FromValues("https://example.test", "token", 0, "TEST"));
            Equal("/confluence/", Settings.BaseUri.AbsolutePath);
            return Task.CompletedTask;
        }
        );
        yield return ("Context path, escaping and bearer authentication", async () =>
        {
            var handler = new FakeHandler(request =>
            {
                Equal("/confluence/rest/api/content/123", request.Uri.AbsolutePath);
                Equal("a & \"b\"", Parameter(request.Uri, "cql"));
                Equal("Bearer test-token", request.Authorization);
                return Json("{\"id\":\"123\",\"extra\":{\"kept\":true}}");
            });
            using AtlassianHttpClient http = Client(handler);
            JsonElement value = await http.GetAsync("/rest/api/content/123", [new("cql", "a & \"b\"")], default);
            True(value.GetProperty("extra").GetProperty("kept").GetBoolean());
        }
        );
        yield return ("Foreign origin/context continuation rejected", () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{}")));
            foreach (string link in new[] { "https://evil.test/confluence/rest/api/content", "https://example.test/other", "http://example.test/confluence/x", "https://user@example.test/confluence/x", "/confluence/../other" })
            {
                Throws<InvalidDataException>(() => http.ResolveLink(link));
            }

            Equal("/confluence/rest/api/content", http.ResolveLink("/confluence/rest/api/content").AbsolutePath);
            return Task.CompletedTask;
        }
        );
        yield return ("Non-JSON success and empty read rejected", async () =>
        {
            using AtlassianHttpClient html = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }));
            await ThrowsAsync<InvalidDataException>(() => html.GetAsync("/rest/api/content", null, default));
            using AtlassianHttpClient empty = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
            await ThrowsAsync<InvalidDataException>(() => empty.GetAsync("/rest/api/content", null, default));
        }
        );
        yield return ("Malformed JSON rejected", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{")));
            await ThrowsAsync<JsonException>(() => http.GetAsync("/rest/api/content", null, default));
        }
        );
        yield return ("Rate-limit GET retries exactly twice", async () =>
        {
            int attempts = 0;
            using AtlassianHttpClient http = Client(new FakeHandler(_ => ++attempts < 3 ? Json("{\"message\":\"busy\"}", HttpStatusCode.TooManyRequests) : Json("{}")));
            await http.GetAsync("/rest/api/content", null, default);
            Equal(3, attempts);
        }
        );
        yield return ("GET retries exhausted and long Retry-After honored", async () =>
        {
            int attempts = 0;
            using AtlassianHttpClient http = Client(new FakeHandler(_ =>
            {
                attempts++;
                var response = Json("{}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return response;
            }));
            await ThrowsAsync<AtlassianApiException>(() => http.GetAsync("/rest/api/content", null, default));
            Equal(1, attempts);
            var handler = new FakeHandler(_ => Json("{}", HttpStatusCode.ServiceUnavailable));
            using AtlassianHttpClient exhausted = Client(handler);
            await ThrowsAsync<AtlassianApiException>(() => exhausted.GetAsync("/rest/api/content", null, default));
            Equal(3, handler.Requests.Count);
        }
        );
        yield return ("Mutations never automatically retried", async () =>
        {
            int attempts = 0;
            using AtlassianHttpClient http = Client(new FakeHandler(_ =>
            {
                attempts++;
                return Json("{\"message\":\"busy\"}", HttpStatusCode.ServiceUnavailable);
            }));
            await ThrowsAsync<AtlassianApiException>(() => http.WriteAsync(HttpMethod.Post, "/rest/api/content", new JsonObject(), default));
            Equal(1, attempts);
        }
        );
        yield return ("HTTP errors preserve status and redact PAT", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{\"message\":\"bad test-token\",\"errors\":{\"field\":\"required\"}}", HttpStatusCode.Forbidden)));
            try
            {
                await http.GetAsync("/rest/api/content", null, default);
                throw new InvalidOperationException("Expected API error.");
            }
            catch (AtlassianApiException exception)
            {
                Equal(HttpStatusCode.Forbidden, exception.StatusCode);
                Contains("[redacted]", exception.Message);
                Contains("field: required", exception.Message);
                True(!exception.Message.Contains("test-token", StringComparison.Ordinal));
            }
        }
        );
        yield return ("Redirect explicitly rejected", async () =>
        {
            using AtlassianHttpClient http = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)));
            await ThrowsAsync<AtlassianApiException>(() => http.GetAsync("/rest/api/content", null, default));
        }
        );
        yield return ("Cancellation propagates without timeout fallback", async () =>
        {
            var handler = new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Json("{}");
            });
            using AtlassianHttpClient http = Client(handler);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await ThrowsAsync<OperationCanceledException>(() => http.GetAsync("/rest/api/content", null, cancellation.Token));
        }
        );
        yield return ("Timeout explicitly reported", async () =>
        {
            var handler = new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Json("{}");
            });
            using var http = new AtlassianHttpClient(Settings with { Timeout = TimeSpan.FromMilliseconds(20) }, handler);
            await ThrowsAsync<TimeoutException>(() => http.GetAsync("/rest/api/content", null, default));
        }
        );
        yield return ("Attachment atomic download and no overwrite", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "atlassian-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "attachment.txt");
            try
            {
                using AtlassianHttpClient http = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("downloaded") }));
                await http.DownloadAsync("/confluence/download/file", file, default);
                Equal("downloaded", await File.ReadAllTextAsync(file));
                await ThrowsAsync<IOException>(() => http.DownloadAsync("/confluence/download/file", file, default));
                Equal(1, Directory.GetFiles(directory).Length);
            }
            finally
            {
                File.Delete(file);
                Directory.Delete(directory);
            }
        }
        );
        yield return ("Failed download leaves no partial file", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "atlassian-cli-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using AtlassianHttpClient http = Client(new FakeHandler(_ => Json("{}", HttpStatusCode.NotFound)));
                await ThrowsAsync<AtlassianApiException>(() => http.DownloadAsync("/confluence/download/file", Path.Combine(directory, "file"), default));
                Equal(0, Directory.GetFiles(directory).Length);
            }
            finally
            {
                Directory.Delete(directory);
            }
        }
        );
        yield return ("JSON response byte limit at exact 32 MiB boundary", async () =>
        {
            const int maximum = 32 * 1024 * 1024;
            string payload = "{\"s\":\"" + new string('a', maximum - 8) + "\"}";
            Equal(maximum, System.Text.Encoding.UTF8.GetByteCount(payload));
            using AtlassianHttpClient atLimit = Client(new FakeHandler(_ => Json(payload)));
            JsonElement result = await atLimit.GetAsync("/rest/api/content", null, default);
            Equal(maximum - 8, result.GetProperty("s").GetString()!.Length);
            using AtlassianHttpClient aboveLimit = Client(new FakeHandler(_ => Json(payload + " ")));
            await ThrowsAsync<InvalidDataException>(() => aboveLimit.GetAsync("/rest/api/content", null, default));
        }
        );
    }
}
