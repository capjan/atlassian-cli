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
| `jira project list` | Pagination | No |
| `jira sprint list` | Required `--project` (key or ID), optional `--state active|future|closed` (comma-separated), pagination | No |
| `jira sprint issues` | Required `--sprint` (positive numeric sprint ID), repeat `--status NAME/+NAME/-NAME` (comma-separated), `--include-body`, pagination | No |
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

Sprint lists require Jira Software's Agile REST API. The CLI retrieves all visible
Scrum boards associated with `--project` and their sprints, deduplicates by sprint
ID, sorts by descending ID (highest ID first, not by start date), then applies
`--start`/`--limit`/`--all` to the combined
list. All states are included unless `--state` is supplied. Boards may span
multiple projects: this lists board sprints, not just sprints containing issues
from the specified project. No visible Scrum boards or no matching sprints yields
an empty list; an API failure aborts the command rather than returning partial
results. Even a bounded list requires fetching all matching board sprints.
Sprint START and END columns use `dd.MM.yy HH:mm` in table and Markdown output,
keeping the timestamp's source time zone without showing seconds or the offset.
Missing dates remain blank. JSON preserves the original timestamps.

`jira sprint issues --sprint ID` retrieves visible issues directly from the sprint,
without a project or board filter, in Jira's returned order. It requires Jira
Software. By default only summary, status and assignee fields are requested,
alongside the issue identity, and no descriptions are displayed. `--include-body`
requests all issue fields and displays descriptions below the table or Markdown
list. JSON preserves the returned fields: overview fields by default, all fields
with `--include-body`. `jira issue get` and `jira issue search` are unchanged. The default limit
is 100; use `--all` for all visible issues. An empty sprint returns an empty list;
API errors abort the command rather than returning partial results.

Sprint issue status filters use Jira status names, not sprint states. Repeat
`--status 'In Progress'` (or `'+In Progress'`) to include a status or
`--status='-Done'` to exclude one. Combine entries with commas, for example
`--status='-Done,+Ready,+ReadyForTest'`; the `+` is optional.
Multiple inclusions match any included status; exclusions remove any excluded
status. With only exclusions, all other statuses remain. Commas separate names
and cannot be part of a status name; empty entries are invalid. Names are trimmed and duplicate names
are ignored case-insensitively. Including and excluding the same name is an
input error. Filtering is server-side JQL, before pagination; unknown names are
reported by Jira as API errors. Without filters, behavior is unchanged.

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
