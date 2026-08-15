# Provider: Azure DevOps

Terminology: PR = pull request, identified by PR number. Review comments live in **threads**.

## Tool map

Use Azure DevOps MCP tools. Do not write ad hoc shell, Python, `az repos`, `curl`, or REST scripts for any step covered below.

| Step | Tool |
|---|---|
| Project / repo discovery | `mcp_azuredevops_m_core_list_projects`, `mcp_azuredevops_m_repo_repository` |
| Read PR metadata | `mcp_azuredevops_m_repo_pull_request` |
| List all review threads (inventory) | `mcp_azuredevops_m_repo_pull_request_thread` |
| Read one thread (Phase 2) | `mcp_azuredevops_m_repo_pull_request_thread` (that thread only) |
| Reply to a thread | `mcp_azuredevops_m_repo_pull_request_thread_write` |
| Update PR metadata (only if required) | `mcp_azuredevops_m_repo_pull_request_write` |
| Related commits (review context only) | `mcp_azuredevops_m_repo_search_commits` |

Do not substitute:
- `mcp_azuredevops_m_wit_*` (work items) for PR review comments
- `mcp_azuredevops_m_wiki*` for PR review comments
- `mcp_azuredevops_m_repo_file` to emulate thread reads or replies

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

Azure DevOps thread status is set via `mcp_azuredevops_m_repo_pull_request_thread_write`
(thread status `fixed` / `closed`). Only set status when the user has approved the reply.

## Commit link format

`https://dev.azure.com/<org>/<project>/_git/<repo>/commit/<sha>`
