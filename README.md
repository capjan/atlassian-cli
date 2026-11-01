# atlassian-cli

**Read Jira issues and Confluence spaces, pages and comments from your terminal.**
A portable CLI for Atlassian **Data Center**, with remote writes disabled by default.
No .NET installation or package manager is needed to run the native executable.

[Quickstart](#quickstart) · [Common tasks](#common-tasks) · [All commands](docs/COMMANDS.md) · [Troubleshooting](#troubleshooting)

## Quickstart

### 1. Install

Each command below does one visible job. **No downloaded shell scripts are
executed**, and no `sudo`, GitHub token or package manager is needed.

**First installation:** download directly into your personal command directory
with the name `atlassian-cli`. `mkdir -p` creates the directory if needed,
`curl -fL` follows GitHub's redirects and reports HTTP errors, and `chmod`
makes the downloaded file executable.

Use **only the block for your machine**. These blocks overwrite
`~/.local/bin/atlassian-cli` if it exists; for an existing installation, use
the [update instructions](#updates) instead. The `&&` separators stop the block
if directory creation or download fails.

<details>
<summary>macOS — Apple Silicon</summary>

```bash
mkdir -p "$HOME/.local/bin" &&
curl -fL -o "$HOME/.local/bin/atlassian-cli" \
  https://github.com/capjan/atlassian-cli/releases/latest/download/atlassian-cli-osx-arm64 &&
chmod +x "$HOME/.local/bin/atlassian-cli"
```

</details>

<details>
<summary>Linux — Intel/AMD 64-bit</summary>

```bash
mkdir -p "$HOME/.local/bin" &&
curl -fL -o "$HOME/.local/bin/atlassian-cli" \
  https://github.com/capjan/atlassian-cli/releases/latest/download/atlassian-cli-linux-x64 &&
chmod +x "$HOME/.local/bin/atlassian-cli"
```

</details>

<details>
<summary>Linux — ARM64</summary>

```bash
mkdir -p "$HOME/.local/bin" &&
curl -fL -o "$HOME/.local/bin/atlassian-cli" \
  https://github.com/capjan/atlassian-cli/releases/latest/download/atlassian-cli-linux-arm64 &&
chmod +x "$HOME/.local/bin/atlassian-cli"
```

</details>

Alternatively, download that same file in your browser from
[Releases](https://github.com/capjan/atlassian-cli/releases), then open a terminal
in its download directory. Do not continue if the download failed.

These commands download over HTTPS without a separate integrity check.
For a fixed release,
replace `/latest/download/` in the download URL with `/download/v0.1.0/`, using
the tag you want.

**Make it available in this terminal.** Add your personal command directory to
the shell's search path, then check the installed command:

```bash
export PATH="$HOME/.local/bin:$PATH"
atlassian-cli version
```

For future terminals, add the `export PATH=...` line **once** to your shell
configuration (`~/.zshrc` for Zsh, `~/.bashrc` for interactive Bash on Linux,
or the startup file used by your shell). These commands do not edit that file.

Release archives (`.tar.gz`) are also available and include documentation,
dependency information and license notices. The same license notices are
available as separate release downloads; keep them when redistributing the
binary. See [Compatibility](#compatibility) for system-library requirements.

<details>
<summary>Updates</summary>

### Updates

Download to a separate file before replacing a working installation: an
interrupted direct download could damage the existing binary. Example for macOS
ARM64; substitute the Linux asset name in the URL if applicable:

```bash
curl -fL -o "$HOME/.local/bin/atlassian-cli.download" \
  https://github.com/capjan/atlassian-cli/releases/latest/download/atlassian-cli-osx-arm64
```

Only after a successful download, make the new file executable, check it runs,
then replace the installed binary:

```bash
chmod +x "$HOME/.local/bin/atlassian-cli.download" &&
"$HOME/.local/bin/atlassian-cli.download" version &&
mv -i "$HOME/.local/bin/atlassian-cli.download" "$HOME/.local/bin/atlassian-cli"
```

`mv -i` asks before replacing the existing file. No shell startup files are changed.

</details>

### 2. Connect the service you need

Use a **Data Center personal access token (PAT)** from your account's personal
access token settings. Configure only Confluence, only Jira, or both.

**Confluence**

```bash
export CONFLUENCE_URL=https://confluence.example.com/confluence
export CONFLUENCE_PERSONAL_TOKEN='your-pat'
```

**Jira**

```bash
export JIRA_URL=https://jira.example.com
export JIRA_PERSONAL_TOKEN='your-pat'
```

Replace the placeholders with your instance URL and token. Include the context
path, such as `/confluence`, if your installation uses one. URLs must use HTTPS.
These shell examples work in Bash and Zsh and apply to the current terminal.

> Treat PATs as secrets. Supply real tokens through your usual secret management;
> do not save them in source code, shared configuration or shell history.
> For strictly read-only access, also restrict the account's permissions.

### 3. Make your first request

For Confluence, list the spaces you can access:

```bash
atlassian-cli confluence space list
```

The result shows space **keys and names**. Use a key from that list in the next
section.

For Jira, check your authenticated account:

```bash
atlassian-cli jira myself
```

If either request fails, start with [Troubleshooting](#troubleshooting).

## Common tasks

### Browse Confluence: space → page → comments

Replace `DOCS` with a space key from `space list`, then use a page ID from
`page list`:

```bash
atlassian-cli confluence page list --space DOCS
atlassian-cli confluence page get 12345 -o markdown
atlassian-cli confluence comment list 12345 --all -o json
```

Comments include **footer, inline and resolved comments plus replies** by default.
Use `--location inline` to filter inline comments or `--root-only` to exclude
replies. JSON retains available inline-resolution and parent/container metadata.

Find content or retrieve all page bodies in a space:

```bash
atlassian-cli confluence search 'space = DOCS AND type = page' --all
atlassian-cli confluence page list --space DOCS --include-body --all -o json
```

Also available: blogposts, labels, restrictions and attachment downloads.
`conf` is a shorter alias for `confluence`.

### Work with Jira issues

Replace `PROJ` and `PROJ-123` with your project key and issue key:

```bash
atlassian-cli jira issue search 'project = PROJ'
atlassian-cli jira issue get PROJ-123 -o json
atlassian-cli jira issue comment list PROJ-123 --all
```

Also available: subtasks, transitions and browser opening. Use
`jira issue open PROJ-123 --url-only` to get the link without a browser handler.

See the [complete command reference](docs/COMMANDS.md), or append `--help` to any
command for its arguments and options.

## Output and complete results

| Option | Best for |
|---|---|
| `-o table` | Scanning results in the terminal; default |
| `-o json` | Scripts and original resource data, including custom fields |
| `-o markdown` | Reading content and sharing human-readable output |

Lists return **at most 100 items by default**. Use `--all` for every result, or
`--limit 1..10000` and `--start` for a bounded request. Output reports whether more
results exist; JSON lists have this shape:

```json
{
  "results": [],
  "start": 0,
  "limit": 100,
  "size": 0,
  "total": null,
  "hasMore": false
}
```

`size` is the returned count; `total` is included when available. `limit` records
the configured bound and is ignored with `--all`. Restriction users and groups are
aggregated separately.

**Use JSON when fidelity matters.** Confluence Markdown supports common XHTML,
tables, nested lists and selected macros. Unsupported elements/macros and merged
tables are visibly preserved as XML. Jira wiki headings are converted; other wiki
syntax remains escaped and readable. Neither view claims full rendering fidelity.

`--all` buffers results in memory. Concurrent content changes can affect
offset-based pagination; the API does not provide snapshot isolation.

## Write only when you intend to

Every remote mutation requires **`--allow-write`**. Jira issue deletion also
requires **`--yes`**. These are CLI guards, not account permission boundaries.

```bash
atlassian-cli jira issue create --project PROJ --summary 'New task' \
  --assignee alice --allow-write
atlassian-cli jira issue update PROJ-123 \
  --field 'summary="Updated"' --field 'labels=["a","b"]' --allow-write
atlassian-cli confluence page create --space DOCS --title 'New page' \
  --body-file page.xml --allow-write
```

- Jira assignees are **DC usernames**, not Cloud account IDs.
- `--field` values are strict JSON, without comma splitting. Use `--fields-file`
  for a JSON object of arbitrary issue fields.
- Confluence bodies are **storage-format XML**, not Markdown. Use a file or stdin
  instead of inline arguments for sensitive or lengthy content.
- Page updates replace the full title/body and use the retrieved version + 1
  unless `--version` is explicit. Conflicts fail; writes are never retried
  automatically.
- **Restriction writes are intentionally absent.** The documented DC API replaces
  collections without an atomic additive operation.

> A timeout or output failure after a write does not prove the remote operation
> failed. Inspect the resource before retrying.

## Troubleshooting

| Symptom | Check |
|---|---|
| Installation download returns HTTP 404 | A public release must exist; private or unpublished assets cannot be downloaded anonymously |
| `atlassian-cli` is not found | Add `~/.local/bin` to `PATH`, or run `~/.local/bin/atlassian-cli` directly |
| Authentication or permission failure | PAT validity, account permissions and the variables for the selected service |
| Redirect or unexpected HTML instead of JSON | Use the final HTTPS instance URL with its correct context path, not a login page |
| TLS certificate failure | Trust your internal CA through the OS; certificate validation remains enabled |
| Missing results | Check permissions and `hasMore`; use `--all` to retrieve all pages |
| Request times out | Increase `--timeout` (1–600 seconds; default 30) |
| Attachment download fails | Use an attachment ID; its destination directory must exist and the file must not already exist |
| Browser cannot open | Use `jira issue open KEY --url-only` |

Redirects and links outside the configured origin/context path are rejected.
JSON responses are limited to **32 MiB per request**. Downloads stream through a
temporary file and never overwrite existing files.

Errors go to **stderr**; results go to **stdout**. For scripts:

| Exit code | Meaning |
|---|---|
| `0` | Success |
| `2` | Invalid input or configuration |
| `3` | Authorization failure |
| `4` | Conflict |
| `5` | API, network or timeout failure |
| `6` | Invalid response or content |
| `7` | File or output failure |
| `130` | Cancellation |

GET requests retry at most twice for HTTP 429/502/503/504. Retry-After is honored
up to 30 seconds within the request timeout. Writes are never automatically retried.

## Compatibility

Target installations: **Jira Data Center 10.3.26** and **Confluence Data Center
10.2.19**. Confluence contracts were checked against its official 10.2.19 OpenAPI
specification; Jira uses DC REST v2 conventions. **Live compatibility with these
installations has not yet been verified.**

| Platform | Requirements |
|---|---|
| macOS ARM64 | Apple Silicon, macOS 12+; Apple system frameworks/libraries |
| Linux x64 / ARM64 | glibc-based Linux; built on Ubuntu 22.04; system CA certificates, OpenSSL and ICU supported by .NET 10 |

Native AOT removes the need for an installed .NET runtime; it does **not** make the
binary fully static or OS-independent. These Linux binaries are not Alpine/musl
builds. The artifact's `dependencies.txt` lists link-time dependencies; dynamically
loaded Linux OpenSSL/ICU libraries may not appear there.

Browser opening optionally uses macOS `open` or Linux `xdg-open`. All other CLI
operations require no separately installed language runtime or CLI framework.
**System.CommandLine 2.0.14** is the only additional application library and is
compiled into the executable.

Not included: Atlassian Cloud, OAuth, TUI, MCP, synchronization services or plugins.

## Development

Only needed if you want to build or change the CLI.

Use the **official Microsoft .NET SDK 10.0.403 / C# 14**. Source-rebuilt SDKs can
introduce distribution-specific native link dependencies. Native build prerequisites
are Xcode command-line tools on macOS, or Clang and zlib development files on Linux.
NuGet restore obtains System.CommandLine and Microsoft's AOT compiler/linker packs.

```bash
dotnet restore AtlassianCli.slnx
dotnet run --project tests/AtlassianCli.Tests -c Release --no-restore
dotnet format AtlassianCli.slnx --verify-no-changes --no-restore
dotnet publish src/AtlassianCli/AtlassianCli.csproj -c Release \
  -r osx-arm64 -o artifacts/osx-arm64
```

Use `linux-x64` or `linux-arm64` for Linux builds on the matching native runners.
Native AOT must be built on the target OS. CI builds and tests macOS ARM64, Linux
x64 and Linux ARM64, including native contract tests, package policy, formatting
and dependency checks.

Tests use a dependency-free console runner and fake `HttpMessageHandler`, not
live credentials or external test packages.

## License

[MIT](LICENSE). Reference and dependency attribution:
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
