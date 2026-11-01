# Command reference

Run any command with `--help` for options.

Global options: `--output`/`-o` (`table`, `json`, `markdown`), `--timeout` (seconds),
`--allow-write`, `--yes`. They work before or after subcommands.

Paginated commands: `--start` (default 0), `--limit` (default 100), `--all`
(ignores limit, starts at the specified offset). Invalid bounds fail.

## Jira

| Command | Arguments/options | Remote write |
|---|---|---|
| `jira myself` | None | No |
| `jira issue get` | `KEY` | No |
| `jira issue search` | `JQL`, pagination | No |
| `jira issue create` | `--summary`, `--project` or `JIRA_DEFAULT_PROJECT`, `--type` (Task), `--description` or `--description-file`, `--assignee` (username), `--priority`, repeat `--labels` | Yes |
| `jira issue update` | `KEY`, repeat `--field name=JSON`, `--fields-file` (fields object) | Yes |
| `jira issue delete` | `KEY`; requires `--yes` as well | Yes |
| `jira issue subtasks` | `KEY`; API subtask references, not separately fetched full issues | No |
| `jira issue transitions` | `KEY` | No |
| `jira issue transition` | `KEY TARGET`; TARGET is ID or unambiguous name/status | Yes |
| `jira issue open` | `KEY`, `--url-only` | No |
| `jira issue comment list` | `KEY`, pagination | No |
| `jira issue comment add` | `KEY`, exactly one of `--body`, `--body-file`, `--body-stdin` | Yes |

Create responses may only contain ID/key/self; the CLI preserves that response
instead of fetching an issue after creation and possibly obscuring a successful
mutation with a failed subsequent read.

Field strings must be JSON-quoted: `--field 'summary="New"'`. Arrays/objects/numbers,
booleans and null keep their types. Duplicate field names and invalid JSON fail.

## Confluence

`confluence` has alias `conf`.

| Command | Arguments/options | Remote write |
|---|---|---|
| `confluence space list` | Pagination | No |
| `confluence space get` | `KEY` | No |
| `confluence page get` | Content `ID`; also reads blogposts/comments | No |
| `confluence page list` | `--space`, `--type page|blogpost`, `--include-body`, pagination | No |
| `confluence search` | `CQL`, `--include-body`, pagination | No |
| `confluence page create` | `--space`, `--title`, optional `--parent`, body source | Yes |
| `confluence page update` | `ID`, `--title`, body source, optional next `--version`, `--message` | Yes |
| `confluence comment list` | Content `ID`, `--location all|footer|inline|resolved`, `--root-only`, pagination | No |
| `confluence label list` | Content `ID`, pagination | No |
| `confluence label add` | Content `ID`, one or more space-separated label names | Yes |
| `confluence label remove` | Content `ID`, one label name (including `/`) | Yes |
| `confluence restriction list` | Content `ID`; all read/update user/group entries | No |
| `confluence attachment list` | Content `ID`, pagination | No |
| `confluence attachment download` | Attachment `ID`, `--file DESTINATION`; never overwrite | No |

Body source is exactly one of `--body`, `--body-file`, `--body-stdin`, containing
Confluence storage XML. No Markdown-to-storage conversion. Invalid XML fails before
network requests. Use numeric content IDs; titles, URLs and `SPACE:Title` references
are not resolved by this implementation.

Attachment IDs come from `attachment list`; the CLI fetches the matching attachment
and uses its validated download link. The destination's parent directory must exist.

All remote write commands require `--allow-write`. Restriction add/remove,
Confluence page deletion and Confluence comment writes are intentionally absent.
