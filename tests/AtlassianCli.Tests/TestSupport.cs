using System.Net;
using System.Text;
using System.Text.Json;
using AtlassianCli.Configuration;
using AtlassianCli.Http;

namespace AtlassianCli.Tests;

internal sealed record Request(HttpMethod Method, Uri Uri, string Body, string? Authorization);

internal sealed class FakeHandler(Func<Request, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Request> Requests { get; } = [];

    public FakeHandler(Func<Request, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request))) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new Request(request.Method, request.RequestUri!,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString());
        Requests.Add(captured);
        return await respond(captured, cancellationToken);
    }
}

internal static class TestSupport
{
    public static readonly ConnectionSettings Settings = ConnectionSettings.FromValues("https://example.test/confluence", "test-token", 30, "TEST");

    public static AtlassianHttpClient Client(FakeHandler handler)
        => new(Settings, handler, (_, _) => Task.CompletedTask);

    public static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void True(bool condition, string message = "Assertion failed.")
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contains(string expected, string actual)
        => True(actual.Contains(expected, StringComparison.Ordinal), $"Missing '{expected}' in '{actual}'.");

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static JsonElement Parse(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public static string Parameter(Uri uri, string name)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split('&'))
        {
            string[] pieces = pair.Split('=', 2);
            if (Uri.UnescapeDataString(pieces[0]) == name)
            {
                return pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : "";
            }
        }

        return "";
    }
}
