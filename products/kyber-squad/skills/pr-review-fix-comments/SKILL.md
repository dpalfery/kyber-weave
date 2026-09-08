---
name: pr-review-fix-comments
description: Prompt workflow for addressing pull request review comments on Azure DevOps or GitHub. Start with a status-only inventory of review threads, then stop and wait for permission before analyzing any single comment.
license: MIT
---

# Code Review Remediation

Structured workflow for addressing PR review comments with user approval at each step.
Provider-agnostic: all host-specific tool names, thread identity, and status mapping live in the
provider sub-file selected below.

## Provider Selection

Resolve the provider before Phase 1, in this order:

1. Explicit provider in `$ARGUMENTS` (`github` / `gh` / `azdo` / `ado` / `azure-devops`).
2. Remote URL of the current git repo:
   - contains `dev.azure.com` or `visualstudio.com` -> Azure DevOps
   - contains `github.com` -> GitHub
3. Only one of the two provider MCP servers is connected -> that provider.
4. Otherwise ask the user which provider and stop.

Then read the matching sub-file and use only its tools:

| Provider | Sub-file |
|---|---|
| Azure DevOps | `providers/azure-devops.md` |
| GitHub | `providers/github.md` |

Rules:
- Read exactly one provider sub-file per run. Do not mix tools across providers.
- Everywhere this file says `<provider tool: ...>`, use the tool named for that step in the sub-file.
- Use local `git` only for local source-control work (diff, commit, push), for either provider.
- If a named provider tool cannot supply required data, say exactly what is missing and stop.
  Do not fall back to CLI, `curl`, handwritten REST, or custom scripts unless the sub-file
  explicitly allows fallback for that exact step.

## Workflow

### Output Contract
- Inventory first.
- One thread per line.
- No analysis during inventory.
- Stop after the permission question.
- One thread at a time after permission is granted.

### Workflow State Management
Once the user grants permission for a specific thread, you MUST create and maintain a checklist using the `todo` tool. This tool is the authoritative record of workflow state; you do not need to maintain a visible ledger in your responses.

State management rules:
- Exactly one thread may be active at a time.
- As soon as a thread becomes active, create the following checklist items in the `todo` tool:
  - [ ] Permission granted
  - [ ] Analysis completed
  - [ ] Options presented
  - [ ] User selected option
  - [ ] Changes implemented
  - [ ] Code review passed
  - [ ] Diff shown
  - [ ] Commit approved by user
  - [ ] Commit created
  - [ ] Changes pushed
  - [ ] Response approved by user
  - [ ] Reply posted to PR thread
- Update the `todo` tool immediately as each step is completed.
- Do not move to another thread until all checklist items for the current thread are complete.
- If any checklist item is incomplete, the thread is not finished.
- Rely exclusively on the `todo` tool for workflow state; do not provide a visible ledger in your response.

### Transition Gates
These transitions are mandatory:
- Do not enter `analysis` until the user has granted permission for the selected thread.
- Do not enter `implementation` until the user has selected an option, or explicitly told you to proceed without options.
- Do not enter `commit approval` until code changes are implemented and the code review loop has passed.
- Do not create a commit until all of these are true: `Changes implemented`, `Code review passed`, `Diff shown`, and `Commit approved by user`.
- Do not push until the commit exists.
- Do not draft or post the PR reply until the commit has been pushed.
- Do not move to another thread until `Reply posted to PR thread` is complete for the active thread.

Forbidden transitions:
- `options` -> `commit approval`
- `implementation` -> `commit and push`
- `implementation` -> `response approval`
- `code review` skipped entirely
- switching to a new thread while the current thread has incomplete `todo` items

### Phase 1: Inventory Only
For the selected PR, do these steps in order:
1. Read and acknowledge the root `AGENTS.md` file.
2. Resolve the provider and read its sub-file (see Provider Selection).
3. Read the PR number from `$ARGUMENTS`.
4. If the PR number is missing, ask for it and stop.
5. Fetch the PR and all review comment threads from the provider.
6. List every discovered review thread using the exact inventory format below.
7. Select the thread with the lowest numeric thread ID whose status is `unanswered` or `needs review`.
8. Do not select threads classified as `automated/system`; they are informational and non-actionable for remediation.
9. If no thread has status `unanswered` or `needs review`, but one or more threads are classified as `automated/system`, report that only non-actionable automated system messages remain and stop.
10. If no thread has status `unanswered` or `needs review`, and the remaining thread metadata is still insufficient to choose a thread, ask the user to pick one from the inventory.
11. Stop immediately.

Phase 1 tool usage:
- For step 5, use `<provider tool: read PR metadata>` and `<provider tool: list all review threads>`.
- If project or repository identity is missing, use `<provider tool: project / repo discovery>` before asking the user.
- Thread identity, grouping, and numeric ID definition come from the provider sub-file.
- Do not use scripts, CLI wrappers, or REST calls for inventory when these tools are available.

Inventory format:
- Thread <id>: <status> | last responder: <us/them/none> | response present: <yes/no>

Status definitions:
- resolved: the thread is closed, marked fixed, or already addressed in the current PR state
- responded: the latest reply is from us and the thread still needs no further action
- unanswered: no reply from us yet
- needs review: the thread has a response from us but is not resolved
- automated/system: a provider- or bot-generated informational notification that is visible in the thread list but is not a remediation comment
- unknown: the thread status cannot be determined from metadata alone

The provider sub-file defines how each status is detected for that host.

Inventory rules:
- Classify automated system notifications as `automated/system` using the signals listed in the provider sub-file. Prefer thread metadata; use notification text only for this classification step.
- Treat `automated/system` threads as informational only: include them in the inventory, but ignore them during remediation thread selection.
- Do not analyze comment content.
- Do not infer priority, severity, or fixability.
- Do not propose fixes.
- Do not compare threads against each other.
- Do not mention anything except the inventory fields.

