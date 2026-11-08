---
id: plans/2026-11-07-conductor-run-speed-standards-tool
title: Standards through the tool (conductor run-speed, slice 3)
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-07
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - DocGraph
  - KyberSquad
---

# Standards through the tool (conductor run-speed, slice 3)

**Status: Archived.** Closed 2026-11-08; see [Closeout](#closeout) and [Closeout mapping](#closeout-mapping) at the end. It was approved for execution under AP1, with resolved autonomy `autonomous`. It is the child plan of the parent design for the conductor run-speed work, which lives on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) and is deliberately not in this tree. Per the parent's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only slice 3's decision rows, design, harvest rows, ADR disposition and tasks. The parent design is archived by the last slice's pull request with a mapping to the children; this plan was archived in this slice's own pull request.

## Problem and goal

The parent design's measured run found that the standards tool exists and no worker uses it. `docs_get_coding_standard_for_technology` is named by no agent file, only by `toolchain.yml`; all 13 code-writing agents say to read the standard file instead. And the tool fails its own documented example: called live with `csharp` and topic `error handling`, it returned nothing, although the standard has an "Errors and diagnostics" section — a header, a rule-id line, then `no section matches the topic 'error handling', so nothing is excerpted for it.` and a marker naming the very section the caller needed. Handed no prose, the caller's next move is to open the file, which is the cost the size limit and the tool both exist to prevent.

The goal of this slice: make `docs_get_coding_standard_for_technology` the only route to a coding standard (fix 4), repair the topic match so a topic written in plain words reaches the section that answers it, and make the size cut safe — the answer says what was cut and that the cut is expected, and workers are told the same, so nobody opens the file.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D4 | How do workers get coding standards? | Only through `docs_get_coding_standard_for_technology`. Workers do not read plan files. | A file read is allowed only when the tool is missing. | user: explicit decision 2026-11-07 |
| D5 | Where does project-specific knowledge live? | Out of portable agents and skills. There are no technology reference files in portable skills. Project content goes in `review.gates`, `review.task-gates`, and a new `## Completion` section in each coding standard. | [docs/standards/README.md](../../standards/README.md) already states the rule. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and the pull request CI runs `docs validate --merge-ready`. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows the parent design's D12. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`. | autonomous: recommendation accepted |
| D47 | Why does this slice branch from slice 2's branch rather than from `main`? | Because slice 3 edits files slice 2 rewrote and slice 2 is not merged yet: `src/KyberWeave.Mcp/DocsTools.cs` (slice 2 put the CodeGraph gate at the top of every tool) and every agent body slice 2's T14 edited. Branching from `main` would make the pull request conflict on the first push, and GitHub does not run `pull_request` workflows on a conflicted pull request, so CI would never report. The slice's pull request therefore targets slice 2's branch and is retargeted to `main` automatically when slice 2 merges. | Measured twice on slice 2: a conflicted pull request produced no run at all, and the only way to get a verdict was to merge the base first. This branch is off `origin/factory/feat/conductor-run-speed-code-index` at 84f971de, which carries slice 2 and every commit merged into `main` up to that point. | autonomous: recommendation accepted |
| D48 | Which files does T19's edit invalidate beyond its task list? | The migration reports under `products/kyber-squad/migration/` for the agents it edits. Ten of the thirteen have reports — `csharp-dev`, `dal-dev`, `github-devops`, `maui-dev`, `pulumi-dev`, `python-dev`, `react-dev`, `sql-database-architect`, `tauri-dev`, `test-dev` — and every one whose agent body T19 edits carries that agent's new `final-body-sha256`, so the ten reports are in T19's scope. `typescript-dev`, `go-dev` and `rust-dev` have no report, so they have no digest to reconcile. | `SquadCanonicalContentTests` requires the `final-body-sha256` scalar from each report it reads (`SquadCanonicalContentTests.cs:2658`) and compares it with the digest the Squad loader computes from the agent body, so an edited body without an updated report fails the suite. Slice 2 recorded the same trap as its D45. | autonomous: recommendation accepted |
| D49 | Do the five other dev skills that carry the same standard-read line join T19? | Yes. `maui-dev`, `typescript-dev`, `go-dev`, `rust-dev` and `python-dev` `SKILL.md` each open with the same instruction as the `test-dev` skill the parent named: the decisions live in the path declared as `<technology>-coding-standard` in the root `AGENTS.md` — "Read that before the references below — this skill is procedure, not policy". They join T19's file list, so no skill an agent actually reads still tells it to open the file. | The parent's fix 4 names the `test-dev` skill only. An agent that follows its body's tool call and then reads its skill reference would still be told to read the standard file, which leaves this slice's own risk — workers told the cut is expected must not open the file anyway — alive. `csharp-dev` and `dal-dev` `SKILL.md` carry no such line (their reference files only), so they need no change. | autonomous: recommendation accepted |
| D50 | Does `code-reviewer.md` need the same change? | No. Its one instance of the phrase names **<review-guidance>** (`code-reviewer.md:62`) — REVIEW.md sections the reviewer pastes into council seats — and the file contains no coding-standard reference at all. The reviewer is not a code-writing worker under fix 4. | Verified by search: `code-reviewer.md` has no match for `coding-standard`. The `code-review` skill's technology checklists point at the declared standard as review input (`code-review/SKILL.md:159-161`), a consumption route the parent's fix 4 does not name; extending fix 4 to the review side is the parent's decision, not this child's. | autonomous: recommendation accepted |

In D12, "this file" names the parent design on its own branch, not this child plan.

## Investigation findings

Verified against this tree (branch `factory/feat/conductor-run-speed-standards-tool`, off `origin/factory/feat/conductor-run-speed-code-index` at `84f971de`), not assumed from the packet. Discovery: codegraph 2, docs 3, direct reads 4. The kw-docs server answered the runbook's tool contract (its provenance: `root=` this checkout, `rev=84f971de`, `documents=191`); two queries about the standards README rule missed their source, so that file was read directly as the narrow fallback the discovery order permits, labelled here. Code facts were checked through the CodeGraph index first; the direct searches that follow enumerate instruction files, migration reports, workflows and test files with exact line citations a symbol query does not return.

**The tool and its topic path.** `DocumentIndex.CodingStandards(string technology, string? topic = null, int charBudget = DefaultCharBudget)` sits at `src/KyberWeave.Core.Docs/Search/DocumentIndex.cs:236` (CodeGraph). The budget constants are verified in source: `DefaultCharBudget = 12000` (`DocumentIndex.cs:71`), `MinPerDocumentBudget = 1500` (`:77`), `MinRelevanceScore = 0.25` (`:89`) and `MinSectionScore = 0.02` (`:59`) — the cut a topic-selected section must clear. With no topic the whole standard comes back under budget (`WholeExcerpt`, verified verbatim at `DocumentIndex.cs:630-659`: sections in document order until the budget bites, omitted headings recorded, `budgetExhausted` flagged); with a topic, sections are scored against the topic vector.

**The topic bug's mechanism, verified in source.** The topic is vectorized by `TextVectorizer.VectorizeFused` and rarity-weighted by `DocumentCorpus.WeightByRarity`. `VectorizeFused` adds each adjacent token pair as one extra term at half weight (`TextVectorizer.cs:65-81`), and `WeightByRarity` multiplies each term by its inverse document frequency — so `error handling` carries `error`, `handling` and the fused `errorhandling`, and a term that appears in almost no document gains a rarity weight that dominates the vector. No section of the C# standard contains `errorhandling`, so no section's cosine similarity against the fused vector clears `MinSectionScore`, and the reply hands back no prose. The parent reproduced this live at this revision: `technology: csharp`, `topic: error handling` — the runbook's own documented example — returns a header, a rule-id line, then `no section matches the topic 'error handling', so nothing is excerpted for it.` plus `[also in this document, not matching this topic: … Errors and diagnostics …]`. The standard has the section; the topic selects none of it.

**The runbook documents today's behaviour, and T18 rewrites exactly it.** The tool contract is `### docs_get_coding_standard_for_technology(technology, topic = null, charBudget = 12000)` at `docs/docgraph/mcp-runbook.md:1227` (kw-docs, confidence 0.90). It states: `charBudget` is clamped to 1000–120000, defaulting to 12000, shared across the standards one technology resolves to; the `[omitted for space, ask again with a larger charBudget: …]` marker names what the budget dropped; and — the sentence this slice reverses — "A topic no section clears returns no prose — it does not fall back to the whole standard."

**The budget cut on the test standard.** The sections are verified present: `Tests that start processes` at `docs/standards/test/README.md:125` and `Commands` at `:187`. The parent measured the file at 21,354 bytes with those two sections at 7,516 and 4,728 — recorded from the parent's measurement, not re-derived here; every other section totals well under the 12,000 default, so the document-order cut drops exactly those two. That is the outcome the parent declares safe.

**Test files, counted in this tree.** `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs` exists with exactly 24 tests (23 `[Fact]`, 1 `[Theory]` — counted directly, reason: a test count is an enumeration a symbol query does not return). `tests/KyberWeave.Tests/SquadStandardsToolContractTests.cs` does not exist; T17 creates it. `tests/KyberWeave.Tests/DocumentIndexTests.cs` carries the class `CodingStandardsLookupTests` at `:439`, covering `index.CodingStandards` end to end, including the topic hit (`:797`, `topic: "naming"`) and the topic miss (`:816`, `topic: "zzz-no-such-topic"`) — the assertion whose expectation T18 changes. All three live in the product project `tests/KyberWeave.Tests`; no class this slice touches lives in `tests/KyberWeave.RepoPolicy.Tests`, and no file under `tests/` pins the phrase being replaced.

**The agent bodies, enumerated.** Exactly 13 agent files instruct a coding-standard file read — T19's 13, no more. Twelve carry the phrase "Read the path declared as **<technology>-coding-standard**": `csharp-dev.md:58`, `typescript-dev.md:45`, `python-dev.md:41`, `go-dev.md:44`, `rust-dev.md:45`, `tauri-dev.md:47`, `maui-dev.md:45`, `pulumi-dev.md:45`, `react-dev.md:27`, `sql-database-architect.md:92`, `github-devops.md:81` and `test-dev.md:50`. `dal-dev.md` words it differently — "Read **<data-access-layer-coding-standard>**, **<csharp-coding-standard>**, and **<sql-coding-standard>** before writing persistence code" (`dal-dev.md:55`, with `:15` and `:83-85`) — but it is the same file-read instruction. No agent file names `docs_get_coding_standard_for_technology`; in this tree the tool is named by `products/kyber-squad/toolchain.yml`, the renderers, `src/KyberWeave.Mcp/DocsServerInstructions.cs`, `src/KyberWeave.Mcp/DocsTools.cs` and the docs and tests — never by an agent body. Every agent body carries a `## Discovery order` section (`csharp-dev.md:87` verified), and `SquadCanonicalContentTests.cs:2154-2155` pins that the section exists, so the tool joins a section that is already load-bearing.

**The skills, enumerated.** Six dev skills carry the policy-read line: `skills/test-dev/SKILL.md:9-11` ("Test policy lives in the path declared as **<test-coding-standard>**… Read those before the references below"), and the same line shape in `skills/maui-dev/SKILL.md:9-11`, `skills/typescript-dev/SKILL.md:9-11`, `skills/go-dev/SKILL.md:9-10`, `skills/rust-dev/SKILL.md:9-11` and `skills/python-dev/SKILL.md:9-11`. The parent's fix 4 named only the `test-dev` skill; the other five carry the identical instruction and join T19 (D49). `create-pull-request/SKILL.md:60-63` runs the commands the declared standard names — that consumption moves with the parent's fix 3 slice, not this one.

**The reviewer, checked.** `code-reviewer.md:62` carries the phrase for **<review-guidance>** — REVIEW.md sections pasted into council seats — and the file contains no coding-standard reference at all (D50: no change).

**The migration reports.** Eighteen reports exist under `products/kyber-squad/migration/`; ten cover agents T19 edits, and `typescript-dev`, `go-dev` and `rust-dev` have none (D48). `SquadCanonicalContentTests` loads the reports, requires their locked source and `final-body-sha256` (`:2658`), so an edited agent body without an updated report fails the suite.

**Guards that stay green, and why.** `SquadRustGoSpecialistTests.cs:50-51` and `SquadTypeScriptSpecialistTests.cs:62` assert the rendered agent body contains the registry property token (`<rust-coding-standard>`, `<go-coding-standard>`, `<typescript-coding-standard>`). T19's acceptance keeps the property names, so those assertions stay green — and they are the guard that the names stay.

**The naming check exists on this branch.** The packet expected no `pr-naming` job here; that no longer holds. Slice 1 merged into `main` before this branch's base, and this branch carries it: `.github/workflows/ci.yml:115` declares the `pr-naming` job, `.github/workflows/pr-naming-recheck.yml` re-runs it when a title is edited, and `ci.yml:1188` feeds it to the pull request summary. The job gates this slice's pull request title, so the title follows `<type>(<scope>): <summary> [factory]` or CI fails. The branch is already created in the right shape, `factory/feat/conductor-run-speed-standards-tool` (`<harness>/<type>/<slug>`).

**The docs server this slice edits.** Slice 2's CodeGraph gate sits at the top of every docs tool (`AgentCodingStandards` calls `CheckCodeGraph` first; the handler is at `src/KyberWeave.Mcp/DocsTools.cs:592`), so T18's changes to `DocsTools.cs` must keep that gate where it is.

**The runner commands.** PRODUCT-RUN and POLICY-RUN are restated below from the same sources slice 2's contract restated them from — `docs/standards/test/README.md` ("Commands") and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md` — because this child reads no other plan.

## Design

This slice's design, in the parent's voice. Fix 4 verbatim:

### Fix 4: standards only from the tool

- Change that step in all 13 agents and the `test-dev` skill, and add the tool to the Discovery order. A file read is allowed only when the tool is missing.
- **Topic bug.** Match on words rather than the phrase. On a miss, return the standard up to the size limit with a one-line note.
- **Size limit stays, and the cut becomes safe.** It drops "Commands" (7.5 KB) and "Tests that start processes" (4.8 KB) from the 21 KB test standard. Commands now arrive in the prompt, and a worker starting processes can ask for that topic. Workers are told the cut is expected, so they do not open the file.

**The topic match.** The topic's fused pair is the defect: `error handling` vectorizes as `error`, `handling` and the fused `errorhandling`, rarity weighting gives the pair a weight no section can match, and selection scored against the whole vector clears `MinSectionScore` for nothing. The repair is the parent's rule: match on the topic's words rather than the phrase, so a topic in plain words reaches the section that answers it. The Test contract pins the parent's own example — `csharp` with topic `error handling` returns the "Errors and diagnostics" prose.

**The miss path.** Today a miss hands back no prose, which is why the caller opens the file. On a miss the standard comes back up to the size limit, in document order, with a one-line note saying no section matched the topic. The budget still binds the miss path — a miss must not become the one call that ignores it — and the reply still names what the cut dropped.

**The safe cut.** The 12,000-character default and the 1000–120000 clamp stay. `WholeExcerpt` keeps its document-order behaviour: on this repository's test standard the cut drops "Commands" (7,516 characters) and "Tests that start processes" (4,728), and the parent declares that outcome safe — commands arrive in the worker's prompt (fix 3, its own slice), and a worker starting processes asks for that topic by name. What changes is that the answer says so: the omission marker names the dropped sections and the reply states the cut is expected, so a worker never reads silence as "go and open the file".

**Tool only.** The 13 agents' first workflow step becomes a call to `docs_get_coding_standard_for_technology`, and the tool joins each agent's Discovery order. The registry property names stay in the bodies — the tool accepts `<csharp-coding-standard>` as its `technology` key, and a draft or missing standard still returns its question, so the agent's ask-the-human rule (for example `dal-dev.md:84`) is unchanged. A file read is allowed only when the tool is missing — not when the tool answers with a cut. `dal-dev.md`'s differently-worded line and the six dev skills' policy line get the same change (D49), so nothing a worker actually reads still tells it to open the file. Workers are told the cut is expected, so they do not open the file.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | The topic match, the safe cut, and what the answer says about it | DocGraph | runbook | `docs/docgraph/mcp-runbook.md`, the tool contract section for `docs_get_coding_standard_for_technology` |
| H2 | Standards are reached only through the tool; a file read only when the tool is missing; the registry property names stay | KyberSquad | standard | `docs/standards/README.md` |

## ADR disposition

No ADR — ADR waiver: fix 4 refines the contract of an existing tool (`docs_get_coding_standard_for_technology`) and records a routing rule, not a new boundary, dependency, schema or contract of its own; D4 and D5 are already the owner's explicit decisions and are carried in this plan's Decisions table; and the durable behaviour lands in the runbook's tool contract (H1) and in `docs/standards/README.md` (H2).

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. No row runs the full suite — every command below is narrowed by class, and the process guard refuses an unfiltered run from a dev agent (exit 89). A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces.

The two runner definitions, as slice 2's contract states them, restated from `docs/standards/test/README.md` ("Commands") and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`:

- **PRODUCT-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`
- **POLICY-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`

Every class this contract names lives in the product project `tests/KyberWeave.Tests`: `McpAgentCodingStandardsTests` (`McpAgentCodingStandardsTests.cs`, 24 tests today), `SquadCanonicalContentTests` (`SquadCanonicalContentTests.cs`) and `CodingStandardsLookupTests` (`DocumentIndexTests.cs:439`). `SquadStandardsToolContractTests` is new in T17, in the same project. No row is a POLICY-RUN: no class this slice touches lives in the policy project, and no policy test pins the files being edited. The policy project's classes keep the `KyberWeave.Tests` namespace, so a fully-qualified name names the same test wherever it lives.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T17 | `tests/KyberWeave.Tests/` — `McpAgentCodingStandardsTests`, `SquadStandardsToolContractTests` (new), `SquadCanonicalContentTests` | PRODUCT-RUN over the three classes | Rows T18 and T19 are written and fail for the reason they name | The topic expectations fail against the live topic bug — `csharp` with `error handling` returns no prose today; the new `SquadStandardsToolContractTests` fails because no agent routes the standard through the tool; the canonical pins for the tool step fail against today's "Read the path declared as" text, while the migration-digest assertions stay green and are kept green through T19's same-change report updates | T17's acceptance: rows T18 and T19 are written and fail for the reason they name |
| T18 | `tests/KyberWeave.Tests/` — `McpAgentCodingStandardsTests`, `CodingStandardsLookupTests` (`DocumentIndexTests.cs:439`), `SquadStandardsToolContractTests` | PRODUCT-RUN over the three classes | The topic matches on words: `error handling` returns the "Errors and diagnostics" prose; a miss returns the standard up to the budget with a one-line note; the budget cut names what it dropped and says the cut is expected; the 12,000 default and the 1000–120000 clamp stay | Covered by T17's RED | T18's acceptance: fix 4's topic rule holds, the 12,000-character default stays, the answer says what was cut and that this is expected |
| T19 | `tests/KyberWeave.Tests/` — `SquadCanonicalContentTests`, `SquadStandardsToolContractTests` | PRODUCT-RUN over the two classes | The 13 agent bodies and six skill files route standards through the tool, with a file read only when the tool is missing; the registry property names stay; every edited agent's migration report carries the new `final-body-sha256` | Covered by T17's RED | T19's acceptance: D4 holds — the read becomes a tool call, the property names remain, and D48 and D49's file scopes are delivered |
| T20 | No test class — documentation and closeout | `docs validate .` and `docs drift .` after the docs edits (CLI gates, through `scripts/proc-guard.sh run --`) | Harvest, child plan archived, zero findings | None — documentation task | T20's acceptance: harvest, child plan archived, zero findings |

## Tasks

- **T17: RED for the standards tool**
  - **Files:** `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs`, `tests/KyberWeave.Tests/SquadStandardsToolContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T18 and T19 are written and fail for the reason they name.
- **T18: Topic matching and the safe cut**
  - **Files:** `src/KyberWeave.Core.Docs/Search/DocumentIndex.cs`, `src/KyberWeave.Mcp/DocsTools.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T17
  - **Acceptance:** fix 4's topic rule. The 12,000-character default stays. The answer says what was cut and that this is expected. T18's row holds.
- **T19: Workers use the tool**
  - **Files:** `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/dal-dev.md`, `products/kyber-squad/agents/github-devops.md`, `products/kyber-squad/agents/go-dev.md`, `products/kyber-squad/agents/maui-dev.md`, `products/kyber-squad/agents/pulumi-dev.md`, `products/kyber-squad/agents/python-dev.md`, `products/kyber-squad/agents/react-dev.md`, `products/kyber-squad/agents/rust-dev.md`, `products/kyber-squad/agents/sql-database-architect.md`, `products/kyber-squad/agents/tauri-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/typescript-dev.md`, `products/kyber-squad/skills/test-dev/SKILL.md`, `products/kyber-squad/skills/go-dev/SKILL.md`, `products/kyber-squad/skills/maui-dev/SKILL.md`, `products/kyber-squad/skills/python-dev/SKILL.md`, `products/kyber-squad/skills/rust-dev/SKILL.md`, `products/kyber-squad/skills/typescript-dev/SKILL.md`, `products/kyber-squad/migration/csharp-dev.md`, `products/kyber-squad/migration/dal-dev.md`, `products/kyber-squad/migration/github-devops.md`, `products/kyber-squad/migration/maui-dev.md`, `products/kyber-squad/migration/pulumi-dev.md`, `products/kyber-squad/migration/python-dev.md`, `products/kyber-squad/migration/react-dev.md`, `products/kyber-squad/migration/sql-database-architect.md`, `products/kyber-squad/migration/tauri-dev.md`, `products/kyber-squad/migration/test-dev.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T18
  - **Acceptance:** D4. "Read the path declared as" becomes a tool call, and a file read is allowed only when the tool is missing. The registry property names remain so a draft or missing standard still returns its question. T19's row holds.
- **T20: Docs and closeout for slice 3**
  - **Files:** `docs/docgraph/mcp-runbook.md`, `docs/standards/README.md`, `docs/plans/README.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T18, T19
  - **Acceptance:** harvest, child plan archived, zero findings.

## Dependency graph

- T17 stands alone.
- T18 depends on T17.
- T19 depends on T18.
- T20 depends on T18 and T19.

## Risks

- **A topic miss that returns the whole standard could re-create the cost the size limit exists to prevent.** The miss path must still respect the budget and say plainly what it did: one line, the sections the cut dropped, the note that the cut is expected. A miss that silently ignores `charBudget` would make the cheapest call the most expensive one; the Test contract pins the miss reply under a small budget so this cannot regress quietly.
- **Workers told the cut is expected must not open the file anyway, or the slice buys nothing.** The instruction lands in the same workflow step that names the tool, in all 13 agents and all six dev skills (D49) — an agent that reads its skill after its body would still meet the old line if the five extra skills were left. The file-read fallback is allowed only when the tool is missing, never when the tool answers with a cut.
- **The stacked-branch risk from D47.** This branch sits on slice 2's unmerged branch; the pull request targets slice 2's branch and is retargeted to `main` when slice 2 merges. Until then a conflict can produce a pull request GitHub runs no `pull_request` workflows on at all — measured twice on slice 2. Do not merge slice 2 into this branch and do not rebase onto `main`; push to the stacked branch only.
- **An edited agent body without an updated migration report fails the suite** (D48). The ten reports update in the same change as the bodies, not after; `typescript-dev`, `go-dev` and `rust-dev` have no report, and none may be fabricated for them (`SquadCanonicalContentTests` checks that too).

## Out of scope

- The parent's other slices — each has its own child plan on its own branch and its own pull request.
- `## Completion` in each coding standard and the onboarding step: slice 7's, and both depend on this slice's topic repair (D13: fix 7 depends on fix 11, which depends on fix 4).
- Commands in the worker's prompt (fix 3, its own slice) — this slice only makes the answer say what the cut dropped; the "Commands" section stays in the test standard, reachable by asking for that topic.
- The review side's standards route (D50): `code-reviewer.md` and the `code-review` skill's technology checklists stay as they are.
- `create-pull-request/SKILL.md`'s consumption of the standard's named commands — it moves with fix 3's slice.
- The `charBudget` clamp, the 12,000 default, the registry property names and the tool's miss reply for an undeclared technology — all unchanged.

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — `scripts/proc-guard.sh run` refuses an unfiltered run from a dev agent with exit 89, and the full suite belongs to the review council's `test` gate and CI.
- `docs validate .` and `docs drift .` run after every plan save and after the docs task (T20), each at zero findings. `docs validate . --merge-ready` is **not** the check while this plan is open: KW-DOC-LIFECYCLE-003 fails an open plan by design, and the pull request CI runs it, which is what archives this plan in this slice's pull request at closeout.
- **Branch and title.** The `pr-naming` CI job gates this slice's pull request title (verified on this branch: `ci.yml:115` with `pr-naming-recheck.yml`; slice 1 merged into `main` before this branch's base, so the packet's expectation that no naming job gates this branch no longer holds). The branch is already created as `factory/feat/conductor-run-speed-standards-tool` — the `<harness>/<type>/<slug>` shape — and the pull request title follows `<type>(<scope>): <summary> [factory]` or the naming job fails it.

## Review

This slice's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review, its command derived from the `code-review` skill with `--base` — never copied into this plan.

## Closeout

`docs-dev` closed this child plan in this slice's pull request on 2026-11-08, verifying the delivered tree rather than the plan's own account of it.

**Verified against the tree.** `DocumentIndex.TopicVector` (`src/KyberWeave.Core.Docs/Search/DocumentIndex.cs`) now drops a synthesised compound that no candidate section states and keeps every word the caller wrote; `Excerpt` still selects only sections clearing `MinSectionScore`, and `DocumentIndex.CodingStandards` still returns no prose for a topic nothing answers. `DocsTools.AgentCodingStandards` (`src/KyberWeave.Mcp/DocsTools.cs`) writes `no section matches the topic '<topic>', so the standard follows without it.` and then returns that standard in document order inside the same budget, and `AppendExcerpt` follows a budget marker with `[this cut is expected: the document is longer than the charBudget, so what did not fit is left out rather than missing]` while leaving the relevance marker untouched. The 12,000-character default, the 1000–120000 clamp, `MinPerDocumentBudget` and `SectionSaturation` are unchanged in source, and the tool's own `[Description]` now states the topic-miss fallback.

**Harvested.** Both Design harvest rows landed in canonical documentation: H1 in the DocGraph MCP runbook's tool contract and in the retrieval reference, H2 in the coding-standards index. Each page was edited and its `last-reviewed` moved to 2026-11-08, so the harvest is a reviewed change rather than a claim.

**Not verified here.** The product test classes this plan's Test contract names were run by the developer tasks' own runs and by this slice's council on the pull request, not re-run by this closeout: `docs-dev` runs only the documentation checks below.

**Gates.** `docs validate . --merge-ready` and `docs drift .` were run after the archive move; this slice's own finding, the `KW-DOC-LIFECYCLE-003` on this plan while it was open, is cleared by the archive.

## Closeout mapping

Every decision D4, D5, D12, D13, AP1, D47, D48, D49 and D50, and both Design harvest items H1 and H2, maps to a destination below. Documentation-impact for every row: `extends-component`. Last reviewed: 2026-11-08 for every document this closeout edited, and the date each page itself carries for a page it mapped without an edit. Status: archived.

`DocGraph` and `KyberSquad` both stay in `impact-components`. `DocGraph` is carried by the runbook and the retrieval reference, both edited here. `KyberSquad` is carried by the Kyber-Squad onboarding guide, which names `docs_get_coding_standard_for_technology` as the reason the orchestrator holds the `kw-docs` grant, and therefore names the grant that puts this slice's only route in every worker's hands; that page is reviewed no earlier than this plan, in its frontmatter and in the row below. A coding standard carries no `component` by ontology design — one language's standard governs code in every component — so the standards index cannot carry `KyberSquad` itself, and the guide that states the grant is where the component is recorded.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D4 | [Kyber-Squad onboarding, why the orchestrator is included](../../kyber-squad/onboarding.md) — the `kw-docs` grant that puts `docs_get_coding_standard_for_technology` in every specialist's hands, which is what makes a tool-only route possible at all; [Coding standards, a standard is read through the tool](../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) — the rule itself: the tool is the only route, a file read only when the tool is missing, and never as the fallback for a cut answer | extends-component | KyberSquad, DocGraph | 2026-11-07 | archived |
| D5 | [Coding standards, the page preamble](../../standards/README.md) — a standard is project-specific while the agents and skills that read it are not, which is why project content lives in `review.gates`, `review.task-gates` and the standards themselves rather than in portable agents | extends-component | KyberSquad | 2026-11-08 | archived |
| D12 | [Plan inventory, one plan never spans several pull requests](../../plans/README.md) — the slice rule this plan was carved under, and this plan's row now sits in Archived Plans | extends-component | DocGraph | 2026-11-08 | archived |
| D13 | Documentation waiver — the parent design's build order across the slices this plan does not carry; execution sequencing with no surviving architectural, operational or behavioural invariant, and the parent design carries the order for its own closeout | extends-component | DocGraph | 2026-11-08 | archived |
| AP1 | Documentation waiver — approval provenance for this slice's Test contract and execution; no lasting artefact cites the approval itself | extends-component | DocGraph | 2026-11-08 | archived |
| D47 | [Plan inventory, a slice whose base is unmerged is stacked on it](../../plans/README.md) — branch from the unmerged slice, target its branch, retarget to `main` as it merges, and do not merge the base in or rebase to unblock CI, because a conflicted pull request runs no `pull_request` workflow at all | extends-component | DocGraph | 2026-11-08 | archived |
| D48 | Documentation waiver — the ten migration reports T19's agent-body edits invalidate, whose `final-body-sha256` the shipped `SquadCanonicalContentTests` pins; test-fixture bookkeeping with no surviving invariant beyond the test itself | extends-component | KyberSquad | 2026-11-08 | archived |
| D49 | [Coding standards, a standard is read through the tool](../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) — the instruction sits in the skill beside the agent as well as in the agent body, so no file a worker reads still tells it to open the standard | extends-component | KyberSquad | 2026-11-08 | archived |
| D50 | [Skill-resource dispositions, the `code-review` references](../../kyber-squad/skill-resource-dispositions.md) — the reviewer's technology references stay review-input pointers to the declared standard, the route this slice deliberately left unchanged; the reviewer writes no code and is not under the tool-only rule | extends-component | KyberSquad | 2026-10-30 | archived |
| H1 | [DocGraph MCP runbook, the standards tool contract](../../docgraph/mcp-runbook.md#docs_get_coding_standard_for_technologytechnology-topic--null-charbudget--12000) — the topic matches on its words with the fused-pair measurement table, the rejected wider variant and why it was rejected, the topic-miss fallback and that the fallback is the tool's decision, and both omission markers with the expected-cut line; [Retrieval and ranking, excerpt budgeting](../../docgraph/retrieval.md#excerpt-budgeting-docs_get_coding_standard_for_technology) — the topic-vector rule, the section floor, the expected-cut statement, and the index-returns-no-prose / tool-falls-back split | extends-component | DocGraph | 2026-11-08 | archived |
| H2 | [Coding standards, a standard is read through the tool](../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) — standards are reached only through the tool, a file is read only when the tool is missing, and the registry property names stay because a draft or undeclared standard must still return the worker's own question | extends-component | KyberSquad, DocGraph | 2026-11-08 | archived |
