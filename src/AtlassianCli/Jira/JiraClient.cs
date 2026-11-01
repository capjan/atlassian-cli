using System.Text.Json;
using System.Text.Json.Nodes;
using AtlassianCli.Http;
using AtlassianCli.Serialization;

namespace AtlassianCli.Jira;

public sealed class JiraClient(AtlassianHttpClient http)
{
    private static string IssuePath(string key) => "/rest/api/2/issue/" + JsonValues.Segment(key);

    public Task<JsonElement> MyselfAsync(CancellationToken cancellationToken)
        => http.GetAsync("/rest/api/2/myself", null, cancellationToken);

    public Task<JsonElement> GetIssueAsync(string key, CancellationToken cancellationToken)
        => http.GetAsync(IssuePath(key), null, cancellationToken);

    public Task<ListResult> SearchAsync(string jql, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync("/rest/api/2/search", "issues", [new("jql", jql)], page, cancellationToken);

    public Task<ListResult> CommentsAsync(string key, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync(IssuePath(key) + "/comment", "comments", [], page, cancellationToken);

    private async Task<ListResult> CollectAsync(
        string path,
        string property,
        KeyValuePair<string, string>[] query,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        page.Validate();
        var results = new List<JsonElement>();
        int start = page.Start;
        int next = start;
        int total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = page.All ? 100 : Math.Min(100, page.Limit - results.Count);
            KeyValuePair<string, string>[] parameters = [.. query, new("startAt", JsonValues.Number(next)), new("maxResults", JsonValues.Number(requested))];
            JsonElement response = await http.GetAsync(path, parameters, cancellationToken);
            JsonElement[] items = JsonValues.RequiredArray(response, property);
            int actualStart = JsonValues.RequiredInt(response, "startAt");
            total = JsonValues.RequiredInt(response, "total");
            if (actualStart != next || items.Length > requested || (items.Length == 0 && actualStart < total))
            {
                throw new InvalidDataException("Jira returned inconsistent pagination metadata; refusing a potentially incomplete result.");
            }

            results.AddRange(items);
            next = checked(actualStart + items.Length);
            if (next >= total || (!page.All && results.Count >= page.Limit))
            {
                break;
            }
        }

        return new ListResult(results.ToArray(), start, page.Limit, results.Count, total, next < total);
    }

    public async Task<JsonElement> CreateAsync(JsonObject fields, CancellationToken cancellationToken)
        => JsonValues.RequireIdentity(await http.WriteAsync(HttpMethod.Post, "/rest/api/2/issue", new JsonObject { ["fields"] = fields }, cancellationToken), "issue");

    public Task<JsonElement> UpdateAsync(string key, JsonObject fields, CancellationToken cancellationToken)
        => http.WriteAsync(HttpMethod.Put, IssuePath(key), new JsonObject { ["fields"] = fields }, cancellationToken);

    public Task<JsonElement> DeleteAsync(string key, CancellationToken cancellationToken)
        => http.WriteAsync(HttpMethod.Delete, IssuePath(key), null, cancellationToken);

    public async Task<ListResult> SubtasksAsync(string key, CancellationToken cancellationToken)
    {
        JsonElement issue = await GetIssueAsync(key, cancellationToken);
        if (!issue.TryGetProperty("fields", out JsonElement fields))
        {
            throw new InvalidDataException("Issue response is missing fields.");
        }

        JsonElement[] items = JsonValues.RequiredArray(fields, "subtasks");
        return new ListResult(items, 0, items.Length, items.Length, items.Length, false);
    }

    public async Task<ListResult> TransitionsAsync(string key, CancellationToken cancellationToken)
    {
        JsonElement response = await http.GetAsync(IssuePath(key) + "/transitions", null, cancellationToken);
        JsonElement[] items = JsonValues.RequiredArray(response, "transitions");
        return new ListResult(items, 0, items.Length, items.Length, items.Length, false);
    }

    public async Task TransitionAsync(string key, string target, CancellationToken cancellationToken)
    {
        ListResult transitions = await TransitionsAsync(key, cancellationToken);
        JsonElement[] matches = transitions.Results.Where(item =>
            JsonValues.Text(item, "id") == target
            || string.Equals(JsonValues.Text(item, "name"), target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(JsonValues.Text(item, "to", "name"), target, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            string available = string.Join(", ", transitions.Results.Select(item => $"{JsonValues.Text(item, "id")}: {JsonValues.Text(item, "name")}"));
            throw new ArgumentException($"Transition '{target}' has {matches.Length} matches. Choose an unambiguous transition ID. Available: {available}");
        }

        string id = JsonValues.Text(matches[0], "id");
        if (id.Length == 0)
        {
            throw new InvalidDataException("Transition response is missing an ID.");
        }

        await http.WriteAsync(HttpMethod.Post, IssuePath(key) + "/transitions",
            new JsonObject { ["transition"] = new JsonObject { ["id"] = id } }, cancellationToken);
    }

    public async Task<JsonElement> AddCommentAsync(string key, string body, CancellationToken cancellationToken)
        => JsonValues.RequireIdentity(await http.WriteAsync(HttpMethod.Post, IssuePath(key) + "/comment", new JsonObject { ["body"] = body }, cancellationToken), "comment");

    public Uri BrowseUri(string key) => new(http.BaseUri, "browse/" + JsonValues.Segment(key));
}