### Key Insights
- Ignore automated system messages during remediation selection.
- If a thread is already resolved, fixed, or otherwise clearly addressed in the current PR state, report it as already addressed and do not treat it as actionable by default.
- Do not begin work on the next comment until the current comment is fully completed and acknowledged.
- Reply to the existing thread rather than creating a new top-level comment.
- When posting a thread reply, do not invent line metadata unless the API requires it.

After the inventory, ask exactly this question:
`I am going to proceed with this comment first: <thread id or short summary>. Do I have your permission?`

If the user has not granted permission, do not continue.

### Phase 2: One Comment at a Time
Only after permission is granted for one specific thread:
1. Read only that thread.
2. Analyze only that thread.
3. Propose fix options if needed.
4. Wait for the user's choice before editing.
5. Do not touch any other thread.
6. Initialize the `todo` tool with the workflow checklist and keep it updated as you progress.

Phase 2 tool usage:
- For step 1, use `<provider tool: read one thread>` for the selected thread only.
- Do not fetch or analyze other threads once a thread is active.

## User Input

```text
$ARGUMENTS
```

The user should provide a pull request number in `$ARGUMENTS`, optionally preceded by a provider
(`github <number>` or `azdo <number>`). If no provider is given, resolve it per Provider Selection.

Do not read beyond Phase 1 until the permission question has been answered.

If the PR contains multiple actionable comments, do not batch them together. Complete the full workflow for one comment, then move to the next comment after the current comment is resolved.

### 1. Review & Analyze
- Read only the selected review comment from the PR thread for the provided PR number
- Check whether the selected thread is already resolved or whether the concern has already been addressed in the PR
- Determine if it's still an issue
- Provide a short summary of the selected comment
- Describe the problem the comment is pointing out
- Explain your assessment after the user has granted permission for that specific comment

### 2. Propose Fix
- Before listing options, restate the comment summary and the problem description in concise form
- Use this structure:
  Comment summary: <one or two sentence summary of the reviewer comment>
  Problem: <plain description of the underlying issue the reviewer is pointing out>
- Example:
  Comment summary: The reviewer is asking us to avoid duplicating environment parsing logic in the deployment script.
  Problem: The current change repeats parsing behavior that already exists elsewhere, which increases maintenance cost and makes future fixes easier to miss.
- Recommend specific changes with options if they exist
- Explain each option and pros and cons
- Recommend a specific option based on best practices and context
- Example recommended fixes:
  Option A: Reuse the existing shared parsing helper from the deployment utilities.
  Pros: Keeps the logic in one place and reduces maintenance risk.
  Cons: May require a small refactor to match the helper's current interface.
  Option B: Keep the new parsing logic local but extract it into a new helper in this script.
  Pros: Limits the change to the current file.
  Cons: Still duplicates behavior that already exists elsewhere.
  Recommended option: Option A, because it removes duplication at the source and aligns the change with the existing shared implementation.
- Give each option an identifier (Option A, Option B, etc.)
- Ask the user to choose one using this format:
Which option would you like?
  A. Option A
  B. Option B
  C. Option C
  D. Something else

- **Never advance past the analysis step automatically. If the user has not yet chosen an option, the only allowed action is to ask for that choice. Stop immediately after presenting the options.**

### 3. Implement Changes
- Make the code changes

### 3.5 Code Review
- use the code-review.md command workflow to do a code review loops cycle until the code-reviewer agent is satisfied
- once code review passes Show the diff
- **Get user approval before committing**
- Treat code review as a hard gate, not a suggestion. The workflow is blocked at this phase until the reviewer passes or all review findings are fixed and re-reviewed.
- If you are about to commit and `Code review passed` is not checked in your `todo` list, stop and return to code review.

### 4. Commit Changes
- Commit with format: `fix: <brief issue description>`
- Keep commit message concise (one line)
- note the commit hash so you can use it in the comment response

### 4.5 Push Changes
- Push to the PR branch so the fix is included in the PR before responding
- Use local `git push` for this step. Do not replace a normal push with a provider script.

### 5. Propose Response
- Draft a one-line comment response
- Format: "Fixed: <brief description>" or "Resolved: <brief description>" must include the commit hash and a commit link in the provider's link format so it is easy for the reviewer to find the changes
- **Get user approval for the comment**

### 6. Post Response
- Find the original comment thread on the PR and respond to it with the response
- Use `<provider tool: reply to a thread>` to reply to the existing PR comment thread
- Optionally mark the thread resolved with `<provider tool: resolve a thread>`, only after the user approves
- The reply should reference the commit hash and link from the pushed change
- After the reply is posted, return to the inventory and move to the next unresolved comment only then
- Once the reply is posted, ensure all `todo` items for the thread are marked complete before returning to the inventory.

  Key Insights:
  - You MUST push first - the commit needs to be in the PR
  - Reply to the existing thread rather than creating a new top-level comment
  - When posting a thread reply, do not invent line metadata unless the API requires it
  - Do not begin work on the next comment until the current comment is fully completed and acknowledged
  - Use MCP thread read and thread write tools for thread operations; do not substitute scripts when those tools are available

## Response Guidelines

Keep all responses concise:
- One line maximum
- Start with action verb (Fixed, Resolved, Updated, Added, Removed)
- Describe what changed, not how
- No technical details unless critical

**Good examples:**
- "Fixed: null pointer check in validation"
- "Resolved: race condition in worker pool"
- "Updated: error messages for clarity"

**Avoid:**
- Multiple sentences
- Implementation details
- Verbose explanations
