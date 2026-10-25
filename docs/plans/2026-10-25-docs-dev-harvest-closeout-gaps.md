---
id: plans/2026-10-25-docs-dev-harvest-closeout-gaps
title: "Close the docs-dev decision-harvest gaps: routing description, spec parity, merge-ready closeout gate, three backfills"
doc-type: plan
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-25
development-mode: test-first
---

# Close the docs-dev decision-harvest gaps

**Status: Draft — decision-complete (D1–D18, Q1, Q2 all ANSWERED), awaiting the owner's
approve-and-execute. Q1 and Q2 are orchestrator technical calls the owner may override at
that gate. Pre-approval checks run by the conductor in this worktree on 2026-10-25:
`docs validate .` returned no findings; `docs drift .` returned no findings, against a
CodeGraph index built in the worktree.**

**Development mode:** `test-first` (packet). **Autonomy:** `supervised` (packet; overrides the
Config Reg `<autonomy>` default for this run).

**Working copy:** worktree `.claude/worktrees/harvest-gaps`, branch `docs/harvest-closeout-gaps`,
cut from `origin/main` `a0862e7c`. Every path below is relative to that worktree.

## Problem and goal

### Problem

`docs-dev`'s main job is to harvest the decisions a plan or specification made into canonical
documentation at closeout. Issue #279 (PR #291) set that contract for plans. Three things now
let it slip:

1. **Closeouts are being skipped.** Three plans archived 2026-10-22..24 never got a real
   closeout and still carry `status: draft` with a body status of `Ready`:
   - `docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md` (PR #423). Its §11
     closeout ran, recorded the architecture harvest as `RESIDUAL` with no todo, and the plan
     was archived anyway, which breaks `docs-dev` Plan closeout step 7 ("do not archive").
   - `docs/archive/plans/2026-10-23-opencode-context-capture-kyberdash.md` (PR #425). Moved
     into the archive as an unchanged rename (commit `8beb97ae`, git `R100`) to satisfy
     `KW-DOC-LIFECYCLE-003`. No closeout ran; §8 is the plan-time forecast only, yet the
     plan-index row claims "D1–D7 and AP1 mapped in the plan's §8 Closeout".
   - `docs/archive/plans/2026-10-23-pi-renderer-network-read-mcp.md` (PR #426). Added
     straight into the archive (commit `6dd4ce3f`) while its body said Ready. The mapping was
     promised "in the completion summary", which does not exist.
2. **Nothing mechanical catches it.** `KW-DOC-LIFECYCLE-003` (`OpenWorkValidator`, run by
   `docs validate --merge-ready`) only checks that no plan or spec remains in the active
   folder. A bare move into the archive makes CI green.
3. **The contract is weaker than it looks.** `docs-dev`'s routing description never mentions
   closeout; the spec path never got the #279 fix (no permanent Decisions table in the
   design phase, no decision-to-destination mapping or waiver rules in spec closeout).

Root cause (D3): the closeouts were **skipped**, not done badly. So the primary fix is the
mechanical gate (item C); the text changes (A, B) and the backfills (D) support it.

### Goal

- **A — Routing.** `docs-dev`'s description leads with plan/spec closeout and decision
  harvesting, still routes READMEs, ADRs and runbooks, and keeps the spec-authoring
  exclusion. Its Scope list puts closeout first.
- **B — Spec parity.** Design phase requires a permanent five-column `## Decisions` table;
  spec closeout (in `docs-dev.md` and `closeout-phase.md`) requires the same closeout mapping,
  ADR-waiver vs documentation-waiver rules, and "do not archive" when unmapped; the
  intent-alignment lens and the conductor spec path check specs as well as plans;
  `docs/specs/README.md` mirrors the plan policy.
- **C — Gate.** New rule `KW-DOC-LIFECYCLE-004`: under `--merge-ready`, an archived plan or
  spec fails unless it carries closeout evidence. A bare move, an archive-while-Ready, and an
  unresolved `RESIDUAL` each fail. Test-first.
- **D — Backfill.** The three plans above get a verified per-decision closeout mapping, the
  archive status, and reconciled index rows, without rewriting their history.

## Decisions

Permanent table; preserved intact across all lifecycle transitions (Draft → Ready → execution
→ closeout → archive).

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode? | `test-first`. | Packet sets it; no opt-out supplied. The Test contract below is the matching contract. | ANSWERED — per packet 2026-10-25 |
| D2 | Autonomy for this run? | `supervised`. | Packet sets it; the Config Reg default (`autonomous`) does not apply to this run. A change of autonomy reopens nothing. | ANSWERED — per packet 2026-10-25 |
| D3 | What is the root cause the plan fixes? | Closeouts were **skipped**, not done badly. Three real failure shapes: (1) unchanged move into the archive (#425, `8beb97ae`, R100, done "to satisfy KW-DOC-LIFECYCLE-003"); (2) added straight into the archive while the body said Ready (#426, `6dd4ce3f`); (3) closeout ran, recorded `RESIDUAL`, archived anyway (#423). Item C is the primary fix. | Conductor-relayed git evidence. This author could not run git (no process tool), but the archived files corroborate every shape: all three carry `status: draft` and a body `**Status: Ready`; #423 §11 carries two `RESIDUAL` rows; #425 §8 and #426 §11 are forecasts. | ANSWERED — conductor-relayed evidence 2026-10-25; file state verified in worktree |
| D4 | New rule id, or extend `KW-DOC-LIFECYCLE-003`? | New id **`KW-DOC-LIFECYCLE-004`**, new class `ArchiveCloseoutValidator`. `KW-DOC-LIFECYCLE-003` is unchanged. | Rule ids are permanent (root `AGENTS.md`): suppressions and SARIF baselines key on them, and 003 means "still in the active folder". Changing its meaning would silently change what every existing suppression covers. The existing `OpenWorkValidatorTests.ArchivedPlansAndSpecificationsAreClosed` stays true. `004` is unused (grep: no `LIFECYCLE-00[4-9]` anywhere). | ANSWERED — architect technical call |
| D5 | What counts as closeout evidence? | The six conditions in "Gate contract" below: archive frontmatter status; no live body status; a `## Decisions` table; a `## Closeout mapping` table; every decision ID mapped to an existing canonical file or a documentation waiver; every `RESIDUAL` linked to an existing todo. One finding per entry, naming every failed condition, with a hint. | Built from the three real shapes (D3), so each one fails for its own reason. A loose "any heading containing Closeout" match was rejected: #425's §8 "Review and closeout (docs-dev)" forecast would satisfy it. `ADR waiver` alone does not map an ID, which enforces the existing policy line "No ADR is NOT a documentation waiver". | ANSWERED — architect technical call |
| D6 | When does `KW-DOC-LIFECYCLE-004` run? | Only under `docs validate --merge-ready`, beside 003. | Matches the packet framing and the 003 precedent. `review gates`' `docs-validate` gate stays without `--merge-ready`, because an active plan is legitimate on a branch and would fail 003. The gap (closeout happens after review) is covered by the post-closeout gate run (T12) and by Q2. Cheap to reverse. | ANSWERED — architect technical call (low risk) |
| D7 | Frontmatter status convention for archived documents | Accept `archived`, `complete`, or `superseded` (case-insensitive) under 004. New closeouts write `status: archived` and a first body line `**Status: Archived <date> — …**`. The ontology's closed status vocabulary is **not** widened. | The loader excludes the `archive` segment (`OntologyConfig.DefaultExcludedSegments`, `DocumentLoader.IsExcluded`), so archived documents are never schema-validated. That is why `archived`/`complete`/`completed`/`Archived` all exist in the archive today. 004 is the only check that reads them. `docs-dev.md` step 6 already says "Archived". | ANSWERED — architect technical call |
| D8 | Where does a spec's closeout mapping live? | In the spec's `design.md`, under `## Closeout mapping`, next to its `## Decisions` table. Every document in the archived spec folder carries the archive status. | `design.md` exists in both spec layouts: this repo's folder-with-README layout and the host three-file layout (`requirements.md`, `design.md`, `tasks.md`). Keeping the table beside the IDs it maps mirrors plans. | ANSWERED — architect technical call |
| D9 | How does the validator read the archive? | Through the existing `DocumentLoader` parsing and the `DocsRootPath.EnumerateContainedFiles` symlink-safe walk, over `<docs-root>/archive/plans/` and `<docs-root>/archive/specs/` only. The entry grouping `EntryBelow` moves from `OpenWorkValidator` into `InventoryReachability`, so it is shared rather than copied. | No second frontmatter parser, which the duplicate-implementation lens would flag. Path derivation matches the 003 hint (`{DocsRoot}/archive/{plans,specs}/`). The default loader cannot be reused unchanged, because it excludes the archive. | ANSWERED — architect technical call |
| D10 | `docs-dev` description wording | Closeout leads. Working text: "Closes out finished plans and specs: harvests every recorded decision into canonical docs or ADRs, writes the closeout mapping, and archives the artifact. Also writes READMEs, API docs, ADRs, runbooks, and inline docs in Markdown/Mermaid. Use when a plan or spec needs its docs-dev closeout, or when the deliverable is a .md file. Do not use when the deliverable is a formal requirements/design/tasks spec to author (product-owner) rather than reference documentation or a closeout." The worker may refine it with the `writing-trigger-descriptions` skill, but must keep the pinned phrases (T4). | Scored against `DescriptionScorer`: trigger clause 35, negative boundary 20, opening 15, keywords 15, length about 495 chars, 15 points: 100/100. It passes `KW-AGENT-LINT-002` (trigger regex) and stays above `KW-AGENT-LINT-001`'s 50. The exclusion now names spec **authoring**, so spec **closeout** routes to `docs-dev`. | ANSWERED — architect technical call |
| D11 | Move `docs-dev` off `model-profile: fast`? | **Not pursued.** `model-profile` stays `fast`; no change in this plan. | The evidence (D3) shows closeouts were skipped, not done weakly. A bigger model does not fix skipping. The owner agrees. | ANSWERED — out of scope; owner position relayed by conductor 2026-10-25 |
| D12 | `docs/dash/architecture.md:359` links `../todo/kyberdash-opencode-plugin-live-verification.md`, which does not exist | Remove the dangling link. State the owner live gate as open and `[NOT LIVE-VERIFIED]`, with no todo link. Do not recreate the todo. | The #423 index row records that the owner-directed opencode todos were "since removed separately". Root `AGENTS.md`: create a todo only when the user accepts. The `[NOT LIVE-VERIFIED]` marker is the established form (rows 2026-10-19, 2026-10-23). Not caught today: body links are not validated. | ANSWERED — architect technical call |
| D13 | How do the backfills change archived history? | Annotate; do not rewrite. Keep each Decisions table and every original section. Insert a new first body line, `**Status: Archived <date> — closeout backfilled by plan 2026-10-25-docs-dev-harvest-closeout-gaps**`. Label the original status paragraph "Pre-closeout status (preserved as history)", text unchanged. Append a `## Completion summary` and a `## Closeout mapping`. Mark false index claims as corrected, as the #192 correction did. | Follows the precedent of `docs/archive/plans/2026-10-05-harvest-plan-decision-records.md` (the #192 false-claim correction). The 004 body-status check reads the **first** `**Status:` line, so history stays readable below it. | ANSWERED — architect technical call |
| D14 | Who edits agent and skill markdown, and the migration digest? | `docs-dev` edits `products/kyber-squad/**/*.md` text. `test-dev` re-pins `products/kyber-squad/migration/docs-dev.md` `final-body-sha256`. | Precedent: the 2026-10-05 plan put `docs-dev.md` edits on documentation work. `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` asserts `final-body-sha256 == agent.BodyDigest` (SHA-256 of the LF-normalized body). Any `docs-dev.md` body edit turns it RED. `docs-dev` has no process tool to compute the digest; `test-dev` does, and reads the actual value from the assertion. | ANSWERED — architect technical call |
| D15 | Older partial mappings (`2026-10-09-issue-312`, `2026-10-10-kyberdash-claude-code-turn-content`, `2026-10-09-issue-319`) | Out of scope. They are listed in the 004 baseline (Q1). A follow-up todo is filed only if the owner accepts one. | Packet scope. | ANSWERED — per packet 2026-10-25 |
| D16 | The in-flight `squad-model-presets` spec | Out of scope. It will meet 004 at its own closeout: its `design.md` already has a `## Decisions` section, and its closeout must add `## Closeout mapping`. | Packet scope. | ANSWERED — per packet 2026-10-25 |
| D17 | Which harness copies must change with canonical agent/skill text? | None are committed. At `a0862e7c`, `.github/` holds only `CODEOWNERS`, the PR template and `dependabot.yml`; there is no `.github/agents`, `.github/skills` or `.claude/agents`. Renderers project `products/kyber-squad` at `squad install/update`. The only propagated artifact is the migration record (D14). | The 2026-10-05 plan edited `.github/agents/docs-dev.agent.md`; that tree has since been retired (Hotshot golden retirement, 2026-10-21). `SquadCanonicalContentTests.ExpectedSourceHashManifest` pins historical source hashes only, not current text. | ANSWERED — finding, verified in worktree |
| D18 | The `product-owner` **skill** description says "…tasks, or closeout…", although `closeout-phase.md` says `docs-dev` performs closeout | Out of scope. Recorded as a follow-up. | The overlap is between a skill and an agent, not two agents, and `closeout-phase.md` redirects to `docs-dev`. Fixing it is a separate routing surface. | ANSWERED — architect technical call; follow-up only |
| Q1 | How does 004 avoid failing the ~115 legacy archived entries, and how do hosts adopt it? | **(a) Frozen baseline.** `ontology.archive-closeout.baseline: .kyber-weave/archive-closeout-baseline.txt` lists every archived plan entry and spec folder at `a0862e7c` except the three backfill targets. Every other archived entry, including every future archive, is checked. The rule is silent until a host adopts the key. The baseline file joins `policy.always-human`, and a stale baseline entry is a 004 error. | **(a) Frozen baseline — RECOMMENDED and chosen.** Config `ontology.archive-closeout.baseline: .kyber-weave/archive-closeout-baseline.txt` lists every archived plan entry and spec folder at `a0862e7c` except the three backfilled plans. Everything else, including every future archive, is checked. The rule is silent until a host adopts the key, following the `KW-CONFIG-REG` precedent "silent rather than failing on a structure it never asked for". The baseline file joins `policy.always-human`. A baseline entry naming a missing path is a 004 error, so the list cannot rot. Pros: exact, fail-closed, works the same locally and in CI, covers specs, no git. Cons: a one-time ~113-line list; hosts must opt in. **(b) Date cutoff** `ontology.archive-closeout.since: 2026-10-22` against the plan's filename date. Pros: one line. Cons: heuristic. A plan authored before the cutoff but archived later escapes; spec folders have no date, so specs need a second mechanism. **(c) Git-scoped.** Check only entries added, renamed or modified in the archive relative to the merge base (`--base`). Pros: no list; matches "the change that archives it". Cons: adds a git dependency to `docs validate`, which is filesystem-only today. CI checkout is shallow (the `skill-docs-gate` job that runs `docs validate --merge-ready` uses `actions/checkout` at default depth 1, `ci.yml:506-515`), so the workflow must change. It needs a fail-closed rule for runs without git, and any later edit to a legacy archived plan (even a link fix) pulls it into scope. | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); the owner may override at approve-and-execute, 2026-10-25 |
| Q2 | Should the conductor and `docs-dev` also refuse an archive move that has no closeout digest? (Text only; defense in depth beside 004) | **(a) Yes.** `docs-dev` returns `STATUS: CLOSEOUT_COMPLETE` or `STATUS: CLOSEOUT_BLOCKED`. Only the `docs-dev` closeout task moves a plan or spec into the archive; any other worker that meets 003 routes the closeout to `docs-dev`. The conductor delivers only with `CLOSEOUT_COMPLETE` plus a post-closeout `docs validate . --merge-ready` run (T5 text, T4 pins). | **(a) Yes — RECOMMENDED and chosen.** `docs-dev` gains a closeout digest: `STATUS: CLOSEOUT_COMPLETE`, with `ARTIFACT`, `MAPPED: <n>/<n> decision IDs`, `WAIVERS` and `VALIDATION`, or `STATUS: CLOSEOUT_BLOCKED` with `GAPS`. `execution-and-review.md` §Closeout and `autonomy.md` §Delivery state three rules: only the `docs-dev` closeout task moves a plan or spec into the archive; any other worker that meets `KW-DOC-LIFECYCLE-003` (for example, review remediation) routes the closeout to `docs-dev` instead of moving the file; and the conductor does not deliver (commit, push, PR) without `CLOSEOUT_COMPLETE` plus a post-closeout `docs validate . --merge-ready` run by a worker with a process tool. Pros: makes "only a successful closeout completes the objective" enforceable, and names the #425 shape (archive moved by a review fix). Cons: more `always-human` instruction text; prose is only as strong as agent compliance, which is why 004 stays the primary fix. **(b) No.** Rely on 004 alone. Pros: smaller diff. Cons: the conductor still cannot tell a skipped closeout from a done one until CI. | ANSWERED — orchestrator technical call under the owner's standing rule (make low-risk technical calls and report them); the owner may override at approve-and-execute, 2026-10-25 |

## Investigation findings

**Discovery:** `Discovery: codegraph 8, docs 2, direct reads 39` (plus about 55 grep/glob calls).
The CodeGraph and docs MCP servers answer from the main checkout at `9efb0288` (branch
`docs/squad-model-presets-spec`), not this worktree. Every cited line was confirmed in the
worktree. Direct reads of indexed code and their reasons:

- `DocsValidateCommand.cs`, `DocsValidateSettings.cs`: codegraph returned callers but not the bodies.
- `OpenWorkValidatorTests.cs`, `SquadCanonicalContentTests.cs` (four ranges), `SquadSpecGateCollapseTests.cs`, `SquadRustGoSpecialistTests.cs`, `AgentGovernanceTests.cs`: to match existing test patterns exactly, and for worktree confirmation.
- The `DocFixture` grep in `DocGovernanceTests.cs`: to learn whether fixtures load the archive. They do not.
- Grep in `OntologyConfig.cs`, `DocumentLoader.cs`, `OntologyYamlSection.cs`: to confirm in the worktree what codegraph returned from the other checkout.

Everything else read directly is not in either index: markdown under `products/` (outside the
docs corpus), `.kyber-weave/kyber-weave.yml`, `ci.yml`, and archived plans (the docs index
excludes the archive).

1. **Why the archive holds non-vocabulary statuses.** `OntologyConfig.DefaultStatuses` is
   `current, draft, needs-review, superseded` (`OntologyConfig.cs:30-31`). `DefaultExcludedSegments` starts with
   `ArchiveSegment = "archive"` (`:41-44`), and `DocumentLoader.Load` skips excluded paths (`DocumentLoader.cs:65`).
   So no archived document is schema-validated. Observed archive statuses: `archived`,
   `complete`, `completed`, `Archived`, `superseded`, and `draft` (the three backfill targets only).
   Active plans keep frontmatter `draft` through Ready ("per closed ontology — body Ready is
   authoritative", quoted in all three targets).
2. **How merge-ready works today.** `DocsValidateCommand.Execute` (`DocsValidateCommand.cs:27-33`)
   loads one `DocumentSet` (archive excluded), runs `DocSpecValidator`, `ConfigRegValidator`,
   `PlanInventoryValidator` (001), `TodoInventoryValidator` (002), and, only when
   `MergeReady`, `OpenWorkValidator` (003). CI runs `docs validate . --merge-ready --format table`
   (`ci.yml:580-585`). `review gates`' `docs-validate` gate runs without `--merge-ready`
   (`kyber-weave.yml:90-92`).
3. **Test fixtures never see the archive.** `DocFixture.Load()` is `new DocumentLoader(Root).Load()`
   (default exclusions), so `OpenWorkValidatorTests.ArchivedPlansAndSpecificationsAreClosed`
   passes without loading the archived files at all. The new validator must read the archive
   itself, and its tests must assert against files the fixture writes there.
4. **No committed harness copies** (D17). `SquadSourceLoader.ParseAgent` computes
   `BodyDigest = Sha256(frontmatter.Body)` (body only). `SquadCanonicalContentTests`
   `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` (`:769-803`) compares
   it with `products/kyber-squad/migration/docs-dev.md` `final-body-sha256`. A description
   (frontmatter) edit does not move the digest; any body edit does.
5. **Existing text pins.** `LoadDocsDevDefinesCloseoutMappingTableAndWaiverConventions`
   (`SquadCanonicalContentTests.cs:543-553`) pins "Plan closeout", "mapping table", "ADR waiver",
   "Documentation waiver" in the `docs-dev` body. Keep those phrases. Description pins have
   precedent in `SquadRustGoSpecialistTests` (`:72-115`). Conductor reference pins have
   precedent in `SquadSpecGateCollapseTests` (a `Read(...)` + `AssertAll` helper pattern).
6. **Description gates for agents.** `AgentSyncLinter.LintSet` raises `KW-AGENT-LINT-002` when
   `RoutingLinter.TriggerClauseRegex` does not match, and `KW-AGENT-LINT-001` when
   `DescriptionScorer.Score(...).Total < 50`. `skill lint --min-desc-score 70` gates only
   `.apm/skills/*`, not squad agents, so the new test pins the score ≥ 70 itself.
7. **Spec path gaps.** `design-phase.md` has no Decisions-table requirement.
   `closeout-phase.md` (Closeout steps 1-5) and `docs-dev.md` §Specification closeout
   (steps 1-7) have no mapping table and no waiver rules. `spec-path.md` `SPEC_READY`
   (`:18`) does not check a Decisions table, while `plan-path.md:18` does for plans.
   `intent-alignment.md:49-54` speaks of "the archived plan's durable decisions" only.
   `execution-and-review.md:88-95` §Closeout lists no mapping for spec-backed work.
   `docs/specs/README.md` has no Decisions or Closeout Policy section.
8. **Backfill target facts (verified in worktree).**
   - #423 plan: Decisions IDs D1–D7, Q1–Q3, AP1, R1, R2 (§10). §11 rows mark the
     architecture contract `RESIDUAL`. The section has since landed:
     `docs/dash/architecture.md` "OpenCode plugin span/turn envelope (`kyberdash-opencode`)",
     lines 286-360. Its last paragraph links a missing todo (D12). The index row still says
     "RESIDUAL per plan §11 … no section landed", which is stale.
   - #425 plan: IDs D1–D7, AP1 (§4). §8 is a forecast. The index row's "mapped in the plan's
     §8 Closeout" is false. Likely gaps to verify: D6 (`opencode.context.skills` as a
     Skills-view listing source; `architecture.md` "Skills view contract" at :747 has no
     opencode mention) and D1 (the standalone raw-JSON plugin on port 8080 is rejected).
   - #426 plan: IDs D1–D11 (§2). Expected destinations: ADR 0019 amendment, and
     `docs/kyber-squad/requirements.md` / `onboarding.md` (`pi-extensions` and
     `pi-mcp-adapter` both occur there). The index row is malformed: the Archive Date column
     holds `KyberSquad / test-first, autonomous` and there is no canonical-docs cell. The
     archive date must come from git (merge of PR #426 / commit `6dd4ce3f`); the conductor
     supplies it in the packet.
9. **Archive inventory at `a0862e7c`.** 115 entries under `docs/archive/plans/`: plan
   files, one `.json` evidence file, and the folder `2026-09-06-kyberdash-spine-tasks/`.
   Two spec folders under `docs/archive/specs/`: `kyberdash/` and
   `kyberdash-context-surfaces/`. `docs/archive/specs/kyberdash/` is not listed in
   `docs/specs/README.md`; that is an observation, out of scope.
10. **Pre-approval checks (conductor-run in this worktree, 2026-10-25).**
    - `docs validate .`: no findings.
    - `docs drift .`: no findings, against a CodeGraph index built in the worktree.
    - Confirmed: the `ci.yml` `skill-docs-gate` job checks out at default depth.
    - Confirmed: the `archive` segment is excluded by default (`OntologyConfig.cs:43-44`).

## Gate contract (`KW-DOC-LIFECYCLE-004`)

**Rule.** `KW-DOC-LIFECYCLE-004` — Error — an archived plan or specification lacks closeout
evidence. Raised only by `docs validate --merge-ready` (D6), and only once the host adopts
`ontology.archive-closeout` with its baseline file (Q1 = a).

**Scope.** Entries directly below `<docs-root>/archive/plans/` and `<docs-root>/archive/specs/`,
where `<docs-root>` is `OntologyConfig.DocsRoot`.

- A plan entry is a `*.md` file. For a folder, it is the folder's `README.md`.
- A spec entry is a folder; its mapping document is `design.md`.
- Non-markdown files are ignored.
- Entries listed in the baseline file are skipped.
- A baseline line naming a path that does not exist is itself a 004 error, with the hint "remove the stale baseline entry".

**Conditions.** Each failed condition is named in the entry's single finding.

1. **C1 Archive status.** The frontmatter `status` of every document in the entry is `archived`,
   `complete` or `superseded` (case-insensitive) (D7).
2. **C2 No live body status (plans).** The first body line starting `**Status:` does not
   continue with `Draft`, `Ready`, `In progress`, `Blocked` or `Review required`
   (case-insensitive). If there is no such line, C2 is not raised.
3. **C3 Decisions table.** The plan document (or the spec's `design.md`) has a `##` heading
   whose text is `Decisions`, optionally preceded by a `N.` section number. Its section
   (`DocumentLoader.SplitSections`) holds at least one Markdown table with at least one
   body row. The decision IDs are the first-cell values of the body rows, with `**` and
   backticks stripped, matching `^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*$`.
4. **C4 Closeout mapping table.** The same document has a `##` heading `Closeout mapping`
   (optionally numbered, case-insensitive) whose section holds a Markdown table.
5. **C5 Every decision ID is mapped.** A mapping row maps the IDs listed in its first cell
   (comma-separated lists allowed; ranges such as `D1–D7` are **not** expanded) when the
   rest of the row contains either:
   - (i) a relative Markdown link that resolves (`DocGraphProjection.ResolveLink`, relative
     to the document) to an existing file outside `<docs-root>/archive/`; or
   - (ii) the phrase `Documentation waiver` (case-insensitive).

   A row containing the word `RESIDUAL` maps nothing. `ADR waiver` alone maps nothing. The
   hint lists the unmapped IDs.
6. **C6 Residuals are tracked.** Every line in the Closeout mapping section containing the
   word `RESIDUAL` also contains a relative link to an existing document inside the
   `todo-index` folder.

**The three real shapes (D3) fail as follows:**

- Unchanged move (#425): C1, C2, C4, C5.
- Added straight into the archive while Ready (#426): C1, C2, C4, C5.
- Archived with an admitted residual (#423): C1, C2, C4, C5 and C6. Each test below isolates
  its distinguishing condition with every other condition satisfied.

**Message and hint.** One finding per failing entry:
`Archived plan '<entry>' lacks closeout evidence: <conditions>.` The hint names the fix, for
example "Add `## Closeout mapping` rows for D3, Q2", "Set `status: archived`", or "Link a
todo for the RESIDUAL row, or harvest it". The finding's file is the plan document (specs:
`design.md`, or the folder `README.md` when `design.md` is missing).

## Test contract

Runner prefix for every .NET row: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj
-c Release --filter <filter>`, after `dotnet build KyberWeave.sln -c Release`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | No-test task (API skeleton) | `dotnet build KyberWeave.sln -c Release` + full `dotnet test` | `ArchiveCloseoutValidator` and `MissingCloseoutEvidence = "KW-DOC-LIFECYCLE-004"` exist; `Validate()` returns an empty report; the config key parses but has no effect; nothing is wired into `DocsValidateCommand`. | n/a. A skeleton so T2's tests compile and fail on behavior rather than as a compile error that would block T4/T6 in the same test project. | Build has 0 warnings; the existing suite is unchanged and green. |
| T2 | New `tests/KyberWeave.Tests/ArchiveCloseoutValidatorTests.cs`; additions to `tests/KyberWeave.Tests/OntologyConfigTests.cs` | `--filter "FullyQualifiedName~ArchiveCloseoutValidatorTests"`, then `--filter "FullyQualifiedName~OntologyConfigTests"` | Each Gate contract condition, plus scope: <br>(1) `AnUnchangedMoveIntoTheArchiveIsKWDOCLIFECYCLE004` (#425 shape); <br>(2) `APlanArchivedWhileItsBodySaysReadyFails` (C2 isolated; #426 shape); <br>(3) `AnArchivedPlanWithAnUntrackedResidualFails` (C6 isolated; #423 shape); <br>(4) `ADecisionRowMarkedResidualIsNotMapped`; <br>(5) `ADraftFrontmatterStatusFails` (C1 isolated); <br>(6) `AMissingDecisionsSectionFails`; <br>(7) `AMissingCloseoutMappingSectionFails` (incl. a forecast `## Review and closeout` heading that does **not** satisfy C4); <br>(8) `AnUnmappedDecisionIdIsNamedInTheHint`; <br>(9) `AnAdrWaiverAloneDoesNotMapADecision`; <br>(10) `ADocumentationWaiverMapsADecision`; <br>(11) `AMappingLinkMustResolveToAnExistingNonArchivedFile`; <br>(12) `AResidualLinkedToAnExistingTodoPasses`; <br>(13) `ACommaSeparatedIdCellMapsEachIdButARangeDoesNot`; <br>(14) `ACompleteCloseoutPasses` (status `archived` and `complete` both accepted); <br>(15) `AnArchivedSpecificationFolderIsOneFindingAndMapsFromDesign`; <br>(16) `ACompleteArchivedSpecificationPasses`; <br>(17) `ABaselinedEntryIsSkipped`; <br>(18) `AStaleBaselineEntryIsAnError`; <br>(19) `AHostThatHasNotAdoptedTheRuleSeesNothing`; <br>(20) `DocsValidateReportsMissingCloseoutEvidenceOnlyWithMergeReady` (theory false→exit 0 without 004, true→exit 1 with 004; mirrors `DocsValidateReportsOpenWorkOnlyWithMergeReady`); <br>config tests: the key parses, a missing baseline file is a config error, a baseline entry outside the archive is rejected. | Every new test fails against the T1 skeleton: an empty report where a finding is expected, or no 004 on the `--merge-ready` theory. Tests (14), (16), (17), (19) are the passing-side controls and must be shown failing to discriminate. Record each test's failing assertion. | The same tests pass, with no assertion weakened. `OpenWorkValidatorTests` stays green and unedited. |
| T3 | Same files as T2 | Same as T2, then the full suite | Behavior per the Gate contract. | T2's recorded RED. | All T2 tests green. Full suite green. 0 warnings (`TreatWarningsAsErrors`, `AnalysisMode=all`). |
| T4 | New `tests/KyberWeave.Tests/SquadCloseoutHarvestContractTests.cs` | `--filter "FullyQualifiedName~SquadCloseoutHarvestContractTests"` | Pins the phrases listed under T5. Description: `DescriptionScorer.Score(...).Total >= 70`; no `KW-AGENT-LINT-001`/`-002` from `AgentSyncLinter.LintSet` over an `AgentModel` carrying it (the `AgentGovernanceTests` construction); `IndexOf("closeout", OrdinalIgnoreCase) < IndexOf("README", Ordinal)`. Body: the first bullet after `You own:` contains `closeout`. | Run against the unedited tree: each closeout-first, spec-parity and closeout-digest (Q2) assertion fails. The current description scores 100, so its score assertion passes as a regression guard; say so. | Same tests pass after T5 with no edit to the test file. |
| T5 | Text files only (see task) | — (verified by T6) | — | T4's recorded RED. | T6 green. |
| T6 | T4's file + existing `SquadCanonicalContentTests` | `--filter "FullyQualifiedName~SquadCloseoutHarvestContractTests"` and `--filter "FullyQualifiedName~SquadCanonicalContentTests"` | Pins green. `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest` is green after the re-pin. `LoadDocsDevDefinesCloseoutMappingTableAndWaiverConventions` stays green. | The digest test fails after T5's body edit (expected value is the old `6b0c5703…`). Record the actual value it reports. | Both classes green. `final-body-sha256` equals the reported `BodyDigest`. Full suite green. |
| T7 | No-test task (docs) | `docs validate .` + `docs drift .` (T10 runs them) | `governance.md`, `rule-reference.md`, `configuration.md` describe 004 truthfully. | n/a | Zero findings (T10). `governance.md` `code-refs` resolves `ArchiveCloseoutValidator` in a refreshed index. |
| T8 | Real corpus (no new test) | `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- docs validate . --merge-ready --format json` | Adoption works on the real archive. | **Real-corpus RED:** exactly three `KW-DOC-LIFECYCLE-004` findings (the three backfill targets) plus one `KW-DOC-LIFECYCLE-003` for this active plan. Nothing else. | Same run after T9 (in T10). |
| T9 | No-test task (docs) | — (verified by T10) | Three backfilled closeouts meet the Gate contract. | T8's real-corpus RED. | T10. |
| T10 | Real corpus | `docs validate . --merge-ready` + `docs drift .` | Gate green on the real archive. | T8. | Zero 004 findings. The only finding is 003 for this still-active plan. `docs drift` zero findings. |
| T11 | No-test task (closeout) | — (verified by T12) | This plan's own closeout meets 004 (dogfood; not baselined). | n/a | T12. |
| T12 | Real corpus + gates | `docs validate . --merge-ready` + `docs drift .` | After this plan is archived, the corpus is clean. | n/a | Zero findings of any rule. This plan is checked by 004 and passes. |

Changing an approved Test-contract row, including weakening an assertion to reach green, is
a scope change: the plan returns to Draft and the conductor relays reapproval.

## Tasks

Required skills are named, not agents. Every developer task ends with the worker's own
`task-gate` run; a failing gate goes straight back to the same worker.

### T1 — Gate API skeleton (no behavior)

- **Objective:** Make T2's tests compile so their RED is behavioral.
- **Files/symbols:**
  - New `src/KyberWeave.Core/Docs/Validation/ArchiveCloseoutValidator.cs`: `public sealed class ArchiveCloseoutValidator(string repoRoot, KyberWeaveConfig config)`, `public const string MissingCloseoutEvidence = "KW-DOC-LIFECYCLE-004"`, and `public DiagnosticReport Validate()` returning an empty report. Its `<remarks>` record why (D3, D4).
  - Config plumbing per Q1 (a): `OntologyYamlSection.ArchiveCloseout` (`baseline` path), `OntologyConfigLoader.Merge`, and an `OntologyConfig.ArchiveCloseout` property (null = not adopted).
- **Acceptance:** Release build has 0 warnings; the full suite is unchanged and green; nothing is wired into `DocsValidateCommand`.
- **Dependencies:** none.
- **Required skills:** `csharp-dev`; **<csharp-coding-standard>**.

### T2 — RED: gate tests

- **Objective:** Author the T2 Test-contract tests against the skeleton.
- **Files:**
  - New `tests/KyberWeave.Tests/ArchiveCloseoutValidatorTests.cs`. Use `DocFixture` (it writes files; the validator reads the archive itself) and the `Index`/`Work` builder style of `OpenWorkValidatorTests`. Fixtures that adopt the rule write `.kyber-weave/kyber-weave.yml` with `ontology: { docs-root: 6-Docs, archive-closeout: { baseline: … } }`.
  - `tests/KyberWeave.Tests/OntologyConfigTests.cs`: the config cases.
- **Acceptance:** Every test listed in T2 exists and fails for the intended missing behavior, with the failing assertions recorded. The project compiles. `OpenWorkValidatorTests` is untouched.
- **Dependencies:** T1.
- **Required skills:** `test-dev`; **<test-coding-standard>**.

### T3 — GREEN: gate implementation and wiring

- **Objective:** Implement the Gate contract.
- **Files/symbols:**
  - `ArchiveCloseoutValidator.cs`: archive walk through `DocumentLoader` parsing and `DocsRootPath.EnumerateContainedFiles` (D9), and conditions C1–C6 with hints.
  - `InventoryReachability.cs`: receives `EntryBelow`, moved from `OpenWorkValidator.cs`, which then calls it.
  - `DocumentLoader.cs`: only if a narrowly scoped internal entry point is needed to parse the archive subtree. No change to default exclusions.
  - `DocsValidateCommand.cs`: under `if (settings.MergeReady)`, also run `new ArchiveCloseoutValidator(settings.Path, config.WithOntology(ontology)).Validate()`.
  - `DocsValidateSettings.cs`: the `--merge-ready` description names 003 and 004.
  - `<remarks>` explain why the archive is read here though the loader excludes it, and why `ADR waiver` alone does not map.
- **Acceptance:** T2 tests green, full suite green, 0 warnings. Diagnostics carry hints (house style). No new dependency.
- **Dependencies:** T2.
- **Required skills:** `csharp-dev`; **<csharp-coding-standard>**.

### T4 — RED: closeout-harvest text pins (A, B, Q2)

- **Objective:** Pin the A/B/Q2 text contract before it is written.
- **Files:** New `tests/KyberWeave.Tests/SquadCloseoutHarvestContractTests.cs`, following the `SquadSpecGateCollapseTests` `Read(...)`/`AssertAll` pattern. Use `SquadSourceLoader.Load` for the `docs-dev` description and body. Pins are `StringComparison.Ordinal` unless the T4 row says otherwise. The exact phrases are listed in T5. Section-scoped pins extract the text between a heading and the next `## `.
- **Acceptance:** RED recorded per the T4 row.
- **Dependencies:** none (separate test file; compiles independently of T2).
- **Required skills:** `test-dev`; **<test-coding-standard>**.

### T5 — GREEN: agent, skill and policy text (A, B, Q2)

- **Objective:** Make T4 green with truthful, minimal text.
- **Files and pinned phrases:**
  - `products/kyber-squad/agents/docs-dev.md`:
    - (A) `description` per D10. The pins require the phrases `Use when`, `Do not use when`, `decision`, `README`, `ADR`, `runbook`, `product-owner`, and "closeout" before "README". `model-profile` is unchanged (D11).
    - (A) Scope: the closeout bullets come first, and the first `You own:` bullet contains `closeout`.
    - Plan closeout: keep `Plan closeout`, `mapping table`, `ADR waiver`, `Documentation waiver`. Name the section `## Closeout mapping` (one row per decision ID, a relative link or `Documentation waiver`). State the archive status rule "`status: archived`" plus the first body line `**Status: Archived`. Keep "do not archive" in step 7.
    - (B) Specification closeout: add `## Closeout mapping` in `design.md`, `decision ID`, `ADR waiver`, `Documentation waiver`, `do not archive`.
    - (Q2) Completion digest: add `STATUS: CLOSEOUT_COMPLETE` and `STATUS: CLOSEOUT_BLOCKED`.
  - `products/kyber-squad/skills/product-owner/references/design-phase.md` (B1): a permanent `## Decisions` table with `ID`, `Question`, `Answer / decision`, `Mitigating and supporting information`, `Status / approval provenance`, "preserved intact" through archive.
  - `products/kyber-squad/skills/product-owner/references/closeout-phase.md` (B2): `## Closeout mapping`, `ADR waiver`, `Documentation waiver`, and "do not archive" when any decision ID is unmapped. Digest gains `MAPPED:`.
  - `products/kyber-squad/skills/product-owner/references/tasks-phase.md` (:40): the closeout task names the decision mapping.
  - `products/kyber-squad/skills/code-review/references/lenses/intent-alignment.md` (B3, :49-54): "archived plan or specification". Add as a `critical` finding: a draft/Ready status, a missing `## Closeout mapping`, or an untracked `RESIDUAL` on an archived artifact.
  - `products/kyber-squad/agents/conductor/references/spec-path.md` (B3, :18): `SPEC_READY` verifies `design.md` carries the permanent `## Decisions` table with complete fields including `Mitigating and supporting information`.
  - `products/kyber-squad/agents/conductor/references/execution-and-review.md` (:88-95): spec-backed closeout names the `closeout mapping`. Q2 adds the archive-move rule, `CLOSEOUT_COMPLETE`, and the post-closeout `docs validate . --merge-ready` run.
  - `products/kyber-squad/agents/conductor/references/autonomy.md` (Q2, :44): Delivery requires `CLOSEOUT_COMPLETE`.
  - `docs/specs/README.md` (B4): a "Specification Decisions and Closeout Policy" section mirroring `docs/plans/README.md`'s.
  - `docs/plans/README.md`: policy section only. Name `## Closeout mapping`, `KW-DOC-LIFECYCLE-004`, and the D7 status convention. Rows are untouched.
  - `conductor.md` body is **not** edited, so its migration digest is unaffected.
- **Acceptance:** T6 green. `docs validate .` clean for the two READMEs (run in T10). No other files.
- **Dependencies:** T4.
- **Required skills:** `kyber-weave-docs`, `writing-trigger-descriptions`.

### T6 — GREEN verification and migration digest re-pin

- **Objective:** Prove T5 and re-pin the digest.
- **Files:** `products/kyber-squad/migration/docs-dev.md` (`final-body-sha256` only).
- **Acceptance:** Per the T6 row. If a pin fails because T5's text deviates, return it to T5's worker. Do not edit the test.
- **Dependencies:** T5.
- **Required skills:** `test-dev`.

### T7 — Gate documentation

- **Objective:** Document 004 where the gates are documented.
- **Files:**
  - `docs/docgraph/governance.md`: add a rule-table row. Rewrite the 003 paragraph's "not semantic proof" sentence to point at 004 as the mechanical evidence check. Describe C1–C6, the D7 status convention, adoption and the baseline. Add `ArchiveCloseoutValidator` and `OpenWorkValidator` to `code-refs`. Bump `last-reviewed`.
  - `docs/ci-pipelines/rule-reference.md`: a 004 row.
  - `docs/configuration.md`: the `ontology.archive-closeout` key under "Ontology keys".
- **Acceptance:** Truthful to T3's shipped behavior. `docs validate` and `docs drift` zero findings (T10).
- **Dependencies:** T3.
- **Required skills:** `kyber-weave-docs`.

### T8 — Adopt the gate on the real repository; record real-corpus RED

- **Objective:** Turn 004 on here and prove it catches the three real failures.
- **Files:**
  - `.kyber-weave/kyber-weave.yml`: `ontology.archive-closeout.baseline`, and the baseline path added to `policy.always-human`, with a comment giving the reason.
  - New `.kyber-weave/archive-closeout-baseline.txt`: every entry under `docs/archive/plans/` and `docs/archive/specs/` at `a0862e7c`, **excluding** the three backfill targets. A header comment cites this plan and Q1.
  - `.github/workflows/ci.yml:580-581`: the comment names 003 and 004. Comment only.
- **Acceptance:** The T8 real-corpus RED recorded verbatim (three 004 findings, one 003).
- **Dependencies:** T3.
- **Required skills:** `github-devops`.

### T9 — Backfill the three closeouts (D)

- **Objective:** Real closeouts for the three plans, per D13.
- **Files:**
  - The three archived plans.
  - Canonical docs where a decision's content is missing: expected `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`, `docs/dash/runbook.md`, `docs/adr/0019-pi-native-subagents-and-primary-lowering.md`, `docs/kyber-squad/requirements.md`, `docs/kyber-squad/onboarding.md`.
  - `docs/plans/README.md`: the three rows only.
- **Work:**
  1. Map every decision ID (13 + 8 + 11) to the exact canonical section after reading it in the worktree. When the content is missing, add it rather than waive it. Candidates: #425 D6 (Skills view contract) and D1. Process and approval rows (mode, autonomy, intake route, AP/R approvals, D4 VOID, D11 test placement) may take a `Documentation waiver`, with a reason, when they carry no surviving invariant.
  2. Fix `docs/dash/architecture.md:359` per D12.
  3. Set `status: archived` and `last-reviewed`. Insert the new first status line and label the history (D13).
  4. Add a `## Completion summary` (what shipped, from the plan's own evidence, with no new claims) and a `## Closeout mapping`.
  5. Reconcile the rows: #423 row "RESIDUAL … no section landed" becomes the landed section, annotated "corrected 2026-10-2x". #425 row: annotate the false "mapped in §8" claim as corrected. #426 row: rebuild it with the archived-table columns, using the archive date from the conductor packet.
  6. Leave #426's deployment gap and the owner live gates as open owner items, marked `[NOT LIVE-VERIFIED]` or "open owner item", not `RESIDUAL`, and file no todo unless the owner accepts one.
- **Acceptance:** Each plan meets C1–C6, proven in T10. No Decisions table or original section is altered beyond D13's labels.
- **Dependencies:** T5 (shared `docs/plans/README.md`; final heading convention), T8 (real-corpus RED recorded first).
- **Required skills:** `kyber-weave-docs`.

### T10 — Real-corpus GREEN

- **Objective:** Prove C and D on the real archive.
- **Acceptance:** Per the T10 row, plus the full .NET suite green.
- **Dependencies:** T6, T7, T9.
- **Required skills:** `test-dev`.

### Review

The conductor dispatches `code-reviewer` once over the whole change after T10. The change
edits `products/kyber-squad/agents/**` and `.kyber-weave/kyber-weave.yml`, both
`policy.always-human`, so the verdict will be `NEEDS_HUMAN` under `KW-REVIEW-008`. In
supervised mode that is terminal until the owner reviews (Risks).

### T11 — `docs-dev` closeout of this plan (dogfood)

- **Objective:** Close this plan under the rules it creates.
- **Files:** This plan, `docs/plans/README.md` (this row), and canonical docs as the mapping requires (expected: `governance.md`, `rule-reference.md`, `configuration.md`, `docs-dev.md`, the policy READMEs).
- **Work:**
  - Add a `## Completion summary` and a `## Closeout mapping` with one row per ID in `## Decisions`, including the AP1 approval row added at finalization.
  - Set `status: archived` and the first body line `**Status: Archived …`.
  - Move the plan to `docs/archive/plans/`. **Not** added to the baseline.
  - Return `STATUS: CLOSEOUT_COMPLETE` (Q2).
- **Dependencies:** Review resolved (APPROVE or owner resolution of `NEEDS_HUMAN`).
- **Required skills:** `kyber-weave-docs`.

### T12 — Post-closeout gate run

- **Objective:** Evidence that the archived plan passes 004 and that the corpus is clean before delivery.
- **Acceptance:** Per the T12 row.
- **Dependencies:** T11.
- **Required skills:** `test-dev`.

## Dependency graph

```text
T1 ─► T2 ─► T3 ─┬─► T7 ──────────────┐
                └─► T8 ─┐            │
T4 ─► T5 ─┬─► T6 ───────┼────────────┼─► T10 ─► Review ─► T11 ─► T12
          └─────────────┴─► T9 ──────┘
```

- First wave: T1 and T4. Then T2 ∥ T5; T3 ∥ T6; T7 ∥ T8.
- T9 waits on T5 (shared `docs/plans/README.md`) and T8 (real-corpus RED first).
- File scopes never overlap between concurrently ready tasks.
- `review.max-concurrent-workers: 4` is never exceeded: at most two tasks are ready at once.

## Risks

| Risk | Mitigation |
|---|---|
| Always-human paths force `NEEDS_HUMAN` (`KW-REVIEW-008`). | Expected. The owner reviews before T11. No remediation loop is triggered: reserved-path escalation bypasses it (`execution-and-review.md:86`). |
| The gate rejects legitimate closeouts because of heading or table variants. | One canonical heading per section, pinned by tests (7) and (14). The hint names the exact fix. `docs-dev` text names the heading (T5). |
| The baseline is used to silence the gate. | The baseline file is in `policy.always-human`; stale entries are errors (test 18); new archives are never baselined (T11 dogfoods this). |
| Host repositories break on upgrade. | The rule is silent until adopted (test 19), following the `KW-CONFIG-REG` precedent. |
| The closeout happens after review, so the council never sees the archive move. | T12 runs `--merge-ready` after closeout. Q2 makes that run part of the conductor's delivery rule. CI runs `--merge-ready` on the PR. |
| The full suite is RED between T5 and T6 (migration digest). | Sequenced and expected. T6 follows T5 directly. Other workers' task-gates are build/format only. |
| `docs drift` needs `ArchiveCloseoutValidator` in the CodeGraph index. | Refresh the index after T3. `docs drift` is a local gate; CI defers it (`ci.yml:587-589`). |
| Backfill uncovers missing canonical content and grows. | Bounded to the 32 IDs of three plans. Add the content; never waive "not documented". |
| The commits `8beb97ae`/`6dd4ce3f` are relayed, not verified here. | The file states corroborate every shape (D3). T9 takes the #426 archive date from git via the conductor. |
| The archive walk follows a symlink out of the tree. | Reuse `DocsRootPath.EnumerateContainedFiles` (D9). |

## Out of scope

- Changing `docs-dev`'s `model-profile` (D11, not pursued).
- Partial mappings in `2026-10-09-issue-312`, `2026-10-10-kyberdash-claude-code-turn-content`,
  `2026-10-09-issue-319` (D15; baselined; possible follow-up).
- The `squad-model-presets` spec (D16).
- The `product-owner` skill description's "or closeout" overlap (D18; follow-up).
- #426's `squad install/update --global` deployment gap (open owner item).
- `docs/archive/specs/kyberdash/` missing from `docs/specs/README.md`, and that README's
  outdated `6-Docs/specs/` note (observations).
- Review-loop skills outside this repository that may move plans (for example, a user-level
  `kilo-review-loop`); 004 catches their output mechanically.
- `scripts/fast-loop-classes.txt` regeneration for the new test classes.
- Any dash/ change; `./scripts/update-loop.sh` is not required, because the release path,
  packaging and self-updater are untouched and only canonical text that already ships in the
  pack changes.

## Verification gates

Before the review (T10) and again after closeout (T12), from the root `AGENTS.md`:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

- `review gates` adds the `kyber-weave-cli` skill trio, InspectCode, duplicates, and the TS
  gates.
- Before T11, `docs validate --merge-ready` is expected to report exactly one finding:
  `KW-DOC-LIFECYCLE-003` for this active plan. After T11 it reports zero.
- `scripts/fast-loop.sh` is not evidence.

## Review and closeout

- Whole-change review: `code-reviewer`, once, after T10. `NEEDS_HUMAN` is expected (Risks).
- Closeout: T11 (`docs-dev`, dogfood), then T12.
- The permanent `## Decisions` table above is preserved intact through archive.
