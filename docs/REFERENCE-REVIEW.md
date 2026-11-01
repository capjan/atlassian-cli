# Complete reference review

Reference: [putcho01/atlassian-cli](https://github.com/putcho01/atlassian-cli),
fixed commit [`a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67`](https://github.com/putcho01/atlassian-cli/commit/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67).
Review date: 2026-11-01. Target: Jira DC 10.3.26 / Confluence DC 10.2.19.

## Scope and method

Read every Go source/test file, dependency manifests, documentation and all
CI/release configuration at the fixed commit. The logo is a non-executable asset
and was not assessed for visual quality. No Go build/tests, dependency vulnerability
scanner, or live Atlassian requests were run. This is a correctness/design review,
not a security audit. Tests and documentation are evidence of intent, not proof
of compatibility with a real instance.

Useful architectural features: thin command handlers, separate HTTP/product clients,
context-aware requests, path/query escaping in most API calls, reusable formatter,
mock HTTP tests, explicit page version updates, and portable release targets.
MIT licensing permits use as a reference with attribution.

## Findings

Links below point to the fixed reference revision.

| Priority | Finding and evidence | Consequence / new implementation |
|---|---|---|
| High | [Confluence restrictions](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/confluence/client.go#L55): GET `/restriction` decodes directly into `[]Restriction`. DC 10.2.19 documents reads at `/restriction/byOperation*`, with nested paginated subjects. | Wrong endpoint/shape. New implementation reads both operations and independently pages users/groups. |
| High | [Restriction writes](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/confluence/client.go#L61): "Add" sends PUT replacing the corresponding collection; payload has single user/group objects, not arrays. | Can fail validation or replace existing subjects if reshaped without changing semantics. User explicitly elected to omit all restriction writes rather than introduce non-atomic read-modify-write. |
| High | [DC identities](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/jira/client.go#L136) and [Confluence](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/confluence/client.go#L156) use Cloud `accountId`; user DTOs omit DC names/keys. | DC assignment/restrictions are not correctly modeled. Use DC usernames for assignment, retain raw returned identities. |
| High | [Jira comments](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/jira/client.go#L103), [labels/attachments](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/confluence/client.go#L35) discard pagination. Search has only `maxResults`, no start offset. | Silent first-page truncation. New lists report bounds/hasMore and support full traversal. |
| Medium | [Issue field option](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/cmd/jira_issue_update.go#L22) uses Cobra StringSlice, splitting JSON at commas; invalid JSON silently becomes a string. | Arrays/objects can be corrupted or unexpected types sent. Repeat strict `name=JSON` values or use a fields file; no comma splitting. |
| Medium | [TUI](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/tui/issue_list.go#L101) accesses `issues[cursor]` on open/detail even when search returns no issues. | Panic on empty interactive search. TUI excluded from the new CLI. |
| Medium | [Formatter](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/formatter/formatter.go) ignores many writer errors, including page table flush. Unknown formats fall back to table. Restrictions always print JSON despite global format. | Pipelines can appear successful with lost output. New writer propagates errors and rejects invalid formats. |
| Medium | [Markdown conversion](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/htmlconv/converter.go) flattens nested lists, uses fixed code fences, does not escape table pipes/newlines, and treats Jira wiki descriptions as HTML. | Corrupted presentation/lost structure. Separate conservative Jira-wiki view from storage XML; preserve unsupported XML visibly and preserve raw JSON. |
| Medium | [CI](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/.github/workflows/ci.yml) pipes tests through tparse/tee without explicit Bash/pipefail. | Final pipeline command can mask test failure. New test step runs directly; pipelines explicitly use pipefail. |
| Medium | [Transitions](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/jira/client.go#L73) selects the first name/status match. | Ambiguous workflows choose a transition implicitly. New CLI accepts IDs and rejects ambiguous names. |
| Medium | [Error parsing](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/internal/httpclient/client.go#L118) ignores JSON parse errors and does not capture Confluence's single `message` field. There is no read retry handling. | Sparse diagnostics and brittle rate-limited reads. New client reports status/message, distinguishes non-JSON errors and bounds GET retries. |
| Low | [Browser opening](https://github.com/putcho01/atlassian-cli/blob/a0a55bdc6d488e0dbaaf9b34caa08c0d01c39e67/cmd/jira_issue_open.go) concatenates an unescaped key and reports successful process start, not handler completion. Requires PAT even for URL construction. | Can produce invalid URLs/report handler success too early. New escaped URL-only mode and checked OS-handler exit; service configuration still required. |
| Low | Invalid restriction types fall into the group branch; labels are comma-split without normalization; several create commands accept unused positionals. | Typo/invalid-input handling is inconsistent. Restriction writes omitted, normal CLI parsing/validation explicit. |

## Coverage and maintainability observations

- HTTP tests cover auth selection and error string formatting, not the full request/
  response lifecycle, redirects, timeout, malformed content or response limits.
- Product tests are mostly helper-level and small successful/mock responses.
  Restriction tests validate the implementation's own incorrect payload assumptions,
  including treating unknown subject types as groups. Many API methods lack tests.
- Formatter tests check substrings, not complete schemas/escaping or failing writers.
- MCP lacks dedicated tests; mutating tools have no separate approval boundary.
  Unknown tool groups are silently ignored. It modifies storage bodies to Markdown
  while retaining storage representation metadata. TUI/MCP are excluded.
- API request/response shapes are partly Cloud-oriented despite self-hosted claims.
  Older Jira Cloud search endpoints also need separate treatment; Cloud excluded.
- Spaces, Confluence CQL search, page listing and Confluence comments are absent.
  Attachment methods exist internally but have no public CLI commands.
- Dependencies are reasonable for Cobra/TUI/MCP, but do not match the requested
  minimal application dependency policy. New CLI only uses System.CommandLine.
- README says Go 1.21+, while go.mod requires 1.25.0. Main's documented token names
  are not what config reads. Documentation references an absent release workflow,
  and claims all output formats although restrictions force JSON.
- Release config builds multiple targets, but CI does not demonstrate their runtime
  behavior or Native/library dependencies.

## Migration decisions

Independent C# 14 implementation, not copied Go source or copied logo. Only Jira/
Confluence CLI features are carried over, with restriction writes explicitly removed.
No Cloud/TUI/MCP. Original single-resource JSON uses `JsonElement`, preserving custom/
version-dependent fields without reflection or a lossy incomplete DTO projection.
Known list/message output uses source-generated serialization. HTTP requests use
explicit `JsonObject` payloads, including arbitrary user-supplied issue fields.

One explicit System.CommandLine command graph is kept in a single CLI file rather
than introducing a generic command framework. Product clients share the HTTP client
and page options, not an unnecessary common product interface.

Target Confluence source:
[official 10.2.19 REST reference](https://developer.atlassian.com/server/confluence/rest/v10219/)
and [OpenAPI JSON](https://developer.atlassian.com/server/confluence/10.2.19.swagger.v3.json).
The review and mocked contracts do not replace live testing against the specified
Jira and Confluence installations.
