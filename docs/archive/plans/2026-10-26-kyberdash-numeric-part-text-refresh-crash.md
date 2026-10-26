---
id: plans/2026-10-26-kyberdash-numeric-part-text-refresh-crash
title: "KyberDash refresh survives numeric ContentPart.text and degrades per session (tray 'Last refresh failed' TypeError)"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-26
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberDash]
---

# KyberDash refresh survives numeric ContentPart.text and degrades per session (tray "Last refresh failed" TypeError)

**Status: Archived — complete 2026-10-26 (T1–T4 + findings-phase hardening, closeout mapping in §10 Review, closeout, and findings).**
**Development mode:** `test-first` (packet). **Autonomy:** `autonomous` (packet; matches Config Reg `<autonomy>`).

**Intake:** diagnosed defect escalated by the investigator (verdict `NEEDS_ARCHITECT`, 0 fix attempts).
REPRODUCED: no — HEAD refresh succeeds (exit 0); the TypeError is proven only in installed-binary
artifacts (10 identical stderr logs at `/Users/dave/.kyberdash/failures/refresh-2026-10-26T20-09-33-170Z.stderr.log`).
Root-cause **mechanism** proven, **producer** unknown (version skew: the installed binary diverges
from HEAD; new ingest paths landed this week). Live `canon.db`: `records` = 0, `refresh_run` last
row 2026-10-22 — the store was cleaned and every re-ingest refresh since has failed, so nothing
repopulates: a crash loop.

> Discovery: CodeGraph index present; `codegraph_explore` ran first for every code question
> (token counter/`cacheKey`, `partsOf`/`reduceRecordParts`/`deriveRecordViews`/`buildSessions`,
> `reduceContextParts`/`analyzeContext`, `PROVIDER_READERS`/`ingestProviders`,
> `refreshHarnessSources`/`completeRefreshRun`/`recordProblem`, `ContentPart`/`contentFromParts`).
> The Kyber-Weave docs MCP (`docs_explore`) answered the plan inventory/lifecycle and closeout
> policy; `agent_coding_standards` answered the TypeScript error-handling standard. Direct
> reads, each for something an index does not cover: the plan index
> (`docs/plans/README.md`, full inventory format), the architect references under
> `products/kyber-squad/agents/architect/references/` (product tree, not `docs/`), one archived
> plan's frontmatter (exact governed shape for `docs validate`), a catalog grep (component row),
> and globs confirming the four named test files exist. `Discovery: codegraph 4, docs 2,
> direct reads 5`.

---

## 1. Problem and goal

### 1.1 Problem

The tray reports `Last refresh failed: TypeError [ERR_INVALID_ARG_TYPE]: The data argument must
be of type string or Buffer... Received type number (28)`. A canonical `ContentPart` whose
`text` is the number `28` reaches derivation; the chain throws and the refresh run fails:

1. **`partsOf` copies `part.text` unguarded** (`dash/src/canon/sessions.ts:445-453`). The flat
   fallback path filters `typeof entry[1] === 'string'` (`:456`); the structured path has no
   such guard, so a numeric `text` crosses into `ContextPart`.
2. **`reduceContextParts` counts it** (`dash/src/analysis/context.ts:428` via
   `part.tokens ?? countTokens(part.text)`; the marker check at `:400` would also throw on
   `.includes`). `analyzeContext` reads `part.text` the same way (`:519`, `:527`, `:541`).
3. **The memoized counter fails open on non-string**
   (`dash/src/canon/tokens.ts:313-319`): `text.length` on a number is `undefined`, so the
   `minLength` short-circuit is skipped and `cacheKey` (`:85-87`) hands the number to
   `createHash('sha256').update(text, 'utf8')`, which throws `ERR_INVALID_ARG_TYPE`.
4. **The throw burns the entire projection.** `refreshHarnessSources`
   (`dash/src/refresh/orchestrator.ts:142-147`) catches a `projectCanonicalStore` failure only
   coarsely (`derivationFailed = true` → exit 1, run row `failure`): **no** sessions, runs,
   executions or rollups are derived. On the installed binary the error escapes further and
   crashes the process (stderr log captured; the installed orchestrator predates or bypasses
   the coarse catch). After a clean, `records` stays 0 and every refresh fails — the tray never
   recovers.

