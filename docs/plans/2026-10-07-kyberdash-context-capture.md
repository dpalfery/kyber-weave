---
id: plans/2026-10-07-kyberdash-context-capture
title: "KyberDash: fill the context data gap through harness capture and a model-window catalog"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-08
development-mode: test-first
keywords:
  - context capture
  - OpenTelemetry enablement
  - context window provenance
  - turn inspector empty
code-refs:
  - OtlpReceiver
  - HarnessAdapter
  - registerKyberCommands
  - ClaudeContentReader
  - matchingTurns
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0016-kyberdash-harness-source-refresh
  - adr/0018-kyberdash-content-retention-purge
  - adr/0026-kyber-utilities-owned-files-not-settings
---

# KyberDash: fill the context data gap through harness capture and a model-window catalog

**Status: In progress — A4-approved work continues; A5 approved the P2.W2
ensure-if-absent seam (A5-Q1 = (a)).**
Frontmatter remains `status: current`, the governed ontology value for an active plan; the
body and plan index carry the execution lifecycle.
- A3 remains the historical approval for the G1 revision. A4 approved P2.W, now merged at
  `69e068441629dc27388a8a2b59a3a8df53649dea`. A5 approved P2.W2.
- T0–T10 keep their task-review state. T0, T2–T10 are branch tasks with task-reviewer PASS;
  T1 also remains PASS but is read-only and has no branch. In particular, T8 and T10 are
  PASS and are not reopened by this amendment.
- In-flight work that does not consume the capture writer core is unaffected and may
  continue: P2.R, P2.0, P2.4b, P2.T5, and P2.8's non-writer work.
- P2.W2 is approved and may be dispatched. P2.2a retries only after P2.W2 has
  task-reviewer PASS; the other A3/A4-authorized work remains unaffected.
- G1-Q1, G1-Q2 and G1-Q3 remain answered. G1-Q4 remains superseded by D15.
- Development mode remains `test-first` (D8).
- D14 still prohibits pushing before T12 returns APPROVE. This plan and its index row stay
  only in the owner's main checkout and must not be committed on any
  `kyberdash-context/*` branch.

**Executors.** Every task, T1 discovery included, is dispatched to a headless OpenCode, Pi or
Cursor CLI agent. Task text therefore names tools by capability (web fetch, shell, file
edit), never by a harness-specific agent. Each task's **Skills** label tells the conductor
which task-reviewer contract applies. It does not name an executor.

**Branching and PRs (D14).** Branches are stacked locally, and nothing is pushed mid-run.
- Each task runs in its own git worktree on a local branch `kyberdash-context/<task-id>`.
- A task with no dependencies branches from `main` at `c5b35ec`.
- A task with one dependency branches from that dependency's branch tip.
- A task with several dependencies branches from one of them and merges the others locally
  first. For example, T10 branches from `kyberdash-context/T5` and merges
  `kyberdash-context/T7`.
- After each task's task-reviewer PASS, the conductor merges its branch into the local-only
  branch `kyberdash-context/integration`. That branch is the integrated stack: O1 builds
  from it and the T12 council reviews it.
- Nothing is pushed until the end-of-run code-reviewer council (T12) returns APPROVE. Then
  one PR per task opens, in dependency order.

**Execution constraint (read before dispatch).** Pull-request CI runs `docs validate
--merge-ready`. That check fails any branch that still carries an open plan in `docs/plans/`
(`KW-DOC-LIFECYCLE-003`).
- Neither this file nor its plan-index row may be committed on any task branch or on the
  integration branch. They stay in the owner's main checkout.
- The T13 closeout PR adds the archived plan and the index row.
- Each dispatched agent receives its task text from the conductor, not from this file.
- T1's output also stays untracked, at
  `/Users/dave/git/kyber-weave2/artifacts/kyberdash-context-capture/t1-capability-matrix.md`
  (`/artifacts/` is gitignored).

**Discovery provenance.**
- Live-store facts in the problem statement were verified by the conductor earlier on
  2026-10-07, against `~/.kyberdash/canon.db` and kyberdash 0.1.7-rc.16 built from
  `e788336`. The architect did not re-verify them.
- The G1 revision's docs discovery came from Kyber-Weave MCP `docs_*` tools with
  `root=/Users/dave/git/kyber-weave2 rev=c5b35ec dirty=yes`. For this amendment,
  `docs_status` instead reported `root=/Users/dave/git/personal/buzz-router`; that server
  was not trusted, and discovery used the local documentation-index fallback.
- Code came from CodeGraph and from narrow Grep/Read lookups:
  - The CodeGraph MCP server's default project is a different codebase. Called with
    `projectPath=/Users/dave/git/kyber-weave2`, one query succeeded.
  - The P2.1b worktree has no CodeGraph index. Its capture-core citations are narrow
    Read lookups, labelled **[self-gathered]**.
- `docs validate .` and `docs drift .` both report zero findings on this approved amendment.

## Problem and goal

**Problem.** KyberDash's context surfaces are empty or show dashes:
- the turn inspector;
- the harness Scorecard's Context Hygiene and pressure;
- the Context Doctor context detectors.

The cause is a data gap in the canonical store, not a UI defect. Facts verified by the
conductor on 2026-10-07:

- **Content.** There are 677 sessions. `records.content_json` is `{}` on every row, and
  `records.parts_json` holds content. A 14-day purge empties content
  ([ADR 0018](../adr/0018-kyberdash-content-retention-purge.md)).
- **Context window.** Every harness rollup except `codex-desktop` reports `context_pressure`
  as `not_measurable` with the reason "No source reported a context window for N recorded
  session(s)". `codex-desktop` reports 0.7624 (derived).
  - The window is read only from record attributes listed in `CONTEXT_LIMIT_KEYS`
    (`dash/src/canon/context-window.ts:25`).
  - The #181 refusal to invent a window when the model is unknown or has no catalog row is
    correct and stays. D15 adds a vendor-documented catalog lookup when the model is known.
