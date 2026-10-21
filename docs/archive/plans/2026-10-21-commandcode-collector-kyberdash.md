---
id: plans/2026-10-21-commandcode-collector-kyberdash
title: "Complete Command Code in KyberDash — content reader, documentation, and filter-alias repair"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
---

# Complete Command Code in KyberDash — content reader, documentation, and filter-alias repair

**Status: Ready 2026-10-21 — approved for execution (AP3, autonomous: recommendation accepted; AP2 C1 reapproval recorded 2026-10-21).** Previously Ready 2026-10-21 under AP1 (`AUTONOMOUS_APPROVAL: approve and execute for PLAN_READY 2026-10-21`, autonomous: recommendation accepted). Council C1 (2026-10-21) changed the approved T1 Test contract and D5 from `turnId` to `pairingId`, which reopened approval; AP2 closed that reopen and AP3 re-finalized the plan. See ## Decisions (AP2) and the supersession notes in §§2, 3, 4, 5, 7. Authored 2026-10-21 under `autonomy: autonomous`. AP1 and AP2 remain recorded in ## Decisions.

**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`).

---

## 1. Problem and goal

### 1.1 Request

"Collector for commandcode so we can add command code to kyberdash" — enable KyberDash to
ingest **and display** Command Code.

### 1.2 Discovery summary that redefines the request

The Command Code **collector already exists and is merged**: commit `22cd32b`
`feat(squad,dash): add Command Code as a harness target (#382)`, an ancestor of `HEAD`
(`main`). It ships the full ingest half — the `dash/src/providers/commandcode.ts` provider
(JSONL discovery + parse of `~/.commandcode/projects/<slugified-cwd>/<session-id>.jsonl`,
usage with AI-SDK cache-class subtraction, tool/skill/subagent/bash extraction, reported-vs-
estimated cost), its 5-test suite, the harness descriptor (`commandcode`, label "Command
Code", `~/.commandcode/projects`, `jsonl`), the provider disposition
(`commandcode: job(['commandcode'])`), measurability spellings (`commandcode`, `command-code`),
and the synth cache-subtraction note.

What is **not** done is the *display* half, plus two small correctness/documentation gaps:

1. **No content reader.** `synth/readers/` has no `commandcode.ts`; `PROVIDER_READERS`
   (`dash/src/synth/provider.ts`) and `READER_HARNESS_IDS` (`dash/src/refresh/registry.ts`)
   both omit `commandcode`. A synthesized command-code turn therefore carries **no `parts`**,
   so the Context Inspector / ASAD view shows counters but no turn content. The provider
   comment says Command Code writes "the same tree shape Pi writes", and Pi has a reader
   (`piReader`). This is the one real feature gap for "display".
2. **No telemetry-inventory row.** `docs/dash/telemetry-inventory.md` documents every other
   harness (including Pi) but has no Command Code row.
3. **Filter-alias inconsistency.** `HARNESS_FILTER_SPELLINGS` advertises both `commandcode`
   and `command-code`, but `normalizeHarnessName` has no fold `command-code → commandcode`,
   so `?harness=command-code` resolves to a dead id that matches zero stored rows (stored id
   is `commandcode`). Every other alternate spelling in that list folds to its canonical id.

This plan completes those three items. It does **not** re-plan the collector.

### 1.3 Authorized scope (from the packet + investigation)

- **Engine only.** All work lives in `dash/src/**` (the shared KyberDash application), never
  in `dash/web/**` or `dash/tray/**` — the display-layer rule
  (`docs/rules/kyberdash-display-layer.md`, enforced by
  `dash/src/architecture/display-layer.test.ts`) is untouched and must stay green.
- **Content reader** for Command Code mirroring `piReader`, registered and paired by id, with
  the ADR 0016 repair-contract version bump so already-imported sessions re-parse and gain
  content.
- **Filter alias** repair (`command-code` folds to `commandcode`).
- **Telemetry-inventory** documentation row.
- **No** schema change, **no** new dependency, **no** OTLP surface (Command Code is JSONL
  only — the provider notes no OTLP), **no** tray/web edit.

### 1.4 Out of scope

- Re-implementing or re-testing the already-merged collector beyond the one small pairing
  change in §1.3 (§5 T1).
- The Kyber-Squad `CommandCodeRenderer` (`src/KyberWeave.Core/Squad/Rendering/CommandCodeRenderer.cs`)
  — a *different* "Command Code" (a Squad deployment-target renderer), unrelated to the
  KyberDash harness collector.
- Any harness other than Command Code.
- Live owner-capture verification on a real `~/.commandcode` store (the provider was verified
  against the published `command-code` 1.84.0 format on 2026-10-17; no live store is required
  to land the reader, which follows the existing Pi/JSONL reader pattern).

---

## 2. Investigation findings

Discovery: `codegraph 1, docs 0, direct reads 17`. The CodeGraph MCP tool
(`codegraph_explore`) is not exposed in this harness; the shell fallback `codegraph explore
"commandcode"` was used (the CLI is on `PATH` and `.codegraph/` is present). The Kyber-Weave
docs MCP tools (`docs_explore`, `docs_for_symbol`, `docs_glossary`, `docs_status`) are not
exposed either, so the AGENTS.md-permitted documentation-index fallback was used (direct
reads under `docs/`). Direct reads of indexed code were the narrow fallback where the
codegraph query returned only `commandcode.ts` verbatim and the registry/reader contracts
needed verbatim source; each is named below.

Maintenance discovery (council C1 note pass, 2026-10-21): `codegraph 2` (shell fallback; `codegraph_explore` not exposed), `docs 0` (`docs_*` not exposed; documentation-index fallback), `direct reads 16`. Code reads: `dash/src/providers/commandcode.ts` (codegraph returned no provider source; confirm the `pairingId` stamp at lines 232–235), `dash/src/synth/readers/commandcode.ts` (confirm `nativeRecordId` and `positionalPairingUnsafe`), `dash/src/synth/readers/commandcode.test.ts` (confirm the pins named in the supersession notes and their line anchors), `dash/src/canon/measurability.ts` 100–111 (confirm the bucket change is in the working tree), `dash/src/synth/provider.ts` 155–259 (`matchingTurns` semantics relied on by AP2), `dash/src/synth/synth.ts` 106–139 (identity fall-through). Docs reads: `AGENTS.md`, this plan, the amendment, `docs/plans/README.md`, `docs/dash/telemetry-inventory.md` 50–55, `docs/plans/2026-10-21-push-hotshot-retirement-pr.md` 1–30 (CC-R2-03), `products/kyber-squad/agents/conductor/references/plan-path.md`, and the architect references `plan-authoring`, `test-first-contract`, `autonomy-decisions`.

AP2 pass discovery (2026-10-21): `codegraph 0`, `docs 0`, direct reads 3 (`plan-path.md`, the architect `plan-authoring` and `autonomy-decisions` references), text searches 2 (the three AP2 pin titles in `commandcode.test.ts`, re-confirmed at lines 157–158, 263–264, 297–298).

AP3 pass discovery (2026-10-21): `codegraph 0`, `docs 0`, direct reads 0. `git status` and narrow greps confirm the working tree still carries the T1–T3 edits (provider `pairingId` stamp, reader `positionalPairingUnsafe`, registry v2 comment, alias fold, inventory row). The 6708-test suite and TASK_GATE results are conductor-reported and were not re-run here.

- **Collector already merged (§1.2).** `git merge-base --is-ancestor 22cd32b HEAD` is true;
  `git show --stat 22cd32b` shows the provider, its test, the registry/disposition/descriptor
  wiring, measurability spelling, synth note, and parser cost handling — 28 files. The
  provider was verified against `command-code` npm 1.84.0 (2026-10-17).
- **Reader gap.** `PROVIDER_READERS` (`dash/src/synth/provider.ts`) lists `claude*`, `codex*`,
  `opencode`, `kilo*`, `copilot*`, `cursor*`, `pi` — no `commandcode`. `READER_HARNESS_IDS`
  (`dash/src/refresh/registry.ts`) lists the same set; `HARNESS_CONTENT_CAPABILITIES` derives
  `reader: READER_HARNESS_IDS.has(id)`, and `auditProviderRegistry` asserts
  `capability.reader === PROVIDER_READERS.has(harnessId)`, so the two must move together.
  Without a reader, `ingestProviders` never attaches `readerTurn` parts to command-code
  records (§§4–5 of `synth/provider.ts`), so the ASAD Context view has no content for the
  harness.
- **Pairing pitfall.** The provider's `createParser` skips assistant messages whose usage is
  all-zero (`if (input === 0 && output === 0 && …) continue`) and yields calls with
  `deduplicationKey`/`sessionId` but **no `turnId`/`pairingId`** (at authoring; C1 added `pairingId`). A reader that emits one turn
  per assistant message would therefore misalign under `matchingTurns`' positional fallback.
  The fix is id-based pairing: stamp `turnId: entry.id` (superseded 2026-10-21 by council C1: `pairingId: entry.id`, see §5 T1) on assistant calls in the provider and
  emit `nativeRecordId` (the message id) from the reader — the same id-lookup `matchingTurns`
  already does for Codex/Pi.
- **Repair contract.** `parserContractVersionFor` returns the default `'1'` for `commandcode`
  (no special case). Adding a reader changes extracted content, so the version must bump to
  `'2'` (a `COMMANDCODE_PARSER_CONTRACT_VERSION` constant, mirroring `KILO_PARSER_CONTRACT_VERSION`,
  `PI_PARSER_CONTRACT_VERSION`, etc.) so existing checkpoints re-parse and upsert content —
  the ADR 0016 repair contract, additive, never deleting historical metadata
  (`docs/dash/architecture.md` "Local harness-source refresh").
- **Unmeasurable buckets.** Command Code JSONL message blocks carry text and `tool_use`
  blocks but no `system_prompt`, `tool_definitions`, `instruction_context`, or
  `tool_result_content`; its `README_UNMEASURABLE` entry mirrors Pi's
  (`['pi', ['schema_cost','system_prompt','instruction_context','tool_definitions','tool_result_content']]`).
  This keeps the honest-unobservability rule (`docs/rules/honest-unobservability.md`).
- **Filter alias.** `HARNESS_FILTER_SPELLINGS` (`dash/src/canon/measurability.ts`) includes
  both `'commandcode'` and `'command-code'`; `parseHarnessFilter('command-code')` returns
  `{ kind:'harness', id:'command-code' }` because `normalizeHarnessName` has no fold, but no
  record is stored under `command-code`. Other alternates fold
  (`openai-codex → codex-unclassified`, `roo-cline → roo-code`, `kimicode/kimi-code → kimi-code`).
- **Docs scope.** `docs/dash/architecture.md` has no per-harness table (it points at
  telemetry-inventory); `docs/catalog.md` is a component catalog (no per-harness entries);
  `docs/dash/requirements.md` has no harness enumeration; the root `README.md` already lists
  "Command Code (`.commandcode`)" in the Native Agents row (from #382). The single missing
  doc is the telemetry-inventory row.
- **Direct reads of indexed code** (reasons): `dash/src/providers/commandcode.ts` +
  `.test.ts` (exact provider shape; codegraph confirmed symbols), `dash/src/providers/index.ts`
  (core-provider membership), `dash/src/refresh/registry.ts` (descriptor/disposition/
  READER_HARNESS_IDS/parserContractVersionFor), `dash/src/synth/provider.ts` (PROVIDER_READERS +
  pairing contract), `dash/src/synth/readers/types.ts` (ContentReader/ReaderTurn contract),
  `dash/src/synth/readers/pi.ts` (reader template), `dash/src/canon/measurability.ts`
  (READER_UNMEASURABLE / normalizeHarnessName / HARNESS_FILTER_SPELLINGS / parseHarnessFilter).
  Docs read directly (fallback): `AGENTS.md`, `docs/plans/README.md`,
  `docs/rules/kyberdash-display-layer.md`, `docs/dash/README.md`,
  `docs/dash/telemetry-inventory.md`, `docs/catalog.md`, `docs/dash/architecture.md`,
  `docs/documentation-ontology.md`, `docs/dash/requirements.md`.

---

## 3. Decisions

Permanent state. Preserved intact across Draft → Ready → execution → closeout → archive.
Never deleted on finalization.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode | `test-first` (the default; no opt-out supplied) | Packet-default; each implementation task carries a RED→GREEN contract in §4; the docs task is explicitly marked no-test with its replacing verification | ANSWERED — autonomous: default applied 2026-10-21 |
| D2 | Autonomy basis | `autonomous` per AGENTS.md Config Reg `<autonomy>`; all rows below decided by the three-check rule, none met the escalation bar | Every choice is covered by a repository pattern (Pi reader, Codex/Pi id-pairing, ADR 0016 repair contract, normalizeHarnessName fold convention, telemetry-inventory convention) or is cheap to reverse | ANSWERED — autonomous: Config Reg 2026-10-21 |
| D3 | Scope framing | The collector already shipped in #382; this plan completes the remaining display + docs + alias, and does **not** re-plan the collector | Verified `22cd32b` is an ancestor of HEAD; the ingest half (provider, descriptor, disposition, measurability, synth, parser cost) is merged and tested | ANSWERED — autonomous: `git merge-base --is-ancestor 22cd32b HEAD` + `git show --stat` 2026-10-21 |
| D4 | Content reader: add vs remain counters-only | Add a `commandcodeReader` mirroring `piReader`; Command Code turns gain `parts` (conversation_history + tool calls/results) in the Context Inspector | "Display command-code" is the objective; the provider comment names Pi's tree shape; Pi already has a reader, so the pattern is established. Additive (no schema, no data-model change), cheap to reverse | ANSWERED — autonomous: Pi-reader pattern + objective 2026-10-21 |
| D5 | Turn pairing | **Current answer (AP2, approved 2026-10-21):** id-based, not positional. The provider stamps `pairingId: entry.id` (the assistant message id) on assistant calls in `commandcode.ts`; `turnId` stays unset so record identity keeps its digest. The reader emits `nativeRecordId` (message id) and declares `positionalPairingUnsafe: true`. **Superseded original:** stamp `turnId: entry.id` on assistant calls, and emit `nativeRecordId` from the reader. The `turnId` stamp is superseded and must not be reintroduced | The provider skips all-zero-usage assistant messages, so positional pairing would misalign; `matchingTurns` id-lookup is the Codex/Pi mechanism, and the `pairingId`-only arm (the Cursor pattern) now carries Command Code. A `turnId` stamp would re-key every shipped span and duplicate rows on a contract re-parse, which the span-identity test pins | ANSWERED (superseded) — AP2 APPROVED 2026-10-21 (autonomous: recommendation accepted). Original answer ANSWERED — autonomous: matchingTurns id-pairing pattern 2026-10-21, retained as history |
| D6 | Parser contract version | Introduce `COMMANDCODE_PARSER_CONTRACT_VERSION = '2'` (from default `'1'`) so existing checkpoints re-parse and upsert reader content | ADR 0016 repair contract: content extraction changed; additive, never deletes historical metadata. Mirrors `KILO`/`PI` contract constants | ANSWERED — autonomous: ADR 0016 + registry convention 2026-10-21 |
| D7 | Unmeasurable buckets | `READER_UNMEASURABLE['commandcode'] = ['schema_cost','system_prompt','instruction_context','tool_definitions','tool_result_content']` (mirror Pi) | Command Code message blocks carry text + `tool_use` only; honest-unobservability rule forbids defaulting these to measured values | ANSWERED — autonomous: honest-unobservability + Pi precedent 2026-10-21; superseded in part by the measurability amendment (2026-10-21): `tool_result_content` is no longer declared |
| D8 | Filter alias | Fold `command-code → commandcode` in `normalizeHarnessName`; keep `commandcode` canonical | Both spellings are already in `HARNESS_FILTER_SPELLINGS`; the hyphenated form (the npm package's actual name) currently resolves to a dead id. Mirrors `openai-codex`/`roo-cline`/`kimicode` folds | ANSWERED — autonomous: normalizeHarnessName fold convention 2026-10-21 |
| D9 | Documentation scope | One new telemetry-inventory row; no architecture/catalog/requirements/README edits | `architecture.md` has no per-harness table; `catalog.md` is component-level; `requirements.md` has no harness enumeration; README already lists Command Code | ANSWERED — autonomous: docs-structure convention 2026-10-21 |
| AP1 | Approve and execute PLAN_READY 2026-10-21 | Approved — plan is Ready for execution; no open decisions, test-first contract approved, dependency graph schedulable | Conductor-supplied `AUTONOMOUS_APPROVAL: approve and execute for PLAN_FILE docs/plans/2026-10-21-commandcode-collector-kyberdash.md` under resolved autonomy `autonomous`. Frontmatter `status: draft` retained per ontology closed vocabulary (`current`, `draft`, `needs-review`, `superseded`); Ready is carried by body status + index Active row per plan-authoring §Ready and finalization. No scope, mode, or contract change at approval. | APPROVED — autonomous: recommendation accepted 2026-10-21 |
| AP2 | Approve the amended T1 Test contract and the D5 supersession after council C1 (`turnId` → `pairingId`; spanId equality; two-assistant guard; C1 duplicate-row fix)? | APPROVED, option (a): the amended T1 Test contract (§4 amended-contract block) and the D5 supersession (§3 D5 current answer) are approved as implemented. Option (b), revert the provider stamp to `turnId`, is rejected. | Recommendation accepted: (a). The amended contract is pinned in `dash/src/synth/readers/commandcode.test.ts`: (i) the provider assistant call carries `pairingId` = assistant message id and no `turnId`, and the reader `nativeRecordId` equals it (`commandcode provider pairing`); (ii) record spanIds equal the pre-pairing digest identity (`commandcode span identity`); (iii) a call pairs only with its own assistant message when an earlier assistant has no usage (`commandcode pairing guard`). The reader declares `positionalPairingUnsafe: true`. Source-verified 2026-10-21: `dash/src/providers/commandcode.ts` lines 232–235 (`pairingId`, no `turnId`); `dash/src/synth/provider.ts` `matchingTurns` (pairing-only claims first; positional arm off for `positionalPairingUnsafe`); `dash/src/synth/synth.ts` `nativeRecordIdentity` (no `turnId`, so the digest identity holds). Option (b) re-keys every shipped span and duplicates rows on re-parse. The Test-contract change returned the plan to Draft under `test-first-contract.markdown` and §4; mode (`test-first`) and autonomy (`autonomous`) are unchanged | APPROVED — autonomous: recommendation accepted 2026-10-21 (conductor-supplied `AUTONOMOUS_APPROVAL: AP2` for PLAN_FILE `docs/plans/2026-10-21-commandcode-collector-kyberdash.md` under resolved autonomy `autonomous`). Approve-and-execute is a separate gate and is not approved by AP2 |
| AP3 | Approve and execute PLAN_READY 2026-10-21, re-presented after the AP2 reopen (C1 `pairingId` supersession, T1 rework, T2 alias fold, T3 inventory row) | APPROVED — plan is Ready for execution. Scope unchanged from the approved C1 fix: T1 (provider `pairingId` stamp, reader `nativeRecordId` and `positionalPairingUnsafe: true`, amended pins in §4), T2 (`command-code` fold), T3 (telemetry-inventory Command Code row). Mode `test-first`; autonomy `autonomous`. | Conductor-supplied `AUTONOMOUS_APPROVAL: approve and execute for PLAN_FILE docs/plans/2026-10-21-commandcode-collector-kyberdash.md` under resolved autonomy `autonomous`. Verified by architect 2026-10-21: the T1–T3 edits are in the working tree (`providers/commandcode.ts` line 235 `pairingId`; `refresh/registry.ts` v2 comment; `canon/measurability.ts` alias fold; `telemetry-inventory.md` Command Code row with the `pairingId` join and usage-less note). Conductor-reported, not re-run by architect: T1 rework GREEN with 6708 tests passing; TASK_GATE PASS; amended pins GREEN with mutation checks. Residual, recorded not waived: no RED log for amended pins (i)–(iii) is in the tree; the conductor states no new RED is required because the pins are GREEN with mutation checks. Frontmatter `status: draft` retained per the AP1 precedent; Ready is carried by body status and the index row. AP1 and AP2 retained intact | APPROVED — autonomous: recommendation accepted 2026-10-21 |

`NO_QUESTIONS` — the request was decision-complete under autonomous handling: the one real
feature choice (reader vs counters-only) and every supporting choice (pairing, contract
version, unmeasurable buckets, filter alias, docs scope) is covered by an existing repository
pattern or is cheap to reverse, so all were decided above and none met the escalation bar.
The permanent table is retained regardless.

---

## 4. Test contract (test-first)

Per `test-first-contract.md`: each implementation task names its exact surface and command,
asserts observable behavior, and marks genuinely no-test tasks explicitly with their
replacing verification. Changing an approved contract (including weakening an assertion to
reach green) is a scope change returning the plan to Draft.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 content reader + wiring | `dash/src/synth/readers/commandcode.test.ts` (new, vitest) + `dash/src/refresh/registry.test.ts` (audit) | `npm --prefix dash run test` (full suite; focused `npx vitest run dash/src/synth/readers/commandcode.test.ts` during RED) | A JSONL session yields one `ReaderTurn` per assistant message carrying `conversation_history` parts and `toolCalls`/`toolResults` with `nativeRecordId`; a synthesized command-code record carries those parts; `auditProviderRegistry` stays consistent (`PROVIDER_READERS.has('commandcode') === READER_HARNESS_IDS.has('commandcode')`) | New reader test fails before the reader exists (no `commandcodeReader`, pairing drops parts); focused run RED log saved | Same tests pass unweakened; full `npm --prefix dash run test` green; `typecheck`/`lint`/`check:reachable` green |
| T2 filter alias | `dash/src/canon/measurability.test.ts` (extend, or new focused case) | `npm --prefix dash run test` | `parseHarnessFilter('command-code')` returns `{ kind:'harness', id:'commandcode' }`; `normalizeHarnessName('command-code') === 'commandcode'` | Assertion fails before the fold (returns `'command-code'`); RED log saved | Same test passes; full suite green |
| T3 telemetry-inventory row | no-test | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `docs drift .` | A Command Code row exists describing verified collection (JSONL counter ingest + content reader) and its availability gate | n/a (documentation) | Both commands pass with zero findings |

**Amended T1 Test contract — APPROVED under AP2 (autonomous: recommendation accepted, 2026-10-21).** The T1 row above is the base contract and is unchanged. AP2 approves these additional pinned assertions in `dash/src/synth/readers/commandcode.test.ts`: (i) `commandcode provider pairing`: each assistant call carries `pairingId` equal to its assistant message id with `turnId` unset, and the reader turn's `nativeRecordId` equals it; (ii) `commandcode span identity`: record spanIds equal the pre-pairing identity, so a re-parse does not duplicate rows; (iii) `commandcode pairing guard`: a call pairs only with its own assistant message when an earlier assistant has no usage. The reader declares `positionalPairingUnsafe: true`. The row's behaviours remain pinned: one turn per assistant message with `nativeRecordId`, tool calls and attached results, the registry audit, and parser contract `'2'`. The declaration assertions (the registry-wiring bucket test and the end-to-end `tool.invoke` flag test) are governed by the measurability amendment. No RED log for pins (i)–(iii) is in the tree. AP3 records that no new RED is required because the conductor reports the pins GREEN with mutation checks; this is a residual, not a waiver of the GREEN gate.

---

## 5. Dispatchable tasks

- **T1 — Command Code content reader + wiring.** Objective: add a `ContentReader`
  (`dash/src/synth/readers/commandcode.ts`) modeled on `piReader` that streams one
  `ReaderTurn` per assistant message — `parts` from `conversation_history` text blocks,
  `toolCalls`/`toolResults` from `tool_use` blocks, `sessionId` from the `session` header,
  `nativeRecordId` = message id — and wire it so command-code turns display content. Exact
  files/symbols: `dash/src/synth/readers/commandcode.ts` (new),
  `dash/src/synth/readers/commandcode.test.ts` (new), `dash/src/synth/provider.ts`
  (`PROVIDER_READERS` add `['commandcode', commandcodeReader]`), `dash/src/refresh/registry.ts`
  (`READER_HARNESS_IDS` add `'commandcode'`; add `COMMANDCODE_PARSER_CONTRACT_VERSION = '2'`
  and return it from `parserContractVersionFor` for `commandcode`), `dash/src/providers/commandcode.ts`
  (stamp `pairingId: entry.id` on assistant yields for id-pairing per §3 D5, as amended under AP2 (council C1); the original `turnId` stamp is superseded),
  `dash/src/canon/measurability.ts` (`READER_UNMEASURABLE` add the §3 D7 entry). Acceptance:
  §4 T1 row; `auditProviderRegistry` consistent; full dash suite green. Dependencies: none.
  Skill: `test-dev` (test-first vitest contract over `dash/src` TypeScript).
  **Supersession note (council C1, 2026-10-21; AP2 approved 2026-10-21).** The provider stamps `pairingId: entry.id` on assistant calls and leaves `turnId` unset, so record identity keeps the digest it shipped with and spanIds do not change on re-parse. The reader emits `nativeRecordId` = assistant message id and declares `positionalPairingUnsafe: true`. The registry v2 comment (`dash/src/refresh/registry.ts`) and the Command Code row of `docs/dash/telemetry-inventory.md` are updated to match. Pins: `commandcode provider pairing`, `commandcode span identity`, `commandcode pairing guard`. The dependency and file scope are unchanged. AP2 is approved and AP3 records approve-and-execute; T1 is landed in the working tree (conductor-reported GREEN).
- **T2 — Filter alias repair.** Objective: fold `command-code → commandcode` in
  `normalizeHarnessName` (`dash/src/canon/measurability.ts`) and pin it. Exact files:
  `dash/src/canon/measurability.ts`, `dash/src/canon/measurability.test.ts`. Acceptance: §4 T2
  row; `parseHarnessFilter('command-code')` yields harness id `commandcode`. Dependencies:
  none (disjoint file scope from T1 — runs in parallel). Skill: `test-dev`.
- **T3 — Telemetry-inventory documentation.** Objective: add a Command Code row to
  `docs/dash/telemetry-inventory.md` (§1.3) stating verified collection (JSONL counter ingest
  with cache-class subtraction and reported/estimated cost, plus the content reader) and its
  availability gate (no OTLP surface; content limited to what the transcript supplies; the
  §3 D7 buckets stay `not_measurable`), mirroring the Pi row. Exact files:
  `docs/dash/telemetry-inventory.md`. Acceptance: §4 T3 row; `docs validate` + `docs drift`
  zero findings. Dependencies: T1 (documents the shipped reader). Skill: `kyber-weave-docs`.

## 6. Dependency graph

`(T1 ∥ T2) → T3`. T1 and T2 are disjoint file scopes (reader/registry/synth vs measurability)
with no shared output; T3 depends on T1's shipped reader because the inventory row documents
its behavior.

---

## 7. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | Display-layer rule violated (logic leaks into `dash/web`/`dash/tray`) | All work is `dash/src/**`; `display-layer.test.ts` stays green under `npm --prefix dash run test` |
| R2 | Reader fabricates content or defaults unmeasured buckets | Reader emits only stored parts; `READER_UNMEASURABLE` (§3 D7) keeps the honest-unobservability rule |
| R3 | Contract-version bump deletes or rewrites history | ADR 0016 repair is additive upsert over stale checkpoints, never `records.raw` rewrite |
| R4 | Pairing misaligns because the provider skips zero-usage messages | Id-based pairing (§3 D5, as amended by C1 under AP2): `pairingId` on the provider call and `nativeRecordId` on the reader turn, both the assistant message id, never positional; `turnId` stays unset so span identity holds |
| R5 | Weakening an assertion or adding `NoWarn`/suppression to reach green | Forbidden: scope change returning the plan to Draft (§4); `TreatWarningsAsErrors` + `AnalysisMode=all` stay intact |
| R6 | `docs validate`/`drift` fail on the new plan/index row | §4 T3 runs both; any finding is fixed in the document, never by widening the ontology |

## 8. Verification gates

`npm --prefix dash run typecheck lint test check:reachable` (all green, counts recorded), plus
`dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and
`docs drift .` (zero findings). No .NET build/test is touched — this plan modifies only
`dash/src/**` TypeScript and `docs/**`.

## 9. Review

One `code-reviewer` council pass over the whole change (small-to-medium dash edit: one new
reader + wiring + one fold + one doc row) before closeout, per the repository's single
end-of-run council discipline.

## 10. `docs-dev` closeout

Harvest the durable decisions to canonical documentation: the Command Code content reader and
its unmeasurable buckets land in `docs/dash/telemetry-inventory.md` (T3) — the canonical
per-harness home — and the reader capability is recorded there. **No ADR — ADR waiver:** the
reader refines the existing content-reader/ASAD architecture (ADR 0009 multi-signal ingestion,
ADR 0011 ASAD-only context view, ADR 0016 repair contract) with no new boundary, dependency,
schema, or lasting invariant; it is a fourth JSONL content reader beside Claude/Codex/Pi and a
one-line filter fold. **No documentation waiver:** the telemetry-inventory row is the required
canonical documentation and is created by T3.

**Closeout mapping (docs-dev, 2026-10-21).** Destination for every §3 decision and approval. The
single canonical destination is `docs/dash/telemetry-inventory.md`, Command Code row (`commandcode`).
Its contents were checked against source at closeout: `dash/src/canon/measurability.ts` (the
four-bucket `READER_UNMEASURABLE` entry, line 106; `normalizeHarnessName` fold, line 252),
`dash/src/providers/commandcode.ts` (`pairingId` stamp, line 235, `turnId` unset), `dash/src/synth/synth.ts`
(`status` derivation, line 688), `dash/src/synth/provider.ts` (`PROVIDER_READERS`, line 123), and
`dash/src/refresh/registry.ts` (`COMMANDCODE_PARSER_CONTRACT_VERSION = '2'`).

| ID | Durable item | Canonical destination | Disposition |
|---|---|---|---|
| D1 | Development mode `test-first` | none | Documentation waiver: transient plan-execution mode; no surviving invariant. |
| D2 | Autonomy basis | none | Documentation waiver: transient plan-governance record; retained in §3. |
| D3 | Collector already shipped (#382), scope framing | telemetry-inventory Command Code row (file-source JSONL collection) | Harvested. |
| D4 | Content reader `commandcode` (one turn per assistant message, `nativeRecordId` = message id) | telemetry-inventory Command Code row | Harvested. ADR waiver: see below. |
| D5 | Id-based pairing (`pairingId` on the counter call, `turnId` unset, `positionalPairingUnsafe`) | telemetry-inventory Command Code row | Harvested. ADR waiver: see below. |
| D6 | Parser contract `'2'` (ADR 0016 repair, additive re-parse) | telemetry-inventory Command Code row | Harvested. ADR 0016 governs the repair mechanism; no new ADR. |
| D7 | Unmeasurable buckets: `schema_cost`, `system_prompt`, `instruction_context`, `tool_definitions`; `tool_result_content` measured (amendment) | telemetry-inventory Command Code row | Harvested. Amendment reflected: `tool_result_content` is not in `READER_UNMEASURABLE`. |
| D8 | Filter alias `command-code` folds to `commandcode` | telemetry-inventory Command Code row (added at closeout; no prior canonical home existed) | Harvested. Not a waiver: the filter behaviour is a surviving invariant and is now documented. |
| D9 | Docs scope: one inventory row; no architecture, catalog, requirements, or README edit | telemetry-inventory Command Code row (sole destination) | Harvested. The non-edited documents carry no Command Code invariant beyond the row; README already lists Command Code (#382). |
| AP1 | Approve and execute (AUTONOMOUS_APPROVAL) | none | Documentation waiver: approval-gate provenance, retained in §3. |
| AP2 | Approve amended T1 contract and D5 supersession | telemetry-inventory Command Code row (`pairingId` join) | Harvested through D5; approval record retained in §3. |
| AP3 | Approve and execute re-presented after the AP2 reopen | none | Documentation waiver: approval-gate provenance, retained in §3. |

**ADR waiver (D4, D5, the reader as a whole):** no ADR — ADR waiver: a fourth JSONL content reader
beside Claude, Codex, and Pi, using the existing id-pairing mechanism of `matchingTurns`, refines
ADR 0009, ADR 0011, and ADR 0016 with no new boundary, dependency, schema, or lasting invariant.
The one-line filter fold (D8) is an existing normalisation convention and needs no ADR.

**Closeout status.** Acceptance criteria are met by T1–T3 and the amended pins, as recorded in §3
(AP3). Verification reuses the conductor-reported evidence (6708 tests green; typecheck, lint,
reachable, and display-layer green; TASK_GATE PASS x3); the full suite was not re-run at closeout
because the closeout edit touches `docs/**` only. Plan status stays Ready in the index. The plan is
not archived: it moves to `docs/archive/plans/` only on PR merge (KW-DOC-LIFECYCLE-003).