Producer of the numeric `text`: **unknown** — `ContentPart` has 112 construction sites across
`dash/src/synth/readers/*` and `dash/src/canon/adapters/*`, the installed binary diverges from
HEAD, and HEAD's refresh exits 0 on this machine.

### 1.2 Goal

- **No input shape crashes derivation.** A `ContentPart` with non-string `text` is refused at
  every consumption boundary in the projection path, and the token counter fails closed with a
  domain error instead of a crypto `ERR_INVALID_ARG_TYPE`.
- **One malformed session degrades, not aborts.** Derivation failures are recorded per session
  as `problems` rows (visible in Problems view), the session is skipped, every other session
  still derives, and the refresh run completes — so a clean's re-ingest repopulates records and
  the tray goes green even while a producer bug exists.
- **The producer is named or the divergence is proven.** A scanner over `PROVIDER_READERS`
  output runs at HEAD against the real harness stores (read-only), and the installed binary is
  version-diffed against HEAD, so the numeric-text emitter is either fixed at HEAD or recorded
  as an installed-only divergence with the evidence.

## 2. Investigation findings

- **Mechanism (proven).** `createCachedCounter.count` (tokens.ts:313) → `cacheKey` (tokens.ts:85)
  → `createHash.update(28)` throws. `count`'s guard `text.length < minLength` cannot catch a
  non-string (`undefined < N` is `false`), and nothing between `partsOf` and the counter checks
  `typeof`.
- **Same defect class, additional unguarded sites (verified in source).**
  - `reduceRecordParts`' tool-definitions loop reads `part.text` and calls
    `extractToolDefinitionsFromText` whose first line runs `text.trim()`
    (sessions.ts:526-544 → :805) — throws on a number.
  - `taskSummaryOf` guards with `part.text && part.text.trim()` (sessions.ts:596) — `28 &&` is
    truthy, `.trim()` throws.
  - `analyzeContext` calls `part.text.includes` / `countTokens(part.text)`
    (context.ts:519, :527, :541).
  - `contentFromParts` (types.ts:152) does not crash; it silently coerces via `join('\n')` —
    ingest-time, out of scope (§8).
- **Orchestrator today.** `refreshHarnessSources` wraps `projectCanonicalStore` in a catch and
  marks `derivationFailed` (orchestrator.ts:142-147, :150, :172) — the run closes as `failure`
  with `derivation failed` in the summary and exit 1. `RefreshReport` already carries a
  `derivationFailed` boolean; `BuildSessionsReport` counts
  `{built, skipped, pruned, rollups, findings}` (sessions.ts:1044). `store.recordProblem`
  (store.ts:5439) upserts `SpanProblem` rows (span id, code, severity, message, optional
  session/harness/stack) — the existing record-and-continue seam (R1.3).
- **Ingest is not the crash site.** `ingestProviders` (synth/provider.ts:331) already
  record-and-continues per provider; the writer commits records before projection. The live
  `records = 0` is consistent with the crash loop on the installed binary, not with a HEAD-path
  ingest failure.
- **Version skew.** Installed binary ≠ HEAD (investigator: new ingest paths this week). HEAD
  refresh exits 0 here, so the producer either is not at HEAD or needs this machine's real
  transcripts to trigger. The stderr logs and `kyberdash --version` are the attribution inputs.
- **Coverage.** No existing test exercises non-string `part.text` anywhere in the chain
  (investigator: EXISTING COVERAGE: NONE; codegraph blast radius shows no covering tests for
  `partsOf`/`deriveRecordViews`).

## Decisions

