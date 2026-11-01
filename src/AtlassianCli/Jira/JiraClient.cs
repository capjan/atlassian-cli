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

    public async Task<ListResult> ProjectsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        page.Validate();
        JsonElement response = await http.GetArrayAsync("/rest/api/2/project", cancellationToken);
        JsonElement[] projects = response.EnumerateArray().Select(project => project.Clone()).ToArray();
        if (projects.Any(project => project.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException("Jira project list contains a non-object item.");
        }

        JsonElement[] results = projects.Skip(page.Start).Take(page.All ? int.MaxValue : page.Limit).ToArray();
        bool hasMore = (long)page.Start + results.Length < projects.Length;
        return new ListResult(results, page.Start, page.Limit, results.Length, projects.Length, hasMore);
    }

    public Task<ListResult> SearchAsync(string jql, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync("/rest/api/2/search", "issues", [new("jql", jql)], page, cancellationToken);

    public Task<ListResult> CommentsAsync(string key, PageRequest page, CancellationToken cancellationToken)
        => CollectAsync(IssuePath(key) + "/comment", "comments", [], page, cancellationToken);

    public Task<ListResult> SprintIssuesAsync(
        int sprintId, PageRequest page, CancellationToken cancellationToken,
        bool includeBody = false, string[]? statuses = null)
    {
        if (sprintId <= 0)
        {
            throw new ArgumentException("--sprint must be a positive numeric sprint ID.");
        }

        string jql = StatusJql(statuses ?? []);
        KeyValuePair<string, string>[] query = [new("fields", includeBody ? "*all" : "summary,status,assignee")];
        if (jql.Length > 0)
        {
            query = [.. query, new("jql", jql)];
        }

        return CollectAsync("/rest/agile/1.0/sprint/" + JsonValues.Number(sprintId) + "/issue",
            "issues", query, page, cancellationToken);
    }

    private static string StatusJql(string[] statuses)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in statuses)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            {
                throw new ArgumentException("--status requires non-empty status names without control characters.");
            }

            foreach (string entry in value.Split(','))
            {
                string status = entry.Trim();
                bool exclude = status.StartsWith("-", StringComparison.Ordinal);
                string name = status.StartsWith("+", StringComparison.Ordinal) || exclude ? status[1..].Trim() : status;
                if (name.Length == 0)
                {
                    throw new ArgumentException("--status cannot contain empty entries or a sign without a name.");
                }

                (exclude ? excluded : included).Add(name);
            }
        }

        if (included.Overlaps(excluded))
        {
            throw new ArgumentException("A status cannot be both included and excluded.");
        }

        string Quote(string name) => "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var clauses = new List<string>();
        if (included.Count > 0)
        {
            clauses.Add("status IN (" + string.Join(", ", included.Select(Quote)) + ")");
        }

        if (excluded.Count > 0)
        {
            clauses.Add("status NOT IN (" + string.Join(", ", excluded.Select(Quote)) + ")");
        }

        return string.Join(" AND ", clauses);
    }

    public async Task<ListResult> SprintsAsync(string project, string? state, PageRequest page, CancellationToken cancellationToken)
    {
        page.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (project.Any(char.IsControl))
        {
            throw new ArgumentException("--project cannot contain control characters.");
        }

        if (state is not null && state.Split(',').Any(value => value is not ("active" or "future" or "closed")))
        {
            throw new ArgumentException("--state must contain active, future or closed, separated by commas.");
        }

        JsonElement[] boards = await CollectAgileAsync("/rest/agile/1.0/board",
            [new("projectKeyOrId", project), new("type", "scrum")], cancellationToken);
        var boardIds = new HashSet<int>();
        var sprints = new SortedDictionary<int, JsonElement>();
        foreach (JsonElement board in boards)
        {
            int boardId = AgileId(board);
            if (!boardIds.Add(boardId))
            {
                continue;
            }

            JsonElement[] boardSprints = await CollectAgileAsync(
                "/rest/agile/1.0/board/" + JsonValues.Number(boardId) + "/sprint",
                state is null ? [] : [new("state", state)], cancellationToken);
            foreach (JsonElement sprint in boardSprints)
            {
                sprints.TryAdd(AgileId(sprint), sprint);
            }
        }

        JsonElement[] results = sprints.Values.Reverse().Skip(page.Start).Take(page.All ? int.MaxValue : page.Limit).ToArray();
        return new ListResult(results, page.Start, page.Limit, results.Length, sprints.Count,
            (long)page.Start + results.Length < sprints.Count);
    }

    private static int AgileId(JsonElement value)
    {
        int id = JsonValues.RequiredInt(value, "id");
        return id > 0 ? id : throw new InvalidDataException("Jira Agile returned a non-positive board or sprint ID.");
    }

    private async Task<JsonElement[]> CollectAgileAsync(
        string path,
        KeyValuePair<string, string>[] query,
        CancellationToken cancellationToken)
    {
        var results = new List<JsonElement>();
        int next = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement response = await http.GetAsync(path,
                [.. query, new("startAt", JsonValues.Number(next)), new("maxResults", "100")], cancellationToken);
            JsonElement[] items = JsonValues.RequiredArray(response, "values");
            int actualStart = JsonValues.RequiredInt(response, "startAt");
            int? total = response.TryGetProperty("total", out _) ? JsonValues.RequiredInt(response, "total") : null;
            bool isLast;
            if (response.TryGetProperty("isLast", out JsonElement last))
            {
                if (last.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("Jira Agile returned invalid isLast pagination metadata.");
                }

                isLast = last.GetBoolean();
            }
            else if (total is int count)
            {
                isLast = (long)actualStart + items.Length >= count;
            }
            else
            {
                throw new InvalidDataException("Jira Agile list response is missing isLast or total pagination metadata.");
            }

            long end = (long)actualStart + items.Length;
            if (actualStart != next || items.Length > 100 || (!isLast && items.Length == 0)
                || end > int.MaxValue || (total is int size && (end > size || isLast != (end >= size))))
            {
                throw new InvalidDataException("Jira Agile returned inconsistent pagination metadata; refusing a potentially incomplete result.");
            }

            results.AddRange(items);
            if (isLast)
            {
                return results.ToArray();
            }

            next = (int)end;
        }
    }

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
