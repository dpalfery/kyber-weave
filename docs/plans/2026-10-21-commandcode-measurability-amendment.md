---
id: plans/2026-10-21-commandcode-measurability-amendment
title: "Command Code measurability amendment — stop declaring paired tool results not measurable"
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
---

# Command Code measurability amendment — stop declaring paired tool results not measurable

**Status: Ready 2026-10-21 — approved for execution (AUTONOMOUS_APPROVAL: approve and execute for PLAN_READY 2026-10-21, autonomous: recommendation accepted).** Authored 2026-10-21 under `autonomy: autonomous`. Final approval recorded in ## Decisions (AP1) 2026-10-21.

**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`).
**Parent plan:** [2026-10-21-commandcode-collector-kyberdash.md](2026-10-21-commandcode-collector-kyberdash.md) (Ready 2026-10-21: approve-and-execute recorded as AP3 after AP2; see its §3). This amendment is a follow-up. Its scope and approval are unchanged; the parent is annotated, not re-scoped.

---

## 1. Problem and goal

### 1.1 Request

Resolve finding F4 from the Ready Command Code plan: its decision D7 declares
`tool_result_content` not measurable for `commandcode`, while the same reader pairs tool
results into tool records. The contradiction makes `oversizedResults` report
`not_measurable` for sessions that do contain tool results, and the telemetry inventory codifies
the contradiction in its Command Code caveat.

### 1.2 Premise (confirmed)

- `READER_UNMEASURABLE['commandcode']` (`dash/src/canon/measurability.ts:106`) declares
  `['schema_cost', 'system_prompt', 'instruction_context', 'tool_definitions', 'tool_result_content']`.
- `measurabilityFor(provider)` (`measurability.ts:359-365`) builds the declared map from that entry,
  so the LLM parent record carries `tool_result_content: not_measurable`.
- The file-synth tool record copies the parent's measurability (`dash/src/synth/synth.ts:812`,
  `...parentRecord.measurability`) and nothing clears the declaration on that path, although
  `parts` carries the paired result text (`synth.ts:758-767`). The result text is written,
  and the availability flag says it is not measurable.
- `oversizedResults` (`dash/src/analysis/signals.ts:602-607`) returns `not_measurable` as soon as
  `measurability.tool_result_content` is not measurable, before it looks at the results.
- Precedent (from the request, consistent with the code): `copilot-vscode` and `pi` declare the bucket
  and emit none; `claude`, `codex` and `cursor` emit and do not declare. Command Code is the
  only reader that does both.

### 1.3 Goal

Make the declaration match what the reader emits. Remove `tool_result_content` from the Command Code
declaration, correct the test that pins the declaration, and correct the telemetry-inventory row.

---

## 2. Investigation findings

Discovery: `codegraph 1, docs 0, direct reads 8`. The `codegraph_explore` MCP tool and the
`docs_*` MCP tools are not exposed in this harness, so the documentation index fallback
(`AGENTS.md` → `docs/plans/README.md`) was used. The CodeGraph shell fallback
(`codegraph explore "READER_UNMEASURABLE commandcode tool_result_content measurabilityFor"`) was
used and returned the verbatim `measurability.ts` source with its callers. Text search
(`grep`) over `dash/src` and `dash/web/src` for `commandcode`, `command-code` and
`tool_result_content` was used as the narrow fallback to confirm that no other test pins the Command
Code buckets.

Direct reads, each with its reason:

- `dash/src/synth/readers/commandcode.test.ts` (lines 1–237): the assertion that pins the declaration
  is at lines 144–152, and the end-to-end characterization is at lines 191–236. The packet cited
  121–129 for the declaration assertion; those lines are the array-flattening test. The line numbers
  here are the correct ones.
- `dash/src/synth/synth.ts` 745–815: tool record `parts` and the `measurability` inheritance at line 812.
- `dash/src/canon/measurability.ts` 80–130 and 240–260, 350–370: the declaration map, the
  `normalizeHarnessName` fold (already present, see §2.1) and `measurabilityFor`.
- `dash/src/analysis/signals.ts` 598–650 and 1160–1185: how `oversizedResults` gates on
  `tool_result_content`, and how it builds its input from record `parts`.
- `docs/dash/telemetry-inventory.md` line 53 (the Command Code row), `docs/plans/README.md`
  (Active Plans table), the parent Ready plan (to scope the amendment), and the repository root
  `AGENTS.md` (Config Reg and docs fallback).

### 2.1 Baseline is the working tree, not HEAD

The Ready plan has been executed as **uncommitted working-tree changes** on top of `HEAD`
(`c712658`). `git status` shows modified `dash/src/canon/measurability.ts`,
`measurability.test.ts`, `providers/commandcode.ts`, `refresh/registry.ts`, `synth/provider.ts`,
`docs/dash/telemetry-inventory.md`, and the untracked `synth/readers/commandcode.ts` and
`commandcode.test.ts`. The `normalizeHarnessName` fold for `command-code` is present at
`measurability.ts:251-252`. This amendment applies only on top of that working tree. It does not
revert any Ready-plan change.

Verified fact that reconciles the Ready plan: the `command-code` alias fold (its D8/T2) is already in
the working tree, so the amendment does not touch it.

### 2.2 Declaration options

| Option | Change | Result |
|---|---|---|
| **A (chosen)** | Remove `tool_result_content` from `READER_UNMEASURABLE['commandcode']` | Declaration matches emission. The reader emits paired results, so the flag becomes measured for paired results. `oversizedResults` gates on real results. |
| B (rejected) | Keep the declaration; clear `tool_result_content` per record when a result is paired | Violates the declaration rule: availability is then decided by the value, not by the reader's declared capability. The change also reaches the file-synth clearing path that every provider uses. |
| C (rejected) | Keep the declaration as is | Keeps a false "not measurable" reason on paired results and codifies the contradiction in the test and the inventory. |

### 2.3 What the reader and status derivation do (for N2)

- The reader sets `isError` only from `block.is_error === true` (`synth/readers/commandcode.ts`, line 116).
  The packet states that the `command-code` 1.84.0 writer persists no error flag. This Draft did
  not re-verify that claim against the package. It is recorded as packet-sourced.
- `synth.ts:687-688` derives `status` as `unset` when there is no result, `error` when `isError`,
  and `ok` otherwise. A paired result therefore always reads `status: ok`.

### 2.4 Residual buckets, not verified in this Draft

`system_prompt`, `instruction_context` and `tool_definitions` remain declared. This Draft did not
verify them against the writer. Keeping them declared is the conservative direction: a
false "not measurable" is an honest gap, while a false "measured" would be a fabricated claim
(`docs/rules/honest-unobservability.md`). Any move of those buckets is a separate amendment.

### 2.5 Test pins

A search of `dash/src` and `dash/web/src` for `commandcode` and `command-code` shows that only
`commandcode.test.ts` pins the Command Code bucket list. No other test asserts the
`tool_result_content` flag for this harness. `registry.test.ts` pins the reader audit only, and
`measurability.test.ts` pins only the alias.

### 2.6 Consistency with council C1 (2026-10-21)

Council C1 changed the parent plan's turn-pairing key from `turnId` to `pairingId` (the assistant message id, `dash/src/providers/commandcode.ts`). This amendment does not depend on that key. Its assertions pin the declaration (`READER_UNMEASURABLE`) and the tool-record flag, and its characterization helper pairs calls and turns by index, so it does not exercise `matchingTurns`. Line anchors in §§2, 3 (D6) and 5 (T-A) were correct at authoring. After C1, in `dash/src/synth/readers/commandcode.test.ts`: assertion (a) is at lines 145–154, assertion (b) at lines 250–257, and the characterization block at lines 190–258. The C1 describes (pairing 157–188, span identity 263–292, pairing guard 297–325) are not this plan's assertions.

Discovery for this consistency note (2026-10-21): `codegraph 0` and `docs 0` (MCP tools not exposed; no index query was needed for the anchors), direct reads 2 (`dash/src/canon/measurability.ts` 100–111, to confirm T-A is in the working tree; `docs/dash/telemetry-inventory.md` 50–55, to confirm T-B; the parent plan was read earlier in the same pass), text searches 2 (`commandcode.test.ts` describe and assertion line anchors; `commandcode.ts` pairing key).

Observed 2026-10-21 (no change made to this plan): the T-A declaration edit (`measurability.ts` line 106 lists four buckets) and the T-B inventory caveat are already present in the working tree, while this plan is still Ready and its closeout is open.

---

## 3. Decisions

Permanent state. Preserved intact across Draft → Ready → execution → closeout → archive. Never
deleted on finalization.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode | `test-first` (the default; no opt-out supplied) | Packet default; §4 carries the RED→GREEN contract for the one implementation task | ANSWERED — autonomous: default applied 2026-10-21 |
| D2 | Autonomy basis | `autonomous` per AGENTS.md Config Reg `<autonomy>`; every row below was decided by the three-check rule | Each choice is a repository pattern (declaration rule, Pi/Copilot precedent) or is one map entry, cheap to reverse | ANSWERED — autonomous: Config Reg 2026-10-21 |
| D3 | Baseline | The working tree (uncommitted Ready-plan execution) is the baseline; `docs/plans/2026-10-21-commandcode-collector-kyberdash.md` is not edited | §2.1: `git status` lists the Ready-plan files as modified/untracked; `HEAD` has no Command Code reader | ANSWERED — autonomous: git status 2026-10-21; parent annotated by council C1, reapproved under AP2 and made Ready under AP3 on 2026-10-21 (§8) |
| D4 | Declaration option for paired tool results | **Option A:** remove `tool_result_content` from `READER_UNMEASURABLE['commandcode']` (`measurability.ts:106`) | Matches the precedent: `claude`, `codex`, `cursor` emit and do not declare; `copilot-vscode` and `pi` declare and emit none. Option B rejected (per-record clearing breaks the declaration rule). Option C rejected (keeps a false reason and codifies it in the test and inventory). Reverting is one line | ANSWERED — autonomous: declaration precedent + honest-unobservability rule 2026-10-21 |
| D5 | Residual declared buckets: `system_prompt`, `instruction_context`, `tool_definitions` | Keep declared. `schema_cost` also stays declared | Not verified against the writer in this Draft (§2.4). Keeping them declared is the honest direction; a move needs its own verified amendment | ANSWERED — autonomous: conservative default, cheap to revisit 2026-10-21 |
| D6 | Test-first shape for the declaration change | Two RED assertions, both in `commandcode.test.ts`: (a) the bucket-list assertion (lines 144–152) is updated to the four-entry list, so it fails before the map edit; (b) the end-to-end characterization (lines 191–236) gains a record-level assertion that a synthesized tool record's `measurability.tool_result_content` is not `not_measurable`, which fails before the map edit. The existing flattened-text assertion in (b) is characterization and stays GREEN before and after | (a) is a specification-level RED on the declared value. (b) is the behavioural RED that reproduces the contradiction on the record. Neither assertion is weakened | ANSWERED — autonomous: test-first contract (test-first-contract.markdown) 2026-10-21 |
| D7 | Telemetry-inventory Command Code row (N2) | Rewrite the caveat sentence in `docs/dash/telemetry-inventory.md` line 53: (i) the declared not-measurable list is the four buckets; (ii) paired tool results are measured; (iii) tool records report `status: ok` for any paired result because the 1.84.0 writer persists no error flag, so an error result cannot be distinguished from success; (iv) a call with no following result reads `status: unset` | §2.3 for the reader and status derivation. The writer-side claim is packet-sourced and labelled as such in the row | ANSWERED — autonomous: packet N2 + synth.ts:687-688 2026-10-21 |
| D8 | Scope exclusions | No schema change, no synth logic change, no naming mapping (N1 KEEP), no cache survey (N3 KEEP), no re-key (F3 KEEP), no change to the `normalizeHarnessName` fold, no change to the Ready plan | The packet fixes these; the amendment touches one map entry, one test file, one doc row | ANSWERED — autonomous: packet scope 2026-10-21 |
| D9 | Architecture record | No ADR — ADR waiver: refines the existing honest-unobservability declaration for one harness, with no new boundary, dependency, schema, or lasting invariant. Documentation: the telemetry-inventory row is updated by T-B; no documentation waiver is claimed | `docs/plans/README.md` closeout policy; §11 | ANSWERED — autonomous: docs-structure convention 2026-10-21 |
| D10 | Escalation check | None of D1–D9 meets the impactful bar: each is one map entry, a test assertion, or a doc sentence; each is covered by an existing declaration precedent; none changes a data model, public interface, dependency, or persisted shape. The only behaviour change is an availability flag that moves from `not_measurable` to `measured` for paired results | §2.2 and §7 R1 | ANSWERED — autonomous: three-check rule 2026-10-21 |
| AP1 | Approve and execute PLAN_READY 2026-10-21 | Approved — plan is Ready for execution; declaration option A (D4), test-first contract (§4), and task graph (§5–6) are approved unchanged. No scope, mode, or contract change at approval | Conductor-supplied `AUTONOMOUS_APPROVAL: approve and execute for PLAN_FILE docs/plans/2026-10-21-commandcode-measurability-amendment.md` under resolved autonomy `autonomous`. Frontmatter `status: draft` retained per ontology closed vocabulary (`current`, `draft`, `needs-review`, `superseded`); Ready is carried by body status and the index Active row, as in the parent Ready plan. D1–D10 were decided by the three-check rule; none met the escalation bar, so no question was returned | APPROVED — autonomous: recommendation accepted 2026-10-21 |

`NO_QUESTIONS` — the request was decision-complete under autonomous handling: the one material
choice (declaration option A, B or C) is covered by the declaration precedent in
`measurability.ts`, and every supporting choice is either a one-line map change or a doc sentence.
No item met the escalation bar, so none is returned as `NEEDS_DECISION`.

---

## 4. Test contract (test-first)

Per `test-first-contract.markdown`: the single implementation task names its exact surface and
command, asserts observable behaviour, and records RED before GREEN. Changing this contract,
including weakening an assertion to reach green, returns the plan to Draft.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T-A declaration + test | `dash/src/synth/readers/commandcode.test.ts` (existing, vitest) | RED and focused GREEN: `npx vitest run src/synth/readers/commandcode.test.ts` from `dash/` (or `npx vitest run dash/src/synth/readers/commandcode.test.ts` from the repository root). Full gate: `npm --prefix dash run test` | (a) `READER_UNMEASURABLE.get('commandcode')` equals `['schema_cost','system_prompt','instruction_context','tool_definitions']`. (b) In the end-to-end characterization, the synthesized `tool.invoke` record's `measurability.tool_result_content` is not `not_measurable`, and its `parts` still carry the paired text `file.txt`. The flattened-text assertion (b, existing) stays unchanged | Edit (a) and add (b) **before** touching `measurability.ts`. Run the focused command: (a) fails because the declared list still has `tool_result_content`; (b) fails because the record inherits `not_measurable`. Keep the failing vitest output as the RED log. The existing flattened-text assertion passes before and after, so it is characterization, not RED | Same two assertions pass, unweakened; the existing flattened-text assertion still passes; full `npm --prefix dash run test` green; `typecheck`, `lint`, `check:reachable` green |
| T-B documentation row | no-test (documentation) | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | Command Code row in `docs/dash/telemetry-inventory.md` states the four declared buckets, paired-result measurement, `status: ok` with the no-error-flag note, and `status: unset` for unpaired calls | n/a — documentation. Replacing verification: a read-back of the row against §3 D7 and the two commands above | `docs validate` and `docs drift` report **no finding that the plan's changes introduce**. The three pre-existing findings (see §5 and §6) remain unchanged and out of scope |

---

## 5. Dispatchable tasks

- **T-A — Declaration removal and test update (test-first).** Objective: (1) Edit the two assertions
  in `dash/src/synth/readers/commandcode.test.ts` as §4 T-A specifies, and run them to RED. (2) Remove
  the `'tool_result_content'` entry from the `['commandcode', [...]]` line at
  `dash/src/canon/measurability.ts:106`, and change nothing else in that map. (3) Re-run to GREEN, then the
  full dash gates. Exact files: `dash/src/canon/measurability.ts` (one entry),
  `dash/src/synth/readers/commandcode.test.ts` (assertion (a) at lines 144–152; new assertion (b) inside
  the characterization block at lines 191–236). Acceptance: §4 T-A row. Dependencies: none.
  Skill: `test-dev`.
- **T-B — Telemetry-inventory row (documentation).** Objective: rewrite the caveat sentence in the
  Command Code row of `docs/dash/telemetry-inventory.md` (line 53) per §3 D7. Keep the rest of the row
  unchanged, including the parser-contract sentence. Exact files: `docs/dash/telemetry-inventory.md`. Acceptance:
  §4 T-B row; the two documentation commands report no new finding. Dependencies: **T-A** (the
  row documents the shipped declaration, so it is written after the change lands). Skill:
  `kyber-weave-docs`.

---

## 6. Dependency graph

`T-A → T-B`. T-B describes the declared buckets and the status behaviour that T-A makes true, so
it is sequenced after T-A. The file scopes are disjoint, but the row's content depends on T-A's
shipped behaviour.

Pre-existing `docs validate` / `docs drift` findings, which this plan does not touch and does not
fix: two in `code-review/architecture.md` and one in `dash/architecture.md`. Per the packet, they
remain out of scope and must be unchanged by this plan.

---

## 7. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | Command Code sessions with paired results now report `tool_result_content` as measured, so `oversizedResults` may return a ratio and the oversized-tool-result detector may produce new findings for this harness | This is the intended correction, since the parts exist. T-A's test pins the record-level flag. The implementer confirms in the full suite that no existing test pins the old `not_measurable` for Command Code (§2.5 found none). Reverting is one line |
| R2 | Per-record `tool_result_content` stays `measured` on a tool record with no paired result (`status: unset`, empty `parts`) | Same convention as `claude` and `codex`. Session-level `oversizedResults` counts only `parts`, and returns `not_measurable` ("No tool results captured") when none exist. Recorded in §8 as a residual, not changed here |
| R3 | `status: ok` on every paired result hides an error result that the writer does not flag | Stated in the inventory row (T-B) so no reader treats `ok` as verified success. Writer-side behaviour is packet-sourced (§2.3) |
| R4 | Declared-but-unverified buckets (`system_prompt`, `instruction_context`, `tool_definitions`) may be measurable in the writer | Kept declared (D5). A verified move is a separate amendment |
| R5 | Weakening an assertion to reach green | Forbidden by §4. Any change to the §4 contract returns the plan to Draft |
| R6 | Docs gate fails on the new index row or the edited row | §4 T-B runs both commands. Findings are fixed in the document; the ontology is not widened |
| R7 | Ordering with the Ready plan's own closeout, which touches the same telemetry-inventory row | T-B edits the row as it stands in the working tree. If the Ready plan's closeout runs first, T-B re-reads the row before editing |

---

## 8. Out of scope

- The parent plan (`docs/plans/2026-10-21-commandcode-collector-kyberdash.md`) is not re-scoped by this amendment. Council C1 annotated it on 2026-10-21, AP2 reapproved its amended Test contract, and AP3 (2026-10-21) returned it to Ready for execution. That record lives in the parent.
- No schema change, no synth logic change (`synth.ts` is not edited), no change to
  `normalizeHarnessName` or `HARNESS_FILTER_SPELLINGS`.
- No naming mapping (N1 KEEP), no cache survey (N3 KEEP), no re-key (F3 KEEP).
- No verification of `system_prompt`, `instruction_context` or `tool_definitions` against the writer.
- No change to the three pre-existing `docs validate` / `docs drift` findings.
- Residual R2 (unpaired tool records report measured-with-no-part) is recorded, not changed.

---

## 9. Verification gates

Run from the repository root, in this order:

1. `npm --prefix dash run test` (full suite) and the focused command in §4 T-A (RED, then GREEN).
2. `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run check:reachable`.
3. `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
4. `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`

