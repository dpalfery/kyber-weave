# Code Review Loop

Complete the authorized code review feedback cycle: verified fixes on the PR branch,
replies that identify the pushed commits, resolved review threads, and a new review request
when the user asks for one. Use the platform selected by the user or identified by
the review URL; pull requests and merge requests are called PRs below.

## Establish the current state

- Read the repository instructions, inspect the checkout for existing work, and obtain
  the PR's repository, base, head branch, and current head SHA. Use a separate checkout
  if necessary to preserve unrelated edits. Attach the PR to the chat when supported.
- Read all review threads, including replies and resolution state, plus the latest
  top-level review summary. Paginate when necessary. Distinguish actionable feedback
  from summaries and bot onboarding messages; the user's remembered comment count
  may no longer match the review platform.
- Assess each concern against the current head. A resolved flag alone does not prove
  the fix exists, and a clean incremental review may omit older findings. For an
  existing fix, inspect its commit and confirm it remains in the PR history.
- Honor the task's authorization. A request to fix comments, commit, push, reply,
  resolve, and request re-review authorizes those actions without repeated permission
  questions. Applying this skill alone does not authorize external messages, merging,
  deployment, or work beyond the requested PR.

## Fix and validate

Use the repository's prescribed code and documentation discovery tools before text
search. Make the smallest change that addresses the actual concern and preserve
existing fixes. Do not broaden dependencies, policy, or scope merely to satisfy a bot.

Run the checks required by the repository and the affected surfaces. Inspect each
result; record unavailable checks as unavailable rather than passed. Use focused
tests when behavior changes, and review the final diff for regressions and unrelated
files before committing. Escalate a material blocker instead of resolving a thread
whose concern remains open.

Create focused commits with a clear mapping from concerns to commits. One commit may
address related findings when the mapping remains easy to review. For already-fixed
comments, use the verified historical commit instead of creating an empty commit.

Refresh the remote head before pushing. If another actor advanced the branch,
reconcile their changes and revalidate the affected work. Push normally to the PR's
head branch; never force-push as a shortcut. Confirm each cited commit is reachable
from the remote PR head before posting its hash.

## Reply and resolve

Use the review platform's available tools or authenticated API/CLI. Identify its
thread states and reply permissions before mutating them. Keep multiline bodies in
files or structured arguments instead of shell interpolation.

- Reply in each original review thread. When the user asks to update a comment, edit
  an existing reply authored by the authenticated user; leave the reviewer's original
  comment intact. If no such reply exists, add one.
- Include the fix and a clickable commit link, for example:
  `Fixed: preserve existing edits during initialization. Commit: <platform commit URL> (abcdef1).`
- Resolve the thread only after its concern is addressed and the reply is successfully
  posted. Verify the resulting body and resolution state. Already-resolved threads
  may only need their commit reference updated.
- If a comment has no resolvable state on the selected platform, answer its concerns
  with the relevant commit links and validation evidence. Report that limitation
  instead of claiming it was marked resolved. Do not substitute closing the PR or
  approving the entire review for resolving an individual finding.

On GitHub, prefer its available tools; an authenticated `gh` CLI is a fallback.
Top-level review comments are not resolvable threads.
`gh api repos/OWNER/REPO/pulls/PR/comments` reads inline comments; GraphQL `reviewThreads` supplies resolution state. Edit an authored reply with
`PATCH /repos/OWNER/REPO/pulls/comments/COMMENT_ID`, or add a reply with
`POST /repos/OWNER/REPO/pulls/PR/comments/COMMENT_ID/replies`. Resolve the verified thread
ID with GraphQL `resolveReviewThread`. Re-read state after mutations and inspect partial
errors before continuing. On other platforms, use their documented reply and
resolution operations and verify the resulting state through the same platform.

## Request another review

After pushing and updating the addressed threads, request the reviewer specified by
the user through its established trigger. For Kilo, use `@kilocode-bot review` in a PR
comment and include the current head SHA and concise evidence when helpful.

A posted request is not a completed review or an approval. If a reviewer requires
account linking or onboarding, report that required user action and stop retrying.
Do not repeatedly request the same review while it is pending.

For a request for one re-review, stop after confirming the request was posted. If the
user explicitly asks to continue until clean, wait for a review of the current head
and repeat for new actionable findings. Stop and report remaining work after three
fix-and-review rounds without convergence, on an external access blocker, or when
feedback needs a user decision. Report which head was reviewed and avoid treating
an older review as approval of a newer commit.

Finish with the PR link, fix commit references, thread results, check results, and
whether re-review was requested, completed, or blocked. Include the saved skill path
when this workflow was newly created.
