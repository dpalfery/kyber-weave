# Provider: GitHub

Terminology: PR = pull request, identified by PR number. Review comments are grouped into
**review threads**; each thread has a GraphQL node ID (`PRRT_...`) and a root review comment
with a numeric `commentId`.

## Tool map

Use GitHub MCP tools. Do not write ad hoc shell, Python, `gh api`, `curl`, or REST scripts for
any step covered below.

| Step | Tool |
|---|---|
| Repo discovery when missing from context | `search_repositories` |
| Read PR metadata | `pull_request_read` with `method: get` |
| List review comments (inventory) | `pull_request_read` with `method: get_review_comments` |
| List submitted reviews | `pull_request_read` with `method: get_reviews` |
| List PR-level (non-inline) comments | `pull_request_read` with `method: get_comments` |
| Diff / files for review context | `pull_request_read` with `method: get_diff` or `get_files` |
| Reply to a review thread | `add_reply_to_pull_request_comment` (`commentId` = root comment of the thread) |
| Resolve a thread | `pull_request_review_write` with `method: resolve_thread`, `threadId` = `PRRT_...` |

Required params for `pull_request_read` and `pull_request_review_write`: `owner`, `repo`, `pullNumber`.

Do not substitute:
- `add_issue_comment` for a review-thread reply — that creates a new top-level comment
- `create_pending_pull_request_review` / `submit_pending_pull_request_review` for a simple reply
- `issue_*` tools for PR review comments

If required data cannot be obtained from the named tool, state exactly what is missing and stop.

## Thread identity

- Inventory `<id>` = the numeric `id` of the thread's **root** review comment.
- Group review comments into threads by `in_reply_to_id`: a comment with no `in_reply_to_id` is a
  root; comments carrying `in_reply_to_id` belong to that root's thread.
- Ordering rule "lowest numeric thread ID" = lowest root comment id.
- Keep the thread's `PRRT_...` node ID alongside the numeric id; it is needed to resolve.

## Status mapping

| Skill status | GitHub signal |
|---|---|
| `resolved` | thread `isResolved` true, or `outdated` and superseded by a later commit |
| `responded` | latest reply author is us and thread not resolved but needs no action |
| `unanswered` | no reply from us in the thread |
| `needs review` | we replied, thread not resolved |
| `automated/system` | comment author is a bot (`type: Bot`, or login ending in `[bot]`) such as `github-actions[bot]`, `dependabot[bot]`, `copilot-pull-request-reviewer[bot]` |
| `unknown` | cannot be determined from metadata |

Bot-authored review comments that raise a real code concern (e.g. Copilot code review) are **not**
`automated/system`. Classify as `automated/system` only when the comment is a status or
notification message, not a remediation request.

## Resolving

Reply first with `add_reply_to_pull_request_comment`, then resolve with
`pull_request_review_write` `method: resolve_thread`. Only resolve after the user approves the reply.

## Commit link format

`https://github.com/<owner>/<repo>/commit/<sha>`