Gates 3 and 4 pass when the plan's changes introduce no new finding. The three pre-existing findings
(§6) remain and are reported as pre-existing, not as passes. The .NET build and test gates are not
touched by this plan.

---

## 10. Review

One `code-reviewer` council pass over the whole change (one map entry, two test assertions, one
doc row) before closeout, per the repository's single end-of-run council discipline.

---

## 11. `docs-dev` closeout

Harvest the durable fact to canonical documentation. The Command Code row in
`docs/dash/telemetry-inventory.md` (T-B) becomes the home for: the four-bucket declaration,
measured paired results, the `status: ok` no-error-flag note, and `status: unset` for unpaired calls.
**No ADR — ADR waiver:** the change refines the existing honest-unobservability declaration for
one harness, with no new boundary, dependency, schema or lasting invariant; it is one map entry
that is cheap to reverse. **No documentation waiver is claimed:** the inventory row is updated
by T-B.

### Closeout mapping (2026-10-21)

Closeout verified 2026-10-21 against the working tree (`c712658` plus the Ready-execution
changes of §2.1). Acceptance criteria are satisfied: T-A's focused suite is green at 6706
with `TASK_GATE: PASS`, and T-B's row read-back is green. `docs validate` reports no findings;
`docs drift` reports the same three pre-existing `KW-DOC-DRIFT-001` findings the plan does not
touch (§6) and no finding this closeout introduced. The closeout reused the T-A/T-B evidence;
the full dash suite was not re-run and is not claimed.

