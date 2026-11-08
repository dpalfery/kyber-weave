---
id: plans/2026-11-07-conductor-run-speed-project-knowledge
title: Project knowledge and onboarding (conductor run-speed, slice 7)
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
created: 2026-11-08
last-reviewed: 2026-11-08
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - ReviewCouncil
  - DocGraph
---

# Project knowledge and onboarding (conductor run-speed, slice 7)

**Status: Review required.** This plan is **not closeable as delivered**, and it has not been
archived. Three acceptance criteria are unmet in this tree, and the closeout below records what was
verified rather than claiming the rest. **T38 is not implemented here**: there is no `onboarding`
setting reader, no `docs onboarding <done|declined>` verb (`src/KyberWeave.Cli/Commands/Docs/`
holds no command for it and `Program.cs` registers none), and `RunStartPlanner` evaluates no
onboarding check, so `OnboardingCommandTests`, `RunStartOnboardingTests` and the `ConfigReg` cases
that name the setting — the RED classes T36 wrote — are failing. The only trace of D6 is
`onboarding: done` in `.kyber-weave/kyber-weave.yml`, which nothing reads. **T39's onboarding step
is not written**: the `kyber-docs-onboarding-and-assessment` skill carries none (D7). **T41's
evidence is absent**: no `.agent_scratchpad/T41/format-include-check.md` exists in this tree and no
run on `ubuntu-latest` or `windows-latest` is cited anywhere in it. T37, the rest of T39, and T40
are delivered and harvested; see [Closeout](#closeout).

It was approved for execution under AP1 with resolved autonomy `autonomous`, and that approval is what the decision rows below carry. It is carved from the parent design for the conductor run-speed work, which lives on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) and is deliberately not in this tree. Per the parent's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only slice 7's decision rows, design, harvest rows, ADR disposition and tasks. The parent design is archived by the last slice's pull request with a mapping to the children; this plan is archived in this slice's own pull request, which has not happened. Every decision is answered below with its provenance; the parent's AP1 approval covers the Test contract this plan restates and carries to this child through `AUTONOMOUS_APPROVAL` per D12.

## Problem and goal

The parent design's measured runs showed project knowledge living where it is re-sent on every round trip and drifts per project: portable agent and skill files carry repository-specific paths and commands — in this tree, `KyberWeave.Tests` and `src/KyberWeave` appear on 12 lines under `products/kyber-squad` (11 instruction lines in this slice's file list plus one roster comment, D62) — while the parent design's D5 rule ("project content goes in `review.gates`, `review.task-gates`, and a `## Completion` section in each coding standard") has no home for the third kind yet, because no standard has a `## Completion` section (verified: none of the 14 templates or this repository's 7 standards carries one). A repository's machine checks live only in its configuration: a task whose files select no gate reports `TASK_GATE: NONE_DECLARED`, which is a valid result but no executed evidence, and in this repository `docs-dev` — the role every completed change passes through — declares no task gates at all, so its gate reports `KW-REVIEW-027` and runs zero gates (verified, D60). A host scaffolded by `kyber-weave docs init` gets whole-suite test gates from the shipped catalogue without the `full-suite: true` key, so its developer agents can run the full suite that [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) reserves for the council and CI (verified: the catalogue and block writer emit no `full-suite`). And there is no setup flow: nothing asks a new repository which gates and standards it wants, so the defaults go unreviewed.