Permanent section, preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Counter behaviour on non-string input: fail closed with a domain error, coerce to `String`, or return 0? | **Fail closed.** `count` throws `TypeError` naming the contract and the received `typeof` (bounded value preview), before the memo/`cacheKey` path. | Coercion or 0 would hide the producer bug and poison derived counts; the domain message is what the per-session degrade (D3) records and what a future producer fix greps for. The TypeScript standard (catch where the code can recover or add useful context) supports translating at this boundary. Alternatives declined: `String(text)` coercion; silent 0. | Decided — directed by intake request ("fail-closed counter in tokens.ts"); autonomous confirmation of message shape. |
| D2 | `partsOf` structured-path guard semantics: filter non-string parts (mirror the flat path at `:456`) or substitute empty text and keep measured `tokens`? | **Filter**, exactly mirroring the flat path: a part whose `text` is not a string is dropped before it becomes a `ContextPart`. | One rule for both paths (the flat path's precedent is in-repo); a corrupt part's harness-reported `tokens` are not trustworthy evidence once the record violates the `ContentPart` contract (`text: string`, types.ts:128); the producer hunt plus per-session problem row make the loss visible rather than silent. Alternative declined: `text: ''` substitution preserving `tokens`. | Decided — directed by intake request ("typeof guard in partsOf matching flat path :456"). |
| D3 | Degrade boundary and exit semantics for derivation failures: whole-projection catch only (today), per-session catch with run `failure`, or per-session catch with run `success`? | **Per-session catch in `buildSessions`**: the failing session is skipped, a `SpanProblem` (severity `error`, code `derivation_failed`, stack carried) is recorded, `BuildSessionsReport` gains a `derivationDegraded` counter, and the refresh run **succeeds** (exit 0) when only sessions degraded — the summary names the degraded count. The whole-projection catch in the orchestrator stays as the outer belt. | "Degrade not abort" is the intake directive; marking the run failed forever is the tray crash loop this plan removes (records never repopulate after a clean). Honesty is preserved: the problem row is visible in Problems view, the summary carries the count, and the outer belt still fails the run if the projection itself cannot run. Follows the R1.3 record-and-continue pattern. Cost of wrong (an operator misses a skipped session) is cheap to reverse — flip exit semantics in one comparison. Alternatives declined: keep whole-run failure; per-session catch but exit 1. | Decided — `autonomous: low-confidence, no authoritative source` (exit-code choice is the plan's call; degrade directive itself is from the intake request). |
| D4 | Scope of guards: only the three intake-named sites, or all verified unguarded consumers in the same defect class? | **All verified sites in the projection path**: `partsOf`, the `reduceRecordParts` tool-definitions loop, `taskSummaryOf` (all sessions.ts), and `reduceContextParts` + `analyzeContext` (context.ts, one module-local safe-text helper). | Codegraph-verified `.trim()`/`.includes()`/`countTokens` reads on `part.text` in each; a guard on only three sites leaves the crash reachable through the others once D2's filter is bypassed by a future path. Same file, same mechanism, one task each — no scope creep beyond the class. | Decided — `autonomous: low-confidence, no authoritative source` (blast-radius evidence in §2). |
| D5 | Producer-hunt scanner disposition: committed tool, or uncommitted diagnostic with findings recorded? | **Uncommitted** throwaway script run via `tsx` from `dash/`, findings recorded in this plan; if the producer reproduces at HEAD, the failing fixture becomes a committed regression test in the follow-up fix. | The TypeScript standard forbids tests reading the developer's home; a permanent scanner over live harness stores would violate it. A committed fixture from real transcripts has the same problem — a minimized synthetic fixture is acceptable only once a producer is actually named. | Decided — covered by the TypeScript coding standard (tests must not read live home state). |
| D6 | Does the defensive work ship even if the producer is not found? | **Yes.** T1–T4 are independent of producer attribution; T5 records whatever it finds. | The installed-binary skew means attribution may require the machine's real transcripts; the crash loop must stop regardless. If T5 proves an installed-only divergence, the defensive layers are the fix until the next release carries them. | Decided — directed by intake request ("defensive hardening independent of producer"). |

## Design harvest

`documentation-impact: extends-component`, `impact-components: [KyberDash]` — every change sits
in the shared KyberDash application (`dash/src/canon`, `dash/src/analysis`, `dash/src/refresh`);
no tray or web file changes (display-layer rule, ADR 0033).

Design harvest (one row per durable item; harvested at closeout):

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | ARCHITECTURE (`dash/architecture.md` part-text boundary rule) | `ContentPart.text` is a string at every derivation consumer; non-string parts are filtered (both `partsOf` paths), the token counter fails closed with a domain error, and the analysis layer reads part text through a safe-text helper. |
| H2 | KyberDash | ARCHITECTURE (`dash/architecture.md` per-session degrade contract) | One malformed session records a `derivation_failed` problem row and is skipped; the run succeeds with a degraded count in the summary; the whole-projection catch remains the outer belt. |
| H3 | KyberDash | RUNBOOK (`dash/runbook.md` degraded-but-successful operation) | `derivation_failed` rows in Problems view name the session and carry the stack; a healthy tray with a degraded count means skip-and-record, not silent loss. |

ADR disposition: **no new ADR — ADR waiver**: defensive refinement under ADRs 0009/0033 with no new boundary, dependency, schema, or lasting invariant beyond the boundary rule and degrade contract above.

## 5. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/tokens.test.ts` | `npm --prefix dash run test -- src/canon/tokens.test.ts` | `createCachedCounter(...).count(28 as never)` throws the domain `TypeError` (message names `string` and `number`); string paths unchanged: hit/miss/uncached stats, `cacheKey` digest, flush behavior. | New test fails on HEAD: `count(28)` throws crypto's `ERR_INVALID_ARG_TYPE`, not the domain message. | Same test passes; all pre-existing counter/cache assertions unchanged. |
| T2 | `dash/src/canon/sessions.test.ts` | `npm --prefix dash run test -- src/canon/sessions.test.ts` | A record whose `parts` carry `{part:'tool_definitions', text: 28}` (and a sibling string part) reduces without throwing: the numeric part is dropped, the sibling is counted, tool-definition extraction and `taskSummaryOf` skip non-string text. | New test fails on HEAD: `reduceRecordParts` throws (via `countTokens`→`cacheKey` or `.trim()`). | Same test passes; pre-existing sessions assertions unchanged. |
| T3 | `dash/src/analysis/context.test.ts` | `npm --prefix dash run test -- src/canon/context.test.ts` | `reduceContextParts` and `analyzeContext` treat non-string part text as no text: no marker-method throw, no `countTokens` call on a non-string, measured `tokens` on the part still counted, empty-and-unmeasured contributes nothing. | New tests fail on HEAD: `.includes` / `countTokens` throw on a number. | Same tests pass; pre-existing composition/residual assertions unchanged. |
| T4 | `dash/src/canon/sessions.test.ts`, `dash/src/refresh/orchestrator.test.ts` | `npm --prefix dash run test -- src/canon/sessions.test.ts src/refresh/orchestrator.test.ts` | With one session holding a malformed record and one healthy session: `buildSessions` completes, the healthy session is built, a `derivation_failed` problem row is recorded (error severity, stack carried), `BuildSessionsReport.derivationDegraded` is 1; `refreshHarnessSources` exits 0 with the degraded count in the summary; the whole-projection catch still marks `derivationFailed` when the projection itself throws. | New tests fail on HEAD: the malformed session's throw propagates (per-session test), and any projection failure marks the run `failure` (orchestrator test as written today). | Same tests pass; the coarse-catch behavior for projection-level failure is still asserted. |
| T5 | No committed test — read-only investigation (D5). | Uncommitted `tsx` scanner + `kyberdash --version` + git diff of the installed version (commands in the task). | Scanner output over every `PROVIDER_READERS` entry against the real harness stores reports any non-string part text with provider + file + part kind; installed version identified and diffed vs HEAD. | n/a (explicitly no-test). | Findings recorded in §10 with evidence: producer named (provider, file, code site) or installed-only divergence proven. |
| T6 | No-test (documentation). | `kyber-weave docs validate .` / `docs drift .` (conductor or docs-dev). | Architecture and runbook carry the harvest rows of §4. | n/a (explicitly no-test). | `docs validate` zero findings; `docs drift` zero findings. |

## 6. Tasks

### T1 — Fail-closed token counter

- **Objective.** Refuse non-string input at the counter boundary with a domain error instead of
  the crypto `ERR_INVALID_ARG_TYPE`.
- **Files/symbols.** `dash/src/canon/tokens.ts` — `count` in `createCachedCounter` (~:313):
  first-statement `typeof text !== 'string'` guard throwing
  `TypeError("token counter requires text: string (received typeof <t>: <preview>)")` with a
  bounded value preview. `cacheKey` (:85) keeps its signature and behaviour for strings.
- **Acceptance.** Test contract T1 RED→GREEN; no behavior change for string inputs (existing
  tests untouched and passing).
- **Dependencies.** None.
- **Skills.** `typescript-dev` (contract test first per §5).

### T2 — Structured-parts text guards in `sessions.ts`

- **Objective.** Non-string `part.text` never crosses `partsOf` into derivation, and the two
  other in-file consumers skip it instead of calling `.trim()`.
- **Files/symbols.** `dash/src/canon/sessions.ts`:
  - `partsOf` (:445-453) — filter parts whose `text` is not a string, mirroring the flat path
    (:456);
  - `reduceRecordParts` tool-definitions loop (:526-544) — skip non-string `part.text`;
  - `taskSummaryOf` (:591-613) — `typeof part.text === 'string'` before `.trim()`.
- **Acceptance.** Test contract T2 RED→GREEN; filter semantics identical to the flat path.
- **Dependencies.** None (file-disjoint from T1/T3).
- **Skills.** `typescript-dev`.

### T3 — Safe part-text reads in the analysis layer

- **Objective.** `reduceContextParts` and `analyzeContext` never invoke string methods or the
  counter on a non-string `part.text`.
- **Files/symbols.** `dash/src/analysis/context.ts` — one module-local safe-text helper used at
  `reduceContextParts` (:400 marker check, :406 strip, :424/:428 count) and `analyzeContext`
  (:519, :527, :541); non-string reads as `''` so measured `tokens` still count and an
  empty/unmeasured part contributes nothing (the existing structure rule at :424 governs).
- **Acceptance.** Test contract T3 RED→GREEN; composition/residual arithmetic unchanged for
  string parts.
- **Dependencies.** None.
- **Skills.** `typescript-dev`.

### T4 — Per-session derivation degrade

- **Objective.** One malformed session records a problem and is skipped; the rest of the
  projection and the refresh run complete.
- **Files/symbols.**
  - `dash/src/canon/sessions.ts` — `buildSessions` (:1048 session loop): wrap the per-session
    unit (record collection → grouping → view derivation → row build/upsert → subagent
    projection) in try/catch; on catch, `store.recordProblem({spanId: <session's first record
    or session key>, code: 'derivation_failed', severity: 'error', message: <error message>,
    sessionId/harness where known, stack: <error.stack>})`, `report.skipped += 1`,
    `report.derivationDegraded += 1`, continue; the counter's `flush()` still runs on the
    normal path.
  - `BuildSessionsReport` (:1044) — add `derivationDegraded: number`.
  - `dash/src/refresh/orchestrator.ts` — surface the degraded count in the run summary
    (:169-172; e.g. `"; N session(s) skipped: derivation failed"` appended on success); the
    coarse `projectCanonicalStore` catch (:142-147) and `RefreshReport.derivationFailed`
    semantics are unchanged.
- **Acceptance.** Test contract T4 RED→GREEN; refresh with only degraded sessions exits 0;
  projection-level failure still closes the run as `failure`.
- **Dependencies.** **T2** (same file `sessions.ts` — serialized after it).
- **Skills.** `typescript-dev`.

### T5 — Producer hunt and installed-binary diff (investigation; read-only, no repo writes)

- **Objective.** Name the numeric-text emitter or prove the divergence is installed-only.
- **Steps/files.**
  1. Capture `kyberdash --version` and the installed binary path (`which kyberdash`).
  2. Diff the installed version against HEAD: `git log --oneline <installed-tag>..HEAD --
     dash/src/synth dash/src/canon/adapters dash/src/providers` and the release notes for the
     ingest paths that landed this week; if the installed commit predates the coarse
     derivation catch, record that the crash-to-stderr path is explained.
  3. Uncommitted scanner (D5), run from `dash/` via `npx tsx`: import `PROVIDER_READERS`
     (`dash/src/synth/provider.ts:103`), iterate every registered reader over this machine's
     real harness stores **read-only**, collect every emitted turn/part whose `text` is not a
     string, print provider + source file + part kind + `typeof`. Write nothing under
     `~/.kyberdash` (no refresh, no store open).
  4. If (and only if) the producer reproduces at HEAD: record it, and open the follow-up as a
     RED fixture (synthetic, minimized) + producer fix — a new task or todo under
     `docs/todo/`, authorized by the finding, not by this plan silently.
- **Acceptance.** Findings with evidence recorded in §10: producer named (provider, file, code
  site) **or** installed-only divergence proven with the introducing diff.
- **Dependencies.** None (read-only; may run parallel to T1–T4).
- **Skills.** `typescript-dev` (read-only; the conductor may instead route the live-machine
  steps to the owner).

### T6 — Design harvest

- **Objective.** Land the §4 harvest rows in canonical documentation.
- **Files.** `docs/dash/architecture.md` (part-text boundary rule; per-session degrade
  contract), `docs/dash/runbook.md` (`derivation_failed` rows in Problems view; tray semantics
  of a degraded-but-successful refresh). `last-reviewed` bumps on both.
- **Acceptance.** Test contract T6; `docs validate` and `docs drift` zero findings.
- **Dependencies.** T2, T3, T4 (harvest the landed behavior; may be drafted in parallel).
- **Skills.** `docs-dev`.

## 7. Dependency graph

```
T1 (tokens.ts)        T2 (sessions.ts guards)      T3 (context.ts)      T5 (investigation, read-only)
                       │
                       └──> T4 (sessions.ts loop + orchestrator.ts)
                                │
T1 ─────────────────────────────┴──> T6 (docs harvest: T2+T3+T4)
                                          │
                                          └──> whole-change review → closeout (docs-dev archive)
```

Parallel front: T1, T2, T3, T5 start immediately. T4 follows T2 only (same-file overlap).
T6 follows T2/T3/T4 for final wording. Each developer task ends with its own `task-gate` run.

## 8. Risks and out-of-scope boundaries

- **Risks**
  - Producer not found (version skew, machine-specific transcripts): defensive layers still
    stop the crash and the loop (D6); T5 records the evidence trail either way.
  - Degrade could read as silent data loss: mitigated by the `derivation_failed` problem row
    (error severity, stack), the summary count, and the runbook row (T6).
  - D2 drops measured `tokens` on a corrupt part: recorded trade-off; the composition re-derives
    correctly once the producer is fixed and the store re-ingests.
  - Context-layer guard changes counts for corrupt parts only — no string-input arithmetic
    changes (T3 asserts this).
- **Out of scope**
  - Fixing the installed binary in place: the defensive layers reach the machine via the next
    release/update (`kyber-weave update`); the owner live gate (§9) verifies after install.
  - The producer fix itself, if T5 names a HEAD producer: follow-up task/todo with its own
    RED fixture (T5 step 4) — this plan ships the hardening.
  - `contentFromParts` silent coercion at ingest (types.ts:152 — no crash): noted, not
    touched.
  - Tray and web surfaces: unchanged (display-layer rule, ADR 0033).
  - No new dependencies; no schema migration (the `problems` table already exists).

## 9. Verification gates

1. `npm --prefix dash run typecheck`
2. `npm --prefix dash run lint`
3. `npm --prefix dash run test` (full dash suite — T1–T4 contract tests included)
4. `npm --prefix dash run check:reachable`
5. `kyber-weave docs validate .` and `kyber-weave docs drift .` — **outstanding in this
   authoring harness (no process tool)**; the conductor runs both before execution, per the
   2026-10-23 pi-renderer plan precedent (G3).
6. **Owner live gate** (post-merge, after the next release/update installs the fixed binary):
   trigger a refresh on the affected machine; assert `records > 0`, latest `refresh_run` row
   `success`, tray "Last refresh" healthy, and — for the malformed session, if it still exists
   — a `derivation_failed` row in Problems view. Recorded `[NOT LIVE-VERIFIED]` until
   performed, per repo convention.

## 10. Review, closeout, and findings

- Whole-change review (`code-review` council) after T1–T6, then closeout by `docs-dev`:
  verify against evidence, harvest §4, archive this plan per `KW-DOC-LIFECYCLE-003`.
- **Closeout mapping**: D1–D4 + guards → `dash/architecture.md` rows 1–2; D3 + problem row →
  `dash/runbook.md` row 3; D5/D6 + T5 findings remain in this plan's record. ADR waiver: bug
  fix and defensive refinement under ADRs 0009/0033 with no new boundary, dependency, or
  schema — no new ADR.
- **T5 findings (executed 2026-10-26):** installed `kyberdash 0.1.8-rc.8` (`/Users/dave/.local/bin/kyberdash`) has no matching git tag (tags end at `v0.1.8-rc.7`; HEAD `v0.1.8-rc.7-7-g13a97f11`; `dash/package.json` `0.9.23`) so tag-range diff unresolvable; recent-week log used instead. Uncommitted `tsx` scanner over `PROVIDER_READERS` (2907 sources, 2756 files, 63190 turns, 346579 parts) reported `NO_NON_STRING_PART_TEXT` — no HEAD reader emits non-string `text`. Verdict: **installed-only divergence**, defensive layers (T1–T4 + findings-phase follow-ups in `skills.ts`/`twin-dedupe.ts`/`runs.ts`) are the fix until the next release carries them. Follow-up gap recorded by test-dev: `outcome.ts:377/:408` still read `part.text` unguarded (caught by per-session degrade).

## Closeout mapping

Every durable decision below is now recorded in canonical documentation. The permanent
`## Decisions` table is preserved intact above and was not edited during closeout. The Design harvest table is fully landed — verified in the working tree before archiving.

| ID | Destination |
|---|---|
| D1 | Fail-closed token counter (`TypeError` naming contract + received `typeof`, bounded preview) → [`docs/dash/architecture.md`](../../dash/architecture.md) part-text boundary rule; implementation in `dash/src/canon/tokens.ts` (`createCachedCounter.count`) |
| D2 | `partsOf` structured path filters non-string `text`, mirroring the flat path → [`docs/dash/architecture.md`](../../dash/architecture.md) part-text boundary rule; implementation in `dash/src/canon/sessions.ts` (`partsOf`) |
| D3 | Per-session degrade: `derivation_failed` problem row + skip + `derivationDegraded` counter, refresh exits 0 with count; outer whole-projection catch stays → [`docs/dash/architecture.md`](../../dash/architecture.md) per-session degrade contract; [`docs/dash/runbook.md`](../../dash/runbook.md) degraded-but-successful operation; implementation in `dash/src/canon/sessions.ts` (`buildSessions`) + `dash/src/refresh/orchestrator.ts` |
| D4 | Guards at all verified sites in the class (`partsOf`, tool-definitions loop, `taskSummaryOf`, `reduceContextParts`, `analyzeContext` via safe-text helper; plus findings-phase `skills.ts`/`twin-dedupe.ts`/`runs.ts`) → [`docs/dash/architecture.md`](../../dash/architecture.md) part-text boundary rule |
| D5 | **Documentation waiver** — transient investigation disposition: the uncommitted `tsx` scanner and installed-binary version skew are process evidence recorded in this plan's T5 findings; no durable architectural invariant survives beyond the installed-only-divergence verdict that justifies shipping D1–D4 |
| D6 | Defensive layers ship independent of producer attribution → [`docs/dash/architecture.md`](../../dash/architecture.md) (the shipped D1–D4 boundary rule and degrade contract are the fix until the next release carries them) |
| H1 | Part-text boundary rule harvest → [`docs/dash/architecture.md`](../../dash/architecture.md) |
| H2 | Per-session degrade contract harvest → [`docs/dash/architecture.md`](../../dash/architecture.md) |
| H3 | Degraded-operation runbook harvest → [`docs/dash/runbook.md`](../../dash/runbook.md) |

**ADR waiver:** no new ADR — restated from the Design harvest section: defensive refinement under ADRs 0009/0033 with no new boundary, dependency, schema, or invariant beyond the boundary rule and degrade contract.

**Documentation waiver:** D5 only — transient investigation disposition (uncommitted scanner, version-skew evidence); no durable invariant beyond the installed-only-divergence verdict recorded in this plan. All other facts are in `dash/architecture.md` (boundary rule, degrade contract) and `dash/runbook.md` (degraded operation).

## Completion summary

- Implementation: T1–T4 + findings-phase hardening (`skills.ts`, `twin-dedupe.ts`, `runs.ts`) complete test-first; `otel/service.test.ts` updated to the degrade contract (per-session failure records a `derivation_failed` problem, no `onError`).
- Tests: 17 new regression tests GREEN (tokens/sessions/context/orchestrator/skills/twin-dedupe/runs); targeted suites pass; full dash suite: only pre-existing `mcp/*` failures remain (untouched files).
- Docs: `docs validate . --merge-ready` passes after this archive (no open plan remains in `docs/plans/`); `docs drift` zero findings.

---

*Plan authored 2026-10-26 from the investigator's findings plus independent discovery. No fix
was attempted before this plan.*