| ID | Durable decision | Destination in canonical documentation |
|---|---|---|
| D1 | Development mode `test-first` | **Documentation waiver — transient mode/governance:** no surviving architectural, operational, or behavioral invariant. |
| D2 | Autonomy basis `autonomous` (Config Reg) | **Documentation waiver — transient mode/governance.** |
| D3 | Baseline is the working tree; the parent plan is not edited | **Documentation waiver — transient mode/governance:** an execution-state fact; the parent carries its own record. |
| D4 | Option A — remove `tool_result_content` from `READER_UNMEASURABLE['commandcode']` | [telemetry-inventory Command Code row](../dash/telemetry-inventory.md): the four-bucket declaration and `tool_result_content` measured. Code anchor `dash/src/canon/measurability.ts:106`. |
| D5 | Keep `system_prompt`, `instruction_context`, `tool_definitions`, and `schema_cost` declared | Same row: the four declared buckets are named. |
| D6 | Test-first shape — two assertions in `commandcode.test.ts` | **Documentation waiver — transient mode/governance (test contract):** the durable behaviour it pins is carried by the row via D4 and D7. Pin: `dash/src/synth/readers/commandcode.test.ts:145-154, 250-257`. |
| D7 | Telemetry-inventory row content — four buckets, paired results measured, `status: ok` with the no-error-flag note, `status: unset` for unpaired calls | [telemetry-inventory Command Code row](../dash/telemetry-inventory.md) (direct; `status: unset` clause added at closeout). |
| D8 | Scope exclusions; the `command-code` → `commandcode` fold kept | Row records the fold (`normalizeHarnessName`, `dash/src/canon/measurability.ts:252`); the exclusion list stays in §8. **Documentation waiver — transient mode/governance** for the exclusion list itself. |
| D9 | Architecture record | **No ADR — ADR waiver:** the change refines the existing honest-unobservability declaration ([honest-unobservability rule](../rules/honest-unobservability.md)) for one harness under the [ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md) repair path, with no new boundary, dependency, schema, or lasting invariant. Documentation: [telemetry-inventory Command Code row](../dash/telemetry-inventory.md) (D7); no documentation waiver claimed. |
| D10 | Escalation check — no D1–D9 met the impactful bar | **Documentation waiver — transient mode/governance.** |

Closeout state: **complete, pending PR merge.** The [plan index](README.md) Active row stays
`Ready — closeout complete, pending PR merge (archive on merge)`; the plan moves to
`docs/archive/plans/` only when the PR merges (`KW-DOC-LIFECYCLE-003`).
