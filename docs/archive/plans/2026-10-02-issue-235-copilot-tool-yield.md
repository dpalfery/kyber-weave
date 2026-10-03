---
id: plans/2026-10-02-issue-235-copilot-tool-yield
title: "KyberDash: Copilot OTLP sessions drop out of tool_yield (#235)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# KyberDash: Copilot OTLP sessions drop out of tool_yield (#235)

**Status: Complete, archived 2026-10-02.** Decisions Q1–Q3 locked (A7–A9). Approve-and-execute
recorded 2026-10-02: Hal approves; conductor relayed Q1=(a), Q2=(c), Q3=(a). T1–T4 complete;
T2 explicit no-op under A7. Council expected **APPROVE**. No ADR — A9 chose architecture
prose in [dash/architecture.md](../../dash/architecture.md) (honest-measurement /
`tool_yield` re-inclusion). Archived here per `KW-DOC-LIFECYCLE-003` before
`docs validate . --merge-ready`.

This plan addresses GitHub issue
[#235](https://github.com/dpalfery/kyber-weave/issues/235), a follow-up to
[PR #226](https://github.com/dpalfery/kyber-weave/pull/226) and
[issue #180](https://github.com/dpalfery/kyber-weave/issues/180).

**Development mode:** `test-first`. Explicit in the dispatch, no opt-out. Every
implementation task defines a failing contract test before production logic changes.

---

## 1. Problem and Goal

### 1.1 The reported behaviour change

On main before PR #226, a Copilot OTLP session that carried tool definitions and no tool
spans counted as 0 of N tools used. PR #226 made the `tool_yield` digest gate
*measurability-based* rather than value-based:

```173:191:dash/src/canon/harnesses.ts
    if (summary && Array.isArray(summary.tools_offered)) {
      // Measurability, not values: a present tools_invoked — even an empty
      // one — means the producer observed invocations (a measured zero),
      // while an absent one means the invocation side was never exported
      // and the session must not enter the denominator at all.
      const invocationsObserved = Array.isArray(summary.tools_invoked)
      if (invocationsObserved) {
        digest.totalDefinedTools += summary.tools_offered.length
        const offeredSet = new Set(summary.tools_offered)
        if (Array.isArray(summary.tools_invoked)) {
          const invokedOffered = summary.tools_invoked.filter((t) => offeredSet.has(t))
          digest.totalInvokedTools += invokedOffered.length
        }
```

`tools_invoked` is emitted by `buildSessionRow` only when the session's records declare
`tool_calls` measurability, or when the harness is Claude:

```624:634:dash/src/canon/sessions.ts
  if (toolRecords.length > 0) {
    toolCalls = toolRecords.length
    toolsInvoked = Array.from(new Set(invocationsOf(records)))
  } else {
    const isClaude = harness === 'claude' || harness === 'claude-code' || surveyFamily(harness) === 'claude-code'
    const toolCallsAvailability = measurability?.tool_calls ?? (isClaude ? 'measured' : undefined)
    if (toolCallsAvailability === 'measured' || toolCallsAvailability === 'derived') {
      toolCalls = 0
      toolsInvoked = []
    }
  }
```

No Copilot producer declares `tool_calls`, so such sessions are now excluded from
`tool_yield` entirely (the dimension reads unmeasured) instead of reporting 0.

### 1.2 What the issue decided, and what it did not

Issue #235's own body is in two halves, and the distinction is load-bearing:

- **"Why this is accepted for now"** states the current exclusion is the honest posture
  under the unobservability rule, naming three Copilot paths that cannot vouch for tool
  completeness.
- **"What re-includes these sessions"** is a *conditional criterion*, not an approved
  change: *"A Copilot producer path **that can vouch for completeness** should declare
  `tool_calls: 'measured'` measurability on its records."*

The issue does not name which path can vouch, and does not assert that one exists.
Investigation (F3/F4) found none can today without fabricating measurements. Locked
decisions A7–A9 treat the exclusion as accepted behaviour, pin it at the producer
boundary, and record the re-inclusion criterion in architecture docs.

### 1.3 Goal

Keep the Copilot `tool_yield` exclusion as deliberate accepted behaviour: add a
producer-level regression pin (T1), leave measurability unstamped (T2 no-op), and record
the re-inclusion criterion beside the honest-measurement rules in
`docs/dash/architecture.md` (T3). Under no option may a Copilot session report a tool
yield it did not measure. Live OTLP capture and any honest attribution fix that would
enable stamping are deferred follow-ups (A8), not this slice.

---

## 2. Investigation Findings

Discovery used the CodeGraph index (initialized per the root `AGENTS.md` standing
authorization; `git status --short` was clean before and after, so no tracked file was
rewritten). CodeGraph's symbol graph resolves C# well but returned mostly `.NET` Squad
symbols for the TypeScript question, so the Copilot paths below were traced by targeted
reading of the named files. **The Kyber-Weave MCP `docs_*` tools are not available in this
harness**, so documentation discovery fell back to [`docs/README.md`](../../README.md) and
[`docs/dash/architecture.md`](../../dash/architecture.md) as the root `AGENTS.md` requires the
fallback be stated.

### F1. The mechanism the issue describes is already built, and already pinned

`buildSessionRow` → `tools_invoked: []` → digest measured-zero works end to end today, and
the test the dispatch asked for **already exists on main**:

```488:529:dash/src/canon/harnesses.test.ts
  it('reports tool_yield 0 measured when the session measured zero invocations (tools_invoked [])', () => {
    // A present-but-empty tools_invoked is a measured zero, not an unobserved
    // side: the denominator accrues and the yield is honestly 0. Driven
    // through buildSessionRow so the shape is one the producer emits (a
    // session whose records declare tool_calls measurability but carry no
    // tool spans).
```

It builds a record with `measurability: { token_usage: 'measured', tool_calls: 'measured' }`
and a `tool_definitions` part naming `ToolA`/`ToolB`, and asserts
`rollup.toolYield === 0` with `measurability['tool_yield'] === 'measured'`. Its negative
twin at line 531 asserts the exclusion when `tool_calls` is absent. **The deliverable named
in the dispatch as "pin with a test" is therefore already satisfied**; what is missing is a
producer that stamps the declaration — and under A7 no producer will stamp it in this
slice. T1 instead pins the *exclusion* at the Copilot producer boundary.

### F2. No producer anywhere stamps `tool_calls` measurability

Across `dash/src/canon`, `dash/src/synth` and `dash/src/canon/adapters`, `tool_calls`
appears only as the *reader* at `sessions.ts:629` and in tests. The sole route to a measured
zero in production is the hard-coded `isClaude` harness-name branch on the same line.

### F3. Every Copilot file-sourced path is disqualified, including one the issue did not name

| Path | Symbol / site | Why it cannot vouch |
|---|---|---|
| OTEL SQLite join | `dash/src/providers/copilot.ts:1958` — `toolsByTrace.get(spanMetadata.trace_id) ?? []` | Silent empty fallback; named in issue #235 |
| Chat-session journal | `extractChatSessionTools`, `dash/src/providers/copilot.ts:615` | Returns `[]` when `toolCallRounds` metadata is absent; named in issue #235 |
| ASAD session-store reader | `dash/src/synth/readers/copilot.ts:93` — `tools: []` | **Not named in the issue.** The reader emits a hard-coded empty tool list on every turn, so it can never distinguish "no tools" from "tools not read" |
| VS Code journal reader | `dash/src/synth/readers/copilot-vscode.ts` | Reader limits already declared: `READER_UNMEASURABLE` maps `copilot-vscode` to `['schema_cost', 'system_prompt', 'tool_definitions', 'tool_result_content']` (`measurability.ts:101`) |

File-sourced Copilot records additionally receive `measurabilityFor('copilot')`, whose
`READER_UNMEASURABLE` entry is `['schema_cost', ...CANONICAL_CONTENT_KEYS]` — `tool_calls`
is not among the keys any file path declares.

### F4. The OTLP path is the only remaining candidate, and today it would fabricate the zero

This is the decisive finding.

1. `copilotAdapter.normalize` builds on `baseRecord`, which stamps measurability **only**
   from `adapter.unexportedMetrics()` — `['reasoning']` for Copilot — plus the content
   buckets when a span carries no parts (`dash/src/canon/adapters/copilot.ts:634-662`,
   `730-746`). It never declares `tool_calls`.
2. Copilot *does* export tool spans over OTLP: `canonicalOp` maps any span carrying
   `gen_ai.tool.name` to `op: 'tool.invoke'` (`adapters/copilot.ts:112-121`), and the OTEL
   SQLite store — which is Copilot's own OTLP sink — holds `execute_tool` spans
   (`providers/copilot.ts:1883`).
3. **But those tool spans carry no session-identity attribute.** `SESSION_ID_KEYS`
   (`adapters/copilot.ts:129-135`) is `gen_ai.session.id`, `session.id`,
   `copilot_chat.chat_session_id`, `copilot_chat.session_id`, `gen_ai.conversation.id`. The
   SQLite provider's own query proves the tool spans lack them: it resolves a conversation,
   collects the **trace ids** of the spans that carry `gen_ai.conversation.id`, and only
   then re-queries the whole trace to find `execute_tool` spans
   (`providers/copilot.ts:1819-1852`). If tool spans carried the conversation id, that trace
   hop would be unnecessary.
4. Canonical sessions are keyed `COALESCE(session_id, trace_id)`
   (`dash/src/canon/store.ts:1741`), and `ingestBatch` performs **no** session-id
   propagation across a trace (`dash/src/canon/ingest.ts:121-195`).

Therefore, on the OTLP path today, a Copilot chat span keys onto its conversation id while
its `execute_tool` spans key onto the raw trace id — **different session rows**. For every
Copilot OTLP chat session, `toolRecords.length === 0` whether or not the session invoked
tools. Stamping `tool_calls: 'measured'` on `copilotAdapter.normalize` as it stands would
therefore report `toolYield 0` for sessions that invoked dozens of tools: a fabricated
measurement, strictly worse than the exclusion it replaces, and a direct breach of
[`docs/rules/honest-unobservability.md`](../../rules/honest-unobservability.md).

This evidence superseded the issue's suggested "stamp measured on a vouching path" fix for
this slice: no path can vouch without fabricating (A7).

### F5. The empirical gap cannot be closed in this checkout

Confirming whether a current Copilot build attaches `gen_ai.conversation.id` to
`execute_tool` spans requires live data. This host has no canonical store
(`~/.kyberdash/canon.db` absent; `~/.kyberdash/` holds only `cache/`) and the repository
carries no Copilot OTLP tool-span fixture with a session id — the one tool-span test,
`dash/src/canon/adapters/copilot.test.ts:100-108`, uses `github.copilot.chat.turn.id` and
no session key. Under A8 the live capture is deferred to a follow-up; Q1 was decided on
in-repo code evidence (F3/F4).

### F6. Disclosed-failing-test list needs two corrections

- `dash/src/refresh/migration.test.ts` **does not exist**. The DROP COLUMN test is
  `dash/src/canon/migration.test.ts:530`.
- The Cursor six-month-cap date rot was **already fixed on main** by
  [`fae03a2`](https://github.com/dpalfery/kyber-weave/commit/fae03a2) ("derive six-month cap
  floor from clock in cursor provider test", #258), which replaced the hardcoded
  `2026-04-01` with a clock-derived floor. It should not be pre-excluded.

Neither may be fixed by this plan; both are recorded so the closeout disclosure is accurate.

### F7. `dash/node_modules` is absent in this checkout

No Vitest run was performed. Every claim above about test content is from reading the source,
not from a green run. The first implementation task must begin with `npm --prefix dash ci`
and a baseline run so RED evidence is distinguishable from a broken install.

---

## 3. Explicitly Approved Decisions

| Id | Decision | Approval provenance |
|---|---|---|
| **A1** | Development mode is `test-first`, with no opt-out. | Conductor dispatch, 2026-10-02 (explicit). |
| **A2** | The re-inclusion mechanism, *if* it is exercised, is measurability-based: a Copilot producer path that can vouch for completeness declares `tool_calls: 'measured'` on its records; `buildSessionRow` then emits `tools_invoked: []` for span-less sessions and the digest counts a measured 0. | Issue [#235](https://github.com/dpalfery/kyber-weave/issues/235) body, section "What re-includes these sessions". Upstream decision; **conditional on a vouching path existing**. This slice does not exercise it (A7). |
| **A3** | The OTEL SQLite join (`dash/src/providers/copilot.ts`, trace-scoped join with `?? []`) and the chat-session journal (`extractChatSessionTools`) must **not** be stamped `measured`. | Issue #235 body, section "Why this is accepted for now"; restated in the conductor dispatch. |
| **A4** | Absence is not a measured zero. Where a path cannot vouch, exclusion is the correct posture. | Issue #235 body; [`docs/rules/honest-unobservability.md`](../../rules/honest-unobservability.md). |
| **A5** | Any new `codeburn/*` wire-format key requires an entry in the branding `ALLOWED` list (`dash/src/branding/user-visible-name.test.ts:33`) carrying the reason it cannot change. | Root `AGENTS.md` branding gate; conductor dispatch. |
| **A6** | Known pre-existing test failures are disclosed, never fixed, and excluded only if they block. | Conductor dispatch. Subject to the corrections in F6. |
| **A7** | Q1=(a): None. Keep the Copilot `tool_yield` exclusion as accepted behaviour; no measurability stamping in this slice. Add the producer-level regression pin (T1) and the docs criterion (T3). Rationale: OTLP tool spans key by `trace_id`, not conversation id (F4); stamping would fabricate measurements and violate honest-measurement doctrine. The issue's suggested fix is superseded by this evidence. Options (b)/(c)/(d) are rejected for this slice. | Hal / conductor 2026-10-02 (approve-and-execute). |
| **A8** | Q2=(c): Defer live OTLP capture to a follow-up; decide on in-repo code evidence now (consistent with A7). | Hal / conductor 2026-10-02 (approve-and-execute). |
| **A9** | Q3=(a): Record the re-inclusion criterion in `docs/dash/architecture.md` beside the existing honest-measurement rules. | Hal / conductor 2026-10-02 (approve-and-execute). |
| **A10** | Approve-and-execute: finalize this plan Draft → Ready (frontmatter `status: current`) with A7–A9 locked; implementation may proceed on T1 and T3; T2 remains the explicit no-op under A7. | Hal approves / conductor 2026-10-02. |

---

## 4. Test Contract

Per the test-first contract, every implementation task carries a row before implementation
starts. Rows are locked to A7–A9. Changing an approved row here is a scope change that
returns this plan to Draft.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| **T1** | `dash/src/canon/adapters/copilot.test.ts` and `dash/src/canon/sessions.test.ts` | `npm --prefix dash exec vitest run src/canon/adapters/copilot.test.ts src/canon/sessions.test.ts` | A record from `copilotAdapter.normalize` declares no `tool_calls` key, and a session built from Copilot OTLP records with `tool_definitions` parts and no `tool.invoke` records omits `tools_invoked` from its summary — the exclusion is asserted, not incidental (A7). | Named test fails for the intended missing assertion (absent declaration) with full output captured, after `npm --prefix dash ci` and a baseline run per F7. | Same test passes with no weakened assertion; the pre-existing pins at `harnesses.test.ts:488` and `:531` stay green unmodified. |
| **T2** | **Explicit no-op under A7.** No automated test; no production change. | n/a | Verification is T1 GREEN with the production tree untouched (no measurability stamp, no attribution change). | n/a | T1 green; `dash/src/canon/adapters/copilot.ts`, `ingest.ts`, `store.ts`, and `sessions.ts` production paths unchanged by this task. |
| **T3** | **Explicitly no automated test.** Documentation-only. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `… docs drift .` | The re-inclusion criterion and the F4 attribution constraint are stated in `docs/dash/architecture.md` beside the honest-measurement rules (A9). | n/a — replaced by read-only verification: both documentation commands report zero findings, and the criterion is reachable from [`docs/README.md`](../../README.md). | Both commands zero findings; a reviewer can reach the criterion from the documentation index without reading the issue. |
| **T4** | **Explicitly no automated test.** Governance and closeout. | The full gate list in §7 | The plan's index row, lifecycle status and archival obligation are correct and the declared gates pass. | n/a — replaced by the gate run in §7 and the disclosure in §8. | Every gate in §7 reports its result; F6's corrections are carried into the closeout disclosure verbatim. |

No task may reach green by weakening `harnesses.test.ts:488` or `:531`.

---

## 5. Tasks

### T1 — Pin the exclusion at the Copilot producer boundary

- **Objective.** Assert that Copilot OTLP normalization does not declare `tool_calls`
  measurability, and that a span-less Copilot session with `tool_definitions` omits
  `tools_invoked` from its summary — the A7 exclusion is a contract, not an accident.
- **Files / symbols.** `dash/src/canon/adapters/copilot.test.ts` (`copilotAdapter.normalize`),
  `dash/src/canon/sessions.test.ts` (`buildSessionRow`). **No production edits** under A7.
- **Acceptance criteria.** T1's Test-contract row is green; `harnesses.test.ts:488` and
  `:531` unchanged and green; `npm --prefix dash run typecheck` and `lint` clean.
- **Dependencies.** None (A7 locked).
- **Required skills.** `test-dev`.

### T2 — Attribution change — explicit no-op (A7)

- **Objective.** Under A7 this task is a deliberate no-op: no production change is made,
  and verification is T1 passing with the production tree untouched. Silence would be
  invalid under the test-first contract, so the no-op is stated here rather than omitted.
- **Files / symbols.** None under A7. (Were A7 reversed to Q1=(b), scope would be
  `dash/src/canon/ingest.ts` (`ingestBatch`), `dash/src/canon/store.ts` (`sessionKeys`,
  `upsertMany`), `dash/src/canon/adapters/copilot.ts` (`canonicalSessionId`), with
  `PARSER_CONTRACT_VERSION` assessed before any bump — that path is out of scope for this
  slice.)
- **Acceptance criteria.** T1 green; production files listed above unchanged by this task.
- **Dependencies.** T1 (shared verification that production is untouched).
- **Required skills.** None under A7.

### T3 — Record the re-inclusion criterion

- **Objective.** Write down in `docs/dash/architecture.md` (honest-measurement section)
  that Copilot OTLP sessions are excluded from `tool_yield` deliberately, and the two
  conditions that would re-include them (a vouching producer path per A2, and tool spans
  joining the conversation per F4).
- **Files.** `docs/dash/architecture.md` (honest-measurement section).
- **Acceptance criteria.** `docs validate .` and `docs drift .` report zero findings; the
  criterion is reachable from the documentation index.
- **Dependencies.** None (A9 locked). Scope-disjoint from T1, so it may run concurrently.
- **Required skills.** `app-docs-standard`, `second-brain`.

### T4 — Governance and closeout

- **Objective.** Keep plan lifecycle and index in sync, run the declared gates, and disclose
  pre-existing failures accurately.
- **Files.** This plan; [`docs/plans/README.md`](../../plans/README.md) (Active Plans row);
  relocation to `docs/archive/plans/` per `KW-DOC-LIFECYCLE-003`.
- **Acceptance criteria.** §7 gates all reported; F6's corrections carried into the
  disclosure; `docs validate . --merge-ready` passes in the archiving PR.
- **Dependencies.** T1, T2, T3.
- **Required skills.** `app-docs-standard`.

---

## 6. Dependency Graph and Concurrency

```text
T1 ──> T2 (no-op) ─┐
                   ├──> T4
T3 ────────────────┘
```

- `T1 → T2`: under A7, T2 only verifies that T1 left production untouched.
- `T3` shares no file scope with `T1`/`T2` and may run alongside them.
- `T4` consumes the concrete output of all three.

**MAX_CONCURRENCY: 2** — `T1` and `T3` in parallel; T2 is a no-op after T1.

---

## 7. Verification Gates

```bash
npm --prefix dash ci            # F7: absent in this checkout
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable

dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

The branding gate (A5) runs inside `npm --prefix dash run test` via
`dash/src/branding/user-visible-name.test.ts`. No new `codeburn/*` wire-format key is
anticipated under A7 — `tool_calls` is an existing measurability key — but if one
appears it needs its `ALLOWED` entry and reason in the same commit.

---

## 8. Risks, Out of Scope, Review and Closeout

### Risks

| Id | Risk | Mitigation |
|---|---|---|
| **R1** | Stamping `measured` on the OTLP path without fixing attribution reports a fabricated `toolYield 0` for every Copilot OTLP session (F4). | A7 rejects stamping; options (c)/(d) rejected-for-cause. |
| **R2** | A future Q1=(b) attribution change could split or merge existing session rows. | Out of scope this slice (A7); if reopened, pin that no conversation that already had a session id changes it and assess `PARSER_CONTRACT_VERSION`. |
| **R3** | The decision rests partly on an empirical fact about a current Copilot build that cannot be checked here (F5). | A8 defers live capture; A7 proceeds on code evidence (F3/F4) and treats fabrication risk as decisive. |
| **R4** | The dispatch's "pin with a test" deliverable already exists (F1), so a well-meant implementation could duplicate or weaken it. | T1 acceptance forbids touching `harnesses.test.ts:488`/`:531`; T1 pins exclusion at the producer, not the digest. |
| **R5** | Pre-existing failures could be mistaken for regressions. | F6 corrections carried into closeout; `dash/src/canon/migration.test.ts:530` is the real DROP COLUMN site; the Cursor cap test is fixed on main and must not be pre-excluded. |

### Out of scope

- Any change to the Claude-only `isClaude` fallback at `dash/src/canon/sessions.ts:629`.
- Fixing the three disqualified Copilot file paths (F3) so they can vouch.
- Re-opening the PR #226 measurability-gate design; A2 and A4 stand.
- Fixing either disclosed pre-existing failure (A6).
- Any `tool_yield` change for a harness other than Copilot.
- Live OTLP capture and span-attribution / session-id propagation (A8 / rejected Q1=(b)).
- Stamping `tool_calls: 'measured'` on any Copilot producer path (A7).

### Review

A `code-review` council pass on the branch before the PR, with the `static-analysis-triage`
lens reading `artifacts/inspectcode.xml` if any `.NET` file is touched (none is anticipated).
The honest-measurement rule is the review's primary lens here: a reviewer must be able to
state, from the diff alone, that exclusion remains deliberate and that no path was stamped
without vouching evidence.

### `docs-dev` closeout

Harvest nothing to an ADR (A9 chose architecture prose, not an ADR). Body status, frontmatter
`status: complete`, and the plans index row updated in the same save; archived here so
`docs validate --merge-ready` passes `KW-DOC-LIFECYCLE-003`.

### Closeout disclosure (F6)

Known pre-existing host failures are disclosed, never fixed by this plan (A6), subject to
these corrections:

- The DROP COLUMN migration test is `dash/src/canon/migration.test.ts:530` — not
  `dash/src/refresh/migration.test.ts` (that path does not exist).
- The Cursor six-month-cap date rot was already fixed on main by
  [`fae03a2`](https://github.com/dpalfery/kyber-weave/commit/fae03a2) ("derive six-month cap
  floor from clock in cursor provider test", #258). It must **not** be pre-excluded.

### Closeout evidence

| Check | Result |
|---|---|
| T1 producer exclusion pin | Complete — Copilot OTLP normalize / span-less session contracts |
| T2 attribution / measurability stamp | Explicit no-op under A7 — production tree untouched |
| T3 architecture re-inclusion criterion | Complete — [dash/architecture.md](../../dash/architecture.md) honest-measurement / `tool_yield` |
| T4 review / archive | Complete — council expected APPROVE; F6 disclosure above; plan archived 2026-10-02 |
| ADR | None — A9 recorded the criterion in architecture prose |
