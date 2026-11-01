using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using AtlassianCli.Configuration;
using AtlassianCli.Confluence;
using AtlassianCli.Http;
using AtlassianCli.Jira;
using AtlassianCli.Output;
using AtlassianCli.Serialization;

namespace AtlassianCli.Cli;

public sealed class CliApplication
{
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly TextReader input;
    private readonly Func<string, int, AtlassianHttpClient> clientFactory;
    private readonly Option<OutputFormat> format = new("--output", "-o") { Description = "table, json or markdown", Recursive = true };
    private readonly Option<int> timeout = new("--timeout") { Description = "Request timeout in seconds (1-600)", DefaultValueFactory = _ => 30, Recursive = true };
    private readonly Option<bool> allowWrite = new("--allow-write") { Description = "Explicitly enable remote mutations", Recursive = true };
    private readonly Option<bool> yes = new("--yes") { Description = "Confirm destructive deletion", Recursive = true };

    public CliApplication(
        TextWriter? output = null,
        TextWriter? error = null,
        TextReader? input = null,
        Func<string, int, AtlassianHttpClient>? clientFactory = null)
    {
        this.output = output ?? Console.Out;
        this.error = error ?? Console.Error;
        this.input = input ?? Console.In;
        this.clientFactory = clientFactory ?? ((service, seconds) => new AtlassianHttpClient(ConnectionSettings.Load(service, seconds)));
        Root = Build();
    }