The goal of this slice (the parent's fix 11): **project knowledge out of portable files** — runnable checks into `review.gates` and `review.task-gates`, and how-to-finish knowledge into a `## Completion` section in every coding standard, fetched by topic only when a worker is finishing (D4, D5, D31); **an `onboarding` setting and flow** — `run start` checks it before anything else and stops when unset, `kyber-docs-onboarding-and-assessment` walks each technology's defaults and `kyber-weave docs onboarding <done|declined>` writes the value (D6, D7, D33, D34, D40); **catalogue repair** — whole-suite gates carry `full-suite: true`, targeted gates do not, and the generated command is never wrapped in `proc-guard.sh` (D32); **this repository's own gate choices settled** — `sql-database-architect` and `github-devops` get gate sets that match the files they edit, workflow, shell and config edits select at least one gate, `onboarding: done` is set, and `docs-dev` gains its first task-gates entry (D60); and **the `dotnet format --include` leniency confirmed on Linux and Windows** (T41), because it was only ever checked on macOS.

The slice completes three open todos: [docs-init-emit-full-suite-gate-key](../archive/todo/docs-init-emit-full-suite-gate-key.md) (T37), [task-gates-workflow-and-shell-edits](../archive/todo/task-gates-workflow-and-shell-edits.md) (T37 plus T40), and [task-gate-config-review](../todo/task-gate-config-review.md) (T40, plus T41's Linux and Windows evidence — the parent's own condition: without that evidence the todo is narrowed to that one question and left open, never archived).

## Decisions

Rows D4, D5, D6, D7, D12, D13, D31, D32, D33, D34, D35, D40 and AP1 are this slice's rows from the parent design's decision ledger, copied verbatim per the parent's own D12 carve rule. In D12 and AP1, "this file" and "this plan" name the parent design file on its branch, not this child plan. D59 to D62 record the scope and delivery adjustments this slice carries, verified against this tree.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D4 | How do workers get coding standards? | Only through `docs_get_coding_standard_for_technology`. Workers do not read plan files. | A file read is allowed only when the tool is missing. | user: explicit decision 2026-11-07 |
| D5 | Where does project-specific knowledge live? | Out of portable agents and skills. There are no technology reference files in portable skills. Project content goes in `review.gates`, `review.task-gates`, and a new `## Completion` section in each coding standard. | [docs/standards/README.md](../standards/README.md) already states the rule. | user: explicit decision 2026-11-07 |
| D6 | How does the onboarding setting behave? | New `onboarding` setting. At run start, if unset, the run stops and suggests `kyber-docs-onboarding-and-assessment`. If the user says no, ask whether to ask again. If yes, write nothing and continue. If no, set the value and continue. Autonomous runs stop as well, asking nothing and leaving the setting unchanged. This repository gets the done value in the same change. | The owner asked for `done`. D40 records `declined` as the value for a user who says no. | user: explicit decision 2026-11-07 |
| D7 | What does onboarding ask? | It asks about each technology, showing a default suggestion taken from the existing gate catalogue and standard templates. | `kyber-weave review catalogue . --technology <name>` and `docs init --kyber-standards` already hold the defaults. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and the pull request CI runs `docs validate --merge-ready`. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D31 | What happens to the test standard's `## Commands` section? | It becomes `## Completion`, and `AGENTS.md` and other links move to the new anchor. | One fetch by topic gets everything a finishing worker needs. The link checker catches a missed anchor. | autonomous: recommendation accepted |
| D32 | Should catalogue test gates be wrapped in `proc-guard.sh`? | No. Emit `full-suite: true` only. | `proc-guard.sh` does not ship to other repositories (the todo checked: no file under `src/`, `templates/` or `products/` mentions it). The key already makes the CLI refuse. The todo asked for this choice to be recorded. | autonomous: recommendation accepted |
| D33 | What are the new settings called? | Top level: `stop-on-tool-failure` (boolean, absent means stop) and `onboarding` (`done` or `declined`, absent means not done). A small `run:` block: `base-branch` (absent means `origin/HEAD`) and `harness-names` (default true). Both top-level values publish in the Config Reg block the way `autonomy` does, only when set. | `AGENTS.md`'s Config Reg is the only place a portable agent can read a setting. An unknown value is refused the way `ParseAutonomy` refuses. | autonomous: recommendation accepted |
| D34 | How is the onboarding value written? | `kyber-weave docs onboarding <done|declined>`, updating the YAML through `HostConfigYaml` and re-splicing the Config Reg block. The conductor dispatches `docs-dev` to run it, and bug-crusher runs it itself. | The conductor cannot write. `HostConfigYaml` already edits this file without losing comments. | autonomous: recommendation accepted |
| D35 | Which decisions need an ADR? | See this plan's ADR disposition below, and the parent's Design harvest. New ADRs cover the stop rule (the reversal of "never block a run"), the server-side refresh, the tracked task file, project knowledge and onboarding, and run start with the harness name. ADR 0050, ADR 0059 and ADR 0053 are amended in place. Fixes 2, 6 and 7 carry a waiver or a section of another ADR. | Numbers are assigned at each closeout, from 0060. | autonomous: recommendation accepted |
| D40 | Which value records a user who says no to onboarding? | `declined`. It behaves as `done` does: neither asks again. | It shows why a repository has no project checks and lets the doctor mention it. A recommendation that the owner may overturn. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows D12. The owner confirmed on 2026-11-07 that all eight slices run in one continuous run. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`, and then confirmed by the owner. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D59 | Why does this slice branch from slice 5's unmerged branch? | Because slice 7 edits files slices 4 and 5 rewrote: T39 edits `products/kyber-squad/skills/delivery-execution/SKILL.md` (slice 4 created it; slice 5's T29 edits it) and `products/kyber-squad/agents/docs-dev.md` (slice 5's T29 edits it and its migration report), beside `conductor.md`, `csharp-dev.md` and `test-dev.md`, whose bodies and migration reports the slice 4 delivery touched. Branching from `main` would conflict on the first push, and GitHub runs no `pull_request` workflow on a conflicted pull request, so CI would never report. This branch is cut from slice 5's branch at `66f6ff86`. | Measured twice on slice 2 and recorded by slice 4's D51 and slice 5's D55: a conflicted pull request produced no run at all; the only way to a verdict was merging the base first. The slice's pull request targets slice 5's branch and is retargeted to `main` as slice 5 merges. Do not merge slice 5 into this branch and do not rebase onto `main`. | autonomous: recommendation accepted |
| D60 | Where does T40's review-config gate land, and what happens to the docs-dev task-gate todo? | `docs-dev` gains its first `task-gates` entry, holding the review-config load check T37 adds to the catalogue; `github-devops` gains the workflow-linter and `shellcheck` entries for its kinds. Verified in this tree: `review.task-gates` (`.kyber-weave/kyber-weave.yml:314`) declares 11 agent keys and no `docs-dev` entry, so `--agent docs-dev` reports `KW-REVIEW-027` ("No task gates are declared for the named agent", `ReviewGatesCommand.cs:19`, a Warning per [the rule reference](../ci-pipelines/rule-reference.md)) and executes zero gates. A config-only edit in this repository is a `docs-dev` edit — this plan's own T40 edits only `.kyber-weave/kyber-weave.yml`, `AGENTS.md` and `docs/code-review/architecture.md` — so the parent's T40 acceptance ("a task editing only workflow, shell or config files selects at least one gate") cannot hold unless `docs-dev` selects one. The open todo [docs-dev-task-gate-entry.md](../todo/docs-dev-task-gate-entry.md) (filed by slice 2) asks for more than that — a task gate that runs the documentation checks over `{changed}` documents — which this slice does not build: it stays open, and T42 records the narrowing in it rather than archiving it. | The todo's own text separates the two asks ("Decide whether a task gate should run them over `{changed}` documents, and what a docs change that touches nothing but `docs/archive/` should do"). A config load check covers a config change, not a documentation change. `KW-REVIEW-027`'s warning and `TASK_GATE: NONE_DECLARED` remain valid results for a repository with fewer machine checks (fix 11's own last bullet). | autonomous: recommendation accepted |
| D61 | Which migration reports does T39's edit invalidate? | Four: `products/kyber-squad/migration/conductor.md`, `migration/csharp-dev.md`, `migration/test-dev.md` and `migration/docs-dev.md`. T39 edits those four agent bodies (`conductor.md`, `csharp-dev.md`, `test-dev.md`, `docs-dev.md`), and each report's `final-body-sha256` covers the evolved LF-normalized instruction body, so the canonical suite fails until each report carries its body's new digest. The four reports join T39's file list. | The slice 4 D52 trap, verified against this tree the way slice 5's D58 did: the migration directory holds reports for all four agents (listed), and slices 4 and 5 each had to update a report for the same reason. The references and skills T39 edits hold no migration digest. | autonomous: recommendation accepted |
| D62 | The parent's T39 count and its file list disagree about one roster comment — what happens to it? | T39 adds `products/kyber-squad/toolchain.yml` to its file list and rewords the comment at `toolchain.yml:17` to name `SquadToolchainRosterGuardTests` without the repository path, so the parent's GREEN assertion — "`products/kyber-squad` holds no `KyberWeave.Tests` or `src/KyberWeave`" — holds with an unscoped scan. Verified: T39's named files hold exactly the 11 instruction lines enumerated in Investigation findings, and the roster comment is a 12th match the parent's own count included ("9 lines across 5 portable files") while its T39 file list omits the file. | A YAML comment is invisible to the parse that backs the guard — `SquadToolchainRosterGuardTests` reads the exposed `[McpServerTool]` names off `DocsTools` and the roster's YAML data, which a comment cannot enter — and no test pins the comment text (searched). The guard stays reachable by name, and `PortableContentTests`' assertion stays simple and absolute. | autonomous: recommendation accepted |

## Investigation findings

Verified against this tree — the worktree at `/tmp/kw-slice7`, branch `factory/feat/conductor-run-speed-project-knowledge`, cut from slice 5's unmerged branch at `66f6ff86` — not assumed from the packet. Discovery: codegraph 0, docs 1, direct reads 24, plus targeted greps and path-existence globs. The worktree carries no `.codegraph/` index of its own and this harness gives the architect no shell, so `doctor --ensure` cannot run here; the kw-docs and codegraph servers are bound to the canonical checkout (this session's provenance: `root=/Users/dave/git/personal/factory/kyber-weave`, `rev=d9c489c1`, `dirty=no`) — the same repository at slice 4's branch tip, which this branch's base `66f6ff86` extends only with slice 5's plan and its index row, so the server's corpus answers for everything this plan cites except those two files. The one docs call returned the governed frontmatter contract (`docs/documentation-ontology.md`). Per the parent design's worktree rule, a shell-less role reads the worktree's absolute path directly; the direct reads are enumerations and line-citation checks an index query does not return — the task-gate roster, the portable-file line count, rule ids, file existence, the migration-report set, ADR numbering, and line numbers such as `toolchain.yml:17`.

**The task-gate state.** `review.task-gates` starts at `.kyber-weave/kyber-weave.yml:314` and declares 11 agent keys: `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `sql-database-architect` (`:350`), `github-devops` (`:354`), `react-dev`, `tauri-dev`, `typescript-dev`, `rust-dev` and `test-dev` (`:408`). There is no `docs-dev` entry, so `--agent docs-dev` reports `KW-REVIEW-027` and executes zero gates; the open todo `docs-dev-task-gate-entry.md` documents exactly that and asks for documentation checks over `{changed}` documents (D60's boundary). `sql-database-architect` and `github-devops` alias `csharp-dev`'s `fmt-whitespace`, `fmt-style` and `dotnet-build` gates — the open question `task-gate-config-review.md` records, with its 2026-10-27 note that the shipped catalogue answers the first question for installing repositories (the csharp family writes .NET gates for `csharp-dev`, `dal-dev`, `maui-dev` and `pulumi-dev` only). Go and Python developers declare no task gates.

**The setting.** The yml's top-level keys are `ontology`, `review`, `autonomy`, `config-reg`, `mcp` and `arbiter`: no `onboarding` key exists, and no onboarding setting exists in `src/` (searched: the only `Onboarding` in `KyberWeave.Core` is `DocType.Onboarding`, the document type, unrelated). `run start`'s check, the `docs onboarding` command and the setting's parsing are all T38's to build — not already done.

**The catalogue.** `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`, `GateCatalogueFamily.cs` and `src/KyberWeave.Core.Docs/Scaffolding/ReviewBlockWriter.cs` emit no `full-suite` key (searched), matching the todo's 2026-11-06 check. D32's mitigation re-verified: no file under `src/`, `templates/` or `products/` mentions `proc-guard`.

**The standards.** No `## Completion` section exists in any of the 14 templates under `products/kyber-squad/standards/` (sql, github-actions, pulumi, csharp, react, maui, rust, data-access-layer, tauri, typescript, azure, test, go, python — 14 `README.md` files) or any of this repository's 7 standards (csharp, test, typescript, rust, tauri, go, github-actions, per `ontology.technologies`). The test standard's `## Commands` section is at `docs/standards/test/README.md:187` (with `## Tests that start processes` at `:125`), and `AGENTS.md:49` links the `#commands` anchor — both move with D31. `docs/standards/README.md` already holds "A standard is read through the tool, not as a file" (slice 3's harvest of D4/D5), so the `## Completion` route joins an existing rule. The onboarding skill exists at `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md` and is gated by three `skill-*` gates in `review.gates`.

**The portable lines T39 moves.** Exactly 11 instruction lines match `KyberWeave.Tests` or `src/KyberWeave` in T39's named files: `agents/csharp-dev.md:74`, `:75`, `:77`; `agents/test-dev.md:56`, `:71`, `:72`; `agents/docs-dev.md:43`; `agents/conductor.md:72`; `skills/delivery-execution/SKILL.md:42`; `skills/test-dev/SKILL.md:33`; `skills/task-gate/SKILL.md:31`. The 12th match in the tree is the roster comment at `products/kyber-squad/toolchain.yml:17` (D62).

**The test surface.** Six of the eleven classes T36 names exist today: `GateCatalogueRenderTests`, `DocsInitReviewGatesTests`, `GateCatalogueToolchainTests`, `ConfigRegTests`, `KyberStandardsTemplatesTests` (product project, namespace `KyberWeave.Tests`) and `ReviewGateFullSuiteConfigTests` (policy project, namespace `KyberWeave.Tests` — slice 6 created it for the full-suite key pins). Five are new in T36: `OnboardingCommandTests`, `RunStartOnboardingTests`, `PortableContentTests` (product) and `StandardsCompletionSectionTests`, `TaskGatesRepositoryConfigTests` (policy). All T37 and T38 product source files exist; `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs` is new in T38, and `HostConfigYaml` sits at `src/KyberWeave.Core/Docs/Scaffolding/HostConfigYaml.cs` (the parent's path, verified).

**The migration reports.** Reports exist for all four agent bodies T39 edits: `migration/conductor.md`, `migration/csharp-dev.md`, `migration/test-dev.md`, `migration/docs-dev.md` (D61).

**ADR numbering.** `docs/adr/` holds 0001 to 0062: slices 1 and 2 took 0060 to 0062, no slice since added one, and slice 5's plan reserved 0063 at its closeout. This slice's new ADR takes **0064**, assigned and confirmed at closeout per D35.

**The naming job gates this branch.** `pr-naming` is declared at `ci.yml:115` with `pr-naming-recheck.yml` beside it (verified by slices 4 and 5 on this branch chain). The delivery branch `factory/feat/conductor-run-speed-project-knowledge` follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` or the naming job fails it, and the pull request targets slice 5's branch per D59.

**Two tree states this slice does not fix.** (a) The task-file mechanism slice 5 delivers is not live at this branch's base (`PlanTaskFileValidator` exists nowhere in `src/`, searched; no `docs/plans/*.tasks.json` exists), so this plan carries no task file of its own (see Closeout). (b) The fast-loop tray todo the parent's T39 acceptance defers to ([fast-loop-default-tray-cargo-test.md](../todo/fast-loop-default-tray-cargo-test.md)) exists and stays open.

## Design

This slice's design, in the parent's voice. Fix 11 verbatim:

> ### Fix 11: project knowledge out of portable files, and onboarding sets it up
>
> | Kind | Example | Home | Owner |
> |---|---|---|---|
> | Process for any project | Run the gate, report the line, failure-loop rules | `task-gate` skill and `references/rework.md` | Product |
> | Runnable checks | Build, format and test commands | `review.gates` and `review.task-gates` | Project |
> | How to work and finish | Inner-loop commands, what runs when, and why | `## Completion` in each coding standard | Project |
>
> - **`## Completion`** is fetched by topic only when finishing, so it is not re-sent on earlier round trips. The 14 templates and this repository's standards gain it.
> - **Catalogue repair** (D32): without it, onboarding's defaults would let developer agents run the full suite.
> - **No gates and no section:** `TASK_GATE: NONE_DECLARED` stays valid and the worker runs the technology's normal build and tests. The report says the repository has fewer machine checks.
> - **Onboarding step** in `kyber-docs-onboarding-and-assessment`: detect technologies from `ontology.technologies` and the files present; for each, show the default gates and `Completion` text and ask whether to accept or change them; write the answers to `review:` and the standards; set `onboarding: done`.
> - **Run start, before anything else:** D6's flow. Autonomous mode is an exception to `autonomy.md:17` ("Ask the user nothing"), and joins the hard stops at `autonomy.md:54`. Onboarding is checked first, then tools.
> - **A technology added later** gets a doctor warning, not a stop. Workers already return a question when a standard is missing.
> - **This repository** gets `onboarding: done`, and settles its gate choices (`sql-database-architect` and `github-devops` get their own gate sets) and the shell and workflow task gates.

**What `## Completion` holds (T39).** Every coding standard gains a `## Completion` section carrying what a finishing worker needs: the inner-loop commands (build, targeted test, format), what runs when, and why — the knowledge that today sits in the 11 portable lines enumerated above. The test standard's `## Commands` section (`docs/standards/test/README.md:187`) becomes `## Completion` per D31, with `AGENTS.md:49`'s and every other `#commands` link moving to the new anchor (the link checker catches a missed one). All 11 project-path lines leave the portable files (`csharp-dev.md`, `test-dev.md`, `docs-dev.md`, `conductor.md`, the `delivery-execution`, `test-dev` and `task-gate` skills), and the 12th match — the `toolchain.yml` roster comment — is reworded in the same change (D62). The four agent bodies carry migration-report digests, so the four reports update with them (D61). The onboarding skill's step (D7) is written in the same change, and it passes `skill validate`, `lint` and `scan` — its three `review.gates` gates already exist. `autonomy.md:17` and `:54` carry D6's exception: the onboarding stop is a hard stop in autonomous mode too. The test standard does not cite the default fast loop for the tray until the [fast-loop todo](../todo/fast-loop-default-tray-cargo-test.md) is decided.

**What the setting and its flow do (T38, T40).** The `onboarding` setting (`done` or `declined`, absent means not done) publishes in the Config Reg block the way `autonomy` does, only when set (D33). `run start` checks it before anything else: unset stops the run and suggests `kyber-docs-onboarding-and-assessment`; autonomous runs stop as well, asking nothing and leaving the setting unchanged (D6). `kyber-weave docs onboarding <done|declined>` writes the value through `HostConfigYaml`, preserving comments and re-splicing the Config Reg block (D34); the conductor dispatches `docs-dev` to run it and bug-crusher runs it itself. A technology added later is a doctor warning, not a stop. This repository takes `onboarding: done` in T40, so this program's own runs never see the stop once slice 7 lands.

**What the catalogue and this repository's choices do (T37, T40).** The catalogue emits `full-suite: true` on each whole-suite test gate it writes, and a targeted gate does not carry it; the generated command is never wrapped in `proc-guard.sh` (D32) — the key is what makes the CLI refuse, and `proc-guard.sh` does not ship to other repositories. Three new gate kinds join the catalogue, each with its `os` restriction and an install path for the hosts the repository supports: a workflow linter for `.github/workflows/*.yml`, `shellcheck` for changed `*.sh` at task level, and a load check of the review config (`.kyber-weave/kyber-weave.yml`). In this repository, T40 writes the choices: `sql-database-architect` and `github-devops` get gate sets that match the files they edit (their current aliasing of `csharp-dev`'s format and build gates is the open question `task-gate-config-review.md` records); workflow, shell and config edits select at least one gate, which for the config kind means `docs-dev` gains its first task-gates entry (D60); the two questions of `task-gate-config-review.md` are answered in the yml, and `task-gates-workflow-and-shell-edits.md`'s completion condition — a task that edits only these files runs at least one gate — holds. No gate or `## Completion` section at all stays a valid state: `TASK_GATE: NONE_DECLARED` reports that the repository has fewer machine checks.

**What T41 checks.** `dotnet format whitespace`, `style` and `analyzers` each accept a `.csproj` and a `.props` path in `--include` and exit 0 on macOS, and a nonexistent path also exits 0 — the flag is lenient, and the globs only serve to make such a change select the build gate. The todo's completion requires that behaviour confirmed on Linux and Windows, so a throwaway workflow on a `scratch/` branch (triggered on push, never merged) runs the three verbs with a `.csproj` and `.props` in `--include` on `ubuntu-latest` and `windows-latest`, recording what each OS does — not only exit codes, because exit 0 is the lenient case — and the scratch branch is named in the pull request so the owner can delete it. `task-gate-config-review.md` archives only with that evidence; without it the todo is narrowed to that one question and left open.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `## Completion` in every standard and the onboarding step that configures it | KyberSquad | standard | `docs/standards/README.md`, each standard |
| H2 | The `onboarding` setting, its values and flows, and the `docs onboarding` command | DocGraph | reference | `docs/configuration.md`, `docs/cli-command-surface.md` |
| H3 | The catalogue `full-suite` key and the new workflow, shell and config task gates | ReviewCouncil | architecture | `docs/code-review/architecture.md` |

The parent's harvest table carries one row for "`stop-on-tool-failure`, `onboarding`, `run:` settings" targeting `docs/configuration.md`; the `stop-on-tool-failure` and `run:` parts are already harvested by slices 1 and 2, so this child harvests only the `onboarding` part (H2).

## ADR disposition

One new ADR, written by T42 at the next free number: **0064** in this tree (`docs/adr/` holds 0001 to 0062, and slice 5's plan reserved 0063 at its closeout), confirmed at closeout per the parent's D35. It records **project knowledge lives in gates and standards, reached through tools** — D4, D5, D6, D7, D40 — and the rejected alternatives the parent names: technology reference files in portable skills, reading standards from disk. Its index row updates in `docs/adr/README.md` (in T42's file list). No ADR waiver covers this slice's core: the parent's own harvest note lists waivers only for fixes 2, 6 and 7, and fix 11 is in the new-ADR column. The catalogue and task-gate choices (D32, D60) refine existing configuration surfaces and land in H3's target rather than opening a boundary of their own.

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. RED and GREEN are separate workers — never one task for both. No row runs the full suite — every command below is narrowed by class, and the process guard refuses an unfiltered run from a dev agent (exit 89). A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces. RED proofs run on a clean base checkout or an isolated worktree, never with `git stash`.

The two runner definitions, restated from `docs/standards/test/README.md` ("Commands", which T39 renames to `## Completion` under D31) and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`:

- **PRODUCT-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`
- **POLICY-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`

Every class this contract names is fully qualified as `KyberWeave.Tests.<Class>` — including the three that live in the policy project (`ReviewGateFullSuiteConfigTests` today; `StandardsCompletionSectionTests` and `TaskGatesRepositoryConfigTests` new), whose namespace is `KyberWeave.Tests`. New classes are marked new; every unmarked class named below exists today.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T36 | `tests/KyberWeave.Tests/` — `GateCatalogueRenderTests`, `DocsInitReviewGatesTests`, `GateCatalogueToolchainTests`, `OnboardingCommandTests` (new), `RunStartOnboardingTests` (new), `ConfigRegTests`, `KyberStandardsTemplatesTests`, `PortableContentTests` (new); `tests/KyberWeave.RepoPolicy.Tests/` — `StandardsCompletionSectionTests` (new), `TaskGatesRepositoryConfigTests` (new), `ReviewGateFullSuiteConfigTests` | Not applicable | RED authoring task; proof in T37 to T40's rows | New classes fail for the named reason | Not applicable |
| T37 | `tests/KyberWeave.Tests/` — `GateCatalogueRenderTests`, `DocsInitReviewGatesTests`, `GateCatalogueToolchainTests` | PRODUCT-RUN over the three classes | Whole-suite catalogue gates carry `full-suite: true`; targeted gates do not; the workflow-linter, `shellcheck` and review-config entries carry `os` and an install path | Fails: key absent | Passes; `docs validate` gives no `KW-CONFIG-002` |
| T38 | `tests/KyberWeave.Tests/` — `OnboardingCommandTests` (new), `RunStartOnboardingTests` (new), `ConfigRegTests` | PRODUCT-RUN over the three classes | Unset stops first; set continues; the CLI preserves comments and re-splices the Config Reg block; autonomous writes nothing; a new technology gives a doctor warning | Fails: setting absent | Passes |
| T39 | `tests/KyberWeave.Tests/` — `KyberStandardsTemplatesTests`, `PortableContentTests` (new); `tests/KyberWeave.RepoPolicy.Tests/` — `StandardsCompletionSectionTests` (new) | PRODUCT-RUN over the two classes, and POLICY-RUN over `StandardsCompletionSectionTests` | All 14 templates and this repository's standards have `## Completion`; `products/kyber-squad` holds no `KyberWeave.Tests` or `src/KyberWeave` (the D62 roster comment included) | Fails: section absent; 11 instruction lines found, plus the `toolchain.yml` roster comment | Passes |
| T40 | `tests/KyberWeave.RepoPolicy.Tests/` — `ReviewGateFullSuiteConfigTests`, `TaskGatesRepositoryConfigTests` (new) | POLICY-RUN over the two classes | `onboarding: done` set; a task editing only workflow, shell or config files selects at least one gate; `sql-database-architect` and `github-devops` have their own sets | Fails: selection empty | Passes |
| T41 | No test class — a live check on a `scratch/` branch | Not applicable; a throwaway workflow on `ubuntu-latest` and `windows-latest` runs `dotnet format whitespace`, `style` and `analyzers` with a `.csproj` and `.props` in `--include`, and `.agent_scratchpad/T41/format-include-check.md` records the output | Each verb accepts the project-file paths and what it actually formats, per OS, recorded rather than exit codes alone (exit 0 is the lenient case) | Not applicable | Output recorded in the todo's closeout |
| T42 | No test class — documentation and closeout | `docs validate .` and `docs drift .` after the docs edits (CLI gates, through `scripts/proc-guard.sh run --`) | `docs_validate` and `docs_drift` zero findings | None — documentation task | T42's acceptance: harvest, new ADR (0064), the three todos archived (the third only with T41's evidence), child plan archived, zero findings |

## Tasks

- **T36: RED for project knowledge**
  - **Files:** `tests/KyberWeave.Tests/GateCatalogueRenderTests.cs`, `tests/KyberWeave.Tests/DocsInitReviewGatesTests.cs`, `tests/KyberWeave.Tests/GateCatalogueToolchainTests.cs`, `tests/KyberWeave.Tests/OnboardingCommandTests.cs` (new), `tests/KyberWeave.Tests/RunStartOnboardingTests.cs` (new), `tests/KyberWeave.Tests/ConfigRegTests.cs`, `tests/KyberWeave.Tests/KyberStandardsTemplatesTests.cs`, `tests/KyberWeave.Tests/PortableContentTests.cs` (new), `tests/KyberWeave.RepoPolicy.Tests/StandardsCompletionSectionTests.cs` (new), `tests/KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs` (new), `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T37 to T40 are written and fail for the reason they name.
- **T37: Catalogue repair and new gate entries**
  - **Files:** `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`, `src/KyberWeave.Core/Review/Catalogue/GateCatalogueFamily.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ReviewBlockWriter.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T36
  - **Acceptance:** D32. Entries for a workflow linter, `shellcheck`, and a load check of the review config, each with its `os` restriction and install path. T37's row holds. This is the work `docs/todo/docs-init-emit-full-suite-gate-key.md` asks for: a repository scaffolded by `kyber-weave docs init` has `full-suite: true` on each whole-suite test gate it writes, a targeted gate does not carry it, the scaffold tests pin both, and D32 records that the generated command is not wrapped in `scripts/proc-guard.sh`.
- **T38: The `onboarding` setting and flow**
  - **Files:** `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfig.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfigLoader.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ConfigRegMarkdown.cs`, `src/KyberWeave.Core/Docs/Scaffolding/HostConfigYaml.cs`, `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/RunStart/RunStartPlanner.cs`, `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T36
  - **Acceptance:** D6, D33, D34, D40. `run start` checks onboarding before anything else. T38's row holds.
- **T39: `## Completion` and portable clean-up**
  - **Files:** `products/kyber-squad/standards/*/README.md`, `docs/standards/*/README.md`, `AGENTS.md`, `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md`, `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/docs-dev.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/skills/test-dev/SKILL.md`, `products/kyber-squad/skills/task-gate/SKILL.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/agents/conductor/references/autonomy.md`, and in the same change: the migration reports D61 adds (`products/kyber-squad/migration/conductor.md`, `migration/csharp-dev.md`, `migration/test-dev.md`, `migration/docs-dev.md`) and the roster comment D62 adds (`products/kyber-squad/toolchain.yml`)
  - **Required skills:** `docs-dev`
  - **Depends on:** T37, T38
  - **Acceptance:** D5, D6, D7, D31. All 11 project-path lines leave portable files, and their meaning moves to a `Completion` section; the 12th match, the `toolchain.yml` roster comment, is reworded in the same change (D62). The test standard does not cite the default fast loop for the tray until the [fast-loop todo](../todo/fast-loop-default-tray-cargo-test.md) is decided. `docs-dev`'s list gains the onboarding command. `autonomy.md:17` and `:54` carry the exception. The onboarding skill passes `skill validate`, `lint` and `scan`. The four migration reports carry their bodies' new digests (D61). T39's row holds.
- **T40: This repository's gate choices**
  - **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`, `docs/code-review/architecture.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T37, T38, T39
  - **Acceptance:** `onboarding: done`. `sql-database-architect` and `github-devops` get gate sets that match the files they edit, answering the first question of `docs/todo/task-gate-config-review.md` in the yml. Shell, workflow and config edits select a gate, which for the config kind is `docs-dev`'s first task-gates entry (D60) — the completion condition of `docs/todo/task-gates-workflow-and-shell-edits.md`. T40's row holds.
- **T41: Confirm `dotnet format --include` on Linux and Windows**
  - **Files:** `.agent_scratchpad/T41/format-include-check.md`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** a throwaway workflow on a `scratch/` branch, triggered on push and never merged, runs T41's row on `ubuntu-latest` and `windows-latest`. Output is returned for T42 to record, and the scratch branch is named in the pull request so the owner can delete it.
- **T42: Docs and closeout for slice 7**
  - **Files:** `docs/configuration.md`, `docs/standards/README.md`, `docs/kyber-squad/onboarding.md`, `docs/cli-command-surface.md`, `docs/adr/README.md`, `docs/todo/docs-init-emit-full-suite-gate-key.md`, `docs/todo/task-gates-workflow-and-shell-edits.md`, `docs/todo/task-gate-config-review.md`, `docs/todo/docs-dev-task-gate-entry.md` (D60's narrowing note, not an archive), `docs/todo/README.md`, `docs/plans/README.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T37, T38, T39, T40, T41
  - **Acceptance:** harvest, new ADR (0064, per the ADR disposition and D35), the three todos archived (the third, `task-gate-config-review.md`, only with T41's Linux and Windows evidence — without it, the todo is narrowed to that one question and left open), `docs-dev-task-gate-entry.md` left open with its narrowing recorded (D60), child plan archived, zero findings.

## Dependency graph

From the parent: T36 unlocks T37 and T38. T37 and T38 together unlock T39, then T40 (T39 and T40 both edit `AGENTS.md`). T41 stands alone. T42 is last and depends on T37, T38, T39, T40 and T41. Workers run in parallel up to `review.max-concurrent-workers` (4) where file scopes allow; the slice ends with one council, then its closeout.

## Risks

- **The onboarding stop is a new hard stop at run start.** Every run in a repository with the setting unset stops once to suggest onboarding, and autonomous runs stop too (D6). Mitigation: `declined` records the no and never asks again (D40); this repository takes `done` in T40, so this program's own runs never see the stop after slice 7 lands; a technology added later is a doctor warning, not a stop; the failure names the fix.
- **`--include` exit-0 leniency blinds a naive check.** A nonexistent path exits 0 and a `.csproj` in `--include` formats nothing, so T41's evidence records what each verb actually accepted and formatted per OS, not exit codes alone. The `.csproj`, `.props` and `.targets` globs in a task gate only serve to select the build gate, and `task-gate-config-review.md` closes only with the Linux and Windows evidence: without it the todo is narrowed to that one question, left open, and the slice stays open on T42's condition — the plan says so rather than hiding it.
- **An edited agent body without an updated migration report fails the suite (D61).** The four reports update in the same change as their bodies; the references and skills T39 edits hold no migration digest.
- **The stacked-branch risk (D59).** This branch sits on slice 5's unmerged branch; the pull request targets slice 5's branch and is retargeted to `main` when slice 5 merges. Until then a conflict can produce a pull request GitHub runs no `pull_request` workflows on at all — measured twice on slice 2. Do not merge slice 5 into this branch and do not rebase onto `main`; push to the stacked branch only.
- **Slices interleave with other work.** `run start` re-fetches for each slice, and a child plan is carved from the design at that slice's start, so files named here are checked by path plus symbol then — T39's eleven portable lines in particular were re-enumerated against this tree (D62) and are re-checked at execution.
- **A catalogue gate whose tool is missing.** The three new entries carry their `os` restriction and an install path; on an OS outside the restriction the gate is skipped (`KW-REVIEW-026`), never a silent pass, and the install path is what a failed start names.

## Out of scope

- The parent's other slices — each has its own child plan on its own branch and its own pull request. Slice 8 owns the `{tests}` token, the suppression gate, `KW-ARB-GATE-REPORT-001` and the [task-gate-test-project-mapping](../todo/task-gate-test-project-mapping.md) todo.
- The docs-dev documentation-check task gate: [docs-dev-task-gate-entry.md](../todo/docs-dev-task-gate-entry.md) stays open (D60). This slice gives `docs-dev` the review-config load check only; the documentation checks over `{changed}` documents that todo asks for are not fix 11's work.
- Wrapping catalogue-generated commands in `proc-guard.sh` (D32): the key is what refuses, and the guard does not ship to other repositories.
- The fast-loop tray default: [fast-loop-default-tray-cargo-test.md](../todo/fast-loop-default-tray-cargo-test.md) stays open, and the test standard does not cite the default fast loop for the tray until it is decided.
- Fix 9, routing each request with the decision model: [issue 535](https://github.com/dpalfery/kyber-weave/issues/535).
- A shell for the architect, and the fact-finder agent ([issue 221](https://github.com/dpalfery/kyber-weave/issues/221) stays open).
- The per-harness shell hook for the full-run guard: that todo stays open.
- Repairing the `KW-ARB-MODE-001` boolean comparison noted in the parent's findings: reported, not fixed here. Every no-test task in this plan is a `docs-dev` or `test-dev` task (T41, T42), which is the parent's own mitigation.

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — `scripts/proc-guard.sh run` refuses an unfiltered run from a dev agent with exit 89, and the full suite belongs to the review council's `test` gate and CI.
- `docs validate .` and `docs drift .` run after every plan save and after the docs task (T42), each at zero findings, in the tree the work runs in (the worktree protocol; the kw-docs server is bound to the canonical checkout and cannot see a file written in this worktree). They are **pending the conductor's run** for this plan; on zero findings each, the plan's status line and its index row flip to Ready. `docs validate . --merge-ready` is **not** the check while this plan is open: `KW-DOC-LIFECYCLE-003` fails an open plan by design, and the pull request CI runs it, which is what archives this plan in this slice's pull request at closeout. `docs drift` needs a CodeGraph index in the tree it runs in: `doctor --ensure .` runs first in a fresh worktree.
- **Branch and title.** The `pr-naming` CI job gates this slice's pull request (`ci.yml:115`, with `pr-naming-recheck.yml` beside it). The delivery branch `factory/feat/conductor-run-speed-project-knowledge` already follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` or the naming job fails it, and the pull request targets slice 5's branch, retargeted to `main` when slice 5 merges (D59).

## Review

This slice's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review, its command derived from the `code-review` skill with `--base` — never copied into this plan.

## Closeout

**Partially closed 2026-11-08. The plan stays open at `Review required` and was not archived.**
This section records what was read back in the delivered tree, what was written, and the two
criteria that block the archive.

### Verified in the tree

- **T39 — `## Completion` everywhere.** All 7 of this repository's standards
  (`docs/standards/*/README.md`) and all 14 shipped templates
  (`products/kyber-squad/standards/*/README.md`) carry exactly one `## Completion` section, and none
  keeps a `## Commands` section. The test standard's old `#commands` anchor is gone and
  `AGENTS.md:49` now links `#completion`. `docs/standards/README.md` states the rule, what the
  section must name and why it is the one place a finishing worker looks.
- **T39 — portable content left.** The 11 instruction lines that named `KyberWeave.Tests` or
  `src/KyberWeave` are gone from `agents/{conductor,csharp-dev,test-dev,docs-dev}.md` and the
  `delivery-execution`, `test-dev` and `task-gate` skills, and the roster comment at
  `products/kyber-squad/toolchain.yml:17` is reworded (D62). The four agent bodies' migration
  reports carry their new digests (D61).
- **T37 — catalogue repair.** `GateCatalogue` renders `full-suite: true` on every gate whose role is
  `test` and on no other, from a role check rather than an id list, and wraps nothing in
  `scripts/proc-guard.sh` (D32). The three plumbing entries — `workflow-lint`, `shellcheck`,
  `review-config` — are in the csharp family with `os: unix` and a `sources` install page, and
  `github-devops` and `docs-dev` map to the `github-actions` technology that renders them.
- **T40 — this repository's choices.** `onboarding: done` is recorded; `sql-database-architect` gains
  `sql-format` and `review-config`; `github-devops` gains `workflow-lint`, `shellcheck` and
  `review-config`; `docs-dev` gains `review-config` (D60).
- **Documentation written at this closeout:** ADR 0064 (project knowledge lives in gates and
  standards, reached through tools), ADR 0059 amended in place (decision 5: the catalogue writes the
  key), `docs/code-review/architecture.md` (the `full-suite` rule, the three plumbing gates, the two
  agents that receive them, and every file kind now selecting a gate), and the todo changes: two
  archived with their closeout evidence, one narrowed and left open, and one narrowed with its
  boundary recorded.
  No new `KW-*` rule id was introduced by this slice, so `docs/ci-pipelines/rule-reference.md` is
  unchanged.

### Blocking gaps

- **T38 was not implemented in this tree (D6, D7, D33, D34, D40).** No `onboarding` setting reader,
  no `docs onboarding` command, no `run start` check, no Config Reg `<onboarding>` property, and no
  onboarding step in the `kyber-docs-onboarding-and-assessment` skill. Five tests fail on exactly
  that absence: `ConfigRegLoadingTests.OnboardingIsAReservedNameInTheConfigRegPathMap`,
  `ConfigRegLoadingTests.RejectsAnOnboardingValueThatIsNotOneOfTheTwo` (three rows) and
  `ConfigRegValidatorTests.AnOnboardingSettingPublishesAModePropertyTheValidatorAccepts`, with
  `OnboardingCommandTests` and `RunStartOnboardingTests` failing beside them for the same reason.
  `onboarding: done` in `.kyber-weave/kyber-weave.yml` and the comment above it describe behaviour
  the product does not have; that comment is the implementation's to correct when T38 lands, not
  this closeout's to reword.
- **H2 is therefore not harvested.** `docs/configuration.md` and `docs/cli-command-surface.md` are
  deliberately unchanged: there is no setting value to document, no verb to document, and no
  run-start step order to describe. Writing them now would document intent as current guidance.
- **T41's evidence is absent.** No `.agent_scratchpad/T41/format-include-check.md`, and no
  `ubuntu-latest` or `windows-latest` run is cited in this tree. `task-gate-config-review.md` is
  therefore narrowed to that one question and left open, exactly as the plan's own condition
  required; it is not archived.

### Gates run at this closeout

- `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` — 0 errors, 0 critical,
  0 warnings on this plan's files. The only finding is `KW-DOC-TASKS-006` on slice 4's plan, which
  is open on the stacked base and carries no task file. This plan's own warning retired when its
  status left `Ready`.
- `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .` — 0 findings.
- `docs validate . --merge-ready` — 2 `KW-DOC-LIFECYCLE-003` errors, one for this plan and one for
  slice 4's plan. Neither can pass while its plan is open; that is the rule working, and it is
  exactly why this plan is not archived while its criteria are unmet.
- `dotnet test tests/KyberWeave.Tests -c Release --no-build --filter "…Squad|…Standards|…ConfigReg"`
  — total 2932, passed 2924, failed 5, skipped 3. All five failures are the T38 absence above; no
  failure is on a document or a gate this closeout changed.
- `dotnet test tests/KyberWeave.RepoPolicy.Tests -c Release --no-build --filter
  "…Standards|…TaskGates|…ReviewGate"` — total 55, passed 55, failed 0. This is the evidence for
  H1's standards rule, for D60, and for the two archived todos.
- `dotnet test tests/KyberWeave.Tests -c Release --no-build --filter "…GateCatalogue"` — total 81,
  passed 57, failed 0, skipped 24 (the toolchain-gated rows, which run in CI's
  `gate-catalogue-toolchains` job). This is the evidence for the `full-suite` key.
- The task gate, run for real rather than assumed: `review gates . --agent docs-dev` reported
  `KW-REVIEW-026` and executed **0 gates of 1**, because `review-config`'s `include` is the review
  configuration and no changed file of this task matches it. That is D60's boundary, observed.
- Every rewritten relative link in the files this closeout touched was checked with a script, not by
  eye: each resolves to a file that exists.

## Closeout mapping

Rows marked **Documented** name the live governed document that carries the item now. Rows marked
**Blocked** are the T38/T41 gap above and must be filled before this plan is archived; a row marked
**Waived** records why nothing survives.

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| D4 | [docs/standards/README.md](../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file); [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) decision 4 | The tool is the only route to a standard, and a file read is the exception | Documented |
| D5 | [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) decisions 1 to 3; [docs/standards/README.md](../standards/README.md#what-belongs-in-a-standard) | The three homes of project knowledge, and the `## Completion` rule | Documented |
| D6 | Blocked — T38 is not implemented; destination when it lands: `docs/configuration.md` (the setting and its values) and `docs/cli-command-surface.md` (the run-start step order) | The onboarding stop | Blocked |
| D7 | Blocked — the `kyber-docs-onboarding-and-assessment` skill carries no onboarding step | What onboarding asks | Blocked |
| D12 | [docs/plans/README.md](../plans/README.md#plan-decisions-and-closeout-policy) | One plan never spans several pull requests; each slice is its own plan | Documented |
| D13 | Documentation waiver — cross-slice build order: how the eight slices of this program sequence against each other is scheduling, with no surviving behaviour for a future engineer to consult | None | Waived |
| D31 | [docs/standards/README.md](../standards/README.md#what-belongs-in-a-standard); [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) decision 3 | `## Commands` became `## Completion`, one per standard, and the anchor moved | Documented |
| D32 | [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 5 and Consequences, amended in place; [docs/code-review/architecture.md](../code-review/architecture.md#the-shipped-gate-catalogue) | The catalogue writes `full-suite` from the gate's role; the generated command is not wrapped in the guard | Documented |
| D33 | Partially: `stop-on-tool-failure` and the `run:` block are in [docs/configuration.md](../configuration.md#run-controls) from slices 1 and 2; the `onboarding` value and its Config Reg property are blocked with D6 | Setting names and how a value publishes | Blocked |
| D34 | Blocked with D6 — the writing command does not exist | `docs onboarding <done\|declined>` | Blocked |
| D35 | [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) and its [index row](../adr/README.md); the onboarding half of this disposition is blocked with D6 | One new ADR at 0064 | Documented |
| D40 | Blocked with D6 — `declined` is recorded nowhere the product reads | The value that records a refusal | Blocked |
| AP1 | Documentation waiver — execution approval: the owner's approval of the Test contract is provenance, held in this plan's Decisions table and in the slices' commits, not behaviour a future reader consults | None | Waived |
| D59 | Documentation waiver — stacked-branch delivery: which unmerged branch this slice was cut from is history; the rule that survives (branch from the unmerged slice, target its branch, never rebase) is already in [docs/plans/README.md](../plans/README.md#plan-decisions-and-closeout-policy) from slice 3 | None | Waived |
| D60 | [docs/todo/docs-dev-task-gate-entry.md](../todo/docs-dev-task-gate-entry.md) (narrowed, left open); [docs/code-review/architecture.md](../code-review/architecture.md#task-gates) | `docs-dev`'s first task gate is a configuration check, not a documentation check | Documented |
| D61 | Documentation waiver — migration-report bookkeeping: the rule is the bodies' digest check itself, which the reports and the Squad canonical tests carry; no canonical page holds it, as slices 2 and 5 also recorded | None | Waived |
| D62 | [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) Consequences | The roster comment left the portable tree with the instruction lines | Documented |
| H1 | [docs/standards/README.md](../standards/README.md#what-belongs-in-a-standard); the 7 standards under [docs/standards/](../standards/README.md); the 14 templates under `products/kyber-squad/standards/` — the onboarding step the row pairs with the section is blocked with D7 | Every standard ends at one `## Completion` | Documented, except the onboarding half (blocked) |
| H2 | Blocked with D6, D33, D34 and D40 — `docs/configuration.md` and `docs/cli-command-surface.md` are unchanged because the behaviour they would describe does not exist in this tree | The onboarding setting, its values, and the command | Blocked |
| H3 | [docs/code-review/architecture.md](../code-review/architecture.md#the-shipped-gate-catalogue) and [#task-gates](../code-review/architecture.md#task-gates) | The `full-suite` key, the three plumbing gates, and every file kind selecting a gate | Documented |

