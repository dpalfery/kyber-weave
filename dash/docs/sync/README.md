# codeburn sync

Push your AI usage telemetry to a shared backend so teams can track adoption, budgets, and ROI across developers.

Everything stays local-first: codeburn never sends data without your explicit action, and prompts/code are never included.

## Quick Start

```bash
# One-time setup (opens browser for login)
codeburn sync setup https://metrics.your-team.com

# Push recent usage
codeburn sync push

# Check status
codeburn sync status
```

## Commands

### `codeburn sync setup <url>`

Configures sync with a remote endpoint. Opens your browser for a one-time OIDC login.

```bash
codeburn sync setup https://metrics.your-team.com
```

What happens:
1. Fetches server configuration from `<url>/.well-known/codeburn-export.json`
2. Opens your browser to the identity provider's login page
3. After login, stores a refresh token securely in your OS keychain
4. Saves the endpoint configuration (no secrets) to `~/.config/codeburn/sync.json`

You only need to do this once. The token refreshes silently on every push.

### `codeburn sync push`

Sends unsent AI usage data to the configured endpoint.

```bash
# Push unsent calls from the last 7 days (default)
codeburn sync push

# Push a larger window
codeburn sync push --since 30d

# Preview what would be sent
codeburn sync push --dry-run

# Also push git attribution (opt-in — see "Git attribution" below)
codeburn sync push --attribution
```

### `codeburn sync status`

Shows the current sync configuration and authentication state.

```
Endpoint: https://metrics.your-team.com
Traces path: /v1/traces
Issuer: https://auth.your-team.com
Auth: configured
Token storage: keychain
Last sync: 2h ago
```

### `codeburn sync logout`

Removes stored credentials and revokes the token at the identity provider.

```bash
codeburn sync logout
```

### `codeburn sync reset --confirm`

Clears the sent-ledger, causing the next push to re-send all data in the window. Use after a backend migration or if you suspect missing data.

```bash
codeburn sync reset --confirm
```

## What Gets Sent

Each AI interaction becomes one OTLP span with these attributes:

