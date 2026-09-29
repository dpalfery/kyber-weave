# Provider: Azure DevOps

Terminology: PR = pull request, identified by PR number. Review comments live in **threads**.

## Tool map

Use Azure DevOps MCP tools (`microsoft/azure-devops-mcp`). Do not write ad hoc shell, Python,
`az repos`, `curl`, or REST scripts for any step covered below. A harness may prefix these tool
names with the name the host gave the server; match on the bare name.

| Step | Tool |
|---|---|
| Project / repo discovery | `core_list_projects`, `repo_repository` |
| Read PR metadata | `repo_pull_request` |
| List all review threads (inventory) | `repo_pull_request_thread` |
| Read one thread (Phase 2) | `repo_pull_request_thread` (that thread only) |
| Reply to a thread | `repo_pull_request_thread_write` |
| Update PR metadata (only if required) | `repo_pull_request_write` |
| Related commits (review context only) | `repo_search_commits` |

Do not substitute:
- `wit_*` (work items) for PR review comments
- `wiki*` for PR review comments
- `repo_file` to emulate thread reads or replies

If required data cannot be obtained from the named tool, state exactly what is missing and stop.

## Thread identity

- Inventory `<id>` = the numeric Azure DevOps thread ID.
- Ordering rule "lowest numeric thread ID" applies directly.

## Automated/system threads

Azure DevOps injects informational threads into the thread list, e.g. branch reference updates
(`The reference refs/heads/<branch> was updated.`). Classify these as `automated/system`.
Prefer thread metadata for classification; fall back to the obvious notification text only for
this classification step.

## Resolving

Azure DevOps thread status is set via `repo_pull_request_thread_write`
(thread status `fixed` / `closed`). Only set status when the user has approved the reply.

## Commit link format

`https://dev.azure.com/<org>/<project>/_git/<repo>/commit/<sha>`