    public RootCommand Root { get; }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ParseResult parsed = Root.Parse(args);
        int code = await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
        return parsed.Errors.Count > 0 ? 2 : code;
    }

    private RootCommand Build()
    {
        var root = new RootCommand("Portable Jira and Confluence Data Center CLI. Remote writes are disabled unless --allow-write is set.");
        root.Options.Add(format);
        root.Options.Add(timeout);
        root.Options.Add(allowWrite);
        root.Options.Add(yes);
        var version = new Command("version", "Show version");
        Action(version, async (parse, ct) =>
            await Writer(parse).WriteAsync(JsonSerializer.SerializeToElement(
                new VersionInfo("atlassian-cli", "0.1.0"), ApiJsonContext.Default.VersionInfo), OutputKind.General, ct));
        root.Subcommands.Add(version);
        root.Subcommands.Add(BuildJira());
        Command confluence = BuildConfluence();
        confluence.Aliases.Add("conf");
        root.Subcommands.Add(confluence);
        return root;
    }

    private void Action(Command command, Func<ParseResult, CancellationToken, Task> action)
    {
        command.SetAction(async (parse, ct) =>
        {
            try
            {
                if (parse.GetValue(timeout) is < 1 or > 600)
                {
                    throw new ArgumentException("--timeout must be between 1 and 600.");
                }

                if (!Enum.IsDefined(parse.GetValue(format)))
                {
                    throw new ArgumentException("--output must be table, json or markdown.");
                }

                await action(parse, ct);
                return 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await error.WriteLineAsync("Cancelled.");
                return 130;
            }
            catch (ArgumentException exception)
            {
                await error.WriteLineAsync("Invalid input: " + exception.Message);
                return 2;
            }
            catch (AtlassianApiException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return exception.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
                    HttpStatusCode.Conflict => 4,
                    _ => 5
                };
            }
            catch (HttpRequestException exception)
            {
                await error.WriteLineAsync("Network request failed: " + exception.Message);
                return 5;
            }
            catch (TimeoutException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return 5;
            }
            catch (JsonException exception)
            {
                await error.WriteLineAsync("Invalid JSON: " + exception.Message);
                return 6;
            }
            catch (XmlException exception)
            {
                await error.WriteLineAsync("Invalid Confluence storage XML: " + exception.Message + " Use --output json to preserve the original body.");
                return 6;
            }
            catch (InvalidDataException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return 6;
            }
            catch (IOException exception)
            {
                await error.WriteLineAsync("I/O failed: " + exception.Message);
                return 7;
            }
            catch (UnauthorizedAccessException exception)
            {
                await error.WriteLineAsync("File access failed: " + exception.Message);
                return 7;
            }
        });
    }

    private OutputWriter Writer(ParseResult parse) => new(parse.GetValue(format), output);
    private AtlassianHttpClient Http(ParseResult parse, string service) => clientFactory(service, parse.GetValue(timeout));

    private void RequireWrite(ParseResult parse, bool destructive = false)
    {
        if (!parse.GetValue(allowWrite))
        {
            throw new ArgumentException("Remote writes are disabled. This command requires --allow-write.");
        }

        if (destructive && !parse.GetValue(yes))
        {
            throw new ArgumentException("Deletion also requires --yes.");
        }
    }

    private static string Required(string? value, string name)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl)
            ? value : throw new ArgumentException($"{name} cannot be empty or contain control characters.");

    private static Argument<string> Argument(Command command, string name)
    {
        var argument = new Argument<string>(name);
        command.Arguments.Add(argument);
        return argument;
    }

    private static Option<T> Option<T>(Command command, string name, string description, bool required = false)
    {
        var option = new Option<T>(name) { Description = description, Required = required };
        command.Options.Add(option);
        return option;
    }

    private static Command Child(Command parent, string name, string description)
    {
        var command = new Command(name, description);
        parent.Subcommands.Add(command);
        return command;
    }

    private sealed class Pagination
    {
        private readonly Option<int> start;
        private readonly Option<int> limit;
        private readonly Option<bool> all;

        public Pagination(Command command)
        {
            start = Option<int>(command, "--start", "Zero-based start offset");
            limit = Option<int>(command, "--limit", "Maximum returned items (1-10000); default 100");
            limit.DefaultValueFactory = _ => 100;
            all = Option<bool>(command, "--all", "Retrieve all pages from --start, ignoring --limit");
        }

        public PageRequest Read(ParseResult parse)
        {
            var page = new PageRequest(parse.GetValue(start), parse.GetValue(limit), parse.GetValue(all));
            page.Validate();
            return page;
        }
    }

    private sealed class BodyOptions
    {
        private readonly Option<string?> body;
        private readonly Option<string?> file;
        private readonly Option<bool> stdin;

        public BodyOptions(Command command, string name = "body")
        {
            body = Option<string?>(command, "--" + name, "Inline content; prefer file/stdin for sensitive content");
            file = Option<string?>(command, "--" + name + "-file", "Read UTF-8 content from a file");
            stdin = Option<bool>(command, "--" + name + "-stdin", "Read content from standard input");
        }

        public async Task<string> ReadAsync(ParseResult parse, TextReader input, CancellationToken ct)
        {
            string? inline = parse.GetValue(body);
            string? path = parse.GetValue(file);
            bool useStdin = parse.GetValue(stdin);
            int sources = (inline is null ? 0 : 1) + (path is null ? 0 : 1) + (useStdin ? 1 : 0);
            if (sources != 1)
            {
                throw new ArgumentException("Choose exactly one content source: inline, file or stdin.");
            }

            return inline ?? (path is not null ? await File.ReadAllTextAsync(path, ct) : await input.ReadToEndAsync(ct));
        }
    }

    private Command BuildJira()
    {
        var jira = new Command("jira", "Jira Data Center (REST API v2)");
        Command myself = Child(jira, "myself", "Show authenticated user");
        Action(myself, async (parse, ct) =>
        {
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).MyselfAsync(ct), OutputKind.JiraUser, ct);
        });
        Command issue = Child(jira, "issue", "Read and manage issues");
        Command get = Child(issue, "get", "Get an issue with original fields");
        Argument<string> getKey = Argument(get, "key");
        Action(get, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(getKey), "key");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).GetIssueAsync(key, ct), OutputKind.JiraIssue, ct);
        });
        Command search = Child(issue, "search", "Search issues using JQL");
        Argument<string> jql = Argument(search, "jql");
        var searchPage = new Pagination(search);
        Action(search, async (parse, ct) =>
        {
            PageRequest page = searchPage.Read(parse);
            string query = Required(parse.GetValue(jql), "jql");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).SearchAsync(query, page, ct), OutputKind.JiraIssue, ct);
        });
        Command create = Child(issue, "create", "Create an issue; --allow-write required");
        Option<string?> project = Option<string?>(create, "--project", "Project key; defaults to JIRA_DEFAULT_PROJECT");
        Option<string> summary = Option<string>(create, "--summary", "Issue summary", true);
        Option<string> issueType = Option<string>(create, "--type", "Issue type name");
        issueType.DefaultValueFactory = _ => "Task";
        Option<string?> description = Option<string?>(create, "--description", "Original Jira wiki description");
        Option<string?> descriptionFile = Option<string?>(create, "--description-file", "UTF-8 Jira wiki description file");
        Option<string?> assignee = Option<string?>(create, "--assignee", "Data Center username (not Cloud accountId)");
        Option<string?> priority = Option<string?>(create, "--priority", "Priority name");
        Option<string[]> labels = Option<string[]>(create, "--labels", "Repeat for each label; values are not comma-split");
        labels.AllowMultipleArgumentsPerToken = false;
        labels.Arity = ArgumentArity.OneOrMore;
        Action(create, async (parse, ct) =>
        {
            RequireWrite(parse);
            string projectKey = Required(parse.GetValue(project) ?? Environment.GetEnvironmentVariable("JIRA_DEFAULT_PROJECT"), "--project / JIRA_DEFAULT_PROJECT");
            var fields = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = projectKey },
                ["summary"] = Required(parse.GetValue(summary), "--summary"),
                ["issuetype"] = new JsonObject { ["name"] = Required(parse.GetValue(issueType), "--type") }
            };
            string? inline = parse.GetValue(description);
            string? file = parse.GetValue(descriptionFile);
            if (inline is not null && file is not null)
            {
                throw new ArgumentException("Choose --description or --description-file, not both.");
            }

            if (inline is not null || file is not null)
            {
                fields["description"] = file is not null ? await File.ReadAllTextAsync(file, ct) : inline;
            }

            if (parse.GetValue(assignee) is string user)
            {
                fields["assignee"] = new JsonObject { ["name"] = Required(user, "--assignee") };
            }

            if (parse.GetValue(priority) is string priorityName)
            {
                fields["priority"] = new JsonObject { ["name"] = Required(priorityName, "--priority") };
            }

            if (parse.GetValue(labels) is { Length: > 0 } labelNames)
            {
                fields["labels"] = new JsonArray(labelNames.Select(label => JsonValue.Create(Required(label, "--labels"))).ToArray());
            }

            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).CreateAsync(fields, ct), OutputKind.JiraIssue, ct);
        });
        Command update = Child(issue, "update", "Update issue fields; --allow-write required");
        Argument<string> updateKey = Argument(update, "key");
        Option<string[]> field = Option<string[]>(update, "--field", "Repeat name=JSON; e.g. --field 'summary=\"New summary\"'");
        field.AllowMultipleArgumentsPerToken = false;
        field.Arity = ArgumentArity.OneOrMore;
        Option<string?> fieldsFile = Option<string?>(update, "--fields-file", "JSON object containing fields to update");
        Action(update, async (parse, ct) =>
        {
            RequireWrite(parse);
            string key = Required(parse.GetValue(updateKey), "key");
            JsonObject fields = JsonValues.Fields(parse.GetValue(field) ?? [], parse.GetValue(fieldsFile));
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await new JiraClient(http).UpdateAsync(key, fields, ct);
            await Writer(parse).MessageAsync($"Issue {key} updated.", ct);
        });
        Command delete = Child(issue, "delete", "Delete an issue; --allow-write --yes required");
        Argument<string> deleteKey = Argument(delete, "key");
        Action(delete, async (parse, ct) =>
        {
            RequireWrite(parse, true);
            string key = Required(parse.GetValue(deleteKey), "key");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await new JiraClient(http).DeleteAsync(key, ct);
            await Writer(parse).MessageAsync($"Issue {key} deleted.", ct);
        });
        Command subtasks = Child(issue, "subtasks", "List subtask references");
        Argument<string> parent = Argument(subtasks, "key");
        Action(subtasks, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(parent), "key");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).SubtasksAsync(key, ct), OutputKind.JiraIssue, ct);
        });
        Command transitions = Child(issue, "transitions", "List available transitions");
        Argument<string> transitionsKey = Argument(transitions, "key");
        Action(transitions, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(transitionsKey), "key");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).TransitionsAsync(key, ct), OutputKind.Transition, ct);
        });
        Command transition = Child(issue, "transition", "Transition issue by ID or unambiguous name; --allow-write required");
        Argument<string> transitionKey = Argument(transition, "key");
        Argument<string> target = Argument(transition, "target");
        Action(transition, async (parse, ct) =>
        {
            RequireWrite(parse);
            string key = Required(parse.GetValue(transitionKey), "key");
            string to = Required(parse.GetValue(target), "target");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await new JiraClient(http).TransitionAsync(key, to, ct);
            await Writer(parse).MessageAsync($"Issue {key} transitioned to {to}.", ct);
        });
        Command open = Child(issue, "open", "Open issue in OS browser; --url-only avoids requiring a browser handler");
        Argument<string> openKey = Argument(open, "key");
        Option<bool> urlOnly = Option<bool>(open, "--url-only", "Print URL without opening a browser");
        Action(open, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(openKey), "key");
            using AtlassianHttpClient http = Http(parse, "JIRA");
            Uri uri = new JiraClient(http).BrowseUri(key);
            if (!parse.GetValue(urlOnly))
            {
                OpenBrowser(uri);
            }

            await Writer(parse).MessageAsync(uri.AbsoluteUri, ct);
        });
        Command comment = Child(issue, "comment", "Read or add issue comments");
        Command commentList = Child(comment, "list", "List comments with pagination");
        Argument<string> commentKey = Argument(commentList, "key");
        var commentPage = new Pagination(commentList);
        Action(commentList, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(commentKey), "key");
            PageRequest page = commentPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).CommentsAsync(key, page, ct), OutputKind.JiraComment, ct);
        });
        Command commentAdd = Child(comment, "add", "Add comment; --allow-write required");
        Argument<string> addKey = Argument(commentAdd, "key");
        var commentBody = new BodyOptions(commentAdd);
        Action(commentAdd, async (parse, ct) =>
        {
            RequireWrite(parse);
            string key = Required(parse.GetValue(addKey), "key");
            string body = await commentBody.ReadAsync(parse, input, ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new ArgumentException("Comment body cannot be empty.");
            }

            using AtlassianHttpClient http = Http(parse, "JIRA");
            await Writer(parse).WriteAsync(await new JiraClient(http).AddCommentAsync(key, body, ct), OutputKind.JiraComment, ct);
        });
        return jira;
    }

    private Command BuildConfluence()
    {
        var confluence = new Command("confluence", "Confluence Data Center (REST API v1)");
        Command space = Child(confluence, "space", "Read spaces");
        Command spaces = Child(space, "list", "List visible spaces and names");
        var spacePage = new Pagination(spaces);
        Action(spaces, async (parse, ct) =>
        {
            PageRequest page = spacePage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).SpacesAsync(page, ct), OutputKind.Space, ct);
        });
        Command spaceGet = Child(space, "get", "Read a space by key");
        Argument<string> spaceKey = Argument(spaceGet, "key");
        Action(spaceGet, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(spaceKey), "key");
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).SpaceAsync(key, ct), OutputKind.Space, ct);
        });
        Command page = Child(confluence, "page", "Read or write pages");
        Command pageGet = Child(page, "get", "Get content by ID (page, blogpost or comment)");
        Argument<string> pageId = Argument(pageGet, "id");
        Action(pageGet, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(pageId), "id");
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).PageAsync(id, ct), OutputKind.ConfluenceContent, ct);
        });
        Command pageList = Child(page, "list", "List pages or blogposts in a space");
        Option<string> listSpace = Option<string>(pageList, "--space", "Space key", true);
        Option<string> type = Option<string>(pageList, "--type", "page or blogpost");
        type.DefaultValueFactory = _ => "page";
        Option<bool> includeBody = Option<bool>(pageList, "--include-body", "Include full storage body in each result");
        var contentPage = new Pagination(pageList);
        Action(pageList, async (parse, ct) =>
        {
            string key = Required(parse.GetValue(listSpace), "--space");
            string contentType = Required(parse.GetValue(type), "--type");
            if (contentType is not ("page" or "blogpost"))
            {
                throw new ArgumentException("--type must be page or blogpost.");
            }

            PageRequest pagination = contentPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).PagesAsync(key, contentType, parse.GetValue(includeBody), pagination, ct), OutputKind.ConfluenceContent, ct);
        });
        Command search = Child(confluence, "search", "Search content using CQL");
        Argument<string> cql = Argument(search, "cql");
        Option<bool> searchBody = Option<bool>(search, "--include-body", "Include full storage body");
        var searchPage = new Pagination(search);
        Action(search, async (parse, ct) =>
        {
            string query = Required(parse.GetValue(cql), "cql");
            PageRequest pagination = searchPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).SearchAsync(query, parse.GetValue(searchBody), pagination, ct), OutputKind.ConfluenceContent, ct);
        });
        Command create = Child(page, "create", "Create a page; --allow-write required");
        Option<string> createSpace = Option<string>(create, "--space", "Space key", true);
        Option<string> title = Option<string>(create, "--title", "Page title", true);
        Option<string?> parent = Option<string?>(create, "--parent", "Parent page ID");
        var createBody = new BodyOptions(create);
        Action(create, async (parse, ct) =>
        {
            RequireWrite(parse);
            string key = Required(parse.GetValue(createSpace), "--space");
            string pageTitle = Required(parse.GetValue(title), "--title");
            string? parentId = parse.GetValue(parent);
            if (parentId is not null)
            {
                Required(parentId, "--parent");
            }

            string body = await createBody.ReadAsync(parse, input, ct);
            StorageMarkdownConverter.Convert(body);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).CreatePageAsync(key, pageTitle, body, parentId, ct), OutputKind.ConfluenceContent, ct);
        });
        Command update = Child(page, "update", "Replace page title/body; --allow-write required");
        Argument<string> updateId = Argument(update, "id");
        Option<string> updateTitle = Option<string>(update, "--title", "Page title", true);
        Option<int?> version = Option<int?>(update, "--version", "Next version; default current + 1");
        Option<string?> message = Option<string?>(update, "--message", "Version message");
        var updateBody = new BodyOptions(update);
        Action(update, async (parse, ct) =>
        {
            RequireWrite(parse);
            string id = Required(parse.GetValue(updateId), "id");
            string pageTitle = Required(parse.GetValue(updateTitle), "--title");
            int? nextVersion = parse.GetValue(version);
            if (nextVersion is < 1)
            {
                throw new ArgumentException("--version must be positive.");
            }

            string body = await updateBody.ReadAsync(parse, input, ct);
            StorageMarkdownConverter.Convert(body);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).UpdatePageAsync(id, pageTitle, body, nextVersion, parse.GetValue(message), ct), OutputKind.ConfluenceContent, ct);
        });
        Command comments = Child(confluence, "comment", "Read footer/inline comments and replies");
        Command commentList = Child(comments, "list", "List comments, including replies by default");
        Argument<string> commentId = Argument(commentList, "id");
        Option<string> location = Option<string>(commentList, "--location", "all, footer, inline or resolved");
        location.DefaultValueFactory = _ => "all";
        Option<bool> rootOnly = Option<bool>(commentList, "--root-only", "Exclude replies");
        var commentPage = new Pagination(commentList);
        Action(commentList, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(commentId), "id");
            string where = Required(parse.GetValue(location), "--location");
            if (where is not ("all" or "footer" or "inline" or "resolved"))
            {
                throw new ArgumentException("--location must be all, footer, inline or resolved.");
            }

            PageRequest pagination = commentPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).CommentsAsync(id, where, !parse.GetValue(rootOnly), pagination, ct), OutputKind.ConfluenceContent, ct);
        });
        Command label = Child(confluence, "label", "Read or change labels");
        Command labelList = Child(label, "list", "List labels");
        Argument<string> labelId = Argument(labelList, "id");
        var labelPage = new Pagination(labelList);
        Action(labelList, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(labelId), "id");
            PageRequest pagination = labelPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).LabelsAsync(id, pagination, ct), OutputKind.Label, ct);
        });
        Command labelAdd = Child(label, "add", "Add labels; --allow-write required");
        Argument<string> addId = Argument(labelAdd, "id");
        var names = new Argument<string[]>("labels") { Arity = ArgumentArity.OneOrMore };
        labelAdd.Arguments.Add(names);
        Action(labelAdd, async (parse, ct) =>
        {
            RequireWrite(parse);
            string id = Required(parse.GetValue(addId), "id");
            string[] values = (parse.GetValue(names) ?? []).Select(name => Required(name, "label")).ToArray();
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await new ConfluenceClient(http).AddLabelsAsync(id, values, ct);
            await Writer(parse).MessageAsync($"Labels added to {id}.", ct);
        });
        Command labelRemove = Child(label, "remove", "Remove one label; --allow-write required");
        Argument<string> removeId = Argument(labelRemove, "id");
        Argument<string> removeName = Argument(labelRemove, "label");
        Action(labelRemove, async (parse, ct) =>
        {
            RequireWrite(parse);
            string id = Required(parse.GetValue(removeId), "id");
            string name = Required(parse.GetValue(removeName), "label");
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await new ConfluenceClient(http).RemoveLabelAsync(id, name, ct);
            await Writer(parse).MessageAsync($"Label {name} removed from {id}.", ct);
        });
        Command restriction = Child(confluence, "restriction", "Read restrictions; writes intentionally excluded");
        Command restrictionList = Child(restriction, "list", "Read all user/group restrictions for read/update operations");
        Argument<string> restrictedId = Argument(restrictionList, "id");
        Action(restrictionList, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(restrictedId), "id");
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).RestrictionsAsync(id, ct), OutputKind.Restriction, ct);
        });
        Command attachment = Child(confluence, "attachment", "List or download attachments");
        Command attachmentList = Child(attachment, "list", "List attachment IDs and metadata");
        Argument<string> attachmentId = Argument(attachmentList, "id");
        var attachmentPage = new Pagination(attachmentList);
        Action(attachmentList, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(attachmentId), "id");
            PageRequest pagination = attachmentPage.Read(parse);
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await Writer(parse).WriteAsync(await new ConfluenceClient(http).AttachmentsAsync(id, pagination, ct), OutputKind.ConfluenceContent, ct);
        });
        Command download = Child(attachment, "download", "Download attachment ID to an explicit file; never overwrite");
        Argument<string> downloadId = Argument(download, "id");
        Option<string> destination = Option<string>(download, "--file", "Destination file path", true);
        Action(download, async (parse, ct) =>
        {
            string id = Required(parse.GetValue(downloadId), "id");
            string path = Required(parse.GetValue(destination), "--file");
            using AtlassianHttpClient http = Http(parse, "CONFLUENCE");
            await new ConfluenceClient(http).DownloadAttachmentAsync(id, path, ct);
            await Writer(parse).MessageAsync($"Attachment {id} saved to {Path.GetFullPath(path)}.", ct);
        });
        return confluence;
    }

    private static void OpenBrowser(Uri uri)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        start.FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open";
        start.ArgumentList.Add(uri.AbsoluteUri);
        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("The browser handler could not be started.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException($"Browser handler exited with {process.ExitCode}. Use --url-only.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new IOException("No working OS browser handler found. Use --url-only.", exception);
        }
    }
}
