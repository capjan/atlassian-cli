using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlassianCli.Configuration;
using AtlassianCli.Serialization;

namespace AtlassianCli.Http;

public sealed class AtlassianHttpClient : IDisposable
{
    private const int MaximumJsonBytes = 32 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly ConnectionSettings settings;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public AtlassianHttpClient(
        ConnectionSettings settings,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.settings = settings;
        this.delay = delay ?? Task.Delay;
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = settings.Timeout
        });
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Uri BaseUri => settings.BaseUri;

    public Uri ApiUri(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        if (!path.StartsWith("/rest/", StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException("API paths must start with /rest/ and cannot contain a query.");
        }

        Uri uri = new(settings.BaseUri, path.TrimStart('/'));
        if (query is not null)
        {
            string encoded = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            uri = new UriBuilder(uri) { Query = encoded }.Uri;
        }

        ValidateUri(uri);
        return uri;
    }

    public Uri ResolveLink(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new InvalidDataException("API returned an empty continuation or download link.");
        }

        Uri uri = new(settings.BaseUri, link);
        ValidateUri(uri);
        return uri;
    }

    private void ValidateUri(Uri uri)
    {
        if (uri.Scheme != settings.BaseUri.Scheme || uri.Host != settings.BaseUri.Host
            || uri.Port != settings.BaseUri.Port || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.StartsWith(settings.BaseUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Refusing a link outside the configured service origin or context path.");
        }
    }

    public Task<JsonElement> GetAsync(string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
        => GetAsync(ApiUri(path, query), cancellationToken);

    public Task<JsonElement> GetArrayAsync(string path, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, ApiUri(path), null, true, cancellationToken, JsonValueKind.Array);

    public Task<JsonElement> GetAsync(Uri uri, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, uri, null, true, cancellationToken);

    public Task<JsonElement> WriteAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
        => SendAsync(method, ApiUri(path), body, false, cancellationToken);

    public Task<JsonElement> WriteAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>> query, JsonNode? body, CancellationToken cancellationToken)
        => SendAsync(method, ApiUri(path, query), body, false, cancellationToken);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        Uri uri,
        JsonNode? body,
        bool expectJson,
        CancellationToken cancellationToken,
        JsonValueKind expectedRoot = JsonValueKind.Object)
    {
        ValidateUri(uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using HttpResponseMessage response = await SendResponseAsync(method, uri, body, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                if (expectJson)
                {
                    throw new InvalidDataException("Expected JSON but the API returned HTTP 204.");
                }

                return JsonValues.Parse("{}");
            }

            if (response.Content.Headers.ContentLength == 0 && !expectJson)
            {
                return JsonValues.Parse("{}");
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != "application/json" && !(mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                throw new InvalidDataException($"Expected JSON, received '{mediaType ?? "no content type"}'. Check the URL and authentication.");
            }

            byte[] bytes = await ReadBoundedAsync(response.Content, MaximumJsonBytes, timeout.Token);
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != expectedRoot)
            {
                throw new InvalidDataException($"Expected a JSON {expectedRoot.ToString().ToLowerInvariant()} from the Atlassian API.");
            }

            return document.RootElement.Clone();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Request exceeded the {settings.Timeout.TotalSeconds:0}-second timeout.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendResponseAsync(HttpMethod method, Uri uri, JsonNode? body, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("atlassian-cli-dotnet/0.1.0");
            if (body is not null)
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    body.WriteTo(writer);
                }

                request.Content = new ByteArrayContent(buffer.ToArray());
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (method == HttpMethod.Get && attempt < 2
                && response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                TimeSpan wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromSeconds(attempt + 1);
                if (wait >= TimeSpan.Zero && wait <= TimeSpan.FromSeconds(30))
                {
                    response.Dispose();
                    await delay(wait, cancellationToken);
                    continue;
                }
            }

            using (response)
            {
                byte[] bytes = await ReadBoundedAsync(response.Content, 64 * 1024, cancellationToken);
                string details = ErrorDetails(bytes);
                string message = $"API request failed: HTTP {(int)response.StatusCode} ({response.StatusCode}).";
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    message += " Redirects are not followed; configure the final HTTPS service URL.";
                }
                else if (details.Length > 0)
                {
                    message += " " + details;
                }

                message = message.Replace(settings.Token, "[redacted]", StringComparison.Ordinal);
                throw new AtlassianApiException(response.StatusCode, message);
            }
        }
    }

    private static string ErrorDetails(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The server returned an unrecognized error response.";
            }

            var parts = new List<string>();
            string message = JsonValues.Text(root, "message");
            if (message.Length > 0)
            {
                parts.Add(message);
            }

            if (root.TryGetProperty("errorMessages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(messages.EnumerateArray().Select(item => item.ToString()));
            }

            if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
            {
                parts.AddRange(errors.EnumerateObject().Select(item => $"{item.Name}: {item.Value}"));
            }

            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return "The server returned a non-JSON error response.";
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximum)
            {
                throw new InvalidDataException($"API response exceeds the {maximum}-byte response limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public async Task DownloadAsync(string link, string destination, CancellationToken cancellationToken)
    {
        Uri uri = ResolveLink(link);
        string fullPath = Path.GetFullPath(destination);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Destination already exists: {fullPath}");
        }

        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using HttpResponseMessage response = await SendResponseAsync(HttpMethod.Get, uri, null, timeout.Token);
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await response.Content.CopyToAsync(file, timeout.Token);
                await file.FlushAsync(timeout.Token);
            }

            File.Move(temporaryPath, fullPath, false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Attachment download timed out.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose() => client.Dispose();
}
