---
id: plans/2026-09-28-kyber-utilities-status-line-slice
title: Kyber Utilities — status-line deployment slice
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-28
development-mode: test-first
keywords:
  - kyber utilities
  - status line
  - statusline
  - footer
---

# Kyber Utilities — status-line deployment slice

## Status

**Ready (amended).** Approval history, in order:

1. **First Ready grant.** On 2026-09-28 the user explicitly chose **approve and execute**,
   and the conductor relayed that choice (A7). That approval covered D1–D8 and the original
   four-harness Test contract.
2. **C7 triggered, back to Draft.** T0 completed on 2026-09-28 (see "T0 outcome"). It found
   no OpenCode implementation, which is a C7 stop condition, so the plan went back to Draft
   for amendment.
3. **Re-approval.** The user answered with D9 (drop OpenCode from this slice and move it to a
   follow-up issue) and D10 (rewire the `agy` script's OTLP telemetry to the `kyberdash kyber
   antigravity-statusline` stdin hand-off). The conductor relayed those answers as the
   user's re-approval, and on 2026-09-28 the plan returned to Ready. The re-approval covers
   D9, D10, and the amended Test contract below (the three-harness scope and the new rows
   T15–T18).

Implementation continues from the ready queue in "Dependency graph and concurrency".
Changing any Test-contract row, or hitting a new C7 condition, returns the plan to Draft for
re-approval.

**Execution progress at amendment.** This is a narrow self-gathered look at the working tree
on 2026-09-28. It does not attest gate results; T8 re-checks every gate.

- T0 is complete (see "T0 outcome").
- The conductor reports T12, the recorder command, as shipped.
- Artifacts for T1, T2, T3, T10, T11, and T13 are present in the tree:
  - T1: the `KyberUtilities` catalog row, ADR 0025, and `docs/kyber-utilities/`;
  - T2: `UtilitiesStatusLineDeploymentTests.cs`;
  - T3: `src/KyberWeave.Core/Utilities/StatusLine/`;
  - T10: issue #163, linked from the onboarding page;
  - T11: `dash/src/cli/antigravity-statusline.test.ts`;
  - T13: the runbook entry for `kyber antigravity-statusline`.
- Work that landed before D9 still carries OpenCode in `StatusLineTarget`, in the T2 suite,
  and in the T1 pages. T16–T18 remove it.
- T4–T7 have not started: `products/kyber-utilities/` and the artifact and CLI test files do
  not exist yet.

`component: KyberSquad` is a placeholder. D3 creates a `KyberUtilities` component, but the
[catalog](../catalog.md) has no such row yet, and adding one is outside the planner's write
boundary. T1 adds the catalog row, and T9 moves this plan's `component` to `KyberUtilities`.

## Problem and goal

The user runs Claude Code, Pi, and the Antigravity CLI (`agy`, D1) on this Mac, each with a
hand-maintained status line. OpenCode is installed too, but T0 found it has no status-line
implementation, so this slice leaves it out (D9). Nothing in Kyber-Weave deploys, versions, or diagnoses those
status-line files. Kyber-Squad is not a fit as-is. It owns agent and skill files only, and its
architecture rejects owning harness settings files ("Squad does not own settings files",
[Kyber-Squad architecture](../kyber-squad/architecture.md) §3, rejected alternatives).

**Goal.** Ship the first, narrow **Kyber Utilities** slice. It:

1. imports the user's existing status-line implementations into the repository, but only
   after the user has checked each one for secrets and a gitleaks scan comes back clean (D2);
2. deploys those Kyber-owned status-line artifacts to per-user locations the tool records and
   owns, one variant for each of Claude Code, `agy`, and Pi (D3, D4, D9);
3. prints **manual activation guidance**: the exact snippet or step the user applies to their
   own settings, using absolute paths;
4. provides **doctor checks** for the deployed files and their runtime prerequisites;
5. never creates, edits, merges, or takes ownership of any shared user settings file;
6. builds no general utility platform: no plugin registry, no generic utility schema, and no
   second utility type;
7. records Windows support (D4, T10) and OpenCode support (D9, T15) as GitHub issues
   carrying the implementation specifics. Neither is implemented in this slice;
8. adds a new `kyberdash kyber antigravity-statusline` command. It reads an `agy`
   status-line payload on stdin and feeds the existing Antigravity recorder, so KyberDash
   ingests `agy` status-line data as harness `antigravity-cli`. The imported `agy` script
   used to POST OTLP directly; it now pipes its payload to this command instead (D6, D10).

## Intake assessment

Recommendation: **PLAN**, confirmed by the conductor. The slice extends established
precedent: per-harness global-root resolution, a receipt of owned files with SHA-256 digests,
no-overwrite handling of unmanaged files, and a `doctor` verb. It does not need a
requirements-first spec.

## Development mode

`test-first`. The user did not opt out, so the repository default applies.

## Discovery method

- **Documentation:** the Kyber-Weave MCP `docs_explore` tool was not available in the planning
  harness. Following `AGENTS.md`, discovery started at the [documentation index](../README.md)
  and read governed pages directly. These are narrow self-gathered lookups.
- **Code:** the repository has no `.codegraph/` directory, so CodeGraph was skipped as
  `AGENTS.md` directs. Code facts come from narrow self-gathered text search.
- **External harness behaviour:** no research delegation was available, so these facts come
  from self-gathered web lookups of vendor documentation. Installed versions on the user's
  machine are not yet verified (see Risks).
- **Privacy boundary kept during planning:** no user settings file, credential store, shell
  profile, or existing status-line script on this Mac was opened or copied. Under D2, agents
  see the user's implementations only after T0, the user's privacy review.
- **T0 inventory (amendment):** the conductor supplied it on 2026-09-28. The user had
  authorized reading the harness status-line implementations directly (A8). The planner
  did not open those files for this amendment. It recorded only the content-free summary
  the conductor relayed.
- **Execution progress (amendment):** a narrow self-gathered text search and file listing of
  the working tree. A `.codegraph/` index now exists, but this harness has no CodeGraph,
  `docs_explore`, or research-delegation tool.

## Approved decisions

| ID | Decision | Approval provenance |
|---|---|---|
| A1 | Use a governed **plan**, not a spec. | Conductor intake selection, relayed as the user's choice (2026-09-28 dispatch). |
| A2 | This request is **planning only**. No implementation, no Ready status, and no execution without a separate explicit approval. | User instruction relayed by the conductor (2026-09-28 dispatch). |
| A3 | Activation is **manual**: the tool prints guidance, and the user edits their own settings. | User requirement relayed by the conductor (2026-09-28 dispatch). |
| A4 | Kyber Utilities does **not own shared user settings files** and does not build a wider utility platform. | User requirement relayed by the conductor (2026-09-28 dispatch). |
| A5 | No secrets in the repository or in any artifact, and no code copied from credential-bearing or unreviewed local files. | Always-on global rule, restated by the conductor (2026-09-28 dispatch). |
| A6 | `development-mode: test-first`. | Repository default; no opt-out supplied; conductor dispatch confirms. |
| D1 | The Gemini-family target is the **Antigravity CLI (`agy`)**. Gemini CLI is out of scope. | User answer to Q1, relayed by the conductor on 2026-09-28. |
| D2 | The artifacts are a **cleaned-up import of the user's existing Mac status-line implementations**. The user reviews each file for secrets before it enters the repository. The plan includes a privacy-review task (T0), and every imported file must pass a clean gitleaks scan. | User answer to Q2, revised from the recommendation, relayed by the conductor on 2026-09-28. |
| D3 | A new **`kyber-weave utilities statusline deploy\|status\|doctor\|remove`** command group, with its own `KyberUtilities` catalog component and its own receipt (record of owned files). It is separate from Squad's receipt. | User answer to Q3, relayed by the conductor on 2026-09-28. |
| D4 | **Per-user scope only, on macOS and Linux.** Windows is deferred to a **GitHub issue, not a `docs/todo` entry**, that records how Windows support would be built. Creating that issue is a plan task (T10). | User answer to Q4, relayed by the conductor on 2026-09-28. |
| D5 | `utilities statusline doctor` **never reads harness settings files**. It checks only Kyber-owned files (receipt checksums, the executable bit) and prerequisites (`jq`, `node`, the harness binary and its version, plus `kyberdash` for the `agy` variant). It then prints the activation snippet. | User answer to Q5 (option a), relayed by the conductor on 2026-09-28. |
| D6 | Any telemetry or KyberDash calls in the imported `agy` script are **replaced by a call to a new `kyberdash` recorder command** that feeds the existing `recordAntigravityStatusLinePayload`. The slice widens to include that command and its tests. Where needed, data is labelled `antigravity-cli` so KyberDash uses it. | User answer to Q6 (option c), relayed by the conductor on 2026-09-28. |
| D7 | Each script **keeps its existing content** as described in the user's T0 inventory. Tests enforce the safety rules: no unapproved network access (the KyberDash recorder call is the one sanctioned exception), no file writes except through the recorder, no environment values printed, a safe fallback on bad input, and missing values shown as missing. | User answer to Q7 (option a), relayed by the conductor on 2026-09-28. |
| A7 | **Approve and execute**: the plan, D1–D8, and the Test contract as written are approved for execution. | The user's explicit choice at the conductor's approve-and-execute gate on 2026-09-28, relayed by the conductor. |
| D8 | ~~OpenCode uses the plugin-file mechanism of the user's existing implementation (installed 1.18.30).~~ **Superseded by D9.** T0 found that no such implementation exists. | User answer to Q8 (option a) and the supplied version, relayed by the conductor on 2026-09-28. Superseded by D9 on 2026-09-28. |
| A8 | Agents may **read the user's existing harness status-line implementations directly**, read-only. This relaxes C5 for those files, which were inventoried in T0 and are the import source for T5. It still forbids writing to them, and still forbids reading or writing any harness settings file for the slice's own behaviour (D5). | User authorization relayed by the conductor with the T0 findings on 2026-09-28. |
| D9 | **OpenCode is dropped from this slice.** The slice imports and deploys Claude Code, `agy`, and Pi only. OpenCode support becomes a follow-up GitHub issue, like the Windows deferral (issue #163), created by T15. OpenCode code, tests, and doc text that landed before this decision are removed (T16–T18). **Rationale:** T0 found no OpenCode implementation: the plugin directory is empty, `tui.json` has no `plugin` entry, and the OpenCode config holds no status-line key. Shipping OpenCode would mean fresh authoring, which D2 and C7 rule out. Deferring it keeps the slice to what the user already runs. | User decision after C7 was reported, relayed by the conductor as re-approval on 2026-09-28. |
| D10 | The imported `agy` script **stops posting OTLP to the local collector** (`localhost:4318`). Instead it pipes its stdin status payload to `kyberdash kyber antigravity-statusline`, the recorder command T12 shipped, as a background hand-off (C8). The KyberDash recorder owns attribution (`antigravity-cli`). **Rationale:** it makes D6 concrete for what T0 actually found, and gives one attributable ingest path in place of a direct network post that C8 forbids. **Inherent consequence, flagged to the conductor rather than assumed accepted:** the user's local Aspire collector stops receiving `agy` status-line spans from this script. | User decision after C7 was reported, relayed by the conductor as re-approval on 2026-09-28. |

### Derived constraints (consequences of approved decisions, not new choices)

- **C1: no deployment into auto-load directories.** Pi auto-discovers extensions in
  `~/.pi/agent/extensions/` (self-gathered from vendor docs). A file in an auto-load
  directory is active, which A3 forbids. Deployed files therefore go to a Kyber-owned
  staging location, and activation guidance names the entry the user adds themselves: Pi
  `settings.json` `extensions`. Claude Code and `agy` have no auto-load directory for status
  lines.
- **C2: settings are never written.** This covers create-if-missing and "helpful" merges,
  for every harness and every command, including dry-run. Tests assert it (see the Test
  contract).
- **C3: fixtures are synthetic.** Status-line test fixtures are hand-written from published
  payload schemas. They contain no real paths, conversation ids, or account data. They are
  never captured from the user's live sessions.
- **C4: review and scan happen outside the working tree (A5, D2).** The always-on rule bans
  secrets anywhere in an opened folder, tracked or not. The user therefore copies each
  original into a scratch directory **outside** this repository, reviews and redacts it
  there, and runs gitleaks on that directory. A file enters `products/kyber-utilities/` only
  after the scan is clean. Missing gitleaks is a hard stop, never a skip.
- **C5: agents never read the originals (A5, D2), relaxed by A8.** Before T0 completed, no
  agent opened the user's original status-line files. Since T0 completed, A8 allows
  read-only access to the three inventoried implementations, as T5's import source. It is
  still true that no agent writes to those originals, and no agent opens the harness
  settings files beside them.
- **C6: imported files are de-personalised.** Imported artifacts contain no absolute home
  paths, usernames, hostnames, account ids, or private endpoints. Paths are derived at run
  time (`$HOME`, harness env overrides) or come from the deploy step. This is privacy
  hygiene and is also needed for Linux portability (D4).
- **C7: no silent fresh authoring.** If T0 finds no existing implementation for an in-scope
  harness, or finds one that lives only inside a settings file, the plan returns to Draft
  for a user decision. The tool neither writes a replacement from scratch nor extracts
  content from the settings file. **Triggered and resolved on 2026-09-28:** T0 found no
  OpenCode implementation, so the plan returned to Draft, and the user resolved it with D9
  by dropping OpenCode. Claude Code, `agy`, and Pi each have a file-based implementation,
  so C7 still guards them at T14.
- **C8: the recorder is the only sanctioned side effect (D6, D7).** The imported scripts open
  no network sockets and write no files. The one permitted exception is the `agy` variant
  handing its stdin payload to `kyberdash kyber antigravity-statusline`, and only that
  command writes, appending to KyberDash's own cache file. The hand-off runs in the
  background with stdout and stderr discarded, so it cannot delay or corrupt the status
  line. It is skipped silently if `kyberdash` is not on `PATH`. In concrete terms, for what
  T0 found in the `agy` script: T5 removes the async OTLP POST to `localhost:4318` (D10) and
  the write of its last-stdin debug file.
- **C9: the recorder command is invisible to the status line.** `kyberdash kyber
  antigravity-statusline` writes nothing to stdout and always exits 0, whether it records,
  ignores, or rejects a payload. On an I/O failure it writes at most one line to stderr.
  Status-line hosts render stdout and may disable a failing command.

The Draft decision ledger (Q1–Q8) was removed when the plan was finalised. Each answer, with
its provenance, is recorded above as D1–D8. The amendment's decisions came back from the user
already answered, so they went straight into the table as D9, D10, and A8 without a separate
ledger entry. The "missing, not zero" rule in D7 follows the repository's
[honest unobservability rule](../rules/honest-unobservability.md).

## T0 outcome (complete 2026-09-28)

This is the content-free inventory the conductor relayed. The plan holds no file contents.

| Harness | Implementation | Runtime dependencies | Segments displayed | Network and writes | Import notes |
|---|---|---|---|---|---|
| Claude Code | A standalone bash status-line script in the Claude config directory | `jq`, `git`, `awk` | user@host, directory, git branch and porcelain state, model, output style, context % | No network, no writes | A clean import candidate. T5 confirms that the user@host segment is computed at run time and is not a literal (C6). |
| `agy` | A standalone Python 3 script in the `agy` config directory (`~/.gemini/antigravity-cli/`) | `python3` (stdlib only), `git` | `AGY` badge, `📁` repository, `🌿` branch, `🤖` model, `⚡` total tokens with the five-section `(sys tls skl rul msg)` breakdown, `⏳ 5h:` quota % with reset duration; ` │ ` separators | **Network:** an async OTLP POST to a local collector (`localhost:4318`). **Writes:** a last-stdin debug file in its own directory. It has no KyberDash hand-off today. | It has a hard-coded absolute home path and an embedded AGY identity-prompt fallback. T5 normalises the path, drops the fallback, and rewires telemetry (D10). |
| Pi | A TypeScript status-bar module inside a larger local OTLP collector package, loaded through a relative dev-link in Pi's `packages` setting | Pi extension runtime (`node`) | `PI` badge, `📁` repository, `🌿` branch, `🤖` model, `⚡` total tokens with an `(in out cache)` breakdown, `💰` USD cost, `⏳` turn count; ` │ ` separators. A compact `setStatus` footer carries `🤖 model │ ⚡ session tokens │ 💰 cost`. Status text through `ctx.ui.setStatus`, plus a below-editor widget through `ctx.ui.setWidget`, in TUI mode. In other modes it writes to stderr. | Not a standalone file. It sits in a package whose purpose is OTLP collection. | T5 extracts a standalone extension (the status-bar module plus its minimal dependencies), not the collector package. The T4 suite owns the extracted module's stub-context shape: an `activate(ctx)`/default export reading `ctx.ui.mode`, `setStatus`, `setWidget`, `ctx.cwd`, `ctx.model`, `ctx.branch`, `ctx.tokens.{input,output,cache,total}`, `ctx.costUsd`, `ctx.turns` — an adapter detail the plan leaves to the tests. |
| OpenCode | **None.** The plugin directory is empty, `tui.json` has no `plugin` entry, and the OpenCode config holds no status-line key. | — | — | — | **C7 triggered.** Dropped by D9 and deferred to T15's issue. |

- **gitleaks:** a scan of the three existing files was clean, with zero findings. The
  conductor relayed the result without file content.
- **Deviation from T0's planned steps:** the user did not copy files into a scratch
  directory before inventory. Under A8 they authorized reading the implementations
  directly, and the OpenCode finding came from checking its config directory. Only key
  names were relayed. Nothing about that check is used by the slice's own behaviour (D5).
- **agy quota field contract (from T0, declared for T4):** the script reads a `quota`
  object on stdin whose `gemini-5h` (preferred) or `3p-5h` entry carries
  `remaining_fraction` (0–1, rendered as a percentage) and `reset_in_seconds` (rendered
  as `Xh Ym`/`Ym`). The segment renders as `⏳ 5h: N% (Xh Ym)`; with no quota data it
  renders `⏳ 5h: --%`. T4's synthetic fixture uses this shape verbatim.
- **agy settings directory:** the `agy` script lives under `~/.gemini/antigravity-cli/`. That
  supports the T2 suite's pinned `agy` root. T14 still confirms it live.
- **Pi mechanism correction:** the earlier harness table said Pi status lines use
  `ctx.ui.setFooter()`. The user's implementation uses `setStatus` and `setWidget`, and the
  harness table below now says so.

## Investigation findings

**Repository (narrow self-gathered):**

- Squad owns agent and skill files and records them in `squad.receipt.json` with SHA-256
  digests. It never overwrites or deletes an unmanaged file (`UnmanagedCollision`; `--adopt`
  only on identical bytes), and it explicitly rejects owning `.claude/settings.json`
  ([architecture](../kyber-squad/architecture.md) §3–§5;
  [onboarding](../kyber-squad/onboarding.md), Global Scope).
- Squad resolves each harness's global root, with env overrides: `$CLAUDE_CONFIG_DIR` →
  `~/.claude`; `$PI_CODING_AGENT_DIR` → `~/.pi/agent`; `$OPENCODE_CONFIG_DIR` →
  `$XDG_CONFIG_HOME/opencode` → `~/.config/opencode`. Squad's `antigravity` root is
  `~/.gemini/config`, which is **not** the `agy` CLI settings directory. The slice needs its
  own `agy` root rule.
- `SquadDoctorCommand` prints `ok`/`info`/`warn`/`fail` lines through `AnsiConsole` and exits
  1 on issues. It skips a harness that is not in use rather than failing it (the `.zcode/`
  marker rule). Its ZCode MCP check does read a ZCode config file. That is prior art for
  reading config. D5 rules it out for Kyber Utilities.
- KyberDash contains an Antigravity status-line payload parser and recorder
  (`StatusLinePayload`, `recordAntigravityStatusLinePayload`, cache file
  `antigravity-statusline.jsonl`). The recorder has no in-tree caller. The
  [telemetry inventory](../dash/telemetry-inventory.md) lists "Gemini statusline /
  Antigravity" as a source. It states that canonical records must not use harness `gemini`
  and that legacy `gemini` records are quarantined as `excluded_harness`. This is the
  KyberDash legacy data behind D6: a previous status-line implementation on this machine may
  have emitted OTLP telemetry, and T0 must say whether the current `agy` implementation still
  does.
- **Recorder behaviour (for D6).** `recordAntigravityStatusLinePayload(input)` accepts the
  parsed `agy` payload. It requires a non-empty `conversation_id`,
  `context_window.current_usage` with at least one non-zero token count, and a resolvable
  model. It appends one JSON line to `antigravity-statusline.jsonl` under KyberDash's cache
  directory (directory mode `0700`, file mode `0600`) and returns `false`, writing nothing,
  for a payload it rejects. Refresh discovers that file only for the default roots, as a
  source with `project: 'antigravity-cli'` and `provider: 'antigravity'`.
  `dash/src/refresh/registry.ts` resolves `project === 'antigravity-cli'` to harness
  `antigravity-cli`. So the recorder path already carries the attribution D6 asks for, and
  "relabel where needed" becomes an assertion in T11's integration test rather than
  expected code.
- **CLI precedent for the new command.** `kyberdash kyber cursor-hook` in
  `dash/src/cli/register.ts` (`registerKyberCommands`) reads stdin through the injectable
  `readStdin` and `write` seams in `KyberCommandDependencies`, and
  `dash/src/cli/register.test.ts` drives it through `program.parseAsync`.
  `kyber antigravity-statusline` follows the same shape, with a `recordAntigravityStatusLine`
  seam. The command is documented in [KyberDash runbook](../dash/runbook.md) beside
  `cursor-hook`.

**OpenCode (deferred under D9).** The planning-time research on OpenCode 1.18.30 was
self-gathered from upstream specs, PRs, and issues, and never live-verified. It found:

- a 1.x TUI plugin API: a `tui` export from `@opencode-ai/plugin/tui`, registering slots
  with `api.slots.register`, and activated only through `tui.json` `plugin` entries, with no
  directory auto-discovery;
- a server-plugin directory, `~/.config/opencode/plugins/`, that does auto-load;
- a v2 `context.ui.slot` API that does not apply to 1.18.30;
- reported 1.18.x hazards: raw TSX entrypoints, `sidebar_content`, and peer-dependency
  resolution.

T0 then found that no OpenCode implementation exists, so this slice does not use any of it.
The findings carry into T15's follow-up issue body. Older plan revisions hold the full
detail.

- No existing plan, spec, todo, or catalog row named Kyber Utilities at planning time.

**Harness contracts (self-gathered web lookups; installed versions unverified):**

| Harness | Mechanism | Activation surface (user-edited; never written by Kyber) | Auto-load hazard |
|---|---|---|---|
| Claude Code | `statusLine` with `type: command`. Receives JSON on stdin (`model.display_name`, `workspace.current_dir`, `context_window.used_percentage`, …); stdout is rendered. | `statusLine` in the user's Claude settings | None: a deployed file does nothing until activated |
| Antigravity CLI (`agy`) | `statusLine` command. Receives JSON on stdin (`conversation_id`, `model`, `workspace`, `context_window`, `vcs`, `agent_state`, `terminal_width`); stdout is rendered with ANSI. Supports a `stack_with_default` option. | `statusLine` in the `agy` settings. The macOS settings path is documented inconsistently (`~/.gemini/antigravity-cli/` vs `~/Library/Application Support/antigravity-cli/`). The user's script lives under `~/.gemini/antigravity-cli/` (T0), which supports the first; T14 still confirms it live. `agy` does **not** expand `~`, so guidance must print absolute paths. | None |
| Pi | A TypeScript extension. The user's implementation (T0) uses `ctx.ui.setStatus` plus `ctx.ui.setWidget` (below the editor) in TUI mode, and stderr otherwise. It does not replace the whole footer through `setFooter`. | Pi `settings.json` `extensions: ["/abs/path.ts"]` | **Yes**: `~/.pi/agent/extensions/` auto-loads (C1) |
| OpenCode | Out of scope (D9): no implementation exists (T0). | Not applicable; follow-up issue (T15) | Not applicable |
| Gemini CLI | Footer items chosen with `/footer` (`ui.footer.items`). An extension badge API has been proposed. | Out of scope (D1) | Not applicable |

## Scope

In scope (D1–D7, D9, D10). There are three harnesses: Claude Code, `agy`, and Pi.

- a user privacy review of the existing implementations, with a clean gitleaks scan (T0;
  complete);
- importing, de-personalising, and minimally adapting the three reviewed implementations
  into a new canonical source root, `products/kyber-utilities/statusline/` (Claude Code,
  `agy`, Pi). The Pi variant is a standalone extension extracted from the collector package
  (T5);
- removing the OpenCode target, tests, and doc text that landed before D9 (T16–T18);
- `kyber-weave utilities statusline deploy|status|doctor|remove` at per-user scope on macOS
  and Linux, with a Kyber Utilities receipt of owned files and their digests;
- printed per-harness activation guidance with absolute paths, and no settings writes;
- governed docs: a `KyberUtilities` catalog row, an ADR for the ownership boundary, and
  onboarding and architecture pages;
- synthetic fixtures and tests;
- a GitHub issue that records how Windows support would be built (T10);
- a GitHub issue that records how OpenCode support would be built (T15, D9);
- the new `kyberdash kyber antigravity-statusline` command (D6), with its unit, CLI, and
  refresh-attribution tests, plus KyberDash runbook and telemetry-inventory updates;
- a manual live check by the user after deployment (T14).

Out of scope:

- writing, merging, backing up, restoring, or reading harness settings files (A4, C2, D5);
- deploying into auto-load directories (C1);
- a generic utility registry, schema, or second utility type (A4);
- Gemini CLI (D1);
- Windows implementation, and any `docs/todo` entry for it (D4 routes it to a GitHub issue);
- project-scope deployment (D4);
- OpenCode in any form: deployment, root resolution, doctor checks, activation guidance, or
  fresh authoring (D9; deferred to T15's issue);
- agents writing to the original status-line files, or reading the settings files beside
  them (C5, A8);
- importing the Pi collector package or any of its OTLP code (T5 extracts the status-bar
  module only);
- OTLP or any other network telemetry from the imported scripts (C8, D10);
- migrating or re-attributing legacy `gemini` records already in `canon.db` (they stay
  quarantined as `excluded_harness`);
- changes to `recordAntigravityStatusLinePayload`'s parsing rules, or to the refresh
  registry, beyond what T11's attribution test proves necessary;
- changes to Squad's receipt, renderers, or lifecycle. That includes Squad's own OpenCode
  root rule, which T16 and T17 leave alone.

## Test contract (approved 2026-09-28 under A7; amended and re-approved 2026-09-28 under D9 and D10)

**Amendment summary.**

- **Removed:** every OpenCode-specific pinned expectation:
  - the `OPENCODE_CONFIG_DIR` → `XDG_CONFIG_HOME` → `~/.config/opencode` staging-root rule
    and its override and fallback cases;
  - the `.config/opencode/tui.json` (and `opencode.json`) settings sentinels;
  - the `.config/opencode/plugins` auto-load-directory rule;
  - the OpenCode rows in the per-target theories;
  - the OpenCode 1.x TUI-shape assertion;
  - the OpenCode version warning;
  - OpenCode in T14.
- **Kept unchanged:** the Claude, `agy`, and Pi expectations: `CLAUDE_CONFIG_DIR`,
  `PI_CODING_AGENT_DIR`, `AGY_CONFIG_DIR`, the `kyber/statusline` staging segment, the Pi
  `extensions` auto-load rule, and the rule that settings are never touched.
- **Added:** rows T15–T18, and the D10 and T0-derived specifics in T4, T5, and T6.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | **Complete 2026-09-28.** **No test: a user action.** Planned as a scratch-directory review outside this repository (C4). Performed instead as a user-authorized direct inventory (A8). | `gitleaks dir <path> --redact --no-banner` (gitleaks v8.19+). On older versions: `gitleaks detect --no-git --source <path> --redact --no-banner`. | A content-free inventory for each harness: the file and its type, runtime dependencies, displayed segments, network and write behaviour, and import notes. See "T0 outcome". | Not applicable. The replacement evidence is the gitleaks result, recorded **without** file content. | **Met:** gitleaks is clean, with zero findings on the three existing files, and the inventory is recorded in "T0 outcome". **C7 was triggered** (no OpenCode implementation) and resolved by D9. The origin and licence of each file are not stated in the relayed inventory; see T5. |
| T2 | `tests/KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~UtilitiesStatusLineDeploymentTests"` | Run in an isolated temp home for the **three targets: Claude, `agy`, Pi** (D9). Per-harness roots resolve with env overrides (`CLAUDE_CONFIG_DIR`, `AGY_CONFIG_DIR`, `PI_CODING_AGENT_DIR`) beneath the pinned `kyber/statusline` staging segment. The plan targets only Kyber-owned staging paths, never an auto-load directory (Pi `extensions`). An unmanaged file at a target path is refused, not overwritten. The receipt records every owned file with its SHA-256. Remove deletes only receipt-owned, unmodified files. **No settings file path is ever created, opened for write, or modified**: the three sentinel settings files (Claude, `agy`, Pi) are byte-identical before and after, and no settings path appears in the plan. OpenCode rows were part of the original contract and were removed under D9 by T16. | Tests compile against the new public contract and fail on assertions for the missing behaviour. Compile errors or fixture faults are not valid RED evidence. | The same tests pass without any change to their assertions. |
| T3 | same as T2 | same as T2 | Implementation of T2's contract. | Consumes T2's saved failing run. | Same as T2; the full suite passes. |
| T16 | same as T2 (edits the existing suite) | same as T2 | The T2 suite pins the D9 three-target scope. Every OpenCode case is removed: the `OPENCODE_CONFIG_DIR` override case, the `XDG_CONFIG_HOME` fallback case, the `.config/opencode/tui.json` settings sentinel, the `.config/opencode/plugins` auto-load row, the `OPENCODE_PLUGIN_DIR` unrelated-variable row, the OpenCode rows of every per-target theory, and the OpenCode relative-root mapping. Constants that become unused go too. **One assertion is added:** `StatusLineTarget` declares exactly `Claude`, `Agy`, and `Pi`. The Claude, `agy`, and Pi rows are unchanged. | The added assertion fails because `StatusLineTarget.OpenCode` still exists. That is an assertion failure, not a compile error. Record that the Claude, `agy`, and Pi rows still pass. | Consumed by T17. |
| T17 | same as T2 | same as T2, then the full `dotnet test` | `StatusLineTarget.OpenCode` and every OpenCode branch are removed from `src/KyberWeave.Core/Utilities/StatusLine/`: the root resolution, settings paths (`opencode.json`, `tui.json`), the auto-load directory, and the receipt segment mapping. Squad's OpenCode support is untouched. | Consumes T16's saved failing run. | T16 passes without any change to its assertions. The full suite passes, and the build has zero warnings. |
| T4 | `tests/KyberWeave.Tests/UtilitiesStatusLineArtifactTests.cs`; synthetic fixtures under `tests/KyberWeave.Tests/Fixtures/statusline/` (C3) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~UtilitiesStatusLineArtifactTests"` | Each of the three variants (Claude, `agy`, Pi) is checked against the segments its "T0 outcome" row declares (D7).<br>**Claude and `agy` command variants:** Claude runs under `bash` with `jq`, `git`, and `awk`; `agy` runs under `python3` with `git`. They run with fixture JSON on stdin inside a sandbox, and print those segments. The sandbox has a temp `HOME`, a `PATH` limited to a stub directory, a stub `git` that returns a synthetic branch and porcelain state, network-denying stubs for `curl`/`wget`/`nc`, and, for `agy`, a listener check proving that nothing connects to `localhost:4318`. A missing field renders as absent, not `0`. Malformed JSON gives a safe fallback with exit 0. They write no file anywhere in the sandbox, including no `agy` last-stdin debug file, and echo no environment variable (a canary variable must not appear in output). The `agy` variant renders without the embedded identity-prompt fallback: a value the fallback used to supply renders as missing.<br>**`agy` recorder hand-off (C8, D6, D10):** with a stub `kyberdash` on `PATH` that records its argv and stdin, the variant invokes exactly `kyberdash kyber antigravity-statusline` with the byte-identical stdin payload. Stdout is unaffected, and the command returns before the stub completes, which a stub delay proves. With no `kyberdash` on `PATH` the variant still renders and exits 0. No other process, socket, or OTLP call is made.<br>**Pi variant:** the standalone extracted extension runs through a `node --test` harness invoked from the test, with a stub extension context. In TUI mode the stub records `ctx.ui.setStatus` and `ctx.ui.setWidget` calls; in non-TUI mode stderr is captured. The same declared-segment and safety assertions apply. The module imports nothing from the collector package and makes no OTLP or network call.<br>**Portability and privacy (C6):** no artifact contains an absolute home path (`/Users/…`, `/home/…`), username, or hostname literal.<br>The test skips, with the reason recorded, only when a declared runtime (`bash`, `jq`, `git`, `awk`, `python3`, `node`) is unavailable. | Tests fail because `products/kyber-utilities/statusline/` has no imported artifacts yet. Compile errors are not valid RED evidence. | The same tests pass without any change to their assertions. |
| T5 | same as T4; also `products/kyber-utilities/statusline/**` | same as T4, plus `gitleaks dir products/kyber-utilities --redact --no-banner` | The three inventoried implementations are imported read-only from their sources (A8), de-personalised (C6), and minimally adapted so that T4 passes.<br>**Claude:** imported as is, apart from C6 hygiene.<br>**`agy`:** the async OTLP POST to `localhost:4318` is replaced by the background `kyberdash kyber antigravity-statusline` stdin hand-off (D10, C8). The last-stdin debug-file write is removed. The hard-coded absolute home path becomes a run-time derivation from `$HOME`, `AGY_CONFIG_DIR`, or the script's own directory. The embedded identity-prompt fallback is dropped, and any value it supplied renders as missing (D7).<br>**Pi:** `statusbar.ts` is extracted, with only the helpers it needs, into a standalone extension. Nothing from the collector package, its OTLP code, or the dev-link comes with it.<br>The display is not otherwise restyled (D7). | Consumes T4's saved failing run. A missing gitleaks binary blocks the task. | T4 passes without any change to its assertions. **gitleaks exits 0 with zero findings on `products/kyber-utilities/`**, and the user-global gitleaks commit hook passes on the commit that adds the files. |
| T10 | **No test: a GitHub issue.** `dpalfery/kyber-weave` issues | `gh issue create --repo dpalfery/kyber-weave …`, then `gh issue view <n> --repo dpalfery/kyber-weave` | The issue "Kyber Utilities: Windows support for status-line deployment" records how Windows support would be built: PowerShell variants for the command-type status lines; Windows roots for each harness (`%APPDATA%\antigravity-cli`, `%USERPROFILE%\.claude` / `CLAUDE_CONFIG_DIR`, the Pi Windows config directory), to be verified live; forward-slash or escaped JSON paths in activation guidance; `agy` not expanding `~`; `pwsh -File` invocation instead of the executable bit; line-ending handling; doctor prerequisite checks for `pwsh`; a Windows CI leg for the artifact tests. It contains no personal paths, usernames, or secrets. | Not applicable. Before creation, record that no such issue exists (`gh issue list --search "Kyber Utilities Windows"`). | The issue exists. Its number and URL are recorded in this plan's closeout and linked from the Kyber Utilities onboarding page. (Met: issue #163. It was filed before D9 and may still mention OpenCode's Windows directory. That note now belongs to T15's issue, and #163 needs no edit.) |
| T15 | **No test: a GitHub issue.** `dpalfery/kyber-weave` issues. The conductor may file it directly. | `gh issue list --repo dpalfery/kyber-weave --search "Kyber Utilities OpenCode"`, then `gh issue create --repo dpalfery/kyber-weave …`, then `gh issue view <n> --repo dpalfery/kyber-weave` | The issue is titled "Kyber Utilities: OpenCode status-line support". It records:<br>- T0 found no OpenCode implementation, so support needs a user decision on fresh authoring (D2, C7) before any build;<br>- the 1.x TUI plugin mechanism (`tui` export, `api.slots.register`, activation only through a `tui.json` `plugin` entry, no directory auto-discovery), and that the v2 `context.ui.slot` API does not apply to 1.x;<br>- the auto-loading server-plugin directory staging must avoid;<br>- the removed Kyber root rule (`OPENCODE_CONFIG_DIR` → `XDG_CONFIG_HOME` → `~/.config/opencode`), the `tui.json` settings sentinel, and the `plugins` auto-load rule, to restore;<br>- the reported 1.18.x hazards;<br>- a doctor version check;<br>- Windows config-directory notes.<br>It contains no personal paths, usernames, or secrets. | Not applicable. Before creation, record that no such issue exists. | The issue exists. Its URL is recorded here as **`https://github.com/dpalfery/kyber-weave/issues/165`**, filled in by whoever files it, and T18 links it. |
| T6 | `tests/KyberWeave.Tests/UtilitiesCliCommandTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~UtilitiesCliCommandTests"` | `utilities statusline deploy [--target claude\|agy\|pi] [--dry-run]` writes nothing on a dry-run. `--target opencode`, or any other value, is a client-input error with exit 2 (D9). It prints per-harness activation guidance with **absolute** paths (never `~`) and states that the user applies it. `status` reports ok, missing, or drift for each owned file. `doctor` reports receipt checksum health and the executable bit for each owned file. It also checks the prerequisites from "T0 outcome": `jq`, `git`, and `awk` for Claude; `python3` and `git` for `agy`; `node` for Pi. It checks each harness binary and its version, and, for `agy`, whether `kyberdash` is present (a warning, not a failure, because the hand-off is optional under C8). It skips harnesses that are not installed and prints the activation snippet. Exit codes follow the Squad doctor precedent: 0 healthy, 1 issues, 2 client-input error. **No command writes or reads any settings path (D5)**: an I/O-recording test seam proves no settings path is ever opened. | Tests fail because the commands are not registered or not implemented. | The same tests pass without any change to their assertions. |
| T7 | same as T6 | same as T6 | Commands are implemented and registered in `Program.cs` with a description and an example. | Consumes T6's saved failing run. | Same as T6; the full suite passes. |
| T1 | `docs/catalog.md`, `docs/adr/`, new `docs/kyber-utilities/` pages | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . && dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | No executable behaviour test. The replacement verification is a read-back of the docs against A3–A5, D2–D8, and C1–C9, including the Windows-unsupported statement that links T10's issue, plus zero-finding validate and drift. | Record that no catalog row, ADR, or page exists today. | Both commands report zero findings. |
| T18 | `docs/kyber-utilities/README.md`, `docs/kyber-utilities/onboarding.md`, `docs/adr/0025-kyber-utilities-owned-files-not-settings.md` | same as T1 | No executable behaviour test. The replacement verification is a read-back. OpenCode is removed from the harness lists, the `<harness>` values, activation snippets, auto-load examples, and doctor text. Each page says "OpenCode: not supported in this slice; see `https://github.com/dpalfery/kyber-weave/issues/165`". ADR 0025 keeps its decision and gets a dated amendment note that narrows the harness context to Claude Code, `agy`, and Pi under D9. Its `Status` stays Accepted. | Record the current OpenCode mentions in the three pages; an earlier read found them in README §context and the harness table, the onboarding `<harness>` list, the OpenCode section, doctor and auto-load text, and ADR 0025 Context. | Both commands report zero findings, and no OpenCode mention remains apart from the deferral line and the ADR amendment note. |
| T11 | `dash/src/cli/register.test.ts` (new cases); new `dash/src/cli/antigravity-statusline.test.ts`; `dash/src/refresh/refresh.integration.test.ts` (new case) | `cd dash && npx vitest run src/cli/register.test.ts src/cli/antigravity-statusline.test.ts src/refresh/refresh.integration.test.ts` (focused; the package `test` script already passes `src` as a filter, so appending files would not narrow the run) | **Registration:** `registerKyberCommands` registers `kyber antigravity-statusline`, and `program.parseAsync([... 'kyber', 'antigravity-statusline'])` routes the stdin payload through the `readStdin` seam into an injected `recordAntigravityStatusLine` seam.<br>**Stream contract (C9):** for a valid payload, an ignored payload (all-zero usage or no model), malformed JSON, and empty stdin, the command writes **nothing to stdout** and exits 0. On a recorder I/O failure it writes at most one line to stderr and still exits 0.<br>**Real recorder:** with `KYBERDASH_CACHE_DIR` set to a temp directory (the existing test isolation seam), one valid synthetic payload appends exactly one line to `antigravity-statusline.jsonl`, with file mode `0600` on POSIX.<br>**Attribution (D6):** a refresh over that file yields canonical records with harness **`antigravity-cli`** and none with harness `gemini`. | Tests fail because the command is not registered. The attribution case must be run and its result recorded. If it already passes against the existing registry, record that as evidence that no relabelling code is needed; it is not a RED failure to force. Compile or type errors are not valid RED evidence. | The same tests pass without any change to their assertions. |
| T12 | same as T11 | same as T11, then `npm --prefix dash run test`, `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run check:reachable` | `kyber antigravity-statusline` is implemented in `dash/src/cli/register.ts` (a `recordAntigravityStatusLine` seam on `KyberCommandDependencies`, defaulting to `recordAntigravityStatusLinePayload`). Stdin is JSON-parsed defensively. Attribution changes are made only if T11's attribution case failed. | Consumes T11's saved failing run. | T11 passes without any change to its assertions. The full dash test suite, typecheck, lint, and `check:reachable` all pass. |
| T13 | `docs/dash/runbook.md`, `docs/dash/telemetry-inventory.md` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . && dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | No executable behaviour test. The replacement verification is a read-back. The runbook documents `kyberdash kyber antigravity-statusline` beside `cursor-hook` (stdin contract, silent stdout, always exit 0, the cache file it appends to, and that users wire it through Kyber Utilities' `agy` variant rather than by editing settings). The telemetry inventory's "Gemini statusline / Antigravity" row states that the recorder path attributes to `antigravity-cli` and that legacy `gemini` records remain quarantined. | Record the current runbook and inventory wording, which has no such command. | Both commands report zero findings. |
| T14 | **No test: manual live check by the user.** | `kyber-weave utilities statusline deploy`, `… status`, `… doctor`; then `kyberdash dash refresh` | On this Mac, the user deploys, applies each activation snippet by hand (A3), and confirms that each status line renders in **Claude Code, `agy`, and Pi** (D9). For Pi, the user also confirms there is no duplicate status bar from the still dev-linked collector package, or decides whether to unlink it. After one `agy` turn, the user confirms that `kyberdash dash refresh` reports `antigravity-cli` activity from the recorder. Doctor exits 0. Evidence is pass/fail per harness, recorded without screenshots of private content. | Not applicable. The replacement evidence is the user's per-harness attestation. | Every harness is confirmed rendering and the `antigravity-cli` refresh row is non-zero. Any failure returns the owning task to work. A harness whose deployed variant cannot render without fresh authoring triggers C7. |
| T8 | Read-only | `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`; the four dash gates from `AGENTS.md` | Declared .NET gates, the dash gates (typecheck, lint, test, `check:reachable`), gitleaks on `products/kyber-utilities/`, and council review. | None (read-only). Any finding returns work to the task that owns it. | All gates pass and the review approves. |
| T9 | This plan, the index, the archive | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready && dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Closeout and archive, recording T10's and T15's issue URLs. | Before archiving, `--merge-ready` fails only with `KW-DOC-LIFECYCLE-003`. | Both commands pass after archiving. |

Changing any row after approval, including weakening an assertion to reach GREEN, is a scope
change: the plan returns to Draft for re-approval. A C7 condition found at T14 has the same
effect. The D9 and D10 amendment above is such a change, and was re-approved on 2026-09-28.

## Tasks

### T0: User privacy review of the existing implementations (human task) — complete

- **Status:** **complete, 2026-09-28.** The inventory is in "T0 outcome", and gitleaks was
  clean on the three existing files.
- **Objective (as planned):** a secret-free, reviewed copy of each existing status-line
  implementation, with a content-free inventory.
- **How it ran:** the planned steps were copy to a scratch directory (C4), redact, scan, and
  hand over the inventory. The user replaced them by authorizing a direct read of the
  implementations (A8).
- **C7 outcome:** a harness had no file-based implementation (OpenCode). That triggered C7,
  which D9 resolved. No implementation lived only in a settings file.
- **Dependencies:** none. **Skills:** none; this was a user action the conductor relayed.

### T10: Create the Windows-support GitHub issue

- **Objective:** Record the deferred Windows work as a GitHub issue carrying the specifics
  (D4). It is not a `docs/todo` entry.
- **Target:** issues in `dpalfery/kyber-weave`. No repository files change.
- **Acceptance:** As stated in the Test contract. The issue body holds only generic paths and
  environment-variable names.
- **Dependencies:** none. **Skills:** `github-cli` (issue creation).
- **State:** met. Issue #163 exists and the onboarding page links it.

### T15: Create the OpenCode-support GitHub issue

- **Objective:** Record OpenCode support, deferred under D9, as a GitHub issue carrying the
  specifics, parallel to T10. It is not a `docs/todo` entry.
- **Performed by:** an agent with `github-cli`, or the conductor directly. Whoever files it
  replaces every `https://github.com/dpalfery/kyber-weave/issues/165` placeholder in this plan with the real URL. That is a
  plan-only edit and does not count as a Test-contract change.
- **Target:** issues in `dpalfery/kyber-weave`. No repository files change.
- **Acceptance:** As stated in the Test contract. The issue body holds only generic paths and
  environment-variable names. It does not reproduce any of the user's settings content.
- **Dependencies:** none. **Skills:** `github-cli` (issue creation).

### T1: Governed ownership docs

- **Objective:** Declare the Kyber Utilities component and its ownership boundary before any
  code lands.
- **Files:**
  - `docs/catalog.md`: a new `KyberUtilities` row with source root
    `src/KyberWeave.Core/Utilities`.
  - A new ADR under `docs/adr/`: "Kyber Utilities deploys owned files and never owns harness
    settings; activation is manual."
  - New `docs/kyber-utilities/README.md` and `onboarding.md`, covering the commands, the
    per-harness activation snippets, the import and privacy-review process, a privacy
    statement, and "Windows: not supported; see issue #N".
  - The feature entry in `docs/README.md`, and a row in `docs/adr/README.md`.
- **Acceptance:** The docs state the intent of A3–A5, D2–D7, and C1–C9, including:
  - the optional `kyberdash` hand-off for `agy`;
  - doctor never reading settings.

  Activation snippets use placeholder absolute paths only. The Windows statement links T10's issue. Validate and
  drift report zero findings. The OpenCode activation text, written under the superseded D8,
  is removed by T18.
- **Dependencies:** T10, whose issue number the page links. **Skills:** `app-docs-standard`,
  `kyber-weave-docs`, `architecture-decision-record`.
- **State:** the artifacts are present in the tree (see "Execution progress at amendment").

### T18: Remove OpenCode from the Kyber Utilities docs (D9)

- **Objective:** Bring the T1 pages in line with the three-harness scope.
- **Files:** `docs/kyber-utilities/README.md`, `docs/kyber-utilities/onboarding.md`, and
  `docs/adr/0025-kyber-utilities-owned-files-not-settings.md`. The catalog row has no
  OpenCode mention and stays as is.
- **Acceptance:** As stated in the Test contract. ADR 0025 is amended with a dated note. It
  is not superseded, because the ownership decision itself is unchanged.
- **Dependencies:** T15, whose issue URL the pages link. **Skills:** `app-docs-standard`,
  `kyber-weave-docs`, `architecture-decision-record`.

### T2: RED, deployment core contract

- **Objective:** Pin the behaviour of deployment, receipt, collision handling, and removal,
  including the guarantee that settings are never written.
- **Files/symbols:** `tests/KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs`, which
  targets new `StatusLineDeploymentPlan`, `StatusLineReceipt`, and `StatusLineTargetRoots`
  types under `src/KyberWeave.Core/Utilities/StatusLine/`.
- **Acceptance:** As stated in the Test contract.
- **Dependencies:** none. **Skills:** `test-dev`.

### T3: GREEN, deployment core

- **Objective:** Implement T2's contract. Reuse Squad's global-root and physical-path helpers
  where they apply, without changing Squad types or its receipt.
- **Files:** `src/KyberWeave.Core/Utilities/StatusLine/*.cs`.
- **Acceptance:** T2 passes without any change to its assertions, and the build produces
  zero warnings.
- **Dependencies:** T2. **Skills:** `csharp-dev`.
- **State:** T2 and T3 artifacts are present in the tree. They were written against the
  original four-target contract; T16 and T17 narrow them.

### T16: RED, narrow the deployment contract to three targets (D9)

- **Objective:** Take every OpenCode expectation out of the T2 suite, and pin that
  `StatusLineTarget` declares exactly Claude, `agy`, and Pi.
- **Files/symbols:** `tests/KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs` only:
  the `AllTargets`, `DocumentedOverrideVariables`, `UnrelatedEnvironmentVariables`,
  `AutoLoadingHarnesses`, and `ConfirmedSettingsFiles` theory data, the settings-sentinel
  list, the two OpenCode staging-root tests, the relative-root mapping, and the class
  remarks that say "four confirmed settings files" and "two auto-load directories".
- **Acceptance:** As stated in the Test contract. The Claude, `agy`, and Pi rows keep their
  literal values unchanged.
- **Dependencies:** none (T2 and T3 have landed). **Skills:** `test-dev`.

### T17: GREEN, remove the OpenCode target from Kyber Utilities core (D9)

- **Objective:** Implement T16's contract with the smallest removal.
- **Files:** `src/KyberWeave.Core/Utilities/StatusLine/StatusLineTarget.cs`,
  `StatusLineTargetRoots.cs`, `StatusLineReceiptStore.cs`, and any other file in that
  directory that names `StatusLineTarget.OpenCode`. No receipt migration is needed: the CLI
  (T7) has not shipped, so no deployed receipt can contain an `opencode` entry. Squad's
  `SquadGlobalRoots` OpenCode rule is untouched.
- **Acceptance:** T16 passes without any change to its assertions. The full suite passes, and
  the build has zero warnings. No remark in the Utilities code still describes OpenCode
  behaviour.
- **Dependencies:** T16. **Skills:** `csharp-dev`.

### T4: RED, artifact behaviour

- **Objective:** Pin each variant's declared segments, safety rules, and portability rules
  against synthetic payloads, before any file is imported.
- **Files:** `tests/KyberWeave.Tests/UtilitiesStatusLineArtifactTests.cs`,
  `tests/KyberWeave.Tests/Fixtures/statusline/*.json`.
- **Acceptance:** As stated in the Test contract. Fixtures are synthetic (C3). Segment
  expectations come from "T0 outcome", never from reading the originals. A8 allows reading
  them for import (T5), but the tests must not be shaped around their exact output.
- **Dependencies:** T0 (complete), whose inventory supplies the runtimes and declared
  segments. The Claude and `agy` cases can start now. The Pi cases wait until the conductor
  relays the Pi segment list (see "T0 outcome"). **Skills:** `test-dev`.

### T5: GREEN, import the reviewed artifacts

- **Objective:** Import the three inventoried implementations (D2, A8), de-personalise them
  (C6), and make the smallest changes needed to pass T4.
- **Files:** `products/kyber-utilities/statusline/{claude,antigravity,pi}/`, plus a manifest
  listing each file with its harness, runtime, and origin.
  - `claude/`: the bash status-line script.
  - `antigravity/`: the Python 3 status-line script.
  - `pi/`: a **standalone extracted extension**. That is `statusbar.ts` plus only the helper
    modules it imports, in a minimal self-contained layout (no `package.json` dependencies
    beyond what Pi supplies to extensions). It is not the dev-linked collector package.
- **Import adaptations** (only these, beyond C6 hygiene):
  - `agy`: replace the async OTLP POST to `localhost:4318` with a detached background pipe of
    the stdin payload to `kyberdash kyber antigravity-statusline`, skipped silently when
    `kyberdash` is absent (D10, C8). Remove the last-stdin debug-file write. Replace the
    hard-coded absolute home path with a run-time derivation. Drop the embedded AGY
    identity-prompt fallback; where it supplied a displayed value, render that value as
    missing (D7, honest unobservability).
  - Pi: cut every import of collector, OTLP, or exporter modules. Keep the
    `setStatus`/`setWidget` TUI path and the stderr fallback as they are.
  - Claude: none expected.
- **Origin and licence:** the relayed T0 inventory does not state each file's origin. Before
  the commit that adds the files, the implementer records in the manifest "self-written"
  or the licensed source, as the conductor confirms with the user, and adds attribution
  where one applies.
- **Acceptance:** T4 passes without any change to its assertions. gitleaks exits 0 with zero
  findings on `products/kyber-utilities/`, and the global gitleaks hook passes on commit.
  No variant opens a socket, writes a file, or reads or prints environment secrets. The
  `agy` hand-off process is the only sanctioned side effect (C8). D2 requires the user to
  review each file before it enters the repository, and T0's direct inventory included no
  redaction pass. So the conductor shows the user the de-personalised files, and gets the
  user's confirmation, before the commit that adds them.
- **Dependencies:** T0, T4. The hand-off's command name and stdin contract are fixed by this
  plan, and T4 uses a stub `kyberdash`, so T5 does not wait for T12, which has shipped
  anyway. **Skills:** `csharp-dev` for the test harness; shell, Python, and TypeScript
  editing.

### T6: RED, CLI surface

- **Objective:** Pin the commands' behaviour, guidance text, and exit codes.
- **Files:** `tests/KyberWeave.Tests/UtilitiesCliCommandTests.cs`.
- **Acceptance:** As stated in the Test contract.
- **Dependencies:** T3 and T17. The tests compile against the three-target public types,
  and they pin `--target opencode` as a client-input error. **Skills:** `test-dev`.

### T7: GREEN, CLI surface

- **Objective:** Add the `utilities statusline` branch using the three-edit rule in the CLI
  `AGENTS.md`.
- **Files:** `src/KyberWeave.Cli/Commands/Utilities/*`, `src/KyberWeave.Cli/Program.cs`.
- **Acceptance:** T6 passes without any change to its assertions. All output goes through
  `AnsiConsole` with `Markup.Escape`. The full suite passes.
- **Dependencies:** T3, T5, T6, T17. **Skills:** `csharp-dev`.

### T11: RED, `kyberdash kyber antigravity-statusline`

- **Objective:** Pin the new recorder command's registration, its silent-stdout /
  always-exit-0 stream contract (C9), real appends through the recorder, and
  `antigravity-cli` attribution through refresh (D6).
- **Files/symbols:**
  - `dash/src/cli/register.test.ts`: new cases beside the `cursor-hook` case.
  - New `dash/src/cli/antigravity-statusline.test.ts`.
  - `dash/src/refresh/refresh.integration.test.ts`: a new case for a recorder-produced
    source.
  - Targets: `registerKyberCommands`, `KyberCommandDependencies`,
    `recordAntigravityStatusLinePayload`.
- **Acceptance:** As stated in the Test contract. Payloads are synthetic (C3). Cache writes
  are isolated with `KYBERDASH_CACHE_DIR`.
- **Dependencies:** none. **Skills:** `test-dev`; TypeScript/Vitest.

### T12: GREEN, `kyberdash kyber antigravity-statusline`

- **Objective:** Implement the command following the `cursor-hook` pattern: an injectable
  `recordAntigravityStatusLine` seam, defensive JSON parsing, no stdout, and exit 0.
- **Files:**
  - `dash/src/cli/register.ts`.
  - `dash/src/refresh/registry.ts` or `dash/src/providers/antigravity.ts`, only if T11's
    attribution case failed.
- **Acceptance:** T11 passes without any change to its assertions. The full dash test suite,
  typecheck, lint, and `check:reachable` pass. `recordAntigravityStatusLinePayload`'s parsing
  rules are unchanged. Comments explain why the command never writes stdout.
- **Dependencies:** T11. **Skills:** TypeScript (dash), `code-review` awareness of ADR 0020
  first-party rules.
- **State:** the conductor reports it shipped. T11's test file and the registration are
  present in the tree. T8 re-checks the dash gates.

### T13: KyberDash docs for the recorder command

- **Objective:** Document the command and update the Antigravity status-line row in the
  telemetry inventory.
- **Files:** `docs/dash/runbook.md` (beside `cursor-hook`), `docs/dash/telemetry-inventory.md`
  (the "Gemini statusline / Antigravity" row).
- **Acceptance:** As stated in the Test contract. The docs claim nothing about live
  collection until T14 has confirmed it.
- **Dependencies:** none. The contract is fixed here, and T13's files are disjoint from
  T1's. **Skills:** `app-docs-standard`, `kyber-weave-docs`.
- **State:** the runbook entry is present in the tree. The runbook wording names Kyber
  Utilities' `agy` variant as the caller, and D10 does not change that.

### T8: Gates and review

- **Objective:** Run every declared .NET gate, the four dash gates, gitleaks on
  `products/kyber-utilities/`, and the council review. Adding a `kyberdash` subcommand does
  not change how `kyberdash` is built or updated. Run `./scripts/update-loop.sh` only if the
  build or release path is touched after all.
- **Dependencies:** T1, T7, T12, T13, T17, T18. **Skills:** `resharper-clt`, `code-review`.

### T14: Manual live check by the user

- **Objective:** Confirm on this Mac that each of the three harnesses (Claude Code, `agy`,
  Pi) renders its status line after manual activation, and that `agy` data reaches
  KyberDash as `antigravity-cli`. OpenCode is not checked (D9).
- **Performed by:** the user, prompted by the conductor. Agents do not edit settings
  (A3, C2).
- **Acceptance:** As stated in the Test contract.
- **Dependencies:** T8, so that the reviewed code is what gets deployed. **Skills:** none;
  this is a user action.

### T9: `docs-dev` closeout

- **Objective:** Record the evidence: T0's gitleaks results without content, T10's and T15's
  issue URLs, and T14's per-harness attestations. Harvest into canonical docs, including
  the confirmed live collection claim in the telemetry inventory. Set `component` to
  `KyberUtilities`, archive this plan, and update the index. Windows and OpenCode are
  covered by T10's and T15's issues and get no todo. Any other follow-up becomes a todo or
  issue only if the user accepts it.
- **Dependencies:** T10, T14, T15. **Skills:** `app-docs-standard`, `kyber-weave-docs`.

## Dependency graph and concurrency

Tasks marked `✓` are complete, or present in the tree, at the amendment (see "Execution
progress at amendment").

```text
T0 ✓ ──> T4 (RED artifacts) ──> T5 (GREEN import) ─────────────────────────┐
T2 ✓ ──> T3 ✓ ──> T16 (RED 3-target) ──> T17 (GREEN drop OpenCode) ──> T6 (RED CLI) ──> T7 (GREEN CLI) <─┘
T11 ✓ ──> T12 ✓ ─────────────────────────────────────────────────────┐
T13 ✓ ───────────────────────────────────────────────────────────────┤
T10 ✓ ──> T1 ✓ ──────────────────────────────────────────────────────┤
T15 (OpenCode issue) ──> T18 (drop OpenCode from docs) ──────────────┤
T17 ─────────────────────────────────────────────────────────────────┤
T7 ──────────────────────────────────────────────────────────────────┴──> T8 ──> T14 (user live check) ──> T9
T10 ✓, T15 ────────────────────────────────────────────────────────────────────────────────────────────> T9
```

**Ready queue after the amendment (2026-09-28).** None of these tasks depends on another:

| Task | Kind | Scope | Dependencies |
|---|---|---|---|
| T4 | Agent (`test-dev`) | New `tests/KyberWeave.Tests/UtilitiesStatusLineArtifactTests.cs` and `tests/KyberWeave.Tests/Fixtures/statusline/*.json` | T0 ✓ |
| T15 | Agent (`github-cli`) or the conductor | GitHub issue in `dpalfery/kyber-weave` (no repository files) | none |
| T16 | Agent (`test-dev`) | `tests/KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs` | T2 ✓, T3 ✓ |

These tasks unlock next:

- T5 when T4's RED run is saved.
- T17 when T16's RED run is saved.
- T18 when T15 has filed the issue and its URL is recorded.
- T6 when T17 is GREEN.

`MAX_CONCURRENCY: 4` for agent tasks. T14 is a user action and is not scheduled.

- **Now:** T4 (new test file and fixtures), T15 (GitHub only), and T16 (the existing
  deployment suite) share no files.
- **Next:** T5 (`products/kyber-utilities/`), T17 (`src/KyberWeave.Core/Utilities/`), and T18
  (`docs/kyber-utilities/`, ADR 0025) are pairwise disjoint and can run together.
- **Consumption edges:** T6 needs T17's three-target public types. T7 consumes T3, T5, T6,
  and T17. T8 consumes T1, T7, T12, T13, T17, and T18. T14 consumes T8. T9 consumes T10,
  T14, and T15.
- No two concurrent tasks share a file.

## Risks

- **Harness drift.** All three status-line APIs are young and version-dependent, and `agy`'s
  macOS settings path is documented inconsistently. T0 supports `~/.gemini/antigravity-cli/`,
  but that is not live-confirmed. Mitigation: doctor reports each harness version, the docs
  date each contract, and T14 confirms the result live.
- **Pi duplicate status bar.** The user's collector package, dev-linked through Pi's
  `packages` setting, still registers its own status bar. Activating Kyber's extracted
  extension beside it may show two. Mitigation: the Pi activation guidance says so, and in
  T14 the user decides whether to unlink the collector's status bar. Kyber never edits
  `packages` (C2).
- **Pi extraction.** `statusbar.ts` may import collector internals. Mitigation: T5 carries
  only what the status bar needs, and T4 asserts that the extension imports nothing from
  the collector package and makes no network call.
- **Loss of the local Aspire feed (D10).** After activation, the `agy` script no longer
  POSTs OTLP to the user's local collector. Status-line data reaches KyberDash through the
  recorder instead. This follows from D10 and was **explicitly accepted by the user on
  2026-09-28**.
- **Accidental activation.** Deploying into an auto-load directory would breach A3. Tests
  assert C1.
- **Privacy of the import (D2).** This is the highest risk in the slice. T0 found a
  hard-coded home path and an embedded identity-prompt fallback in the `agy` script.
  Mitigation: gitleaks was clean on the three sources (T0). T5 normalises the path and
  drops the fallback. gitleaks gates the canonical source root and the commit, the artifact
  tests forbid personal-path literals (C6), and fixtures stay synthetic (C3). Agents read
  the originals only read-only (A8). gitleaks cannot catch every personal detail (for
  example an internal hostname with no secret pattern), so the user's review stays
  authoritative, and the conductor shows the user the imported files before the T5 commit.
- **Settings reads.** D5 forbids them. T6 proves no settings path is opened.
- **Missing or settings-embedded implementations.** This risk materialised for OpenCode: C7
  stalled the slice, and D9 resolved it. Silent fresh authoring would have contradicted D2.
- **Licence of the imported code.** If an existing script derives from a published example,
  its licence must be carried. The relayed T0 inventory does not state origin, so T5
  records it, as confirmed with the user, before commit.
- **Legacy telemetry.** The current `agy` script POSTs OTLP directly to a local collector
  (T0). D10 replaces that with the recorder hand-off, and T4's sandbox asserts that no
  other network call remains. Legacy `gemini` records already in `canon.db` are not
  migrated.
- **Recorder latency and status-line stability.** `kyberdash` is a Node binary, so a
  foreground call on every status-line refresh would add visible latency. C8 requires a
  detached background hand-off, and C9 requires silent stdout and exit 0. T4 proves the
  variant returns before the stub completes, and T11 proves the stream contract.
- **Recorder volume.** The recorder appends one line per accepted payload, and `agy` may
  refresh often. Refresh already collapses identical consecutive snapshots
  (`parseStatusLineCalls` run grouping), but the file grows without bound. Rotation is out of
  scope. If the user asks, it becomes an issue or todo at closeout.
- **OpenCode removal regressions (D9).** OpenCode code already landed in T2, T3, and T1.
  Mitigation: T16's three-target assertion and T6's `--target opencode` exit-2 case pin the
  removal. T17 does not touch Squad's OpenCode support, and T18's read-back checks that no
  stale OpenCode guidance remains.
- **Wider slice (D6).** The slice now touches KyberDash code and gates. Mitigation: a single
  command following the `cursor-hook` precedent, no parser or registry change unless T11
  proves one is needed, and the dash gates in T12 and T8.
- **Missing prerequisites.** Without a declared runtime (`jq`, `git`, `awk`, `python3`,
  `node`), a variant must degrade to a printed fallback, never an error loop, because `agy`
  disables a status line that keeps failing.
- **Catalog placeholder.** `component: KyberSquad` stays until T9. The `KyberUtilities`
  catalog row is already present (T1).

## Verification gates

The declared repository gates from `AGENTS.md`, run through
`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`, plus the
focused filters in the Test contract. The KyberDash gates from `AGENTS.md` must also pass:
`npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run test`,
and `npm --prefix dash run check:reachable`. The tray gates are not required, because
`dash/tray/` is untouched. In addition, `gitleaks dir products/kyber-utilities --redact
--no-banner` must exit 0 with zero findings, and the user-global gitleaks commit hook must
pass. A missing gitleaks binary is a hard stop.

## Planning verification

The planner had no process-execution tool in any round. The conductor ran the documentation
gates on the decision-complete Draft on 2026-09-28:

- `docs validate .`: clean, zero findings.
- `docs validate . --merge-ready`: only `KW-DOC-LIFECYCLE-003`, which is expected while this
  plan is open. T9 removes it at archive.
- `docs drift .`: **not run**, because there is no local `.codegraph/` index. Earlier plans
  recorded the same environmental limit (for example the squad receipt layout-marker plan),
  and CI runs drift at PR time.

The finalisation edits (approval, Ready status, ledger removal, ready queue) came after that
run. They change this plan's body, frontmatter `status`, and index row only.

The conductor re-ran the gates on the **finalised** file on 2026-09-28:

- `docs validate .`: clean, zero findings.
- `docs drift .`: clean, zero findings. The user approved building the CodeGraph index;
  `codegraph init` indexed 21,598 nodes and 81,398 edges.

Adding this evidence note was the only edit after that run.

**Amendment (D9, D10, 2026-09-28).** The planner edited only this plan and its index row, and
still has no process-execution tool. The conductor must run `docs validate .` and
`docs drift .` on the amended file and record the result here. Until both are clean, the
amendment is saved but not verified.

## Review and closeout

T8 runs the gates and council review after GREEN, and T14 is the user's live check. T9 is
the explicit `docs-dev` closeout.

## Human judgement reserved

Approve and execute was granted on 2026-09-28 (A7). After T0 triggered C7, the plan was
re-approved on 2026-09-28 through the D9 and D10 answers. What remains reserved for the user
at execution time:

- confirming each imported file's origin and licence, and reviewing the de-personalised
  files before the T5 commit (D2);
- manual activation, including the Pi collector-package decision;
- the live per-harness confirmation (T14).

Any new C7 condition, or any further change to the Test contract, returns the plan to Draft
for a new decision.