- **Gemini / Antigravity.** 22,035 records sit under harness `gemini`, source `agy`,
  dated Sep 24 – Oct 1, all with parts. Their raw spans carry
  `gen_ai.agent.name: antigravity`.
  - The attribution fix (PR #220, issue #195) and `kyber renormalize --source agy` exist,
    but the renormalize was never run on this store.
- **Claude Code.**
  - Turns are OTel `claude_code.llm_request` spans whose parts are empty. They carry
    `request_id` and `gen_ai.response.id`; transcripts carry `requestId` and `message.id`.
  - PR #233's twin dedupe transplants content by timestamp proximity, and only 5 sessions
    gained content.
  - PR #276 pairs Claude reader turns onto `synth:` records by message id.
- **Cursor.** 23,003 records, none with parts or a window.
  - Hypothesis, corroborated by code but not verified at runtime:
    - `dash/src/providers/cursor.ts:968` sets `turnId` only when `bubble_type === 1`.
    - `cursorReader` keys its turns by `requestId` and declares
      `positionalPairingUnsafe: true` (`dash/src/synth/readers/cursor.ts:305`).
    - Token-bearing bubbles therefore never pair **[self-gathered]**.
- **Pi.** `~/.pi/agent/models-store.json` declares `contextWindow` per model, but session
  JSONL does not carry it. What `~/.pi/agent/observme.yaml` configures is unverified.

**Goal.** Make the turn inspector, the Scorecard and the Context Doctor show real content and
real context windows for the six priority harnesses (D6), through:
- harness OpenTelemetry export into the KyberDash receiver (D2), enabled by a new reversible
  KyberDash command (D3);
- capture of all content with identity removed (D4);
- harness-declared windows stored under their own provenance (D5);
- vendor-documented model windows stored in a refreshable internal catalog under distinct
  catalog provenance (D15);
- the two pairing fixes that do not need OTel at all (Cursor and Claude Code).

Never fabricate content or a window ([honest unobservability](../rules/honest-unobservability.md)).
A catalog hit is a source-cited documented value, not a measurement. An unknown model or a
known model with no catalog row remains explicitly unobservable.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver as a **plan**, not a spec. The architect's intake recommended PLAN, phased, with a spec trigger at the D7 gate. | User via conductor, 2026-10-07 |
| D1 | `~/.kyberdash/canon.db` is ephemeral; reset or delete is acceptable. No backup, backfill, or migration tasks for existing rows. | User via conductor, 2026-10-07 |
| D2 | Harnesses export only to the KyberDash receiver: `127.0.0.1:4318`, OTLP/HTTP, `POST /v1/traces` and `POST /v1/logs`, JSON or protobuf. Aspire is not a target and `AspireSource` is not wired. The plan must say how the receiver is kept running (answered by D9). | User via conductor, 2026-10-07 |
| D3 | A new KyberDash CLI command enables each harness's export. It writes the per-harness user-level config: `~/.claude/settings.json` env, `~/.codex/config.toml` `[otel]`, `opencode.json`, VS Code settings, `~/.pi/agent/*`, and Antigravity's settings. It must be inspectable (status, dry-run), reversible (disable/restore) and testable against a temporary HOME, and be reconciled with ADR 0026 where that applies. Agents never hand-edit the user's config. Tasks build and test the command; the user runs it. | User via conductor, 2026-10-07 |
| D4 | Capture all content: system prompt, user prompts, tool schemas, tool arguments and output, skills, and all turn data. Drop identity fields: `user.email`, account ids and uuids, organization id, and similar. This replaces the #196 Codex OTLP allowlist for prompts and tool arguments/output. Committed fixtures are synthetic: real span and log shapes and attribute keys, with invented content. | User via conductor, 2026-10-07 |
| D5 | A window a harness declares in its own local config counts, for example Pi `models-store.json` `contextWindow` or a statusline `context_window_size`. It is stored with its own provenance, distinct from per-turn telemetry. Pressure from it is `derived`, never `measured`. D15 supersedes the former prohibition on a #203 model catalog. | User via conductor, 2026-10-07; amended by D15 |
| D6 | Priority order for OTel enablement: Copilot, Claude Code, Codex, OpenCode, Pi, Antigravity. | User via conductor, 2026-10-07 |
| D7 | Where a harness has no out-of-box way to export content, the plan stops at a user decision gate (G1-D7). Post-gate collection work is not part of the approvable scope. | User via conductor, 2026-10-07 |
| D8 | `development-mode: test-first`. Every implementation task carries a Test contract, and RED comes before GREEN. | User via conductor, 2026-10-07 |
| D9 (Q1=a) | **Receiver keep-alive.** Reuse the tray's existing "host receiver" and "launch at login" settings (`dash/tray/src-tauri/src/settings.rs:39-40`, both off by default). `capture status` and `capture enable` probe `GET 127.0.0.1:4318/healthz` (`OTLP_HEALTHZ_PATH`, `dash/src/otel/receiver.ts:50`) and print the exact remedy. No LaunchAgent and no systemd unit. | User via conductor, 2026-10-07 |
| D10 (Q2=a) | **Content precedence is per bucket, OTel first.** For each canonical content bucket, OTel parts win when present. File parts fill only the buckets the OTel record lacks. No bucket is ever filled from two sources. Counters still come from OTel and are never summed. This amends [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md) D4. | User via conductor, 2026-10-07 |
| D11 (Q3=a) | **Capture edits are key-level.** A KyberDash-owned receipt records, for each file, every written key's prior value (or its absence) and the file's pre- and post-edit SHA-256. `disable` restores prior values only for keys that still hold what KyberDash wrote. Otherwise it refuses, prints the drift, and exits non-zero. This is ADR 0026's receipt-and-digest pattern. | User via conductor, 2026-10-07 |
| D12 (Q4=a) | **Compaction against a declared window.** The compaction-hazard finding fires against a declared window, with measurement class `inferred` and a "declared window" caveat. It therefore ranks below deterministic findings (ADR 0013 D6). | User via conductor, 2026-10-07 |
| D13 | **Executors.** Every task, T1 included, runs on a headless OpenCode, Pi or Cursor CLI agent. T1 is harness-agnostic: web research plus read-only local and store inspection, writing only its output file. | User via conductor, 2026-10-07 |
| D14 | **Stacked local branches; PRs only after council APPROVE.** Each task runs in its own git worktree on a local branch `kyberdash-context/<task-id>`. A task with dependencies branches from its dependency's branch tip. Where it has several (T10: T5 and T7), it branches from one and merges the other locally first. Nothing is pushed until the end-of-run code-reviewer council returns APPROVE. Then one PR per task opens, in dependency order. | User via conductor, 2026-10-07 |
| G1-Q1 (a) | **Pi content route.** Adopt `@dpalfery/pi-statusline`. P2.5 stays as written for option (a): KyberDash maps its Pi-specific content keys and the capture writer is status-only. | User via conductor, 2026-10-07 |
| G1-Q2 (b) | **Antigravity content route.** Adopt the existing user-owned Antigravity statusline bridge. The user adds `declaredContextWindow`; agents never edit that script. P2.6 stays as written for option (b). | User via conductor, 2026-10-07 |
| G1-Q3 (a) | **Codex OTel.** Do not enable Codex OTel. P2.3 is status-only and reports file-sourced coverage. | User via conductor, 2026-10-07 |
| G1-Q4 | **Superseded.** None of former options (a), (b) or (c) was selected. The user confirmed that Copilot and Claude Code do not export a context window over OTel and replaced the proposed gap handling with D15. | User via conductor, 2026-10-07 |
| D15 | **Vendor-documented model-window catalog.** When a record's model is known, KyberDash may resolve its context window from an internal table sourced from official vendor documentation. A user-facing button refreshes the table from those fixed documentation sources. Precedence is harness-reported, then harness-declared, then catalog, then the non-measurable fallback. Catalog provenance has its own `catalog` label, distinct from `reported` and `declared`; catalog-derived pressure is never `measured`. A model with no identity or no exact catalog row remains `not_measurable`. This reverses D5's former "#203 model catalog" prohibition and delivers the context-window catalog from issue #203. | User via conductor, 2026-10-07 |
| A2 | **Prior approve-and-execute, now stopped.** The earlier approved scope was T0–T10, stopping at G1. Phase 2 and everything after G1 were never approved. The user has stopped implementation; A2 does not authorize resuming T8 or T10, merging integration, or executing this revised Draft. | User via conductor, 2026-10-07; stopped by user, 2026-10-07 |
| A3 | **Approve and execute the revised Draft.** Resume T8 and T10 through task review, then execute Phase 2 including P2.8, under the existing D14 branching rules and `MAX_CONCURRENCY: 3`. Development mode remains `test-first`. | User via conductor, 2026-10-07 |
| A4 | **Approve and execute the P2.W amendment (A4-Q1 = (a)).** P2.W may be dispatched with the revised writer dependencies and file scopes. The six writers P2.1b, P2.2a, P2.3, P2.4a, P2.5, and P2.6 remain blocked until P2.W has task-reviewer PASS; P2.1b resumes only on the reviewed P2.W tip. This approval does not authorize starting a writer before that PASS. | User via conductor, 2026-10-07 |
| A5 | **Approve and execute the P2.W2 Draft delta (A5-Q1 = (a)).** P2.W2 may be dispatched. P2.2a retries only after P2.W2 has task-reviewer PASS, on the reviewed P2.W2 tip. This approval does not authorize starting the P2.2a retry before that PASS. | User via conductor, 2026-10-08 |

## Decision ledger

| Id | Decision | Options | Recommendation | Dependency | Status |
|---|---|---|---|---|---|
| A4-Q1 | Approve the P2.W capture-writer declaration seam and the revised writer dependencies and file scopes in this amendment. | (a) Approve and execute the amendment; (b) revise the seam contract before execution. | (a) — it preserves every existing writer behaviour while putting the shared multi-file, typed-value and status capabilities in the capture core rather than one harness module. | P2.W may dispatch. All six capture writers remain blocked until P2.W has task-reviewer PASS; P2.1b resumes only on the reviewed P2.W tip. | ANSWERED (a), recorded by A4 |
| A5-Q1 | Approve the P2.W2 ensure-if-absent declaration seam and make P2.2a depend on it. | (a) Approve and execute the Draft delta; (b) revise the seam contract before execution. | (a) — P2.2a already requires preserve-if-present behaviour, and the harness-agnostic core must decide which keys were actually written so receipts and disable remain truthful under D11. | P2.W2 may dispatch. P2.2a retries only after P2.W2 has task-reviewer PASS, on the reviewed P2.W2 tip. | ANSWERED (a), recorded by A5 |

**Spec trigger (G1).** Two of the six D6 harnesses, Pi and Antigravity, lack native export,
so the count condition is met. The trigger converts that work to a specification only when
the user chooses to **build** a non-OTel collector for them.
- **Adopting the user's existing collectors does not count as building one.**
  - `@dpalfery/pi-statusline` is already-deployed OTLP exporter code (a Pi extension).
  - The agy bridge is an already-deployed statusline script that emits OTLP.
  - Both already post to the receiver. The KyberDash work is adapter mapping inside the
    existing ingest seam, plus a status-only writer.
- G1-Q1 (a) and G1-Q2 (a) or (b) therefore keep the work in this plan.
- If either G1-Q1 or G1-Q2 is answered (c), that harness goes to a separate follow-up plan.
  If both are (c), the work becomes a specification (product-owner).
- The selected G1-Q1 (a) and G1-Q2 (b) do not trigger a specification. D15 replaces
  G1-Q4 and uses the existing context-window derivation seam rather than a new collector.

## Investigation findings

### Ingest seam: where exported telemetry lands

1. `OtlpReceiver` (`dash/src/otel/receiver.ts:1056`) accepts HTTP only, on port 4318.
   - It serves `/v1/traces`, `/v1/logs` and `GET /healthz`.
   - It decodes JSON and protobuf.
   - It has **no gRPC and no `/v1/metrics`**, so every exporter must use
     `http/protobuf` or `http/json` **[self-gathered]**.
2. `ingestBatch` (`dash/src/canon/ingest.ts:123`) projects each span to `RawSpan`. It uses
   `service.name` as a grouping key only, never as attribution evidence.
3. `AdapterRegistry.attribute` runs a fingerprint vote over
   `ADAPTERS = [pi, copilot, antigravity, gemini, claude-code, codex]`
   (`ingest.ts:42`). Unclaimed spans are quarantined.
4. `HarnessAdapter.normalize` builds the record. The shared content keys are
   `gen_ai.system_instructions`, `gen_ai.input.messages`, `gen_ai.output.messages`, and
   `gen_ai.tool.definitions` / `gen_ai.request.tools`
   (`dash/src/canon/adapters/copilot.ts:158-163`).
5. Logs go through `ingestLogBatch` (`dash/src/canon/log-ingest.ts`).
   - It correlates on `(trace_id, span_id)`, then on session id within a 5 s window.
   - It maps only `claude_code.api_request_body`, through `parseClaudeApiRequestBody`
     (`adapters/claude-code.ts:56`). That function reads `system` and `tools` and ignores
     `messages` **[self-gathered]**.
6. **No OpenCode or Cursor adapter is registered**, so OpenCode OTel spans would be
   quarantined or claimed only on generic `gen_ai.*` evidence.
7. The Codex adapter persists only a privacy allowlist (#196). D4 replaces that allowlist.

### Window derivation

- `contextLimitOf` (`dash/src/canon/context-window.ts:71`) returns
  `ContextLimitSource = 'reported' | 'default'`. The first `llm.invoke` record that names a
  window wins.
- `digestSessionPayloads` (`dash/src/canon/harnesses.ts:108`) leaves default-window
  sessions out of pressure and counts them as `unknownWindowSessions`.
- Pressure is tagged `derived` only when counts were derived (`harnesses.ts:137,155,350`).
- Readers already flow a per-turn `contextWindow` into raw (`dash/src/synth/synth.ts:419`).
  A declared window must use a **different** key, or it would be classed as `reported`
  **[self-gathered]**.
- D15 extends the planned precedence to `reported` → `declared` → `catalog` → fallback.
  Catalog lookup is exact by the known model identifier and is never measurement.

### Availability is a fixed table, not evidence

`harnessDimensionAvailability` (`dash/src/canon/measurability.ts:908`) is keyed only by
harness name.
- OpenCode `context_pressure`, `context_hygiene`, `cost` and `token_usage` are hard-coded as
  unavailable ("experimental OpenTelemetry disabled and exports no supported session files"),
  even though `opencodeReader` and its provider exist.
- Claude Code `tool_definitions`, Pi tool definitions, and Cursor are fixed the same way.

Callers:
- `dash/src/canon/harnesses.ts`: lines 52, 299, 334, 371, 392, 413.
- `dash/src/analysis/signals.ts`: lines 135, 381, 665, 751.

Until availability follows evidence, enabling OTel cannot move these surfaces
**[self-gathered]**.

### Content precedence today

The derived layer's `dedupeTwinTurns` (`dash/src/canon/twin-dedupe.ts:217`) joins an OTel row
and a file row only when:
- their counters are exactly equal, and
- they sit within the 60 s window (`TWIN_TURN_MAX_SKEW_MS`).

It then unions the keeper's and the donor's parts by exact `(part, text)`
(`:263-288`). Two consequences follow:
- The Claude OTel and file rows of one turn often have different counters, so they never
  join. This is consistent with the 5-session outcome.
- Once Claude raw bodies carry `messages`, the same conversation could be counted twice in
  different formatting.

Hence D10 and T6 **[self-gathered]**.

### Command surface and boundaries

- `registerKyberCommands` (`dash/src/cli/register.ts:94`) owns the `kyber` group:
  `otel`, `backfill`, `renormalize`, `build`, `cursor-hook`, `antigravity-statusline`.
- The tray can host the receiver child, behind the `hostReceiver` setting (off by default).
- [ADR 0026](../adr/0026-kyber-utilities-owned-files-not-settings.md) forbids **Kyber
  Utilities** from creating, editing or reading harness settings files.
  - D3 deliberately does the opposite for a **KyberDash** command, so the departure must be
    recorded in its own ADR and scoped to KyberDash. Kyber Utilities stays unchanged.
  - What D3 reuses from ADR 0026 is its own receipt and the per-file SHA-256 digests
    (T9, ADR 0031).
- `dash/package.json` has no TOML, YAML or JSONC library. VS Code `settings.json` and
  `opencode.json` are commonly JSONC, which is why T8 proposes `jsonc-parser`.
- [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md) records Claude Code's
  default content cap of 61,440 UTF-16 units. It also records that captured content must
  never land in tracked files (R12.3), which is consistent with D4's synthetic fixtures.

### Capture writer declaration gap

The P2.1b worktree at unchanged tip
`3d4c3060e233f84b4175f8736b22834491423b0d` contains T8 and the committed Copilot RED
contract. Narrow source inspection confirmed the implementer's blocker **[self-gathered]**:
- `ManagedHarnessWriter` declares one `resolvePath` and
  `desiredKeys: Record<string, string>`.
- `runCapture` resolves one file per writer and constructs status output entirely inside
  `dash/src/capture/index.ts`.
- `applyJsonEdits` accepts string values even though `jsonc-parser` can preserve typed JSON
  values once the declaration and core carry them.
- There is no declaration seam for config-derived warnings or a process-env status snippet.

The supplied contract run fails 5 of 7 tests; only registration and "does not edit shell
rc" pass. No production edit is committed and the RED assertions were not weakened. Those
facts make the failure a shared T8 declaration limitation, not a Copilot-only
implementation gap. P2.W owns that shared seam before any writer resumes.

### Antigravity statusline

The statusline payload type reads only `context_window.current_usage`
(`dash/src/providers/antigravity.ts:122`). Whether `context_window_size` is present is a
T1 fact **[self-gathered]**.

### Gemini / agy repair is operational

Under D1 the owner resets the store (O1). Live agy spans then arrive attributed to
`antigravity` by the PR #220 adapter. No code gap was found. Keeping the old rows instead
means running `kyberdash kyber renormalize --source agy`.

## Phases and gates

| Phase | Contents | Approval |
|---|---|---|
| Phase 0 | T0: land the uncommitted Findings UI change | Approved (A2) |
| Phase 1 | T1: per-harness discovery and the capability matrix (read-only) | Approved (A2) |
| Phase A | T2–T10: shared seams, pairing fixes, capture CLI core, ADRs, contract bumps. None needs discovery output. | Approved (A2) |
| **G1** | **Capability gate.** T1's matrix is persisted into this plan, which returns to Draft. The architect writes the Phase 2 tasks for harnesses with out-of-box content export. For each harness without it, the plan stops at **G1-D7** and the conductor asks the user: hooks, statusline, proxy, or accept `not_measurable`. **Spec trigger:** if two or more of the six D6 harnesses have no out-of-box content export **and** the user chooses to build a non-OTel collector for them, that work becomes a specification (product-owner) and does not extend this plan. One such harness gets a separate follow-up plan. | Gate |
| Phase 2 | P2.W and P2.W2 plus P2.0–P2.8 per-harness capture writers, adapter mappings, consistency repairs and the model-window catalog | A3 baseline plus A4 amendment approval and A5 delta approval. P2.W may dispatch. A5 approved P2.W2, which may be dispatched. P2.2a retries only after P2.W2 has task-reviewer PASS. The other writers wait for P2.W task-reviewer PASS. In-flight P2.R, P2.0, P2.4b, P2.T5 and P2.8 non-writer work may continue. |
| O1 | Owner run against a `kyberdash` build of the local `kyberdash-context/integration` tip: reset the store, run `capture enable`, keep the receiver up, do live sessions | User-only; after the approved implementation tasks |
| Closeout | T11 docs harvest, T12 review council over the integrated stack, then per-task PRs in dependency order (D14), then T13 archive | Approved (A3), after O1 |

"Out-of-box content export" means the harness's own supported settings, env or config file
make it emit OTLP/HTTP traces or logs that carry turn content: prompts or messages, at
minimum. Partial content, such as messages without tool schemas, still counts, with the
missing buckets documented.

## Test contract (`test-first`)

Every runner command runs from the repository root. Each task uses its **own test files**,
so parallel worktrees never edit a shared test file. P2.1b's already-committed RED file is
the explicit exception to "new": P2.W must not edit it and has its own seam test. Changing
an approved row, including weakening an assertion to reach green, returns the plan to Draft.

| Task | Test project or file | Runner command | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | `dash/web/src/components/analysis/Scorecard.test.tsx` (the existing uncommitted case) | `npm --prefix dash exec vitest run web/src/components/analysis/Scorecard.test.tsx` | The Context Doctor findings list shows every finding inside a scroll region sized to four cards, with no five-item truncation | The test hunk applied alone on clean `main` fails | The same test passes with the `FindingList.tsx` and `ContextDoctor.tsx` change |
| T2 | `dash/src/canon/context-window-declared.test.ts` (new) | `npm --prefix dash exec vitest run src/canon/context-window-declared.test.ts` | A declared-only record gives `{limit, 'declared'}`. Reported plus declared gives `'reported'`. Neither gives the unchanged default. A `ReaderTurn.declaredContextWindow` reaches raw under the declared key and never under `contextWindow`. Per D12, a session over its declared window yields a compaction-hazard finding with measurement class `inferred` and a "declared window" caveat. A default-window session still yields none. | The new assertions fail on `main`, which has no `'declared'` class | All pass. The existing `context-window`, `findings` and report tests stay green unmodified. |
| T3 | `dash/src/canon/availability-evidence.test.ts` (new) | `npm --prefix dash exec vitest run src/canon/availability-evidence.test.ts` | Evidence (bucket parts, or a reported or declared window) overrides a static refusal. With no evidence the static reason comes back verbatim. Declared-window pressure is `derived`, never `measured`. Default-window sessions stay `unknownWindowSessions`. The Copilot `tool_yield` exclusion (#235) is unchanged. | The OpenCode-with-window and Claude-with-tool-definitions cases fail on `main` | All pass. The existing `measurability`, `harnesses` and `signals` tests stay green. |
| T4 | `dash/src/otel/identity.test.ts`, `dash/src/canon/adapters/opencode.test.ts` (new) | `npm --prefix dash exec vitest run src/otel/identity.test.ts src/canon/adapters/opencode.test.ts` | Identity keys are absent from decoded span, resource and log attributes, so absent from stored `raw`. Session and conversation ids and `gen_ai.*` are retained. The OpenCode placeholder claims no span, and the existing ingest vote results are unchanged. | Identity keys survive decode on `main` | All pass. The existing `receiver`, `ingest` and adapter tests stay green. |
| T5 | `dash/src/synth/cursor-pairing.test.ts` (new), plus fixture `dash/src/synth/fixtures/cursor-pairing.json` (synthetic) | `npm --prefix dash exec vitest run src/synth/cursor-pairing.test.ts` | For one request with a prompt bubble and a token-bearing bubble that carries `contextWindow`, exactly one synthesized `llm.invoke` carries the reader parts and the window. Other bubbles of that request carry no duplicate parts, and span identities stay distinct. | The assertion fails on `main`. **If it passes, the hypothesis is false: stop and return evidence (BLOCKED).** | Passes. `src/synth/provider.test.ts` and the Cursor tests stay green. |
| T6 | `dash/src/canon/twin-dedupe-claude-join.test.ts` (new) | `npm --prefix dash exec vitest run src/canon/twin-dedupe-claude-join.test.ts` | A Claude OTel record (counters A, `gen_ai.response.id = msg_1`, no parts) and a file record (counters B, native id `msg_1`, conversation parts) in one share give one record with OTel counters and file parts. Mismatched ids do not join. Per-bucket OTel first (D10): when the OTel record carries `system_prompt` and `tool_definitions` and the file record carries `conversation_history`, the result holds all three, each from one source. When both carry `conversation_history`, only the OTel parts remain in that bucket. | The join case fails on `main` (exact-counter path only). The same-bucket case fails on `main` (union by text). | Passes. The existing twin-dedupe tests (#181, #182, #231, #232) stay green. The one exception: an existing assertion of a same-bucket OTel-and-file union contradicts D10, and may be changed only to the D10 outcome. Each such change is listed in the PR. |
| T7 | `dash/src/synth/readers/pi-declared-window.test.ts` (new), plus fixture `dash/src/synth/fixtures/pi-models-store.json` (synthetic) | `npm --prefix dash exec vitest run src/synth/readers/pi-declared-window.test.ts` | A Pi turn whose model matches a `models-store.json` entry carries `declaredContextWindow`. A missing or unparseable store gives no window and no error. The path resolves from a temporary HOME. | Fails on `main` | Passes. The existing Pi tests stay green. |
| T8 | `dash/src/capture/*.test.ts` (new), all against a temporary HOME | `npm --prefix dash exec vitest run src/capture` | `--dry-run` writes nothing (digests unchanged). `enable` writes and records the receipt (prior values plus pre- and post-edit SHA-256), and a second `enable` is a no-op. `disable` on an untouched file restores it byte-for-byte. If a written key was changed after `enable`, `disable` refuses: it leaves the file unchanged, prints the drift, and exits non-zero (D11). `status` reports per-harness state and receiver liveness: no listener, foreign listener, or KyberDash. When the receiver is down, the remedy names the tray's "host receiver" and "launch at login" settings and `kyberdash kyber otel`, and nothing is installed (D9). Usage errors exit 2. Pending-discovery harnesses report "not yet supported". | The command does not exist on `main` | All pass. `src/cli/register.test.ts` stays green. |
| T10 | `dash/src/refresh/contract-bump-cursor-pi.test.ts` (new) | `npm --prefix dash exec vitest run src/refresh/contract-bump-cursor-pi.test.ts` | A stale Cursor or Pi checkpoint written under contract `'1'` is re-read after the bump | Fails before the bump | Passes. The existing refresh pipeline tests stay green. |
| T1 | **No test (read-only discovery).** Replaced by the capability matrix file, reviewed at G1. | n/a | n/a | n/a | The output file exists at the named path. Every matrix cell is sourced (a vendor doc URL with its version or date, or a local read-only observation) or marked unknown with a reason. `git status --short` in the main checkout is identical before and after. No harness config file's mtime or SHA-256 changed. |
| T9 | **No test (docs).** Replaced by `docs validate` and `docs drift`. | see gates | ADR 0029–0031 exist and are indexed | n/a | Zero findings |
| T11–T13 | **No test (docs, review, closeout).** Replaced by gates, the review verdict, and `docs validate --merge-ready`. | see gates | n/a | n/a | Zero findings; council verdict recorded |
| P2.x | The task-specific rows in the approved Phase 2 Test contract below are fixed and use synthetic fixtures with real keys per D4. | see Phase 2 rows | see Phase 2 rows | see Phase 2 rows | see Phase 2 rows |

## Dispatchable tasks

Every task is cold, and works only within its declared file scope.
- It gets its own git worktree on the local branch `kyberdash-context/<task-id>`, based as D14
  sets out: on `main` at `c5b35ec` with no dependencies, otherwise on the dependency's branch
  tip, with any other dependencies merged locally first.
- It commits locally and never pushes.
- Its PR opens only after the T12 council returns APPROVE, in dependency order (D14).
- The exception is T1, which uses no worktree, makes no branch and no PR, and writes only
  its output file.
- Executors are headless OpenCode, Pi or Cursor CLI agents (D13).

**Dash gates** (run in every code task):

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

**Docs gates** (run when `docs/` changes):

```bash
dotnet build KyberWeave.sln -c Release
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

**Shared rules for every task:**
- Disclose the known pre-existing host failure `dash/src/canon/migration.test.ts` (DROP
  COLUMN), as earlier plans did. Do not fix it.
- Do not commit this plan or its index row.
- User-visible strings say "kyberdash".
- Edit no file under `~`. Read-only reads of harness config are permitted only where a task
  says so.

### T0 — Land the Findings UI scroll region (Phase 0)

- **Objective.** Ship the already-green uncommitted change as its own PR:
  - `FindingList` becomes a scroll region sized to four cards.
  - `ContextDoctor` stops passing `maxItems={5}`.
  - `Scorecard.test.tsx` pins the behaviour.
- **Files.**
  - `dash/web/src/components/analysis/FindingList.tsx`
  - `dash/web/src/pages/ContextDoctor.tsx`
  - `dash/web/src/components/analysis/Scorecard.test.tsx`
- **Input.** The conductor exports the working-tree diff of these three files from
  `/Users/dave/git/kyber-weave2` as a patch. Worktrees off `main` do not contain it.
- **Acceptance.**
  - RED is shown by applying the test hunk alone.
  - GREEN is the full patch.
  - The dash gates pass.
  - No other file changes.
- **Depends on.** Nothing.
- **Skills.** `react-dev`, with `test-dev` for RED.

### T1 — Discovery: per-harness capability matrix (Phase 1, read-only)

- **Executor.** Any headless agent (OpenCode, Pi or Cursor CLI) with a shell and, ideally, a
  web fetch or web search tool. Nothing in this task depends on a particular harness's agent
  or tool names.
- **Objective.** For the six D6 harnesses, establish the facts that G1 and the Phase 2
  capture writers need, and write them to one file. The harnesses are:
  - Copilot: VS Code Chat and Copilot CLI;
  - Claude Code;
  - Codex;
  - OpenCode;
  - Pi;
  - Antigravity.
- **Hard constraints.**
  - **Edit nothing. The only write permitted is the output file below**, plus `mkdir -p`
    of its directory.
  - Specifically, do not:
    - modify, create, rename or `touch` any file under `~` or in the repository;
    - run git commands that change state (no checkout, stash, add, commit, or worktree);
    - start, stop or reconfigure any harness or the KyberDash receiver;
    - run any `kyberdash` subcommand other than `--version`;
    - open `canon.db` other than read-only.
  - Harness CLIs may be run only with `--version` or `--help`.
  - **Never copy secrets or content into the output.** Record key names, value types, and
    non-sensitive values such as endpoints and booleans only.
    - Redact API keys, tokens, `OTEL_EXPORTER_OTLP_HEADERS` values, emails and account ids
      as `<redacted>`.
    - Never quote prompt, message, tool or system text from config or from the store.
- **Output file.**
  `/Users/dave/git/kyber-weave2/artifacts/kyberdash-context-capture/t1-capability-matrix.md`.
  It is untracked: `/artifacts/` is gitignored and this plan stays uncommitted.
- **Method.**
  1. **Integrity baseline.**
     - Record `git -C /Users/dave/git/kyber-weave2 status --short`.
     - Record the `shasum -a 256` and mtime of every config file inspected in step 3.
  2. **Web research of vendor documentation.** Use official docs, official repositories and
     release notes. Use the agent's web tool, or `curl -sL` for read-only fetches.
     - Cite every fact with its URL and a version, release tag, or page date.
     - With no network access at all, mark each fact `unknown: no web access` and finish.
       Do not guess.
  3. **Read-only inspection of local config**, where present:
     - `~/.claude/settings.json`
     - `~/.codex/config.toml`
     - `~/.config/opencode/opencode.json` or `opencode.jsonc`
     - VS Code user `settings.json`:
       - macOS: `~/Library/Application Support/Code/User/` and `Code - Insiders/User/`;
       - Linux: `~/.config/Code/User/`.
     - `~/.copilot/`
     - `~/.pi/agent/observme.yaml`, `models-store.json` and `settings.json`
     - `~/.gemini/` and the `~/.gemini/antigravity*` roots
     - the KyberDash agy statusline recording `antigravity-statusline.jsonl`, for its field
       names only
  4. **Read-only inspection of the live store.** Use
     `sqlite3 'file:/Users/<user>/.kyberdash/canon.db?mode=ro'`, and run `.schema` first so
     queries match the actual columns. Report counts and key names only:
     - `ingest_log` sources with record sums and last-received time;
     - per harness, the count of records whose raw carries any of `contextWindow`,
       `gen_ai.request.max_context_tokens`, `gen_ai.request.context_window` or
       `model_context_window`;
     - Cursor `llm.invoke` records with and without a `turnId` in raw;
     - the distinct attribute **keys** on Claude `llm_request` raw spans;
     - whether `cursor-hook` (`cursor.*`) spans appear in `records` or in quarantine;
     - for Codex, which originator or client has window-bearing records, and which lacks
       them.
  5. **Port 4318.** Run `lsof -nP -iTCP:4318 -sTCP:LISTEN` and
     `curl -s -m 2 http://127.0.0.1:4318/healthz`. Name which process holds the port and
     whether it identifies as KyberDash.
  6. **Integrity check.** Repeat step 1. The output must show the status and digests
     unchanged.
- **Output contents, in this order:**
  1. **Environment snapshot**: harness CLI versions, the holder of port 4318, the `healthz`
     result.
  2. **Capability matrix**: one row per harness, with exactly these columns:
     - harness and surface;
     - exporter mechanism and config file;
     - endpoint and protocol keys, stating http/protobuf or http/json support;
     - content flags (user prompts, tool arguments and output, system prompt, tool
       schemas, skills, raw API bodies);
     - span and log attribute keys per canonical bucket (`system_prompt`,
       `tool_definitions`, `conversation_history`, `tool_result_content`,
       `instruction_context`);
     - truncation limits;
     - reported window attribute;
     - declared-window source (config file or statusline field);
     - identity attributes emitted;
     - current destination, from config and `ingest_log`;
     - **out-of-box content export: yes, partial or no**;
     - sources.
  3. **Per-harness detail**: the exact keys a capture writer would set to point at
     `http://127.0.0.1:4318` with content on. Give the file, the key path and the value
     type, and any conflicts with keys the user already has.
  4. **Store observations**: the queries run, with counts only.
  5. **Unknowns**, each with the reason.
  6. **Integrity evidence**: before-and-after status and digests.
- **Acceptance.** The T1 row of the Test contract. Finish with a final message that gives the
  output path and a one-line verdict per harness: `yes`, `partial` or `no` out-of-box
  content export. The conductor relays the file to the architect, who persists the matrix
  into this plan at G1.
- **Depends on.** Nothing. It may start at once, in parallel with Phase A. It holds one
  dispatch slot but uses no worktree and opens no PR.
- **Skills.** `research` (read-only, no-test task-reviewer contract).

### T2 — Seam 1: declared context-window provenance

- **Objective.**
  - Add `'declared'` to `ContextLimitSource`, plus a dedicated declared key, distinct from
    `CONTEXT_LIMIT_KEYS`.
  - Precedence is reported, then declared, then default. The first-record rule per class is
    unchanged.
  - Add `ReaderTurn.declaredContextWindow` and carry it into raw in `synthesizeCall`.
  - The compaction-hazard detector also fires against a declared window, with measurement
    class `inferred` and a "declared window" caveat (D12). A default window still suppresses
    it (#181).
  - Report types and labels learn `'declared'`.
- **Files.**
  - `dash/src/canon/context-window.ts`
  - `dash/src/synth/readers/types.ts`
  - `dash/src/synth/synth.ts` (the `rawCall` assembly in `synthesizeCall` only)
  - `dash/src/analysis/findings.ts` (the window gate around `:1275-1281`, `:1373`)
  - `dash/src/analysis/report/build.ts`
  - `dash/src/analysis/report/render-text.ts` and `render-markdown.ts`, only where they
    branch on the source
  - New test: `dash/src/canon/context-window-declared.test.ts`
- **Acceptance.** The T2 Test contract row, and the dash gates.
- **Depends on.** Nothing. D12 is decided.
- **Skills.** `test-dev` for RED, then a TypeScript implementer.

### T3 — Seam 2: evidence-driven availability

- **Objective.** For content- and window-dependent dimensions, compute availability from the
  evidence in the harness's recorded sessions:
  - bucket parts for `tool_definitions`, `schema_cost` and `tool_yield`;
  - a reported or declared window for `context_pressure`.

  Static reasons remain the verbatim no-evidence fallback. Further rules:
  - Declared-window pressure is `derived` (D5).
  - Default-window sessions keep the #181 refusal.
  - Copilot `tool_yield` stays excluded (#235).
- **Files.**
  - `dash/src/canon/measurability.ts`
  - `dash/src/canon/harnesses.ts`
  - `dash/src/analysis/signals.ts` (callers at `:135`, `:381`, `:665`, `:751`)
  - New test: `dash/src/canon/availability-evidence.test.ts`
- **Acceptance.** The T3 Test contract row, and the dash gates.
- **Depends on.** T2, for the `'declared'` type.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T4 — Seam 3: identity stripping at the receiver, and an OpenCode adapter slot

- **Objective (1).** Strip identity attributes (D4) from span, resource and log attributes
  at receiver decode, before anything is stored.
  - Use a new pattern-based rule covering `user.email`, `*.account_id`, `*.account_uuid`,
    `user.id`, `organization.id`, `enduser.id`, and their `org`/`account` variants.
  - Retain `session.id`, `conversation.id` and `gen_ai.*`.
  - A pattern rather than a fixed list keeps Phase 2 tasks out of this file.
- **Objective (2).** Register a placeholder `opencodeAdapter` in `ADAPTERS`.
  - It detects nothing and claims no span.
  - It sits at a commented tie-break position.
  - The Phase 2 OpenCode task then edits only its own adapter file.
- **Files.**
  - New: `dash/src/otel/identity.ts`
  - `dash/src/otel/receiver.ts` (decode path only)
  - `dash/src/canon/ingest.ts` (`ADAPTERS` only)
  - New: `dash/src/canon/adapters/opencode.ts`
  - New tests: `dash/src/otel/identity.test.ts`, `dash/src/canon/adapters/opencode.test.ts`
- **Not in scope.** The Codex allowlist (P2.3).
- **Acceptance.** The T4 Test contract row, and the dash gates.
- **Depends on.** Nothing.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T5 — Cursor turn pairing: confirm, then fix

- **Objective.**
  1. Confirm the hypothesis with the RED fixture.
  2. If it is confirmed, give token-bearing bubbles a pairing key that is distinct from the
     identity-bearing `turnId`, for example `ParsedProviderCall.pairingId`.
  3. `matchingTurns` pairs on `turnId ?? pairingId`.
  4. Each reader turn pairs with at most one call per request.
  5. `positionalPairingUnsafe` stays `true` for Cursor.
- **Files.**
  - `dash/src/providers/types.ts`
  - `dash/src/synth/provider.ts` (`matchingTurns`)
  - `dash/src/providers/cursor.ts`
  - `dash/src/synth/readers/cursor.ts`, only if needed
  - New test: `dash/src/synth/cursor-pairing.test.ts`
  - New fixture: `dash/src/synth/fixtures/cursor-pairing.json` (synthetic)
- **Acceptance.** The T5 Test contract row, the dash gates, and Claude, Codex and Pi pairing
  unchanged. If RED is not obtainable, return BLOCKED with the evidence. Do not change code.
- **Depends on.** Nothing.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T6 — Claude Code: deterministic OTel-to-transcript join, and per-bucket precedence

- **Objective.** In `dedupeTwinTurns`, join a Claude OTel `llm_request` record to a Claude
  file-synth record when the OTel `gen_ai.response.id` equals the synth record's native id
  (`message.id`, from PR #276).
  - The join does not depend on counter equality or the 60 s window.
  - Counters come from OTel and are never summed.
  - Content merges per bucket, OTel first (D10):
    - for each canonical bucket, the OTel record's parts stand when present;
    - the file record's parts fill only the buckets the OTel record lacks;
    - no bucket ever holds parts from both sources.

    This rule replaces the union-by-exact-text merge at `twin-dedupe.ts:263-288` for every
    OTel-and-file pairing, not only Claude's. File-and-file joins (#231, #232) keep their
    existing behaviour.
  - Fall back to `request_id` = `requestId` only if a fixture shows `gen_ai.response.id`
    missing.
- **Files.**
  - `dash/src/canon/twin-dedupe.ts`
  - New test: `dash/src/canon/twin-dedupe-claude-join.test.ts`
- **Acceptance.** The T6 Test contract row, and the dash gates. If the OTel and file records
  fall in different shares (session-key mismatch), return BLOCKED with the evidence. Do not
  widen the share key.
- **Depends on.** Nothing. D10 is decided.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T7 — Pi declared window from `models-store.json`

- **Objective.** Pi turns carry `declaredContextWindow` from the user's
  `~/.pi/agent/models-store.json` entry for the turn's model (D5).
  - The path is resolved from HOME and is injectable.
  - A missing or unparseable store means no window, never a default.
  - The agent may read the real file read-only to learn its shape. The committed fixture is
    synthetic.
- **Files.**
  - `dash/src/synth/readers/pi.ts`, or `dash/src/providers/pi.ts` if turns are built there.
    Use one of the two, and name it in the PR.
  - New test: `dash/src/synth/readers/pi-declared-window.test.ts`
  - New fixture: `dash/src/synth/fixtures/pi-models-store.json`
- **Acceptance.** The T7 Test contract row, and the dash gates.
- **Depends on.** T2.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T8 — Capture CLI core: `kyberdash kyber capture status | enable | disable`

- **Objective.** Build the harness-agnostic command for D3, D9 and D11.
- **Options.**
  - `--harness <id>`: repeatable; defaults to the six D6 harnesses.
  - `--dry-run`: print the exact per-file change and write nothing.
  - `--endpoint`: default `http://127.0.0.1:4318`. Only OTLP/HTTP protocols may be written.
- **Receipt.**
  - Kept at `~/.kyberdash/capture-receipt.json`. It is KyberDash's own, separate from Kyber
    Utilities' and Kyber-Squad's.
  - Per file it records the path, format, keys written, prior values (or absence), and the
    pre- and post-edit SHA-256.
- **`disable` (D11).** For each key that still holds the value KyberDash wrote, restore the
  prior value, or remove the key if it was absent before. If any written key was changed
  since `enable`:
  - leave that file untouched;
  - print the drift: the key, the value written, the current value;
  - exit non-zero.

  Never fall back to restoring the whole file.
- **Edit strategies.**
  - JSON and JSONC go through `jsonc-parser`, which preserves comments and formatting.
    Justify the new dependency in the PR (AGENTS.md).
  - TOML and YAML get a marker-delimited managed block at the end of the file. If a
    conflicting table or key exists outside the block, refuse (exit non-zero) and print the
    snippet.
- **`status`.** For each harness, report:
  - the config path, and whether it was found;
  - expected keys against present keys;
  - drift since `enable`;
  - receiver liveness through `GET /healthz`, telling KyberDash, foreign and none apart.

  `enable` runs the same probe. When the receiver is not running, the remedy names the
  tray's "host receiver" and "launch at login" settings, or `kyberdash kyber otel` (D9).
  The command never installs a LaunchAgent, a systemd unit, or any other process manager.
- **Harness writers.** Six writer modules are pre-created as "pending discovery" stubs, in
  one registry. Phase 2 tasks edit only their own writer file.
- **Platform.** macOS and Linux. Windows reports unsupported.
- **Files.**
  - New: `dash/src/capture/` — `index.ts`, `receipt.ts`, `edit-json.ts`, `edit-block.ts`,
    `probe.ts`, `harnesses/registry.ts`
  - New: `harnesses/{copilot,claude-code,codex,opencode,pi,antigravity}.ts`
  - `dash/src/cli/register.ts` (the `capture` subgroup only)
  - `dash/package.json`, `dash/package-lock.json`
  - New tests: `dash/src/capture/*.test.ts`
- **Acceptance.** The T8 Test contract row, the dash gates, and no write outside a temporary
  HOME in any test.
- **Depends on.** Nothing. D9 and D11 are decided.
- **Skills.** `test-dev`, then a TypeScript implementer.

### T9 — ADRs 0029–0031

- **Objective.** Record three ADRs:
  - **ADR 0029**, per-bucket OTel-first content precedence (D10). It amends ADR 0009 D4.
  - **ADR 0030**, declared context-window provenance (D5). Declared-window pressure is
    `derived`, and compaction findings against a declared window are `inferred` (D12).
  - **ADR 0031**, the KyberDash capture command owning harness telemetry keys (D3), with
    key-level edits and receipt restore (D11) and tray-hosted receiver keep-alive (D9).
    - It is a departure scoped to KyberDash. ADR 0026 stays in force for Kyber Utilities.
    - It borrows ADR 0026's receipt-and-digest pattern.

  Update the ADR index. If another PR has taken those numbers, renumber.
- **Files.**
  - New: `docs/adr/0029-*.md`, `docs/adr/0030-*.md`, `docs/adr/0031-*.md`
  - `docs/adr/README.md`
- **Acceptance.** The docs gates show zero findings.
- **Depends on.** Nothing. D9–D12 are decided. It may run in parallel with the code tasks.
- **Skills.** `architecture-decision-record`, via `docs-dev`.

### T10 — Parser contract bumps for Cursor and Pi

- **Objective.** Bump the Cursor and Pi contracts with new constants in
  `parserContractVersionFor`, so `dash refresh` re-synthesizes units on installs other than
  the owner's. The owner resets per D1. This is the ADR 0016 repair contract, not a
  migration.
- **Files.**
  - `dash/src/refresh/registry.ts`
  - New test: `dash/src/refresh/contract-bump-cursor-pi.test.ts`
- **Acceptance.** The T10 Test contract row, and the dash gates.
- **Depends on.** T5, T7.
- **Skills.** `test-dev`, then a TypeScript implementer.

### Phase 2 — written at G1; P2.W approved by A4; P2.W2 approved by A5

The Phase 2 tasks, their Test contract and their dependencies are in the
[G1 revision](#g1-revision-2026-10-07) section below. A3 remains the baseline approval for
completed and unaffected in-flight work. A4 approved P2.W. A5 approved P2.W2, which may
be dispatched. P2.2a retries only after P2.W2 has task-reviewer PASS. D14 and
`MAX_CONCURRENCY: 3` are unchanged.

### O1 — Owner run (user-only; not dispatched)

1. Stop the receiver. Delete `~/.kyberdash/canon.db` (D1). This also removes the 22,035
   legacy `gemini` and `agy` rows.
2. Run `kyberdash kyber capture status`, then `enable --dry-run`, then `enable`.
3. Keep the receiver running per D9. In the tray, turn on "host receiver" and "launch at
   login", then confirm that `kyberdash kyber capture status` reports the KyberDash receiver
   on port 4318.
4. Restart each harness and run one session in each.
5. Run `kyberdash dash refresh`.
6. Record the evidence per harness:
   - the turn inspector shows non-empty parts;
   - Scorecard Context Hygiene and pressure show a value tagged `measured` or `derived`, or
     an honest reason;
   - the Context Doctor context detectors appear where their inputs exist.
7. Measure the store size growth that ADR 0009 predicts (R12.4).

### T11 — Docs harvest (serialized; after O1)

- **Objective.** Update the docs to match shipped behaviour and O1 evidence:
  - the telemetry inventory rows for the six harnesses, identity stripping, declared windows,
    and the capture command;
  - the runbook's capture command and receiver keep-alive;
  - the architecture doc's ingest, availability and precedence sections.
- **Files.**
  - `docs/dash/telemetry-inventory.md`
  - `docs/dash/runbook.md`
  - `docs/dash/architecture.md`
- **Acceptance.** The docs gates show zero findings.
- **Depends on.** O1, and every code task complete with task-reviewer PASS and integrated
  into `kyberdash-context/integration`.
- **Branch.** `kyberdash-context/T11`, from the integration tip.
- **Skills.** `docs-dev`.

### T12 — Review council over the run

- **Objective.** One `code-reviewer` council, with the dash and docs gates, over the
  integrated stack: `c5b35ec..kyberdash-context/integration`, the tip of the stacked
  branches after T11 is merged in. It does not review `main`, because nothing is merged to
  `main` mid-run (D14).
- **After APPROVE.** The conductor pushes each task branch and opens one PR per task, in
  dependency order (D14).
- **Depends on.** T11.
- **Skills.** `code-review`.

### T13 — Closeout

- **Objective.**
  - Harvest durable facts not already in T11.
  - Move this plan to `docs/archive/plans/`, fixing its relative links.
  - Add the archived row to the plan index.
  - Pass `docs validate --merge-ready`.
- **Depends on.** T12.
- **Skills.** `docs-dev`.

## G1 revision (2026-10-07)

### Capability matrix (persisted from T1)

The T1 output file is gitignored, so the facts that drive G1 are kept here.
- T1 was audited PASS on pass 2.
- It was captured read-only on 2026-10-07, 20:24–20:50 (UTC-4), on macOS.
- Vendor facts are cited to their pages as fetched on 2026-10-07.
- Integrity was verified: `git status` and the digests of every inspected config file were
  unchanged.

**What the verdicts mean.** The question is whether the harness's **own** exporter emits
canonical content through documented flags alone.
- **yes**: system prompt, tool definitions, conversation and tool results.
- **partial**: some of those, or truncated, preview-only, or logs-only.
- **no**: no native content-bearing OTLP export. User-installed extensions and bridges are
  excluded from the verdict and named in the row.

Partial counts as out-of-box export. Only **no** goes to G1-D7.

| Harness | Native exporter and config | Content keys per bucket | Truncation | Window | Identity emitted | State on this machine | Verdict |
|---|---|---|---|---|---|---|---|
| Copilot: VS Code Chat (1.130.0; Insiders 1.141.0) | Built in. User `settings.json`, Stable and Insiders: `github.copilot.chat.otel.{enabled, exporterType, otlpEndpoint, protocol, captureContent, maxAttributeSizeChars, captureIdentity}`. Env `COPILOT_OTEL_*`. `exporterType` takes `otlp-http`, `otlp-grpc`, `console` or `file`. | One switch, `captureContent`, covers all content. System prompt: `gen_ai.system_instructions`. Tool definitions: `gen_ai.tool.definitions`. Conversation: `gen_ai.input.messages`. Tool results: `gen_ai.tool.call.result` on `execute_tool`. Instruction context has no dedicated key; skills appear only as `github.copilot.tool.parameters.skill_name`. | `maxAttributeSizeChars`, default `0` (none) | None. A per-request prompt budget arrives as `copilot_chat.request.max_prompt_tokens`. | `session.id`, `gen_ai.conversation.id`. With `captureIdentity` on, also `user.name`, `process.user.name`, `host.name`. | Both settings files: `enabled=true`, `otlp-http`, `http://127.0.0.1:4318`, `captureContent=true`, `protocol="http/protobuf"`. **`ingest_log` (which starts 2026-10-05) has no Copilot batches.** Store: 20 `copilot-chat` records (last 2026-09-23) and 138 `unattributed` (last 2026-09-03). | **yes** |
| Copilot: CLI (1.0.82) | Built in, **env only**: `COPILOT_OTEL_ENABLED`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`, `COPILOT_OTEL_CAPTURE_CONTENT`, `COPILOT_OTEL_EXPORTER_TYPE` (HTTP only), `COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS` | The shared `gen_ai.*` conventions; `service.name=github-copilot`. There is no CLI-specific key table. | Default `0` | None documented | `service.name`; the rest unknown | Set in both `~/.zshrc` (a symlink) and `~/.zshenv`. No OTLP evidence in the store. | **yes** (unverified locally) |
| Claude Code (2.1.261) | Built in. `~/.claude/settings.json` `env.*`. Traces need `CLAUDE_CODE_ENABLE_TELEMETRY=1`, `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1` and `OTEL_TRACES_EXPORTER=otlp`. Logs need `OTEL_LOGS_EXPORTER=otlp`. | Flags: `OTEL_LOG_USER_PROMPTS`, `OTEL_LOG_ASSISTANT_RESPONSES`, `OTEL_LOG_TOOL_DETAILS`, `OTEL_LOG_TOOL_CONTENT` (span event `tool.output`), and `OTEL_LOG_RAW_API_BODIES` (`1` inline; `file:<dir>` writes untruncated files; logs only). System prompt and tool definitions appear only inside the raw-body log `api_request_body.body`, or under detailed beta. Conversation comes from the raw body; tool results from `tool.output`. | `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH`, default 61,440 UTF-16 units. Cut values carry `_truncated` / `_original_length`; raw bodies carry `body_truncated` and `body_length`. | None in OTel. Statusline stdin carries `context_window.context_window_size`. | `organization.id`, `user.id`, `user.email`, `user.account_uuid`, `user.account_id` (T4 strips all five) | Traces go to `http://localhost:4318` as http/protobuf. **Logs `none`, metrics `none`, no content flags.** The 10,946 `llm_request` spans carry no content key and no window key, and `gen_ai.response.id` = `request_id` = `req_…` (see E1). | **partial** |
| Codex (0.159.0-alpha.12.1, bundled in the ChatGPT app, not on `PATH`) | Built in. `[otel]` in `~/.codex/config.toml`: `exporter` / `trace_exporter` = `{ otlp-http = { endpoint = <full signal URL>, protocol = "binary"\|"json" } }`, `metrics_exporter` (default `statsig`), `log_user_prompt` | Only `codex.user_prompt` (gated) and a `codex.tool_result` snippet. No system prompt, tool definitions, history or instruction context. | Snippet length undocumented | None in OTel. Session files carry `model_context_window`. | `service.name`, `conversation.id` | No `[otel]` table. All 545 local session files are `originator = "Codex Desktop"` and carry the window. All 63 `codex-desktop` records carry `contextWindow`. | **partial** (minimal) |
| OpenCode (1.18.30) | Effect app spans are emitted whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set. AI-SDK spans need `experimental.openTelemetry: true` in `~/.config/opencode/opencode.jsonc`. | AI SDK keys: either `gen_ai.*` (current) or legacy `ai.prompt.messages`, `ai.prompt.tools`, `ai.toolCall.result`. The bundled AI SDK version is unknown. Upstream #13438 ("`opencode run` emits no OTLP traces even when enabled") was closed not-planned on 2026-07-31. | Undocumented | Unknown | `service.name`, `opencode.client`, `opencode.process_role` (resource attributes) | The flag is `false`. About 350–400K non-model spans (`sql.execute`, `http.server GET`, `Session.*`) are quarantined, and there are 0 `ai.*` spans. Model records come from the file parser (4,071). | **partial** |
| Pi (1.0.4) | **None native.** Two extensions. (1) The user's collector `@dpalfery/pi-statusline` 0.1.0, listed in `settings.json` `packages`: hard-coded `127.0.0.1:4318/v1/traces`, http/json, always on. (2) ObservMe 0.1.9, `observme.yaml`: http/protobuf only, capture flags default `false`, redaction needs `OBSERVME_HASH_SALT`, not in `packages`. | Collector: `gen_ai.system_instructions`, `gen_ai.input.messages`, `gen_ai.output.messages`, `pi.user.prompt`, `pi.tools.selected`, `pi.tools.snippets`, `pi.skills`, `pi.context_files`, `pi.llm.request.payload` | Collector: about 16 KB per text part. ObservMe: 8,000–32,000 characters per field. | Declared in `~/.pi/agent/models-store.json` (125 of 125 models) | Collector: `pi.session.id`, `gen_ai.session.id`, `pi.cwd.basename` | 2,298 OTLP `pi.llm.request` and 3,722 `codeburn/pi` records; none carry a window | **no** (native) |
| Antigravity (`agy` 1.3.1) | **None native.** `enableTelemetry` is Google usage telemetry, and `OTEL_*` variables produce nothing. The user's own bridge `~/.gemini/antigravity-cli/statusline.py` (its `statusLine.command`) posts `chat` spans as http/json to `127.0.0.1:4318/v1/traces`. It is not the Kyber Utilities repo script. | Bridge: `gen_ai.system_instructions`, `gen_ai.tool.definitions` / `gen_ai.request.tools`, `gen_ai.input.messages`, `gen_ai.skills`, `gen_ai.rules`, `gen_ai.usage.{sys,tool,skill,rule,msg}_tokens`. **No tool results.** | None | The bridge emits none. Stdin carries `context_window.context_window_size`. | `gen_ai.session.id`, `copilot_chat.chat_session_id`, `vcs.*` | 39 `antigravity` records, 22,035 legacy `gemini` records (source `agy`), 3,867 `codeburn/antigravity-cli` | **no** (native) |

**Cross-cutting facts.**
- **Port 4318** is held by kyberdash 0.1.7-rc.16, loopback only. `/healthz` identifies it
  as KyberDash.
- **Login-shell env.** Both `~/.zshrc` and `~/.zshenv` export
  `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318` and
  `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`. This reaches every harness started from a
  shell, and it is why OpenCode sends app spans.
- **Unmatched logs.** `quarantined_logs` holds 23,817 rows marked "identified log remained
  unmatched past pending expiry". Their source is unknown, since Claude's log exporter is
  `none`. P2.D investigates.
- **Window-bearing records.** Only the 63 `codex-desktop` records carry a window. On the
  pre-T5 build, no Cursor record carried a `turnId`. `cursor-hook` spans appear nowhere.

### E1: the Claude OTel-to-transcript join key (resolution)

**Finding.** The finding comes from the T6 audit, escalated end-of-run.
- All 11,145 Claude OTel `llm.invoke` rows in the store carry
  `gen_ai.response.id` = `request_id` = `req_…`. None carries `msg_…`.
- The Claude file-synth record carries only `message.id` (`msg_…`) as its native id
  (`nativeRecordIdOfGroup`, `dash/src/synth/readers/claude.ts:203`).
- `dash/src/providers/claude.ts` never reads the transcript's `requestId`.
- T6's `collapseIdJoins` is therefore inert on real data. Its tests pass only on an invented
  `msg_1`.
- The sample covers the desktop build only. CLI OTel is unverified.

**Resolution: amend with task P2.0.**
- **New join key.** The OTel `request_id` (falling back to `gen_ai.response.id` when it is
  `req_`-shaped) must equal the file record's new raw `requestId`. P2.0 copies that value
  from the transcript line's top-level `requestId`.
- **Old key kept.** The existing `message.id` comparison stays, for any build whose
  `gen_ai.response.id` is `msg_`-shaped.
- **No veto.** The exact-counter path gets no mismatched-id veto (the auditor's ruling).
- **Repair contract.** The Claude parser contract version is bumped (ADR 0016).
- **Evidence.** The PR records a read-only measurement of join yield on the real store.

### Disposition of carried audit notes

| Note | Disposition |
|---|---|
| `report/build.ts` `buildLatestSession` emits declared-window figures as `measured(...)`, and the report types have no derived class. This breaks D5 at the report layer. | **P2.R** |
| T3: `windowSources` is added on a source claim with no finite limit | **P2.R** |
| T3: with `contextLimitSource` undefined and no limit, `compactionPressure` divides by the 200K default and reports `measured` (pre-existing; contrary to #181) | **P2.R** |
| T5: no test pins a two-reply request; stale RED comments and casts remain in `cursor-pairing.test.ts` | **P2.T5** |
| T3: availability follows session existence, not proportion (R3) | T12 council note |
| Declared-window confidence: signal `medium` (T3) against finding `heuristic` (T2) | T12 council note (vocabulary choice) |
| T7: `HOME \|\| homedir()` is not safe when HOME is empty (it reads only, never writes); `turnModel` cross-source pairing | T12 council note |
| T4: the camelCase identity tests have no RED-first commit; the `gen_ai.` prefix and `RETAINED_KEYS` are not discriminated by any test | T12 council note |
| T0: the 16.5rem card height has no browser check; the removal of `maxItems={5}` is pinned by diff only | T12 council note, and O1 checks it visually |
| T1 matrix nits; T13's ADR 0029–0031 status links; environment-only test failures (`copilot.test.ts` under the real HOME; `ANTHROPIC_BASE_URL`) | T12 and T13 notes |

### Phase 2 tasks (A3 baseline; A4 P2.W approved; A5 P2.W2 approved)

**Rules common to every P2 task:**
- A3's completed task-review states remain valid. T8 and T10 are PASS.
- A3 still authorizes unaffected in-flight work that does not consume the capture writer
  core: P2.R, P2.0, P2.4b, P2.T5, and P2.8's non-writer work.
- P2.W is approved and merged. A5 approved P2.W2, which may be dispatched. P2.2a retries
  only after P2.W2 has task-reviewer PASS.
- D14 branching: the task's branch base is named below.
- Synthetic fixtures use the real keys from the matrix (D4).
- Every task uses its own test files and fixtures. P2.1b keeps its already-committed RED
  test unchanged; P2.W and P2.W2 use separate seam tests.
- The dash gates must pass.
- **Writer tasks consume P2.W's declaration contract; P2.2a additionally consumes P2.W2.
  They edit only their declared harness production file, except P2.5's already required Pi
  adapter mapping.** Their task-specific tests and synthetic fixtures remain in their Test
  contract rows. No writer edits the shared capture core, shell rc files, or the user's
  scripts.

#### P2.D — Ingest diagnosis (read-only; no branch, no PR)

- **Objective.** Answer two questions with evidence:
  1. Why `ingest_log` has had no Copilot batches since 2026-10-05.
  2. Whose the 23,817 unmatched quarantined logs are, and which correlation keys they carry
     (key names only).
- **Method.**
  1. Owner step **O0** comes first. The owner sends one Copilot Chat request in VS Code
     Stable and runs `copilot -p "Reply OK"` in a login shell.
  2. The agent then works read-only:
     - time-windowed `ingest_log`, `quarantine` and `quarantined_logs` queries on
       `file:…/canon.db?mode=ro`, reporting counts and key names only;
     - VS Code and Copilot log files, if present, for exporter errors;
     - `lsof -nP -iTCP:4318` during the trigger.
  3. The T1 constraints apply: edit nothing, never copy secrets or content.
- **Output.** `/Users/dave/git/kyber-weave2/artifacts/kyberdash-context-capture/p2d-ingest-diagnosis.md`
- **Acceptance.**
  - Each question is answered with evidence, or marked unknown with a reason.
  - Any KyberDash-side defect is named by file and symbol and returned to the architect as
    a scoped fix task.
- **Depends on.** O0.
- **Skills.** `research` (read-only, no-test).

#### P2.0 — E1 fix: the Claude request-id join

- **Objective.** As in the E1 resolution above:
  - `loadClaudeCalls` stamps `requestId` from the transcript line's top-level `requestId`;
  - `collapseIdJoins` joins on OTel `request_id` (or a `req_`-shaped `gen_ai.response.id`)
    equal to the file `requestId`, and keeps the `message.id` comparison;
  - no veto on the counter path;
  - `CLAUDE_PARSER_CONTRACT_VERSION` goes from `'4'` to `'5'`.
- **Files.**
  - `dash/src/providers/claude.ts`
  - `dash/src/providers/types.ts` (`ParsedProviderCall.requestId?`)
  - `dash/src/canon/twin-dedupe.ts`
  - `dash/src/refresh/registry.ts`
  - New tests: `dash/src/providers/claude-request-id.test.ts`,
    `dash/src/canon/twin-dedupe-claude-request-id.test.ts`
  - New fixtures: `dash/src/canon/fixtures/claude-request-id/`
- **Branch.** From `kyberdash-context/T10` (which contains T2, T5, T7 and T10), merging
  `kyberdash-context/T6`.
- **Depends on.** T6 PASS, T10 PASS.
- **Acceptance.**
  - The Test contract row.
  - Read-only join-yield counts in the PR: how many store `llm_request` `request_id`s
    appear as `requestId` in the local Claude transcripts, counts only.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.R — D5 consistency at the report and rollup layers

- **Objective.**
  - A declared window and its pressure reach the report as a `derived` class, labelled
    "declared window", never as `measured`.
  - The report contract also accepts D15's `catalog` provenance and renders it as
    `derived`, labelled "vendor catalog", never as `reported`, `declared` or `measured`.
  - `digestSessionPayloads` adds a window source only when it has a finite, positive limit.
  - `compactionPressure` with no source and no limit is not measurable. It must not divide
    by the 200K default.
- **Files.**
  - `dash/src/analysis/report/types.ts`
  - `dash/src/analysis/report/build.ts`
  - `dash/src/analysis/report/render-text.ts`
  - `dash/src/analysis/report/render-markdown.ts`
  - `dash/src/canon/harnesses.ts`
  - `dash/src/analysis/signals.ts`
  - New tests: `dash/src/analysis/report/declared-window-derived.test.ts`,
    `dash/src/canon/window-source-guard.test.ts`
- **Branch.** From `kyberdash-context/T3`.
- **Depends on.** T3 PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.T5 — Cursor two-reply regression pin (test-only)

- **Objective.** Pin the claimed-turn guard: a request with two token-bearing replies
  attaches reader parts to exactly one. Also remove the stale "before `pairingId` lands"
  comments and casts in `cursor-pairing.test.ts` (around lines 72–75 and 154–157).
- **Files.**
  - New: `dash/src/synth/cursor-two-reply.test.ts`
  - `dash/src/synth/cursor-pairing.test.ts` (comment and cast cleanup only)
- **Branch.** From `kyberdash-context/T5`.
- **Depends on.** T5 PASS.
- **Skills.** `test-dev`.

#### P2.W — Capture writer declaration seam

- **Objective.** Extend T8's harness-agnostic capture core so a harness declaration can:
  - declare more than one managed config file, each with its own path and format;
  - write typed JSON/JSONC scalar values (`string`, `boolean`, `number`, or `null`) while
    TOML/YAML managed-block values remain strings;
  - declare status-only warnings over present config values without writing those values;
  - declare an optional status snippet built from the capture process environment, with no
    shell rc reads or writes.
- **Preserved contracts.**
  - D11 receipts, drift refusal, dry-run, idempotent enable and byte-for-byte disable apply
    independently to every declared file.
  - Existing single-file harness declarations remain representable.
  - The seam is harness-agnostic: it contains no Copilot key names, paths, warnings or env
    variable names.
  - P2.1b's committed `copilot.test.ts` is not edited by this task.
- **Files.**
  - `dash/src/capture/index.ts`
  - `dash/src/capture/harnesses/registry.ts`
  - `dash/src/capture/edit-json.ts`
  - New test: `dash/src/capture/writer-seam.test.ts`
- **Branch.** `kyberdash-context/P2.W`, from the reviewed
  `kyberdash-context/T8` tip.
- **Depends on.** T8 PASS. A4 records the required amendment approval.
- **Acceptance.** The P2.W Test contract row and the dash gates.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.W2 — Capture writer ensure-if-absent seam

- **Objective.** Extend the harness-agnostic declaration on both `ManagedHarnessWriter` and
  `CaptureConfigFile` with optional ensure-if-absent keys. After the core reads the current
  file, it adds an ensure key to the actual write set only when that key is absent. A
  present value is never overwritten.
- **D11 contract.** Only keys the core actually writes are receipted. An absent ensure key
  is written, receipted with prior absence, and removed by `disable`; a present ensure key
  is left unchanged and omitted from the receipt. Always-written `desiredKeys` retain their
  existing overwrite, receipt, drift, dry-run, idempotence and restore behaviour.
- **Scope.** The seam is harness-agnostic and contains no Claude key names or paths.
- **Files.**
  - `dash/src/capture/index.ts`
  - `dash/src/capture/harnesses/registry.ts`
  - New test: `dash/src/capture/writer-ensure-if-absent.test.ts`
- **Branch.** New `kyberdash-context/P2.W2` branch from the reviewed P2.W tip
  `69e068441629dc27388a8a2b59a3a8df53649dea`.
- **Depends on.** P2.W PASS. A5 records the required delta approval.
- **Acceptance.** The P2.W2 Test contract row and the dash gates.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.1b — Copilot capture writer

- **Objective.**
  - **VS Code**, Stable and Insiders user `settings.json` (JSONC). Ensure:
    - `github.copilot.chat.otel.enabled=true`;
    - `exporterType="otlp-http"`;
    - `otlpEndpoint="http://127.0.0.1:4318"`;
    - `captureContent=true`;
    - `maxAttributeSizeChars=0`.

    Leave `protocol` untouched; `status` warns on `grpc`. Never set `captureIdentity`;
    `status` warns when it is `true` (D4).
  - **Copilot CLI** is **status-only**. It reports the expected env keys as seen in the
    command's own process env and prints a snippet. It never edits shell rc files.
- **Files.** `dash/src/capture/harnesses/copilot.ts` only. The existing committed RED test
  `dash/src/capture/harnesses/copilot.test.ts` is the behavioural contract and must not be
  edited or weakened.
- **Branch.** Resume `kyberdash-context/P2.1b` only after rebasing or rebuilding it on the
  reviewed `kyberdash-context/P2.W` tip. Its current unchanged tip is
  `3d4c3060e233f84b4175f8736b22834491423b0d`.
- **Depends on.** P2.W PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.2a — Claude Code capture writer

- **Objective.** Edit `~/.claude/settings.json` (strict JSON), `env.*` string values.
- **Set:**
  - `OTEL_LOGS_EXPORTER="otlp"` (prior value `none` recorded, D11)
  - `OTEL_LOG_USER_PROMPTS="1"`
  - `OTEL_LOG_ASSISTANT_RESPONSES="1"`
  - `OTEL_LOG_TOOL_DETAILS="1"`
  - `OTEL_LOG_TOOL_CONTENT="1"`
  - `OTEL_LOG_RAW_API_BODIES="1"` (inline; never `file:`)
  - `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH="1048576"` (design proposal; raises the
    61,440 cap)
- **Ensure only if absent:**
  - `CLAUDE_CODE_ENABLE_TELEMETRY="1"`
  - `CLAUDE_CODE_ENHANCED_TELEMETRY_BETA="1"`
  - `OTEL_TRACES_EXPORTER="otlp"`
  - `OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"`
  - `OTEL_EXPORTER_OTLP_ENDPOINT="http://127.0.0.1:4318"`. An existing
    `http://localhost:4318` is accepted, not rewritten.
- **Never set** `ENABLE_BETA_TRACING_DETAILED` or `BETA_TRACING_ENDPOINT`.
- **Files.** `dash/src/capture/harnesses/claude-code.ts` only, plus its task-specific new
  test from the Test contract.
- **Branch.** Retry from the reviewed `kyberdash-context/P2.W2` tip.
- **Depends on.** P2.W2 PASS. The existing RED-only tip
  `03088c1dd7a7a9ea41c3647cde8028f96252eb2b` remains blocked.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.2b — Claude raw-body logs: correlation and mapping

- **Objective.**
  - **Correlation.** An `api_request_body` log enriches its `llm_request` span with
    `system_prompt` and `tool_definitions` parts. Correlation uses the keys P2.D reports, or
    the documented shape if P2.D finds no Claude logs.
  - **Truncated bodies.** A body with `body_truncated=true` that is unparseable yields no
    parts and a stated reason, never fabricated text.
  - **Messages.** `messages` are **not** mapped. Transcript conversation fills that bucket
    under D10.
  - **Tool output.** `tool.output` span events map to `tool_result_content` if the receiver
    decodes span events. If it does not, the task records that and stops there.
- **Files.**
  - `dash/src/canon/log-ingest.ts`
  - `dash/src/canon/adapters/claude-code.ts`
  - New test: `dash/src/canon/claude-raw-body-log.test.ts`
  - New fixtures: `dash/src/canon/fixtures/claude-otel/`
- **Branch.** From `kyberdash-context/T4`.
- **Depends on.** T4 PASS, P2.D.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.3 — Codex status-only coverage (G1-Q3 = (a))

- **Objective.** `dash/src/capture/harnesses/codex.ts` is status-only. It reports the absent
  `[otel]` table and the file-sourced coverage, and it never writes `~/.codex/config.toml`.
- **Files.** `dash/src/capture/harnesses/codex.ts` only, plus its task-specific new test
  from the Test contract.
- **Branch.** From `kyberdash-context/P2.W`.
- **Depends on.** P2.W PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.4a — OpenCode capture writer

- **Objective.**
  - Ensure `experimental.openTelemetry: true` in `~/.config/opencode/opencode.jsonc`
    (JSONC).
  - `status` reports `OTEL_EXPORTER_OTLP_ENDPOINT` from the process env, and never writes
    env.
  - `status` notes upstream #13438 and the non-model span volume.
- **Files.** `dash/src/capture/harnesses/opencode.ts` only, plus its task-specific new test
  from the Test contract.
- **Branch.** From `kyberdash-context/P2.W`.
- **Depends on.** P2.W PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.4b — OpenCode adapter

- **Objective.** Replace T4's placeholder in `adapters/opencode.ts`.
  - **Detection.** Detect AI-SDK model spans by the `ai.*` attribute namespace. `service.name`
    is never evidence (R6.2).
  - **Legacy keys.** Map `ai.prompt.messages` to conversation, with a system-role message
    to system prompt; `ai.prompt.tools` to tool definitions; `ai.toolCall.result` to tool
    results.
  - **Current keys.** `gen_ai.*` keys are already mapped by `canonicalParts`.
  - **Counters.** Carry them from `ai.usage.*` or `gen_ai.usage.*`. Cache classes are
    `not_measurable` unless present.
  - **App spans.** OpenCode's app spans (`sql.execute`, `http.server GET`, `Session.*`)
    stay quarantined as non-model.
- **Files.**
  - `dash/src/canon/adapters/opencode.ts`
  - New test: `dash/src/canon/adapters/opencode-ai-sdk.test.ts`
  - New fixtures: `dash/src/canon/fixtures/opencode-ai-sdk/` (both vocabularies)
- **Branch.** From `kyberdash-context/T4`.
- **Depends on.** T4 PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.5 — Pi (`@dpalfery/pi-statusline`; G1-Q1 = (a))

- **Writer, status-only.** It reads `~/.pi/agent/settings.json` `packages` read-only and
  reports whether the collector or ObservMe is loaded. It warns if both are.
- **Adapter.** In `adapters/pi.ts`:
  - `pi.tools.selected` / `pi.tools.snippets` map to `tool_definitions`;
  - `pi.skills` / `pi.context_files` map to `instruction_context`;
  - `pi.llm.request.payload` stays in raw.
- **Window check.** Verify that a Pi session holding both OTLP spans and `codeburn/pi` rows
  resolves T7's declared window. If the session keys differ, report it; do not widen them.
- **Files.**
  - `dash/src/capture/harnesses/pi.ts`
  - `dash/src/canon/adapters/pi.ts`
  - New tests and new fixtures `dash/src/canon/fixtures/pi-collector/`
- **Branch.** From `kyberdash-context/P2.W`, merging `kyberdash-context/T4` and
  `kyberdash-context/T7`.
- **Scope rationale.** The capture writer still owns only `harnesses/pi.ts`; the existing
  `adapters/pi.ts` ownership is the concrete extra production file required by this task's
  collector-key mapping and is not widened by the seam amendment.
- **Depends on.** T4, T7 and P2.W PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.6 — Antigravity existing bridge (G1-Q2 = (b))

- **Writer, status-only.** It reads `~/.gemini/antigravity-cli/settings.json`
  `statusLine.command`, and greps the referenced script read-only for `4318`. Under (b) it
  prints the one-line `declaredContextWindow` attribute snippet for the user to add.
- **No adapter change.** Content is already mapped.
- **Integration test.** A bridge-shaped span carrying `declaredContextWindow` yields
  `contextLimitSource: 'declared'`, and a bridge span without it yields no window.
- **Files.**
  - `dash/src/capture/harnesses/antigravity.ts`
  - New tests and new fixtures `dash/src/canon/fixtures/agy-bridge/`
- **Branch.** From `kyberdash-context/P2.W`, merging `kyberdash-context/T2`.
- **Depends on.** T2 and P2.W PASS.
- **Skills.** `test-dev`, then a TypeScript implementer.

#### P2.8 — Vendor-documented model-window catalog and refresh button

- **Objective.** Deliver D15 and the context-window portion of issue #203.
  - Add a `model_context_window_catalog` table to the canonical SQLite store. Each exact
    lookup id row carries its canonical model id, a positive context window, vendor,
    official documentation URL, retrieval timestamp and source revision/date when the source
    exposes one. Explicit aliases are rows; no alias is inferred.
  - Add a fixed, code-owned registry of official HTTPS vendor documentation sources. The
    refresh accepts no caller-supplied URL and uses no credentials. Each source adapter
    validates model ids and positive integer windows before opening a transaction.
  - Refresh is last-known-good per vendor: replace that vendor's rows atomically only after
    its response parses and validates. A failed vendor keeps its prior rows and returns a
    bounded per-vendor error; it never empties a working catalog or manufactures a value.
  - Add `GET /api/kyber/model-catalog` for row count, per-vendor status, source URLs and last
    refresh, and `POST /api/kyber/model-catalog/refresh` for the explicit user action.
  - Add a **Refresh model windows** button to Context Doctor's coverage panel. While running
    it is disabled. Success shows updated row count and timestamp; partial or total failure
    names the failed vendors without rendering remote response bodies.
  - After at least one vendor updates successfully, rebuild the derived sessions, harness
    rollups and findings once so the button's catalog changes reach the three context
    surfaces without a separate CLI refresh.
  - Resolve a known model by exact catalog id or explicit alias only. Context-window
    precedence is `reported` → `declared` → `catalog` → fallback.
    `ContextLimitSource` gains `'catalog'`; pressure from a catalog row is `derived`, and a
    compaction finding using it is `inferred` with a "vendor catalog" caveat. It is never
    `measured`.
  - With no model identity or no exact row/alias, retain `not_measurable` and the existing
    unknown-window suppression. Never fall back to fuzzy matching or the 200K constant as
    though it were catalog data.
  - Update the unknown-window UI copy so it names reported, declared or catalog coverage;
    it must not imply that only a harness-reported window can resolve the gap.
- **Files.**
  - New: `dash/src/canon/model-window-catalog.ts`
  - `dash/src/canon/store.ts` (schema, migration and catalog methods)
  - `dash/src/canon/context-window.ts`
  - `dash/src/canon/sessions.ts`
  - `dash/src/canon/findings.ts`
  - `dash/src/analysis/findings.ts`
  - `dash/src/server/bridge.ts`
  - `dash/src/server/routes.ts`
  - `dash/web/src/lib/kyberApi.ts`
  - `dash/web/src/pages/ContextDoctor.tsx`
  - New tests: `dash/src/canon/model-window-catalog.test.ts`,
    `dash/src/server/model-window-catalog-route.test.ts`,
    `dash/web/src/pages/ModelWindowCatalogRefresh.test.tsx`
  - New synthetic fixtures under `dash/src/canon/fixtures/model-window-catalog/`; fixtures
    contain invented model ids and no copied vendor prose.
- **Branch.** From `kyberdash-context/P2.R`, merging `kyberdash-context/T0` for the shared
  Context Doctor file; it is independent of T8.
- **Depends on.** T0, T2, T3 and P2.R PASS. The UI/server task does not authorize resuming
  T8.
- **Skills.** `test-dev`, then TypeScript and React implementation skills.

### Phase 2 Test contract (P2.W approved by A4; P2.W2 approved by A5)

| Task | Test file | Runner command | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| P2.0 | `src/providers/claude-request-id.test.ts`, `src/canon/twin-dedupe-claude-request-id.test.ts` | `npm --prefix dash exec vitest run src/providers/claude-request-id.test.ts src/canon/twin-dedupe-claude-request-id.test.ts` | Fixture shapes are real. The OTel record carries `request_id` = `gen_ai.response.id` = `req_A`. The file record carries `requestId` = `req_A` and `turnId` = `msg_B`, with different counters. They join into one record with OTel counters and per-bucket content (D10). `req_A` against `req_C` does not join. The exact-counter path is unchanged, with no veto. A stale Claude checkpoint is re-read under contract `'5'`. | The join test fails on the T6 tip | Passes. T6's tests stay green. |
| P2.R | `src/analysis/report/declared-window-derived.test.ts`, `src/canon/window-source-guard.test.ts` | `npm --prefix dash exec vitest run src/analysis/report/declared-window-derived.test.ts src/canon/window-source-guard.test.ts` | A declared-window session's report shows window and pressure as `derived`, labelled "declared window". A catalog-provenance fixture is accepted as `derived`, labelled "vendor catalog", never measured. A source claim with no finite limit adds no window source. `compactionPressure` with no source and no limit is not measurable. | Each assertion fails on the T3 tip | Passes. Existing report and signals tests stay green. |
| P2.T5 | `src/synth/cursor-two-reply.test.ts` | `npm --prefix dash exec vitest run src/synth/cursor-two-reply.test.ts` | One request with two token-bearing replies attaches parts to exactly one call | Mutation evidence: the test fails with the claimed-turn guard disabled locally (the change is not committed) | Passes on the T5 tip |
| P2.W | `src/capture/writer-seam.test.ts` | `npm --prefix dash exec vitest run src/capture/writer-seam.test.ts` | A synthetic harness declaration manages two JSONC files, preserves comments, writes a string, booleans and numeric `0` with their JSON types intact, records and restores each file under D11, emits declared status warnings, and prints an optional snippet from injected process-env values without touching shell rc files. A legacy single-file declaration still works. The test contains no Copilot keys or paths. | The new test fails on the reviewed T8 tip because the core accepts one `resolvePath`, string-only `desiredKeys`, and core-built status text with no warning or env-snippet declaration. `copilot.test.ts` is not edited. | The same test passes. Existing `src/capture` tests stay green, including D11 drift refusal and dry-run. |
| P2.W2 | `src/capture/writer-ensure-if-absent.test.ts` | `npm --prefix dash exec vitest run src/capture/writer-ensure-if-absent.test.ts` | A synthetic JSON declaration combines always-written keys with ensure-if-absent keys on the same file. A present ensure key keeps its original value and is omitted from the receipt; an absent ensure key is written and receipted. `disable` restores the always-written prior value, removes the inserted ensure key, and leaves the pre-existing ensure key untouched. | The new test fails on P2.W tip `69e068441629dc27388a8a2b59a3a8df53649dea`: declarations expose only `desiredKeys`, and `enableFile` applies and receipts that whole map. | Passes. Existing `src/capture` tests stay green, including P2.W's multi-file, typed-value, warning, env-snippet, drift and dry-run cases. |
| P2.1b | Existing committed `src/capture/harnesses/copilot.test.ts` | `npm --prefix dash exec vitest run src/capture/harnesses/copilot.test.ts` | Temporary HOME with Stable and Insiders JSONC settings, comments preserved. `enable` sets exactly the five keys and leaves `protocol` alone. `status` warns on `captureIdentity=true` and on `grpc`. The CLI part writes nothing and prints the env snippet. | At unchanged P2.1b tip `3d4c3060e233f84b4175f8736b22834491423b0d`, 5 of 7 tests fail against T8; only registration and "does not edit shell rc" pass. The assertions stay unchanged and no production edit is committed. | All 7 existing tests pass without editing or weakening `copilot.test.ts`; D11 restore holds. |
| P2.2a | `src/capture/harnesses/claude-code.test.ts` | `npm --prefix dash exec vitest run src/capture` | Temporary-HOME `settings.json`: the set keys are written, and the ensure-if-absent keys are not overwritten (`localhost` kept). `OTEL_LOGS_EXPORTER`'s prior `none` is restored by `disable`. The beta-detailed keys are never written. | The stub reports "not yet supported" | Passes |
| P2.2b | `src/canon/claude-raw-body-log.test.ts` | `npm --prefix dash exec vitest run src/canon/claude-raw-body-log.test.ts` | A correlated raw-body log adds `system_prompt` and `tool_definitions` parts to its `llm_request`. A truncated, unparseable body adds none and states a reason. `messages` are not mapped. | Fails on the T4 tip | Passes; existing `log-ingest` tests stay green |
| P2.3 | `src/capture/harnesses/codex.test.ts` | `npm --prefix dash exec vitest run src/capture` | Under (a), `status` reports no `[otel]` and file coverage, and `enable` writes nothing | The stub reports "not yet supported" | Passes |
| P2.4a | `src/capture/harnesses/opencode.test.ts` | `npm --prefix dash exec vitest run src/capture` | Temporary-HOME `opencode.jsonc` with comments preserved: `experimental.openTelemetry` becomes `true`, and `disable` restores `false` | The stub reports "not yet supported" | Passes |
| P2.4b | `src/canon/adapters/opencode-ai-sdk.test.ts` | `npm --prefix dash exec vitest run src/canon/adapters/opencode-ai-sdk.test.ts` | A legacy `ai.*` span and a `gen_ai.*` AI-SDK span are both claimed as `opencode` with mapped buckets. `sql.execute` and `http.server GET` stay quarantined as non-model. | The placeholder claims nothing | Passes; the ingest vote for other fixtures is unchanged |
| P2.5 | `src/canon/adapters/pi-collector.test.ts`, `src/capture/harnesses/pi.test.ts` | `npm --prefix dash exec vitest run src/canon/adapters/pi-collector.test.ts src/capture` | A collector-shaped `pi.llm.request` gains `tool_definitions` and `instruction_context` from the `pi.*` keys. `status` reports which extension is loaded. A mixed OTLP and file session resolves the declared window, or the PR reports the session-key mismatch. | Fails on the merged T4/T7/P2.W base while the Pi writer remains a stub | Passes |
| P2.6 | `src/canon/agy-bridge-window.test.ts`, `src/capture/harnesses/antigravity.test.ts` | `npm --prefix dash exec vitest run src/canon/agy-bridge-window.test.ts src/capture` | A bridge span with `declaredContextWindow` resolves `'declared'`; without it, no window. `status` reports the bridge route. | The writer assertion fails on the stub | Passes |
| P2.8 | `src/canon/model-window-catalog.test.ts`, `src/server/model-window-catalog-route.test.ts`, `web/src/pages/ModelWindowCatalogRefresh.test.tsx` | `npm --prefix dash exec vitest run src/canon/model-window-catalog.test.ts src/server/model-window-catalog-route.test.ts web/src/pages/ModelWindowCatalogRefresh.test.tsx` | Synthetic official-doc responses refresh per-vendor rows atomically and preserve last-known-good rows on one vendor failure. Exact model hits resolve `catalog`; reported and declared values each win over catalog. Missing model and missing row stay unmeasurable. Catalog pressure is `derived`; its finding is `inferred` with a vendor-catalog caveat. GET reports provenance; POST performs refresh and one derived rebuild after any successful update; the button disables in flight and renders bounded success/partial-failure state. Unknown-window copy names reported, declared and catalog coverage. | The catalog table, routes, source and button do not exist on the P2.R tip | All pass. Existing migration, context-window, sessions, findings, routes and Context Doctor tests stay green. No test uses the network. |
| P2.D | **No test (read-only diagnosis).** | n/a | n/a | n/a | The output file answers both questions with evidence or unknowns, with integrity unchanged |

### Phase 2 dependencies and lanes (MAX_CONCURRENCY 3)

| Task | Depends on | Branch base |
|---|---|---|
| P2.D | O0 | none (read-only) |
| P2.R | T3 | `kyberdash-context/T3` |
| P2.T5 | T5 | `kyberdash-context/T5` |
| P2.0 | T6, T10 | `kyberdash-context/T10`, merging T6 |
| P2.W | T8 | `kyberdash-context/T8` |
| P2.W2 | P2.W | New branch from P2.W tip `69e068441629dc27388a8a2b59a3a8df53649dea` |
| P2.1b | P2.W | Resume only on the reviewed `kyberdash-context/P2.W` tip |
| P2.2a | P2.W2 | Retry only on the reviewed `kyberdash-context/P2.W2` tip |
| P2.4a | P2.W | `kyberdash-context/P2.W` |
| P2.4b | T4 | `kyberdash-context/T4` |
| P2.2b | T4, P2.D | `kyberdash-context/T4` |
| P2.3 | P2.W | `kyberdash-context/P2.W` |
| P2.5 | T4, T7, P2.W | `kyberdash-context/P2.W`, merging T4 and T7 |
| P2.6 | T2, P2.W | `kyberdash-context/P2.W`, merging T2 |
| P2.8 | T0, T2, T3, P2.R | `kyberdash-context/P2.R`, merging T0 |

**Approved lanes under A4**, in D6 priority. Already-authorized in-flight non-writer work
does not wait for this sequence:
1. P2.D (with O0), P2.R, P2.0.
2. P2.W.
3. P2.1b, P2.2b.
4. P2.4a, P2.4b, P2.T5.
5. P2.3, P2.5, P2.6, then P2.8 after P2.R.

**Approved lane under A5:** P2.W2 may be dispatched. The P2.2a retry waits for P2.W2
task-reviewer PASS.

**File-overlap audit.** Every file is owned by one active P2 task at a time.
- P2.0: `providers/claude.ts`, `providers/types.ts`, `twin-dedupe.ts`, `refresh/registry.ts`.
- P2.R: `report/*`, `harnesses.ts`, `signals.ts`.
- P2.2b: `log-ingest.ts`, `adapters/claude-code.ts`.
- P2.4b: `adapters/opencode.ts`.
- P2.5: `adapters/pi.ts`.
- P2.W: `capture/index.ts`, `capture/harnesses/registry.ts`, and `capture/edit-json.ts`.
- P2.W2 intentionally follows completed P2.W and reopens `capture/index.ts` and
  `capture/harnesses/registry.ts`; it does not overlap an active parallel task.
- P2.8: `model-window-catalog.ts`, `store.ts`, `context-window.ts`, `sessions.ts`,
  `canon/findings.ts`, `analysis/findings.ts`, `server/bridge.ts`, `server/routes.ts`,
  `web/lib/kyberApi.ts`, and the Context Doctor catalog controls.
- Each writer owns its own `capture/harnesses/<h>.ts`.

P2.R precedes P2.8 so the report layer understands catalog provenance before the derivation
seam can emit it.

## Dependency graph and MAX_CONCURRENCY

| Task | Depends on | Why |
|---|---|---|
| T0 | none | Independent UI change; its PR opens after T12 APPROVE |
| T1 | none | Read-only research. It holds a dispatch slot, but no worktree, no branch and no PR. |
| T2 | none | Seam 1 (D12 decided) |
| T4 | none | Seam 3; its files are owned by no other task |
| T5 | none | Cursor pairing; its files are owned by no other task |
| T6 | none | Claude join plus D10 precedence |
| T8 | none | Capture CLI core (D9 and D11 decided) |
| T9 | none | ADRs recording D9–D12 |
| T3 | T2 | Consumes the `'declared'` `ContextLimitSource` |
| T7 | T2 | Consumes `ReaderTurn.declaredContextWindow` |
| T10 | T5, T7 | Bumps contracts for the parsers they changed. Branches from `kyberdash-context/T5` and merges `kyberdash-context/T7` locally first (D14). |
| G1 | T1 | Capability matrix. Also needs T2, T3, T4 and T8 complete with task-reviewer PASS on their local branches, because P2 builds on them. **The approved run stops here (A2).** |
| P2.D, P2.W, P2.0–P2.8, P2.R, P2.T5 | A3 and A4, plus the per-task dependencies in [Phase 2 dependencies and lanes](#phase-2-dependencies-and-lanes-max_concurrency-3) | Phase 2 baseline written at G1. A4 approved P2.W. A5 approved P2.W2. Unaffected work retains A3/A4 authority. P2.2a retries only after P2.W2 has task-reviewer PASS. |
| P2.W2 | P2.W | A5 approved the ensure-if-absent seam (A5-Q1 = (a)). P2.W2 may be dispatched. P2.2a retries only after P2.W2 has task-reviewer PASS. |
| O1 | every code task (T0, T2–T8, T10, P2.x) complete with task-reviewer PASS and integrated into `kyberdash-context/integration` | The owner runs a `kyberdash` build of the integration tip against real config |
| T11 | O1, T9 | Docs claims must match shipped and observed behaviour |
| T12 | T11 | Review over the integrated stack; APPROVE releases the per-task PRs |
| T13 | T12 | Archive and index; its PR opens last |

**MAX_CONCURRENCY: 3** (the dispatch bound). Every task counts against it, T1 included,
because every task runs on a headless OpenCode, Pi or Cursor CLI agent (D13).

**Current execution state.** T0–T10 retain PASS (T1 is read-only with no branch), and P2.W
is merged at `69e068441629dc27388a8a2b59a3a8df53649dea`. The unaffected work remains
allowed under A3/A4. A5 approved P2.W2, which may be dispatched. P2.2a retries only after
P2.W2 has task-reviewer PASS. O1, T11, T12 and T13 still follow all code tasks.

**File-overlap audit.** Every file above has exactly one owning task among T0–T10:
- `synth/synth.ts` and `readers/types.ts` belong to T2.
- `synth/provider.ts` and `providers/types.ts` belong to T5.
- `refresh/registry.ts` belongs to T10.
- `ingest.ts` and `receiver.ts` belong to T4.
- `twin-dedupe.ts` belongs to T6.
- `measurability.ts`, `harnesses.ts` and `signals.ts` belong to T3.
- `findings.ts` and `report/*` belong to T2.
- `register.ts` and `package*.json` belong to T8.
- `docs/adr/*` belongs to T9.
- `docs/dash/*` belongs to T11.

The only declared dependencies forced by file scope are T10 after T5 and T7. T3 waits on T2
for the type. Phase 2 files were pre-created as placeholders by T4 and T8.

## Risks

- **Merge-ready gate.** If this plan is committed on any task branch, CI on that task's PR
  fails (`KW-DOC-LIFECYCLE-003`). This is mitigated by the execution constraint at the top of
  this plan.
- **Stacked branches (D14).** Branches live only on this machine until T12 approves, so
  losing the local clone loses the run. Because each PR opens on its dependency's branch, it
  shows its base's commits until the earlier PRs merge. Open PRs in dependency order, and
  rebase each remaining branch onto `main` as its predecessors land.
- **Hypotheses may be false.**
  - Cursor (T5) and the Claude share key (T6) may not behave as expected.
  - Both tasks stop with evidence rather than widen scope.
- **User-owned files.**
  - JSONC, TOML and YAML edits can corrupt those files.
  - Mitigations: format-preserving JSONC edits, managed blocks, refuse-on-conflict,
    dry-run, the receipt, and D11's restore-or-refuse semantics.
  - The new dependency needs justification.
- **Writer declaration scope.** Implementing multi-file handling or status hooks separately
  in a harness module would bypass the core's receipt and drift guarantees. P2.W keeps those
  capabilities harness-agnostic and pins them with a synthetic seam test before any writer
  consumes them.
- **Closed port.** Exports sent while no receiver listens are lost. Under D9 the tray hosts
  the receiver, and `capture status` and `enable` report a down or foreign listener. The
  owner must actually turn the tray settings on in O1.
- **D10 changes existing behaviour.** Per-bucket OTel-first replaces the union-by-text merge
  for every OTel-and-file pairing. T6 lists any existing assertion it changes.
- **Content volume and truncation.**
  - Raw bodies raise storage, so R12.4 must be re-measured in O1.
  - Claude caps content at 61,440 UTF-16 units, so long system prompts or tool lists may be
    truncated. T1 confirms; the inventory documents it.
- **Retention.** The 14-day purge (ADR 0018) still empties captured content. This is
  expected.
- **Vendor documentation drift.**
  - A vendor can move a page or change its shape. P2.8 uses fixed official HTTPS sources,
    validates every positive window, updates one vendor atomically, and preserves the last
    known-good rows when that vendor fails.
  - Catalog values can become stale and model aliases can be ambiguous. Every row retains
    source URL and retrieval time; matching is exact or an explicit alias, never fuzzy.
  - A stale or matching catalog row remains `catalog`/`derived`, never `reported`,
    `declared` or `measured`. No model identity or no row remains `not_measurable`.
- **#235 regression.** Evidence-driven availability must not re-include Copilot
  `tool_yield`. T3 pins it.
- **ADR boundary.** D3 departs from ADR 0026's stance. ADR 0031 scopes it to KyberDash so
  Kyber Utilities' guarantee is not silently weakened.
- **Pre-existing failing host test.** `dash/src/canon/migration.test.ts` (DROP COLUMN).
  Disclose only.

## Out of scope

- Aspire as a target, and wiring `AspireSource`.
- Hooks, statusline or proxy collectors for harnesses with no out-of-box export (G1-D7).
- Backfill or migration of existing rows (D1).
- Cursor OTel enablement. Cursor is not on the D6 list; only its pairing fix is in scope.
- Changes to Kyber Utilities.
- Windows support for the capture command.
- Coverage-gap findings naming the enable command (a possible follow-up).

## Verification gates

The dash and docs gates listed under Dispatchable tasks. At T13:

```bash
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
```

## Review and docs-dev closeout

T12 runs the council and T13 archives the plan.
- ADRs 0029–0031 come from T9.
- `docs/dash/*` comes from T11.
- The capability matrix from G1 is harvested into the telemetry inventory.

## GAPS

| Gap | Impact |
|---|---|
| The configured Kyber-Weave docs MCP server reported `root=/Users/dave/git/personal/buzz-router`, not this checkout. | Discovery for this revision used the repository's documentation-index fallback and narrow self-gathered reads. |
| The CodeGraph MCP default project is another codebase. One call with `projectPath` succeeded; the remaining citations are self-gathered. | Line numbers are from Grep/Read at `c5b35ec` plus the working tree. |
| The architect did not independently re-run the live-store queries. | This revision relies on the persisted T1 capability matrix, which was audited PASS after read-only collection and integrity checks. |
