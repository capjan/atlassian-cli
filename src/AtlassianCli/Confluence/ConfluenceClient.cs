using System.Text.Json;
using System.Text.Json.Nodes;
using AtlassianCli.Http;
using AtlassianCli.Serialization;

namespace AtlassianCli.Confluence;

public sealed class ConfluenceClient(AtlassianHttpClient http)
{
    private const string BodyExpand = "body.storage,version,space,ancestors";
    private static string ContentPath(string id) => "/rest/api/content/" + JsonValues.Segment(id);

    public Task<ListResult> SpacesAsync(PageRequest page, CancellationToken cancellationToken)
        => CollectAsync("/rest/api/space", [], page, cancellationToken);

    public Task<JsonElement> SpaceAsync(string key, CancellationToken cancellationToken)
        => http.GetAsync("/rest/api/space/" + JsonValues.Segment(key), [new("expand", "description.plain,homepage")], cancellationToken);

    public Task<ListResult> PagesAsync(string space, string type, bool includeBody, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync("/rest/api/content", [new("spaceKey", space), new("type", type), new("expand", includeBody ? BodyExpand : "version,space,ancestors")], page, cancellationToken);

    public Task<JsonElement> PageAsync(string id, CancellationToken cancellationToken)
        => http.GetAsync(ContentPath(id), [new("expand", BodyExpand)], cancellationToken);

    public Task<ListResult> SearchAsync(string cql, bool includeBody, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync("/rest/api/content/search", [new("cql", cql), new("expand", includeBody ? BodyExpand : "version,space,ancestors")], page, cancellationToken);

    public Task<ListResult> CommentsAsync(string id, string location, bool includeReplies, PageRequest page, CancellationToken cancellationToken)
    {
        KeyValuePair<string, string>[] locations = location == "all"
            ? [new("location", "footer"), new("location", "inline"), new("location", "resolved")]
            : [new("location", location)];
        return CollectAsync(ContentPath(id) + "/child/comment",
            [.. locations, new("depth", includeReplies ? "all" : "root"), new("expand", "body.storage,version,history,container,ancestors,extensions.inlineProperties,extensions.resolution")],
            page, cancellationToken);
    }

    public Task<ListResult> LabelsAsync(string id, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync(ContentPath(id) + "/label", [], page, cancellationToken);

    public Task<ListResult> AttachmentsAsync(string id, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync(ContentPath(id) + "/child/attachment", [new("expand", "version")], page, cancellationToken);

    private async Task<ListResult> CollectAsync(
        string path,
        KeyValuePair<string, string>[] query,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        page.Validate();
        var results = new List<JsonElement>();
        int next = page.Start;
        long? total = null;
        bool hasMore;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri? continuation = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = page.All ? 100 : Math.Min(100, page.Limit - results.Count);
            Uri uri = continuation ?? http.ApiUri(path,
                [.. query, new("start", JsonValues.Number(next)), new("limit", JsonValues.Number(requested))]);
            if (!visited.Add(uri.AbsoluteUri))
            {
                throw new InvalidDataException("Confluence returned a cyclic continuation link.");
            }

            JsonElement response = await http.GetAsync(uri, cancellationToken);
            JsonElement[] items = JsonValues.RequiredArray(response, "results");
            int actualStart = JsonValues.RequiredInt(response, "start");
            if (actualStart != next)
            {
                throw new InvalidDataException("Confluence returned inconsistent pagination offsets.");
            }

            if (response.TryGetProperty("totalCount", out JsonElement count) && count.TryGetInt64(out long countValue))
            {
                if (countValue < 0)
                {
                    throw new InvalidDataException("Confluence returned a negative total count.");
                }

                total = countValue;
            }

            if (!response.TryGetProperty("_links", out JsonElement links) || links.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Confluence list response is missing continuation metadata (_links).");
            }

            string link = JsonValues.Text(response, "_links", "next");
            hasMore = link.Length > 0;
            if (hasMore && items.Length == 0)
            {
                throw new InvalidDataException("Confluence returned a continuation link without any items.");
            }

            int taken = page.All ? items.Length : Math.Min(items.Length, page.Limit - results.Count);
            results.AddRange(items.Take(taken));
            if (taken < items.Length)
            {
                hasMore = true;
                break;
            }

            next = checked(actualStart + items.Length);
            if (!hasMore || (!page.All && results.Count >= page.Limit))
            {
                break;
            }

            continuation = http.ResolveLink(link);
        }

        return new ListResult(results.ToArray(), page.Start, page.Limit, results.Count, total, hasMore);
    }

    public async Task<ListResult> RestrictionsAsync(string id, CancellationToken cancellationToken)
    {
        var operations = new List<JsonElement>();
        foreach (string operation in new[] { "read", "update" })
        {
            var subjects = new JsonObject();
            foreach (string subject in new[] { "user", "group" })
            {
                var collected = new List<JsonElement>();
                int start = 0;
                while (true)
                {
                    JsonElement response = await http.GetAsync(ContentPath(id) + "/restriction/byOperation/" + operation,
                        [new("expand", "restrictions." + subject), new("start", JsonValues.Number(start)), new("limit", "100")], cancellationToken);
                    if (JsonValues.Text(response, "operation") != operation
                        || !response.TryGetProperty("restrictions", out JsonElement restriction)
                        || !restriction.TryGetProperty(subject, out JsonElement subjectPage))
                    {
                        throw new InvalidDataException("Restriction response is missing the expected operation or subject page.");
                    }

                    JsonElement[] items = JsonValues.RequiredArray(subjectPage, "results");
                    if (JsonValues.RequiredInt(subjectPage, "start") != start)
                    {
                        throw new InvalidDataException("Restriction response returned inconsistent pagination offsets.");
                    }

                    collected.AddRange(items);
                    int serverLimit = JsonValues.RequiredInt(subjectPage, "limit");
                    bool more = JsonValues.Text(subjectPage, "_links", "next").Length > 0
                        || (serverLimit > 0 && items.Length == serverLimit);
                    if (!more)
                    {
                        break;
                    }

                    if (items.Length == 0)
                    {
                        throw new InvalidDataException("Restriction pagination made no progress.");
                    }

                    start = checked(start + items.Length);
                }

                subjects[subject] = ArrayPage(collected);
            }

            var root = new JsonObject
            {
                ["operation"] = operation,
                ["restrictions"] = subjects
            };
            operations.Add(JsonValues.Parse(root.ToJsonString()));
        }

        return new ListResult(operations.ToArray(), 0, 2, 2, 2, false);
    }

    private static JsonObject ArrayPage(List<JsonElement> elements)
        => new()
        {
            ["results"] = new JsonArray(elements.Select(item => JsonNode.Parse(item.GetRawText())).ToArray()),
            ["start"] = 0,
            ["size"] = elements.Count,
            ["hasMore"] = false
        };

    public async Task<JsonElement> CreatePageAsync(string space, string title, string body, string? parent, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new JsonObject { ["key"] = space },
            ["body"] = StorageBody(body)
        };
        if (parent is not null)
        {
            request["ancestors"] = new JsonArray(new JsonObject { ["id"] = parent });
        }

        return JsonValues.RequireIdentity(await http.WriteAsync(HttpMethod.Post, "/rest/api/content", request, cancellationToken), "page");
    }

    public async Task<JsonElement> UpdatePageAsync(string id, string title, string body, int? version, string? message, CancellationToken cancellationToken)
    {
        int nextVersion;
        if (version.HasValue)
        {
            if (version < 1)
            {
                throw new ArgumentException("--version must be positive.");
            }

            nextVersion = version.Value;
        }
        else
        {
            JsonElement current = await http.GetAsync(ContentPath(id), [new("expand", "version")], cancellationToken);
            if (!current.TryGetProperty("version", out JsonElement versionObject))
            {
                throw new InvalidDataException("Page response is missing a version.");
            }

            nextVersion = checked(JsonValues.RequiredInt(versionObject, "number") + 1);
        }

        JsonElement updated = await http.WriteAsync(HttpMethod.Put, ContentPath(id), new JsonObject
        {
            ["type"] = "page",
            ["title"] = title,
            ["version"] = new JsonObject { ["number"] = nextVersion, ["message"] = message ?? "" },
            ["body"] = StorageBody(body)
        }, cancellationToken);
        return JsonValues.RequireIdentity(updated, "page", id);
    }

    private static JsonObject StorageBody(string body)
        => new() { ["storage"] = new JsonObject { ["value"] = body, ["representation"] = "storage" } };

    public Task<JsonElement> AddLabelsAsync(string id, string[] labels, CancellationToken cancellationToken)
    {
        if (labels.Length == 0 || labels.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provide non-empty label names.");
        }

        return http.WriteAsync(HttpMethod.Post, ContentPath(id) + "/label",
            new JsonArray(labels.Select(name => (JsonNode)new JsonObject { ["prefix"] = "global", ["name"] = name }).ToArray()),
            cancellationToken);
    }

    public async Task RemoveLabelAsync(string id, string label, CancellationToken cancellationToken)
    {
        await http.WriteAsync(HttpMethod.Delete, ContentPath(id) + "/label", [new("name", label)], null, cancellationToken);
    }

    public async Task DownloadAttachmentAsync(string id, string destination, CancellationToken cancellationToken)
    {
        JsonElement attachment = await http.GetAsync(ContentPath(id), null, cancellationToken);
        if (JsonValues.Text(attachment, "type") != "attachment")
        {
            throw new ArgumentException("The supplied content ID is not an attachment.");
        }

        await http.DownloadAsync(JsonValues.Text(attachment, "_links", "download"), destination, cancellationToken);
    }
}