| Field | Example | Description |
|---|---|---|
| `ai.provider` | `kiro`, `cursor`, `claude` | Which AI tool |
| `ai.model` | `claude-sonnet-4-6` | Model used |
| `ai.input_tokens` | `12500` | Prompt tokens |
| `ai.output_tokens` | `3200` | Billable output tokens (includes separately billed reasoning where applicable) |
| `ai.cost_usd` | `0.085` | Estimated cost |
| `ai.project` | `my-app` | Project basename, only when backed by an exact provider-recorded working directory; otherwise omitted |
| `ai.tools` | `["Edit", "Bash"]` | Tools invoked |
| `ai.speed` | `standard` | Provider-recorded speed tier |
| `ai.cost_estimated` | `true` | Whether the cost is an estimate rather than provider-reported |
| `ai.cache_read_tokens` | `800` | Cache-read tokens, only when the provider recorded a non-zero value |
| `ai.cache_write_tokens` | `200` | Cache-creation tokens, only when the provider recorded a non-zero value |
| `ai.call_count` | `3` | Usage spans the span's session contributes in the synced window |
| `ai.session_duration_ms` | `61000` | First-to-last provider-recorded event time; omitted when either end is missing or unordered |
| `ai.subscription_covered` | `true` | Cost covered by a configured plan or proxy path; omitted when the plan/proxy machinery cannot decide |
| `ai.work_unit_id` | `9f2c…` | Pseudonymous work-unit id (the root session's trace id), grouping a session with its delegated children |
| `ai.session_role` | `root`, `child` | The session's relationship to its work unit |
| `ai.lineage_evidence` | `provider-recorded` | The evidence class behind the grouping |

A pseudonymous `device_id` distinguishes your machines without revealing hostnames. Each export batch also carries a `codeburn.coverage_through` resource attribute: the ISO date your local history is complete through, stamped only when a complete local parse finalized that watermark.

The three lineage fields appear only when your AI tool itself recorded the relationship on disk (today: Claude subagent transcripts and Kimi Code subagent sessions). They are never inferred from timing, shared projects, or directories, and they contain no prompt, title, raw session or agent id, or path: `ai.work_unit_id` is the same SHA-256 derivation the trace id already uses. A session whose parent was not observed (for example, outside the synced window) stays separate rather than being guessed into a group, and a session with no recorded lineage carries none of the three fields.

### Git attribution (opt-in: `--attribution`)

`codeburn sync push --attribution` additionally sends the session→commit correlation that `codeburn yield` computes locally, so the backend can join AI usage to git activity without git hooks. Two extra span types are emitted:

**`codeburn.session.attribution`** — one per session with joinable evidence:

| Field | Example | Description |
|---|---|---|
| `ai.project` | `my-app` | Repository basename derived from normalized `git.repo`; omitted for PR-only evidence |
| `git.repo` | `github.com/acme/widget` | Normalized `origin` remote (credentials and ports stripped) |
| `git.pr_links` | `["…/pull/12"]` | PR URLs captured for the session |
| `git.commit_count` | `2` | Number of attributed commits |

If usage and attribution spans for the same trace carry different safe project
basenames, the repository basename from attribution is authoritative for project
aggregation. The provider-recorded cwd basename on the usage span is a
provisional label and must not create a second project bucket.

**`codeburn.commit`** — one per commit attributed to a session:

| Field | Example | Description |
|---|---|---|
| `git.sha` | `4f2a…` | Commit SHA |
| `git.in_main` | `true` | Whether the commit landed in the main branch |
| `git.was_reverted` | `false` | Whether a later commit reverted it |

Attribution is **inferred** (timestamp-window correlation, the same heuristic as `codeburn yield`); the resource attribute `codeburn.attribution_methodology: timestamp-window` marks it as such. State transitions (a commit merging to main, or being reverted) are re-sent automatically on later pushes — receivers should upsert commits by `(git.repo, git.sha)` and session spans by `traceId` (the same id usage spans already carry; latest state wins). When a commit migrates to a later-parsed session with a tighter window, the losing session re-emits with `git.commit_count: 0` (a retraction), so summing `git.commit_count` across upserted session rows never double-counts. Retractions fire only when the commit was won by another session — commits that merely age out of the `--since` window are not retracted, so a previously-synced count stays correct. Session spans also re-emit when an ongoing session's window grows, keeping the span end time current.

With `--attribution`, normalized repo remote URLs, commit SHAs, commit timestamps (span start times), PR URLs, and the merged/reverted booleans leave your machine — plus the same pseudonymous `codeburn.device_id` resource attribute the usage spans carry. PR links are rebuilt client-side from scheme + host + path only (userinfo, query strings, and fragments are dropped; https, `/org/repo/pull/N` path, bounded length, max 20 per session), and the repo identity itself passes a strict hostname/path allow-list before sending — malformed or transport-helper remotes (`ext::…`, `codecommit::…`) are rejected outright rather than parsed. Precisely what is and is not sent:

- **Commits**: only from repos with a network `origin` remote, and only for sessions whose trusted provider-recorded working directory resolved to that repo. Local grouping labels, provider storage paths, prompt text, local-only repos, `file://` remotes, and Windows filesystem paths are never emitted as repo identities. A session without trusted cwd provenance never inherits the repo of the directory you happen to push from.
- **PR links**: sent whenever a session captured them, even when the session's repo could not be identified — the PR URL itself names the repo, so this adds no information beyond the link the session already recorded.
- Without the flag, none of this is sent.

### What is NOT sent

- **Prompts** — your actual messages to AI are never included
- **Code** — file contents, diffs, and paths stay local
- **Bash commands** — may contain secrets, never sent
- **Your name/email** — identity is derived server-side from your login token; home-directory, email-shaped, and credential-shaped project labels are omitted

There is no flag to override this. Privacy is structural, not configurable. The only additive opt-in is `--attribution` (repo remotes, commit SHAs, and PR URLs — never code or prompts), described above.

Legacy Hermes messages that contain a textual `Current working directory:`
line, including Windows paths, may still supply a local grouping label. Prompt
text never becomes trusted `projectPath`/`workingDirectory` provenance and can
never produce outbound `ai.project`.

## Authentication

Sync uses standard OIDC (the same protocol as "Sign in with Google"). Your team's admin sets up the identity provider — you just click through the browser login once.

- **Token storage**: macOS Keychain, Windows Credential Manager, or Linux libsecret. Falls back to a `0600` file if no keychain is available.
- **Token lifetime**: typically 30–90 days (set by your admin). You'll be prompted to re-login when it expires.
- **Re-login**: run `codeburn sync setup <url>` again.

## FAQ

**Q: Does sync run automatically?**
A: No. You run `codeburn sync push` when you want. A future version may offer opportunistic push (after each `codeburn report`), but it's always explicit.

**Q: What if I push the same data twice?**
A: Safe. A local sent-ledger tracks what's been sent. Re-pushing the same window doesn't create duplicates.

**Q: Why is today's Copilot usage missing from my dashboard?**
A: Copilot input/cache usage is reconciled locally between the per-request `session-store.db` rows and the `session.shutdown` rollups, and that reconciliation keeps changing while a session is live — a rollup residual shrinks as the rows covering it land, and a row that looked like a crash-only request becomes supplementary once its journal entry appears. The sent-ledger is append-once, so a value sent mid-reconciliation could never be corrected at the receiver. A Copilot session is therefore held back until it has been quiet for 24 hours, then pushed once, final. Nothing is dropped; `--dry-run` reports how many calls are held.

**Q: Why didn't my old Copilot sessions resync with the new per-request breakdown?**
A: On purpose. A Copilot session's input/cache can leave your machine in one of two shapes — as one `session.shutdown` **rollup** span per (session, model), or **reconciled** into one span per API request plus a residual for whatever the rows don't cover. They describe the same tokens, so the receiver must never hold both, and a usage span cannot be retracted once the append-once ledger has sent it. Whichever shape a session was first synced in, it stays in; the other is frozen for that session permanently. That runs in both directions: a session synced by a pre-store version keeps its rollup and never sends rows, and a session synced as rows never sends a rollup later — which matters because at the 90-day durable age-out the cached rows are pruned and the rollup starts serving again under a key that was never sent. Growth within the shape a session already uses is unaffected, and per-turn output spans are never frozen. `--dry-run` reports the frozen count. `codeburn sync reset --confirm` clears the local ledger and re-pushes everything under the new breakdown — only do that if the receiver's copy is cleared too, or you get exactly the doubling this avoids.

**Q: What if I'm offline for a week?**
A: Next push catches up. The default window is 7 days; use `--since 30d` or `--since all` (up to 6 months) for longer gaps. A push runs to completion regardless of size — server rate limits (429) are waited out automatically.

**Q: Can my admin see my prompts?**
A: No. Prompts are never included in the payload. The server sees token counts, costs, conservatively sanitized provider/model/tool identifiers, and an optional project basename only when CodeBurn has trusted cwd provenance.

**Q: How do I stop syncing?**
A: `codeburn sync logout` removes everything. Or just stop running `push`.
