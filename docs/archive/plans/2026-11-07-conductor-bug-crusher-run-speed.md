---
id: plans/2026-11-07-conductor-bug-crusher-run-speed
title: Make conductor and bug-crusher runs faster and cheaper without losing a quality check
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-08
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad, DocGraph, ReviewCouncil, KyberArbiter, CI Pipelines]
---

# Make conductor and bug-crusher runs faster and cheaper without losing a quality check

**Status: Archived.** Closed 2026-11-08; see [Closeout](#closeout) and [Closeout mapping](#closeout-mapping) at the end. It was approved and ready to execute (decision `AP1`), and it never merged: per D12 it stayed on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) while each of its eight slices was delivered as its own child plan on its own branch. The last slice's pull request — slice 8, `factory/feat/conductor-run-speed-shrink-reads` — copied this file out of that branch and archived it here with a mapping from every decision to its child, which is the mechanism D61 records.
**Development mode:** `test-first` (packet input).
**Autonomy:** `autonomous` (packet input; every open item is decided and recorded below).
**Scale:** `extends-component`, components `KyberSquad`, `DocGraph`, `ReviewCouncil`, `KyberArbiter`, `CI Pipelines`, `Distribution`.
**Read first:** decision D12. Each delivery slice has to be its own plan, because CI refuses to merge an open plan.

## Problem and goal

**Problem.** Runs of Kyber-Squad's two entry points take hours. Conductor (the multi-agent orchestrator) took at most 30 minutes in version 0.1.6, by the owner's report. Bug-crusher (the one-defect workflow) spent 2h14m on a 4-source-file fix and never reached a review verdict. A conductor run took 2h48m with 1.63 workers running at once on average, against a limit of 4.

**Causes, largest first.**

1. Workers start with no working code search. They then read the code by hand over 44 to 83 model round trips.
2. The reviewer ran its gates repeatedly and never reached a verdict.
3. Plan reads cost less than the first two, and only happen on bug-crusher.

**Goal.** The same checks, in less time and for less input. The estimate for the measured bug-crusher run is 60 to 75 minutes, down from 2h14m and unfinished. Reaching 30 minutes for a bug that size also needs a lighter route ([issue 535](https://github.com/dpalfery/kyber-weave/issues/535), out of scope) or a faster model (out of scope).

**Words used in this plan.**

- **Worker:** a subagent that does one task.
- **RED and GREEN:** the failing test written first by `test-dev`, then the code that makes it pass, written by a different worker.
- **Task gate:** the deterministic checks a worker runs on its own files before reporting done.
- **Council:** the end-of-run review, run once by `code-reviewer`.
- **CodeGraph:** a third-party code index with a command line and a Model Context Protocol (MCP) server.
- **`kw-docs`:** Kyber-Weave's own documentation MCP server, the `kyber-weave-mcp` binary.
- **Harness:** one of the 13 coding tools Squad deploys to (Claude Code, Codex, Command Code and so on).
- **Arbiter:** the hook-driven gate that checks each agent dispatch against the plan.
- **Slice:** a part of this work that lands as its own pull request.

### Measured evidence

These figures come from the owner's session records and gate reports. They are carried here because the source is not in the repository. They were not re-measured for this plan.

**Bug-crusher run, 2026-11-07.** Command Code on `deepseek/deepseek-v4.1-flash`. The production fix touched 4 source files.

| Step | Window (UTC) | Time | Tool calls | Round trips | Tokens sent |
|---|---|---|---|---|---|
| Investigator, 3 passes | 15:06 to 15:26 | 18.7 min | 182 | 115 | 6.0M |
| Architect | 15:26 to 15:34 | 8.0 min | 76 | 54 | 6.0M |
| Main session: approval and worktree | 15:34 to 15:38 | 3.5 min | | | |
| First failing tests (RED) | 15:38 to 15:57 | 19.5 min | 94 | 83 | 12.5M |
| First fix (GREEN) | 15:58 to 16:12 | 14.6 min | 76 | 63 | 6.5M |
| Change one test assertion | 16:13 to 16:24 | 11.2 min | 56 | 46 | 1.8M |
| Second RED, first try (lost) | 16:24 to 16:33 | 9.1 min | 71 | 56 | 9.7M |
| Second RED, retry | 16:33 to 16:42 | 8.7 min | 62 | 44 | 3.4M |
| Second GREEN | 16:43 to 16:52 | 9.9 min | 63 | 62 | 5.6M |
| Docs update | 16:53 to 17:02 | 8.8 min | 108 | 83 | 13.1M |
| Code review (killed, no verdict) | 16:53 to 17:14 | 20.9 min | | | |

**Conductor run, 2026-11-06** (pull request 526). 26 agents, 2h48m before review follow-ups, 8,107 added lines across 193 files. Workers added up to 4h15m, so 1.63 ran at once on average. Conductor-only time was 669 of 10,080 seconds (6.6%).

**What the reviewer did in that 21 minutes.** The shell logs do not name the caller. Timing and output files match the reviewer, and the main session ran none of these.

| Start (UTC) | Command | Result | Time |
|---|---|---|---|
| 16:53 | `review gates --all`, no `--council`, as the plan said | Full-suite gates refused, the other gates still ran. 31 gates, 4 failed | 3m55 |
| 16:58 | Full suite allowed | 34 gates, 5 failed | 7m50 |
| 17:06 | Direct test run | 6,285 tests, 1 failed | 4m19 |
| 17:12 | Without `--all` | 27 gates, 1 failed. Killed at 17:14 while this ran | 8m04 |

Three things forced the repeats. The plan named the wrong command. `--all` removed path filtering, so TypeScript gates ran in a fresh worktree with no `node_modules` (`eslint: command not found`), while the scoped run took about the same time (464 versus 466 seconds). And one test always fails on the owner's machine.

**Other measurements relied on below.**

- Every worker reported `codegraph 0` and 13 to 25 direct reads. Tool use was close to one tool per round trip (94 calls in 83 trips, 63 in 62).
- Task gates are cheap: 14 to 47 seconds each, about 4 of 73 worker minutes. They are not the cause.
- Overlap between docs work and review did not slow tools (tests 269 versus 264 seconds, build 25 versus 30, InspectCode 70 versus 86). It still changed the tree under review.
- CodeGraph timings from a throwaway worktree of this repository: `sync` with nothing changed 0.3 s, 1 file 0.6 s, 50 files 1.5 s; `index` full rebuild 6.9 s; `init` in a fresh worktree 8.1 s. `init` changed no tracked or untracked file and started no daemon. The database is 223 MB.
- 21 docs server processes were running on the owner's machine when checked.
- Bug-crusher's 5 of 6 developer-worker prompts told the worker to read the plan. Conductor's plan-read rule works (1 of 26 prompts, to the architect).
- The conductor rewrote the plan into 128 KB of prompts across 26 dispatches. `Tasks` plus `Test contract` were 33% of the 42 KB plan.
- The docs worker's 13.1M tokens: about 158K per round trip, so nearly all re-sent input. It made 4 successful docs tool calls and 12 direct source reads, and read the 37 KB plan. `docs/kyber-squad/architecture.md` is 130 KB and `docs/cli-command-surface.md` is 48 KB.
- Of the last 200 pull requests, 10 start `claude/`, 8 start `codex/` and 37 are Dependabot's.

**Estimated saving, not measured.** CodeGraph, ready-to-paste tasks and batched reads 20 to 25 minutes. Session reuse 10 to 15. One gate run and a verdict 10. Build check before investigating 6 to 13. Removing the interruption and the permission denial, which were accidents, about 15.

**Not known.** The 30-minute baseline cannot be confirmed: KyberDash reports duration 0 for every session before late October. Since 0.1.6, `[Fact]` and `[Theory]` tests went from 683 to 3,895 and C# source files from 184 to 462, and test-first became the default, so not all of the slowdown is process. Command Code saves no subagent logs, so the split between model time and tool time in workers is an estimate (tools about a third). Why three conductor workers each took over 30 minutes, and why the permission was denied in the lost RED task, are not known.

## Decisions

The first eleven rows are the owner's and are settled. The rest were decided under the autonomous-decisions rule. Rows marked low-confidence are the ones to overturn first.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What happens when CodeGraph or `kw-docs` cannot be repaired? | A project setting `stop-on-tool-failure` in `.kyber-weave/kyber-weave.yml` defaults to stop. A code-editing run stops before dispatching workers. Autonomous runs stop too. `false` warns and continues. | The name is confirmed by D33. Read-only runs do not stop, and a repository with no governed docs skips the check. | user: explicit decision 2026-11-07 |
| D2 | Where do worker prompts live? | The architect writes each worker's prompt into `docs/plans/<plan>.tasks.json`, tracked in git so a push carries it to another machine. `docs-dev` deletes it with `git rm` in the same commit that archives the plan. The Markdown plan stays the authority. | Details in D18, D19, D38 and D39. | user: explicit decision 2026-11-07 |
| D3 | Is there a size limit on plans? | No. Do not propose one. | Regrowth of worker text is stopped by the single-home test in fix 7, not by a size limit. | user: explicit decision 2026-11-07 |
| D4 | How do workers get coding standards? | Only through `docs_get_coding_standard_for_technology`. Workers do not read plan files. | A file read is allowed only when the tool is missing. | user: explicit decision 2026-11-07 |
| D5 | Where does project-specific knowledge live? | Out of portable agents and skills. There are no technology reference files in portable skills. Project content goes in `review.gates`, `review.task-gates`, and a new `## Completion` section in each coding standard. | [docs/standards/README.md](../../standards/README.md) already states the rule. | user: explicit decision 2026-11-07 |
| D6 | How does the onboarding setting behave? | New `onboarding` setting. At run start, if unset, the run stops and suggests `kyber-docs-onboarding-and-assessment`. If the user says no, ask whether to ask again. If yes, write nothing and continue. If no, set the value and continue. Autonomous runs stop as well, asking nothing and leaving the setting unchanged. This repository gets the done value in the same change. | The owner asked for `done`. D40 records `declined` as the value for a user who says no. | user: explicit decision 2026-11-07 |
| D7 | What does onboarding ask? | It asks about each technology, showing a default suggestion taken from the existing gate catalogue and standard templates. | `kyber-weave review catalogue . --technology <name>` and `docs init --kyber-standards` already hold the defaults. | user: explicit decision 2026-11-07 |
| D8 | Where does a run start from? | Every run fetches origin first and branches directly from the remote base branch, without checking out `main`. Resuming a plan or pull request keeps its branch. | Command in D21. | user: explicit decision 2026-11-07 |
| D9 | Is the harness name carried? | In every branch and pull request title, enforced in CI, with Dependabot exempt and `manual` for hand-made work. | Mechanism in D14 and D41. | user: explicit decision 2026-11-07 |
| D10 | When is the CodeGraph index refreshed? | At the start of every architect, developer and review task. | Who does it for each role is in the worktree table under fix 1. | user: explicit decision 2026-11-07 |
| D11 | Who refreshes CodeGraph for roles with no shell? | The `kw-docs` MCP server refreshes CodeGraph itself, and its docs tools fail when CodeGraph fails. Form: use `sync`, run on tool calls rather than at startup, fail the tools rather than the process, and apply only where CodeGraph is declared. | Details in D23. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and `ci.yml:733-757` runs `docs validate --merge-ready` on every pull request. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D14 | How is a title edit checked without changing branch protection? | **Do not add `edited` to `ci.yml`.** Add a `pr-naming` job that `CI Summary` needs, reading the live title and branch through the GitHub API. Add a small `pr-naming-recheck.yml` on `pull_request: edited`, only when the title changed, that re-runs the `pr-naming` job of the latest CI run for that head. | `ci.yml:14` uses the default event types and `ci.yml:20-22` cancels in-progress runs. With `edited`, every description edit, which the council makes when it writes its verdict, would cancel and restart CI. Job-level skipping is no answer: a skipped job counts as passed, so a description edit could turn a red `CI Summary` green. GitHub documents write access to Actions for the job re-run endpoint. Whether `actions: write` on `GITHUB_TOKEN` is enough, and whether a re-run sees the old event payload, is not documented, so the design reads the live title and T7 checks it on a scratch pull request. Pull requests from forks get a read-only token, so a fork's title edit is checked only at its next push; that is a recorded limit. Self-gathered: GitHub REST and Actions docs, 2026-11-07. | autonomous: low-confidence, no authoritative source |
| D15 | Should a refused full-suite gate stop the whole run? | **Only under `--all`.** A refused gate under `--all` exits at once with no gate started. A scoped call without `--council` keeps today's behaviour. | `code-review/SKILL.md:251` and [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) make the pre-push self-review rely on "the other gates still run" (`AGENTS.md:80`). An unconditional stop would leave it no evidence. The measured waste was the `--all` call. Amends ADR 0059 in place. | autonomous: recommendation accepted |
| D16 | How does the reviewer re-check one gate? | New repeatable `review gates --gate <id>`. The reviewer reads `artifacts/gates.json` and re-runs only the gate it needs, with `--council` when that gate is full-suite. | `ReviewSettings.cs` has no such flag, so "re-run only the gate it needs" could not be followed. | autonomous: recommendation accepted |
| D17 | How is a gate with a missing dependency reported? | New optional gate key `requires-paths`. When a listed path is absent the gate does not start and reports `KW-REVIEW-033` with the install hint, and stays a blocking failure. | It must not read as a code failure, and must not silently pass. Set on the dash gates (`dash/node_modules`). The reviewer installs the dependency and re-runs that gate with `--gate`. | autonomous: low-confidence, no authoritative source |
| D18 | What does the task file's digest cover? | A new `PlanDocumentParser.ExecutionDigest(PlanDocument)` over the parsed tasks, files, dependencies, skills, test rows, out-of-scope paths and mode. Not the raw-text `Digest`. | The existing `Digest` hashes the whole file (`PlanDocumentParser.cs:128`), so a status flip or a decisions-row edit would mark the file stale and force an architect dispatch. Stale means the conductor refuses it until the architect regenerates. | autonomous: low-confidence, no authoritative source |
| D19 | How does a conductor with no shell check the task file? | Through `docs_validate_governed_document_schema`, which runs a new `PlanTaskFileValidator`. New ids: `KW-DOC-TASKS-001` malformed or wrong schema, `-002` tasks, files or dependencies differ from the plan, `-003` stale digest, `-004` prompt has no command or acceptance criteria, `-005` prompt tells a worker to open a plan, `-006` (warning) Ready plan with no task file. `KW-DOC-LIFECYCLE-005` fails `--merge-ready` when a task file is left behind. | `capabilities.yml:121` denies the orchestrator process execution, but it has the docs tools. `OpenWorkValidator` reads only `*.md` documents, so it would miss the file. | autonomous: recommendation accepted |
| D20 | How does bug-crusher reuse conductor's worker rules on every harness? | A new canonical skill `delivery-execution` holds the content of `execution-and-review.md`, moved rather than copied. Conductor and bug-crusher both load it. | Canonical skills are emitted for every target. A conductor skill is not (the recorded deployment was Claude Code, Codex, Cursor, Factory and Command Code, not Copilot, Kilo or OpenCode), and [ADR 0036](../../adr/0036-primary-agent-canonical-skill-entry-point.md) lowers it through a fallback. Portable files may not link out of their own directory. Cheap to reverse. | autonomous: low-confidence, no authoritative source |
| D21 | What is the one run-start command? | `kyber-weave run start --harness <token> --type <type> --slug <slug> [--resume <branch-or-pr>] [--failing-version <version>] [--json]`. Exit 0 ready, 1 tool failure under stop, 2 refused input, 3 CodeGraph binary missing. Where the conductor has no shell it dispatches `github-devops` with the exact command and a `RUN-START: true` marker. | `KW-ARB-PLAN-001` escalates a `delegate` with no `PLAN_FILE`, so that dispatch would be blocked on every hook harness. `TriggerClassifier` classifies the marker as `investigate`, which keeps the roster check and `KW-ARB-READONLY-001`. | autonomous: recommendation accepted |
| D22 | What is the command a worker runs first? | `kyber-weave doctor --ensure [path]`: `sync` or `init`, restore any tracked edit `init` made, one `codegraph explore`, one `kw-docs` call, repair once with `codegraph index`, retry once. It reads `stop-on-tool-failure` itself, so no agent judges the result. | `RepositoryDoctor` already takes a path and resolves its git toplevel, so it works inside a worktree. `doctor --provision` changes from `index` to `sync` when an index exists (`CodeGraphProvisioner.cs:172`). `index` stays only as the repair step. | autonomous: recommendation accepted |
| D23 | How does the docs server refresh safely? | First tool call, then at most once every 60 seconds (the interval is a suggestion, tunable by a constant). A 30-second bound. Lock busy means another process is refreshing, so answer with a one-line note, not a failure. Binary missing or `sync` failure: the tool returns `STATUS: TOOL_FAILURE` with the reason and fix under stop, or a warning line under `false`. `docs_get_source_repository_and_revision` always answers, so the failure can be diagnosed. `kyber-weave-mcp --no-codegraph-refresh` lets `doctor --check` keep its promise. | `AcquireLock` waits up to `IndexTimeout`, 600 seconds by default, far too long for a tool call, so this path takes a zero-wait lock. `doctor --check` promises it "Never runs init or index" but `McpLiveProbe` makes a real docs call. Child output cannot reach stdout because `ProcessRunner` captures it. `.codegraph/` is gitignored, so the tools stay read-only for documents. | autonomous: low-confidence, no authoritative source |
| D24 | How is a server bound to the main checkout handled when a worker is in a worktree on another branch? | See "The worktree limitation" under fix 1. Shell roles query the worktree's own index with `codegraph explore`. Shell-less roles get the worktree path in their packet and read it directly. Run start branches in place when the checkout is clean, so the server and the work align. Provenance gains `branch=` and `repo=`, and agents compare the repository, not the path. [Issue 221](https://github.com/dpalfery/kyber-weave/issues/221) stays open. | The canonical binding is deliberate and stays. A session started inside a worktree is already covered, because `--repo-root .` resolves there. | autonomous: recommendation accepted |
| D25 | Does a worktree still share the main checkout's index? | No. Every new worktree indexes itself with `doctor --ensure` at about 8 seconds. This replaces "Conditional worktree indexing". The ban on symlinking `.codegraph/` stays. | The shared index returned the wrong code: `codegraph explore "StoredDeploymentIdentity"` found the new method from the worktree in 0.37 s and did not find it from the main checkout. Disk cost is about 223 MB per worktree until cleanup removes it. | autonomous: recommendation accepted |
| D26 | Which server does the renderer grant? | `codegraph` only, to agents the existing `GrantsOrchestratorExcludedMcpServers` predicate entitles, so the orchestrator stays excluded. `context7` is not widened here. | Scope is the measured failure. Amends [ADR 0050](../../adr/0050-universal-kyber-weave-mcp-grant.md) in place. | autonomous: recommendation accepted |
| D27 | Where does the full-suite ban live? | One always-on line in each worker's instructions, from the single injected source. Not only in `task-gate`. | The source proposed `task-gate`, but that skill loads at the last step, and a raw `dotnet test` bypasses `proc-guard.sh` until the [shell hook todo](../../todo/per-harness-shell-hook-for-full-run-guard.md) lands. A ban read after the mistake is no ban. The count is 27 files, not 14, so the rule map is rebuilt by search. | autonomous: recommendation accepted |
| D28 | What does the suppression gate allow? | `kyber-weave review suppressions` scans added lines only, for `#pragma warning disable`, `SuppressMessage` and `NoWarn`, and fails unless the same or previous added line carries `reason:`. | 21 files already hold `#pragma warning disable`, hence added lines only. `AGENTS.md` asks for "a reason you can state". | autonomous: low-confidence, no authoritative source |
| D29 | How is `TASK_GATE: PASS` verified? | New Arbiter rule `KW-ARB-GATE-REPORT-001` on `delegate.returned` reads the `report=` file and escalates when it is absent or disagrees with the line. Worktree workers report an absolute path. | `GateReportFacts` serves the council and nothing reads `report=` at task end. Where the harness cannot observe returns the rule cannot fire, and the end-of-run gates still catch it. | autonomous: low-confidence, no authoritative source |
| D30 | How does a task gate pick tests? | A `{tests}` token beside `{changed}`, resolved next to `TaskGateSelection`. Mapping: a declared `review.test-map` first, then the name convention `<Stem>Tests`, keeping only classes that exist. Nothing mapped means skipped with `KW-REVIEW-026`, never a pass. | `codegraph affected` is too unreliable on C# to select tests (recorded on issue 535). `-class` needs a fully qualified name, because a short name runs zero tests and exits 0. | autonomous: low-confidence, no authoritative source |
| D31 | What happens to the test standard's `## Commands` section? | It becomes `## Completion`, and `AGENTS.md` and other links move to the new anchor. | One fetch by topic gets everything a finishing worker needs. The link checker catches a missed anchor. | autonomous: recommendation accepted |
| D32 | Should catalogue test gates be wrapped in `proc-guard.sh`? | No. Emit `full-suite: true` only. | `proc-guard.sh` does not ship to other repositories (the todo checked: no file under `src/`, `templates/` or `products/` mentions it). The key already makes the CLI refuse. The todo asked for this choice to be recorded. | autonomous: recommendation accepted |
| D33 | What are the new settings called? | Top level: `stop-on-tool-failure` (boolean, absent means stop) and `onboarding` (`done` or `declined`, absent means not done). A small `run:` block: `base-branch` (absent means `origin/HEAD`) and `harness-names` (default true). Both top-level values publish in the Config Reg block the way `autonomy` does, only when set. | `AGENTS.md`'s Config Reg is the only place a portable agent can read a setting. An unknown value is refused the way `ParseAutonomy` refuses. | autonomous: recommendation accepted |
| D34 | How is the onboarding value written? | `kyber-weave docs onboarding <done|declined>`, updating the YAML through `HostConfigYaml` and re-splicing the Config Reg block. The conductor dispatches `docs-dev` to run it, and bug-crusher runs it itself. | The conductor cannot write. `HostConfigYaml` already edits this file without losing comments. | autonomous: recommendation accepted |
| D35 | Which decisions need an ADR? | See "ADRs" under Design harvest. New ADRs cover the stop rule (the reversal of "never block a run"), the server-side refresh, the tracked task file, project knowledge and onboarding, and run start with the harness name. ADR 0050, ADR 0059 and ADR 0053 are amended in place. Fixes 2, 6 and 7 carry a waiver or a section of another ADR. | Numbers are assigned at each closeout, from 0060. | autonomous: recommendation accepted |
| D36 | Where does the rule map live? | `docs/kyber-squad/worker-rule-map.md`, a permanent reference. | It says what enforces each rule, which stays useful and stops regrowth. A file in a plan folder would be archived away. | autonomous: recommendation accepted |
| D37 | How is the seeded-defect trial run? | In a scratch clone, one Squad deployment per arm, same harness and model on both arms, one fixed packet per worker role. The harness and model are recorded, and the owner's model rule still applies if Pi or OpenCode is used. Only the text differs. | A running session cannot swap agent definitions, so each arm is a fresh headless session in its own deployment. | autonomous: low-confidence, no authoritative source |
| D38 | How does another machine resume? | Each finished task commits on the feature branch with a `Task: <id>` line and the worker's report in the commit body. The branch is pushed after each integrated task. `run start --resume` reads those lines and prints completed tasks and prior results. | Worker prompts said "Do not commit" in the measured run, so a mid-run push carried nothing. The conductor run opened with an architect pass just to assess progress (225 seconds, 64 tool calls). Pushes before a pull request exists start no CI (`ci.yml:12-14`). Squash merging keeps `main` clean. | autonomous: recommendation accepted |
| D39 | Where does a specification's task file go? | `<spec-folder>/tasks.json`, removed with `git rm` before the folder's `git mv`. | Specifications share the execution contract. `KW-DOC-LIFECYCLE-005` also looks under the archive, so a file moved with the folder is caught. | autonomous: recommendation accepted |
| D40 | Which value records a user who says no to onboarding? | `declined`. It behaves as `done` does: neither asks again. | It shows why a repository has no project checks and lets the doctor mention it. A recommendation that the owner may overturn. | autonomous: recommendation accepted |
| D41 | How does a skill know its harness? | A `{{harness}}` placeholder in canonical conductor, bug-crusher and `create-pull-request` text, replaced once in the shared render pipeline with `SquadTargetCatalog.GetToken`. | The 13 `HarnessSpec` roots are distinct. A folder name fails (this Command Code run was in the `Devin` checkout), and so does an environment variable (`OPENCODE_CONFIG` was set in a Claude session). Limit: Antigravity's `.agents/skills` is also read by Pi, ZCode and Command Code, so T48 checks a co-deployment. | autonomous: low-confidence, no authoritative source |
| D42 | What is the name format? | Branch `[<owner>.<agent>.]<harness>/<type>/<slug>`, for example `commandcode/fix/arbiter-global-block-identity`. Title suffix `[<harness>]`, for example `fix(arbiter): qualify global block identity by target [commandcode]`. Tokens are the 13 catalogue tokens plus `manual`, with `agy` written as `antigravity`. Dependabot is exempt by the pull request's author. | A suffix keeps the conventional prefix first. Nothing in CI filters on branch names and nothing parses titles today. The tag lands in `main`'s history and the changelog, which lists subjects as they are. A branch on an open pull request can be renamed through the GitHub API, so no grandfathering is needed. | autonomous: recommendation accepted |
| D43 | Is `cleanupcode` removed from the task-end pass? | Off by default. The skill stays for InspectCode review work. | `.editorconfig` already makes `var`, predefined types and unused usings warnings, the style gate checks them, and the whitespace gate checks formatting. Redundant qualifiers surface only in end-of-run InspectCode, as a rework item, and the rule map says so. It rewrote existing code in the first RED task, and the worker reverted it. The agent text also tells workers to run `dotnet format analyzers`, which the C# standard says task gates omit. | autonomous: recommendation accepted |
| D44 | The Arbiter's lens-skip rule reads a set that is empty on a committed tree. Is that in scope? | Yes. `review.changed-paths` becomes committed plus dirty paths against the review base, and `KW-ARB-LENS-001` no longer skips on an empty set. T27's RED test confirms the behaviour first. | `HookGitFacts.cs:31` uses only `GetDirtyStatuses`. An empty list passes `subset-of ["**/*.md"]`, which would mark `correctness` and `test-adequacy` not applicable. Seen by reading, not by running. Delivery already pushes before the council, and fix 3 makes a clean tree the normal case, so leaving it would lose a quality check. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows D12. The owner confirmed on 2026-11-07 that all eight slices run in one continuous run. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`, and then confirmed by the owner. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |

## Investigation findings

**Discovery:** codegraph 2 (both calls of the tool's budget, each showing the auto-sync banner quoted below), docs 6, direct reads about 60 files and about 75 searches. Direct reads of indexed code were needed for `ReviewGatesCommand`, `RepositoryDoctor`, `McpLiveProbe`, `CorpusProvenance`, `Program.cs`, `DocsTools`, `DocumentIndex`, `GateRunner`, `GitFacts`, `HookGitFacts`, `ReadGuard`, `RulePredicate`, `SquadTarget`, `OpenWorkValidator`, `PlanDocumentParser` and `ArbiterUninstallCommand`, because the CodeGraph budget was spent. Everything else was configuration, YAML or Markdown, which the code index does not cover. Self-gathered: GitHub issues 221 and 535, and GitHub's REST docs. Read tree: this checkout, detached at `decc903f`, the same revision the docs server reported.

**Verified against this checkout.**

- `CommandCodeRenderer` names only the `kw-docs` roster and says so in its class remarks (lines 40 to 52). `OpenCodeRenderer` and `KiloRenderer` emit only `kw-docs_*`. `FactoryRenderer`, `ZCodeRenderer`, `DevinRenderer`, `ClaudeRenderer` and `AntigravityRenderer` use the orchestrator-excluded predicate. `PiRenderer` was not settled, so T13 checks it.
- `.mcp.json` starts the servers with `--repo-root .` and `--path .`, so both follow the session's checkout.
- `conductor.md:62` says "never block a run", `bug-crusher/SKILL.md:43` says "never a stop", and `capabilities.yml:121` denies the orchestrator process execution although `conductor.md:62` tells it to run doctor. That conflict is real.
- `conductor.md:73`, `execution-and-review.md:11` and [the MCP runbook](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding) hold the "Conditional worktree indexing" rule. No test pins that sentence. `SquadCanonicalContentTests:1680` and `:1696` pin the `kyber-weave doctor --check` step.
- `DocumentIndexHost.Current()` builds on first use and rebuilds joins when `.codegraph/codegraph.db` changes. Every docs tool calls it first, 8 of 8. The runbook shows `startup_timeout_sec = 10` for Codex (line 488), so startup is the wrong moment for an `init`.
- `plan-authoring.md:25` asks for "exact files or symbols" and no commands. `DocumentLoader.cs:80` reads only `*.md`. `docs-dev.md:41-47` is a closed command list without `git rm`.
- `docs_get_coding_standard_for_technology` is named by no agent file, only by `toolchain.yml`. All 13 code-writing agents say to read the file. Called live with `csharp` and topic `error handling`, the tool's own documented example, it returned nothing, although the standard has "Errors and diagnostics".
- `AGENTS.md:117-119` requires `root=` to match `git rev-parse --show-toplevel`, which a server on the main checkout can never do from a worktree.
- `AnotherTargetIsDeployed_UnreadableState…` at `ArbiterUninstallCommandTests.cs:504` never sets `_isolated.Value = true`, so `ArbiterCommandComposition.HomeDirectory()` returns the real home. The fix is one line, and the fixture already exists.
- `code-reviewer.md:70` makes a reviewer with no agent tool apply each lens one at a time. `:139` holds the correct command with `--council`.
- `CONTRIBUTING.md:17`, `:19` and `:26` name `develop`. Local remote-tracking refs show no `origin/develop`; this role has no shell to ask the remote.
- `ReadGuard` in `src/KyberWeave.Arbiter/Hooks` guards `worker` and `publishing-worker` profiles and leaves `docs-dev` unguarded. Dispatch hooks are unconfirmed on OpenCode, Kilo and VS Code Copilot ([todo](../../todo/arbiter-global-hook-dispatch-coverage.md)), and Command Code's are `dispatch-unconfirmed`.
- `SquadPerformanceContractTests` has 44 tests, `SquadCanonicalContentTests` 61 and `ArbiterTestDevContractTests` 3. `KyberWeave.Tests` appears on 9 lines across 5 portable files, and `src/KyberWeave` on 2 more.
- `GateCatalogue` and `ReviewBlockWriter` emit no `full-suite` key. `.kyber-weave/kyber-weave.yml` has `codegraph` commented out under `mcp.required-servers`.

**Corrections to the requirements source.**

- The full-suite ban is in **27 files, 28 occurrences** under `products/kyber-squad`, not 14: 15 agents, the conductor reference, 2 skills, 4 skill references and 5 standard templates. T50 rebuilds the list by search.
- Moving the ban only to `task-gate` would load it too late (D27).
- A worktree question for the docs tools is not only about code: `docs_validate` and `docs_drift` also read the server's root, so a plan written into a worktree cannot be validated by a shell-less architect (handled under the worktree limitation).

**Traps found while verifying, each handled by a decision above.**

- A multi-pull-request plan fails CI (D12).
- The always-failing test blocks every slice's council on the owner's machine (D13).
- `edited` on `ci.yml` would restart CI on every description edit (D14).
- An unconditional fail-fast breaks the pre-push self-review (D15).
- The run-start dispatch to `github-devops` would be escalated by `KW-ARB-PLAN-001` (D21).
- `McpLiveProbe.TrailingMetadataRegex` parses the end of the provenance line, so new fields must be added before `rev=` or the regex must change, or `mcp.root` fails (T11).
- `AcquireLock` can hold a tool call for 600 seconds (D23).
- `github-devops` refuses to edit a workflow because no `github-actions` standard is declared here, which is what happened on the last plan (D13).
- `KW-ARB-MODE-001` compares `plan.test-contract.row` to `Not applicable`, but `HookPlanReader` sets that fact to a boolean, so the branch cannot fire. A no-test task given to a non-documentation specialist escalates in test-first mode. This plan therefore gives every no-test task to `docs-dev` or `test-dev`. The defect itself is left alone and reported in the digest.
- The lens-skip rule on a clean tree (D44).

**Live evidence for the lock risk.** Both CodeGraph calls in this session opened with: "CodeGraph auto-sync is DISABLED … CodeGraph file lock held by another process past the retry budget". Adding writers makes that more likely. See the first risk.

## Design

### Fix 1: make CodeGraph work where workers run, and check it first

**Causes.** No tool (subagents on Command Code get only the tools they name) and wrong checkout (the server follows the main checkout, which in the measured run was another branch without the base of the fix).

- **Grant.** Add `codegraph` to the renderers that omit it, through the shared predicate (D26).
- **Provision.** `doctor --provision` runs `sync` when an index exists and `init` when absent (D22). The cross-process lock stays.
- **Worker's first command.** `kyber-weave doctor --ensure .` (D22) for every shell role, including when a session continues with a new task.
- **Stop rule.** `conductor.md:62` and `bug-crusher/SKILL.md:43` change from "warn and continue" to stop by default. A new `STATUS: TOOL_FAILURE` makes the orchestrator stop and report, because the existing escalation route goes to the architect. Read-only runs do not stop. A repository with governed docs but no CodeGraph stops until it installs it or sets `false`. Autonomous mode stops too, and joins the hard stops in `autonomy.md:54`.
- **Which tools count.** `mcp.required-servers`. `kw-docs` is required; `codegraph` is turned on for this repository.
- **Discovery order.** Every agent gains: in a worktree, or when the tool is not listed, run `codegraph explore "…"`. Read files directly only if that fails.
- **Server refresh.** D11 and D23. It runs only where `codegraph` is in `mcp.required-servers`, so other repositories keep working. It does not cover a worktree created after the server starts, and does not grant `codegraph_explore` to subagents.

**Who refreshes the index.**

| Role | Has a shell? | Who refreshes |
|---|---|---|
| Any role working in the server's checkout | Either | The docs server, on its first tool call |
| Developers, `docs-dev`, investigator in a worktree | Yes | The worker, `doctor --ensure .` as its first command |
| `code-reviewer` in a worktree | Yes | The reviewer, before starting any lens |
| `review-lens`, `review-triage` | No | Nothing extra: they rely on the server or the reviewer's refresh |
| `architect` | No, by design | The docs server for its own root; `run start` for a worktree |

### The worktree limitation

The MCP servers stay bound to the canonical checkout on purpose. So a worker in a worktree on another branch can get answers about the wrong code. This is the rule for each role.

| Role | Code questions | Docs and standards | If the check fails |
|---|---|---|---|
| Developers, `test-dev`, `docs-dev`, investigator (shell) | `codegraph explore` from the worktree's shell, against its own index. Never the MCP `codegraph_explore` when `branch=` differs. | `kw-docs` for standards, which rarely differ by branch. Direct read of the worktree's copy for any document being edited. | `doctor --ensure` follows `stop-on-tool-failure` |
| `code-reviewer` (shell) | Same, before any lens | CLI `docs validate` and `docs drift` in the worktree | Same |
| `review-lens`, `review-triage` (no shell) | The packet carries `.agent_scratchpad/<task-id>/review-diff.patch` and the absolute worktree path. Direct reads of that path. MCP code tools only when provenance `branch=` matches the packet. | `kw-docs` for standards | A lens that cannot read reports `SKIPPED` with the reason |
| `architect` (no shell) | The packet carries `RUN_BASE`, `RUN_BRANCH` and, when present, `WORKTREE: <absolute path>`, all from `run start`. If provenance `rev=` and `branch=` match, normal discovery. If not, direct `Read` and `Grep` at the absolute worktree path, each counted as a direct read with reason "server bound to another revision", and the plan's findings state which revision was read. | The server cannot see a plan written into a worktree. The architect writes to `<WORKTREE>/docs/plans/…`, returns `STATUS: BLOCKED` naming "server root differs: run the docs checks in the worktree", and the conductor has the two CLI gates run there (main session's shell or `github-devops`), then re-invokes the architect with `DOCS_CHECKS: <results>`. `PLAN_READY` then reports `DOCS_VALIDATE: pass (cli, <path>)`. | `BLOCKED` when no worktree path was given |

- **Prevention comes first.** `run start` branches in place when the checkout is clean and not in use, which keeps server, architect and workers on one revision. A worktree is only for a dirty or busy checkout and for parallel workers.
- **A session started inside a worktree** is already correct, because `--repo-root .` resolves there. In supervised mode `run start` says so, so the owner can restart there.
- **Provenance.** Every docs response gains `branch=` and `repo=<common-git-dir>`. `AGENTS.md:117` changes to compare `repo=`, and treats a `root=` mismatch with a matching `repo=` as "another worktree of this repository", not "wrong repository".
- **Not closed.** [Issue 221](https://github.com/dpalfery/kyber-weave/issues/221) (a read-only fact-finder for the architect) stays open. The architect still cannot query a worktree's index. `run start`'s output supplies some of the facts issues 219 and 221 ask for.

### Fix 2: bug-crusher uses conductor's worker rules

`bug-crusher/SKILL.md:99` says only "Execute the Ready plan". Once a plan is Ready, bug-crusher loads `delivery-execution` (D20) instead of keeping a second copy, which also stops the main session editing plan status and grepping for it. `bug-crusher.md:12` allows only the investigator and `docs-dev`, so as rendered for Claude Code the escalated path cannot start `architect`, `test-dev`, developers or `code-reviewer`. The list is widened. This fix relies on the `/conductor` skill working live, so that live check is part of it.

### Fix 3: the architect writes each worker's prompt

At finalization the architect saves `docs/plans/<plan>.tasks.json` and returns its path. It is committed with the plan at once.

- **Contents.** Schema version, plan path, execution digest, and for each task: id, role, dependencies, file scope, session group (fix 6) and prompt. Each prompt carries files as path plus symbol, full test class names, exact commands, acceptance criteria and the standards to fetch (`csharp`, `test`). Line numbers are hints at most, because they go stale when an earlier task edits the same file.
- **Slots.** The conductor fills `{worktree}` and `{prior_results}` at dispatch, pasting earlier worker reports unchanged. No plan can know these: the second GREEN prompt needed a cause an earlier worker found.
- **The plan stays.** The owner approves it, it holds the Decisions table, and the Arbiter reads task ids, files, dependencies, skills, task text, test rows and out-of-scope items from it through `PlanDocumentParser`. Only the conductor stops reading it (68 KB in the measured run).
- **Duplication is by design, and checked** (D18, D19).
- **Purpose.** Quality first. Conductor-only time was 6.6% of the run, and the conductor still sends each prompt. The saving is fewer reads by workers. Pointing workers at a file would save more, but it conflicts with the read guard, so this starts with pasting.
- **After archive.** `docs-dev` removes the file with `git rm`, so it never reaches `main`, because this repository only squash merges. Nothing durable is lost, since the archived plan keeps the decisions, tasks and test contract, and the pull request's commits stay reachable.
- **Resume** (D38).
- **Docs worker.** Its prompt names the sections to edit and summarizes what changed, so it does not re-derive that from source.

### Fix 4: standards only from the tool

- Change that step in all 13 agents and the `test-dev` skill, and add the tool to the Discovery order. A file read is allowed only when the tool is missing.
- **Topic bug.** Match on words rather than the phrase. On a miss, return the standard up to the size limit with a one-line note.
- **Size limit stays, and the cut becomes safe.** It drops "Commands" (7.5 KB) and "Tests that start processes" (4.8 KB) from the 21 KB test standard. Commands now arrive in the prompt, and a worker starting processes can ask for that topic. Workers are told the cut is expected, so they do not open the file.

### Fix 5: the review finishes with one gate run

- Repair the failing test first (T1).
- Plans stop naming review commands. The reviewer uses its own, `--base <ref> --council`.
- Fail fast under `--all` (D15). Missing dependency (D17). One suite run, then `--gate` (D16).
- Review runs alone, last, in the foreground. No docs work or edits in that tree during review: in the measured run the main session edited two source files there at 17:02.
- Where the reviewer has no agent tool, as on Command Code, it stops after its gates with `STATUS: COUNCIL_SEATS_NEEDED` listing lenses and scope, the main session runs the seats in parallel, and it re-invokes the reviewer with their output. In-process lenses stay the fallback when the main session cannot either. This risk was not seen in the run: it never got that far.

### Fix 6: keep related work in one worker session

RED and GREEN stay in separate workers. Follow-up fixes and later same-role tasks on one change continue the earlier session where the harness allows it: changing one assertion took a fresh worker 11 minutes. The architect sets a `session` group in the task file, and may merge RED tasks when a later one does not need an earlier GREEN.

### Fix 8: start from the right code, with the harness name on it

Investigation took three passes to find the bug was in the shipped `rc.20` build. The plan was then written in the wrong checkout, copied to the fix branch, and deleted.

`run start` (D21) does this, before investigating or planning:

1. **Fetch** with `git fetch origin --prune`. Failure follows `stop-on-tool-failure`, because stale code is what cost this run.
2. **Find the base:** `run.base-branch` if set, otherwise `origin/HEAD`. Do not assume `main`.
3. **Compare builds.** For a defect, compare the failing binary's version with `HEAD` and `origin/<base>`, and print all three for the investigator's prompt.
4. **Choose where the work belongs.** New work, or a bug in the base or a shipped build: a new branch from `origin/<base>`. Resuming a plan or pull request: its existing branch, reporting if it is behind. A defect only on the current branch: that branch.
5. **Branch.** `git switch -c <branch> --no-track origin/<base>` when the checkout is clean and not in use. `git worktree add -b <branch> --no-track <path> origin/<base>` otherwise. `--no-track` matters: this run's fix branch tracked `origin/main`, so a bare `git push` could aim at the wrong branch.
6. **Index and test the tools** (from slice 2).

Names follow D42. `kyber-weave run start` and `create-pull-request` fill them in, and CI rejects a pull request without them (D14). Other repositories control this with `run.harness-names`, on by default, and onboarding asks about it, because a host may have its own branch rules. The CI job is this repository's; a host adds its own. Also fix `CONTRIBUTING.md`.

### Fix 10: enforce the plan-read ban

The read guard is advisory where hooks are, so nothing stopped the reads on Command Code. New rule `KW-ARB-PLANREAD-001` on `delegate`: after removing the routing header block, does the prompt name a path under `config.planning-dirs`? Documentation-profile targets are exempt, for closeout. It shares `PlanReadInstruction` with `KW-DOC-TASKS-005`. It may not fire where dispatch hooks are unconfirmed.

### Fix 11: project knowledge out of portable files, and onboarding sets it up

| Kind | Example | Home | Owner |
|---|---|---|---|
| Process for any project | Run the gate, report the line, failure-loop rules | `task-gate` skill and `references/rework.md` | Product |
| Runnable checks | Build, format and test commands | `review.gates` and `review.task-gates` | Project |
| How to work and finish | Inner-loop commands, what runs when, and why | `## Completion` in each coding standard | Project |

- **`## Completion`** is fetched by topic only when finishing, so it is not re-sent on earlier round trips. The 14 templates and this repository's standards gain it.
- **Catalogue repair** (D32): without it, onboarding's defaults would let developer agents run the full suite.
- **No gates and no section:** `TASK_GATE: NONE_DECLARED` stays valid and the worker runs the technology's normal build and tests. The report says the repository has fewer machine checks.
- **Onboarding step** in `kyber-docs-onboarding-and-assessment`: detect technologies from `ontology.technologies` and the files present; for each, show the default gates and `Completion` text and ask whether to accept or change them; write the answers to `review:` and the standards; set `onboarding: done`.
- **Run start, before anything else:** D6's flow. Autonomous mode is an exception to `autonomy.md:17` ("Ask the user nothing"), and joins the hard stops at `autonomy.md:54`. Onboarding is checked first, then tools.
- **A technology added later** gets a doctor warning, not a stop. Workers already return a question when a standard is missing.
- **This repository** gets `onboarding: done`, and settles its gate choices (`sql-database-architect` and `github-devops` get their own gate sets) and the shell and workflow task gates.

### Fix 7: shrink what workers read without losing a quality check

**Principle.** Quality comes from checks that run, not from text a worker reads. Cut text only where a check already enforces it, and add a check where one is missing. Agent-file text is re-sent on every round trip. A skill loaded at the last step is re-sent only for the last few.

**These checks stay exactly as they are.** Every task: whitespace format, style format, and a Release build with analyzers as errors; the conductor rejects a report with no `TASK_GATE:` line. End of run: InspectCode, the full test suite, duplicate detection and the review lenses. Pull request: CI.

**The rule map (starting rows; T49 adds a row for every sentence removed).** A rule with no home stays.

| Rule | Today | New home | What enforces it |
|---|---|---|---|
| Run the gate and report it | Long text in 6 agents | One line in each agent, details in `task-gate` | Conductor rejects a missing line, and `KW-ARB-GATE-REPORT-001` |
| Format, style, analyzers | Long text in agents | Commands in `review.task-gates`, process in `task-gate` | The gates |
| Report formats | Agent files | `task-gate`, loaded when finishing | Conductor, and the Arbiter for `RED_EVIDENCE` |
| Full test suite ban | 27 files | One always-on line from the injected source (D27) | `KW-REVIEW-033` for gate runs. `proc-guard` exit 89 only when used. A raw `dotnet test` gets through until the shell hook todo lands, which is why the line stays |
| Build isolation paths | Agent files and conductor | `Completion` section | Gate builds already set their own paths |
| Test commands | Agent files | The task prompt | Exact commands in the prompt, and the `{tests}` gate |
| Failure-loop rules | 1.4 KB in agents | `task-gate/references/rework.md`, loaded after a failed fix | Conductor's rework tally |
| `cleanupcode` pass | Required, plus a 13.8 KB skill | Off by default (D43) | Style and whitespace gates. Redundant qualifiers: end-of-run InspectCode |
| Baseline diagnostics | Required | Only where warnings do not fail the build | Warnings as errors means none to baseline |
| Scope, no weakened tests, no masked output | Hard rules | Stay in the agent file, short | Task prompt and review |
| Judgement rules such as test design | Standards | The standards tool | Review lenses |

**New checks, so quality goes up.** Suppression gate (D28). Verify the gate report (D29). Targeted tests in the task gate (D30), which closes the test mapping todo.

**Other changes.** Tell workers to send independent reads in one round trip. Keep one source for the Discovery order (938 bytes across agents), the ban and the batching line, injected at render the way `<response-preferences>` works; this keeps one copy and does not reduce what a worker reads. Remove the 2.7 KB of `conductor.md` that is word for word in the execution contract.

**Estimated size.** Estimates, assuming skills and standards are read at the start, about 4 bytes per token, and `task-gate` in context for the last 10 round trips. `test-dev` on C#: 72 KB at start today, about 32 KB after, about 9 KB loaded at the end. `csharp-dev`: 46 KB, about 17 KB, about 9 KB. For the first RED task this text goes from about 1.5M tokens to about 0.7M, which is 6 to 7% of that task's 12.5M. Code reads stay the largest share, so fixes 1 and 3 matter more. `python-dev`, `go-dev` and `typescript-dev` (5.4 to 6.7 KB) already work this way.

**Proof that nothing was lost. Nothing is deleted until this passes.**

1. **Rule map** (T49): every sentence removed gets a row.
2. **Seeded-defect trial** (T51), D37. Plant: a compiler warning, a nullable error, `var`, an unused using, bad formatting, an analyzer violation, a suppression, a test that only checks nothing throws, and a test reading the real home folder. Run workers on old and new text. The new text must catch each at the same stage, or the row must say which check now catches it later.
3. **Move the contract tests** (T50). Pins on exact strings move to the skill, and `WorkerRuleSingleHomeTests` fails when a moved rule appears in more than one file. That stops regrowth without a size limit.
4. **Watch real runs** (T53). Compare review findings over the next few runs. If InspectCode rework rises, add a scoped step back.

### Other harnesses

Behaviour this plan cannot confirm on all 13. T48 checks each and records the result in [the capability matrix](../../kyber-squad/requirements.md#target-capability-and-degradation-matrix). Anything not reached stays marked `[NOT LIVE-VERIFIED]`, with its fallback.

| Behaviour | Depends on it | Known | Fallback |
|---|---|---|---|
| Subagent can call `codegraph_explore` | Fix 1 | Named by Claude, Factory, ZCode, Devin, Antigravity and Copilot. Added here for Command Code, OpenCode, Kilo. Pi, Codex, Cursor, Warp unconfirmed | `codegraph explore` from the shell |
| Worker's shell can run `doctor --ensure` | Fixes 1, 8 | The `worker` profile allows it | `STATUS: TOOL_FAILURE` |
| Conductor holds a shell at run start | Fix 8 | A skill in the main session does. An agent under the `orchestrator` profile does not. Copilot does not | Dispatch `github-devops` |
| Per-harness token reaches the rendered text | Fix 8, D9 | 13 distinct roots. `.agents/skills` is also read by Pi, ZCode and Command Code | `--harness` is required and validated, so a wrong token is at worst a wrong label |
| Session continuation | Fix 6 | Discovered from exposed tools. Command Code unverified | A fresh worker with the recovery brief |
| Arbiter sees dispatch | Fix 10, D29 | Unconfirmed on OpenCode, Kilo, VS Code Copilot. `dispatch-unconfirmed` on Command Code | MCP fallback, and `KW-DOC-TASKS-005` at validation |
| `edited` title recheck | D14 | Not run yet | The next push re-checks |
| MCP tool-call timeout above the refresh bound | D23 | Not known per harness | Lower the bound |

### Rules that stay

- Do not check out `main` first. A checkout would carry another session's edits or refuse to run.
- Do not use `git stash` for RED proofs. That rule stands.
- `review-lens` and `review-triage` stay exempt from discovery reporting, because their output schemas are fixed.

## Delivery slices

Each slice lands as its own pull request, on its own child plan (D12). Land in this order. Slices 5 and 6 can be built at once but merge one at a time, because both edit `delivery-execution` and `plan-authoring.md`. Slice 1's own branch and title must already follow the new format, because its CI job checks itself.

| Slice | Name | Fixes | Tasks | Completes these todos |
|---|---|---|---|---|
| 1 | Clean start | Fix 5's test repair, fix 8, the `stop-on-tool-failure` setting | T1 to T9 | none |
| 2 | Code index where workers run | Fix 1, D10, D11, provenance fields | T10 to T16 | none |
| 3 | Standards through the tool | Fix 4 | T17 to T20 | none |
| 4 | Bug-crusher on the shared rules | Fix 2 | T21 to T24 | [claude-conductor-live-verification](../../todo/claude-conductor-live-verification.md) |
| 5 | Ready-to-paste tasks | Fixes 3, 6, 10, D44 | T25 to T31 | none |
| 6 | Review finishes | Rest of fix 5 | T32 to T35 | none |
| 7 | Project knowledge and onboarding | Fix 11 | T36 to T42 | [docs-init-emit-full-suite-gate-key](../todo/docs-init-emit-full-suite-gate-key.md), [task-gates-workflow-and-shell-edits](../todo/task-gates-workflow-and-shell-edits.md), [task-gate-config-review](../../todo/task-gate-config-review.md) |
| 8 | Shrink what workers read | Fix 7, the harness check | T43 to T53 | [task-gate-test-project-mapping](../todo/task-gate-test-project-mapping.md) |

`task-gate-config-review` closes only with T41's Linux and Windows evidence. Without it, `docs-dev` narrows the todo to that one question and leaves it open.

The last pull request also archives this file, with a mapping to each archived child.

## Design harvest

The rows carry an `ID` column so the [Closeout mapping](#closeout-mapping) can address each one; the
ids are `H<n>` and were assigned at this archive, not at authoring.

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `run start`, its exit codes, branch and title format | Distribution | reference | `docs/cli-command-surface.md` |
| H2 | `stop-on-tool-failure`, `onboarding`, `run:` settings | DocGraph | reference | [configuration.md](../../configuration.md) |
| H3 | Stop rule replacing "warn and continue" | KyberSquad | architecture | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#run-start-documentation-check) |
| H4 | `doctor --ensure`, `sync`, server refresh, `TOOL_FAILURE`, `--no-codegraph-refresh`, `branch=` and `repo=` | DocGraph | runbook | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) and `docs/docgraph/architecture.md` |
| H5 | Worktree limitation, per role | KyberSquad | operations | `docs/kyber-squad/onboarding.md`, and the runbook's worktree section |
| H6 | `codegraph` grant | KyberSquad | requirements | `docs/kyber-squad/requirements.md` matrix |
| H7 | Topic matching and the cut | DocGraph | runbook | the runbook's tool contract |
| H8 | `delivery-execution` skill | KyberSquad | architecture | `docs/kyber-squad/architecture.md` |
| H9 | Task file format, rule ids, commit trailer | KyberSquad | reference | `docs/kyber-squad/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/plans/README.md` policy text |
| H10 | `KW-ARB-PLANREAD-001`, `KW-ARB-GATE-REPORT-001`, the lens-skip change | KyberArbiter | architecture | `docs/kyber-arbiter/architecture.md`, rule reference |
| H11 | `--gate`, `requires-paths`, fail-fast scope, `COUNCIL_SEATS_NEEDED` | ReviewCouncil | architecture | `docs/code-review/architecture.md`, `docs/configuration.md#review` |
| H12 | `## Completion`, onboarding step | KyberSquad | standard | `docs/standards/README.md`, each standard |
| H13 | Catalogue `full-suite` key and new task gates | ReviewCouncil | architecture | `docs/code-review/architecture.md` |
| H14 | Rule map and trial result | KyberSquad | reference | `docs/kyber-squad/worker-rule-map.md` |
| H15 | PR naming job and recheck | CI Pipelines | runbook | `docs/ci-pipelines/workflows-runbook.md` |

## ADR disposition

**ADRs (D35).** Each was written by its slice's closeout, at the next free number. The ids are `A<n>`,
assigned at this archive so the mapping can address each row; the `Slice` column names the child that
delivered it.

| ID | Slice | ADR | Records |
|---|---|---|---|
| A1 | 1 | [New: runs start from the fetched remote and carry the harness name](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md) | D8, D9, D14, D21, D42. Rejected: checking out `main`, folder or environment detection, branch-protection changes |
| A2 | 2 | [New: code-editing runs stop on unusable tools](../../adr/0061-code-editing-runs-stop-on-unusable-tools.md) | D1, D10, D22, D25. **The reversal of "never block a run"**, recorded in `docs/kyber-squad/architecture.md` and two agent files. Rejected: warn and continue, a stop only in supervised mode |
| A3 | 2 | [New: the docs server refreshes CodeGraph and fails its tools](../../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md) | D11, D23. The tools write to a cache while staying read-only for documents. Rejected: refresh at startup, exiting the process, `index` per server start |
| A4 | 2 | [Amend ADR 0050 in place](../../adr/0050-universal-kyber-weave-mcp-grant.md) | D26 |
| A5 | 5 | [New: tracked task file, with the Markdown plan as authority](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) | D2, D18, D19, D38, D39. Rejected: pointing workers at a file, moving tasks out of the plan |
| A6 | 5 | [Amend ADR 0053 in place](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) | Two new rules, D44 |
| A7 | 6 | [Amend ADR 0059 in place](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) | D15, D16, D17 |
| A8 | 7 | [New: project knowledge lives in gates and standards, reached through tools](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) | D4, D5, D6, D7, D40. Rejected: technology reference files in portable skills, reading standards from disk |

No ADR, by waiver: fix 2 refines the existing skill pattern (ADR 0036); fix 6 refines existing session
rules; fix 7 applies the principle already in the C# standard and is recorded in the rule map — slice
8's closeout confirmed that waiver against the delivered tree rather than inheriting it.

## Test contract

**Commands.** Everything runs through the watchdog from the repository root. No row runs an unfiltered `dotnet test`, `review gates --all`, `fast-loop.sh --full` or `--scoped`. The council and CI run the full suite. Check every run reports a non-zero test count, because a short class name runs zero tests and exits 0.

- **PRODUCT-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll` followed by each `-class` named in the row.
- **POLICY-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll` followed by each `-class`.
- When a worker passes `--artifacts-path <dir>`, the assembly is at `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll`.
- RED proofs run on a clean base checkout or isolated worktree, never with `git stash`.
- Test classes in `tests/KyberWeave.Tests/Arbiter/` are in namespace `KyberWeave.Tests.Arbiter`. Everything else named here is `KyberWeave.Tests`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs` | `HOME=<scratch with a global Arbiter target>` before PRODUCT-RUN `-class KyberWeave.Tests.ArbiterUninstallCommandTests` | The test passes whatever the real home holds | Fails with a home that holds another global target | Passes with that home and with a blank one, assertion unchanged |
| T2 | Not applicable | Not applicable | RED authoring task; its proof is T3 to T7's rows | Each new class fails for the named reason | Not applicable |
| T3 | `ConfigRegTests`, `ReviewTaskGatesConfigTests`, new `RunSettingsConfigTests` | PRODUCT-RUN `-class KyberWeave.Tests.ConfigRegTests -class KyberWeave.Tests.RunSettingsConfigTests` | The settings parse, an unknown value is refused, and values publish in the Config Reg block only when set | Fails: keys unknown, no property line | Passes; `KW-CONFIG-REG-002` reports a stale block |
| T4 | New `RunStartPlannerTests`, `RunStartCommandTests`, and `Arbiter/ArbiterClassificationTests` | PRODUCT-RUN `-class KyberWeave.Tests.RunStartPlannerTests -class KyberWeave.Tests.RunStartCommandTests -class KyberWeave.Tests.Arbiter.ArbiterClassificationTests` | Branch format, `--no-track`, in-place versus worktree, resume keeps its branch, fetch failure follows the setting, exit codes, version compare output, `RUN-START: true` is not `delegate` | Fails: planner absent; marker classified `delegate` | Passes, using a real temporary git repository for the command tests |
| T5 | New `SquadHarnessTokenRenderTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadHarnessTokenRenderTests` | Each of the 13 renders carries its own token, and no rendered file holds `{{harness}}` | Fails: placeholder not replaced | Passes for all 13 |
| T6 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings; `github-actions` listed in the Config Reg block | Not applicable | Read-only check |
| T7 | New `PrNamingScriptTests`, `PrNamingWorkflowTests` | POLICY-RUN `-class KyberWeave.Tests.PrNamingScriptTests -class KyberWeave.Tests.PrNamingWorkflowTests` | Accepts and rejects the D42 shapes, Dependabot exempt, tokens match `SquadTargetCatalog`, `CI Summary` needs `pr-naming`, `ci.yml` has no `edited` | Fails: script and job absent | Passes, plus the scratch pull request live check |
| T8 | `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadWorktreeMcpContractTests` | Both entry points name `run start` and the shell-or-dispatch route | Fails: text absent | Passes |
| T9 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T10 | Not applicable | Not applicable | RED authoring task; proof in T11 to T14's rows | Each new class fails for the named reason | Not applicable |
| T11 | `RepositoryDoctorTests`, `DoctorCommandTests`, `McpLiveProbeTests`, new `DoctorEnsureTests` | PRODUCT-RUN `-class KyberWeave.Tests.RepositoryDoctorTests -class KyberWeave.Tests.DoctorCommandTests -class KyberWeave.Tests.McpLiveProbeTests -class KyberWeave.Tests.DoctorEnsureTests` | `sync` when present, `init` when absent, `index` only as repair. Restores only tracked edits `init` made. `--ensure` follows the setting. The probe passes `--no-codegraph-refresh` and tolerates the new provenance fields | Fails: verb is `index`; no `--ensure` | Passes with an injected executor recording each verb |
| T12 | New `McpCodeGraphRefreshTests`, `McpToolFailureTests`, `CorpusProvenanceTests` | PRODUCT-RUN `-class KyberWeave.Tests.McpCodeGraphRefreshTests -class KyberWeave.Tests.McpToolFailureTests -class KyberWeave.Tests.CorpusProvenanceTests` | No refresh when `codegraph` is undeclared; first call and interval; busy lock is a note; failure text under stop, warning under `false`; provenance tool always answers; a child's output never reaches stdout | Fails: no refresh seam | Passes with a fake CodeGraph executable |
| T13 | `CommandCodeRendererContractTests`, `SquadUniversalMcpGrantContractTests`, new `SquadCodeGraphGrantContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.CommandCodeRendererContractTests -class KyberWeave.Tests.SquadUniversalMcpGrantContractTests -class KyberWeave.Tests.SquadCodeGraphGrantContractTests` | Read-entitled non-orchestrator agents name `codegraph_explore`; the orchestrator does not; `context7` unchanged | Fails: Command Code, OpenCode, Kilo omit it | Passes; records what Pi needs |
| T14 | `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests`, `SquadPerformanceContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadWorktreeMcpContractTests -class KyberWeave.Tests.SquadPerformanceContractTests` | Stop wording replaces warn and continue, `TOOL_FAILURE` is routed, autonomy hard stop listed, worktree rule per role, conditional-indexing text gone | Fails: old text | Passes, with no assertion weakened except the reversed step, named in the report |
| T15 | `RootAgentsOperationalPointerTests` | POLICY-RUN `-class KyberWeave.Tests.RootAgentsOperationalPointerTests` | `AGENTS.md` compares `repo=`; `codegraph` declared | Fails: old rule text | Passes |
| T16 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T17 | Not applicable | Not applicable | RED authoring task; proof in T18 and T19's rows | New rows fail for the named reason | Not applicable |
| T18 | `McpAgentCodingStandardsTests` | PRODUCT-RUN `-class KyberWeave.Tests.McpAgentCodingStandardsTests` | `error handling` returns "Errors and diagnostics"; a real miss returns the standard up to budget with a note | Fails: empty excerpt | Passes |
| T19 | New `SquadStandardsToolContractTests`, `SquadCanonicalContentTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadStandardsToolContractTests -class KyberWeave.Tests.SquadCanonicalContentTests` | All 13 agents and the `test-dev` skill name the tool; none says to read the path first | Fails: agents say read the path | Passes |
| T20 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T21 | Not applicable | Not applicable | RED authoring task; proof in T22's row | New class fails | Not applicable |
| T22 | New `SquadDeliveryExecutionSkillTests`, `SquadCanonicalContentTests`, `SquadPerformanceContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadDeliveryExecutionSkillTests -class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadPerformanceContractTests` | The skill renders for all 13, both entry points load it, `bug-crusher.md` lists every agent the skill starts, and the old reference is gone | Fails: skill absent; roster short | Passes; moved pins keep their exact strings |
| T23 | Not applicable | Not applicable | Live check in Claude Code: `/skills` visibility, main-thread `/conductor`, reference loading, retained subagent invocation, receipt degradation codes, deployed paths | Not applicable | Evidence recorded; onboarding claims agree |
| T24 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T25 | Not applicable | Not applicable | RED authoring task; proof in T26 to T29's rows | New classes fail for the named reason | Not applicable |
| T26 | New `PlanTaskFileValidatorTests`, `PlanExecutionDigestTests`, `OpenWorkValidatorTests` | PRODUCT-RUN `-class KyberWeave.Tests.PlanTaskFileValidatorTests -class KyberWeave.Tests.PlanExecutionDigestTests -class KyberWeave.Tests.OpenWorkValidatorTests` | Each of `KW-DOC-TASKS-001` to `-006` and `KW-DOC-LIFECYCLE-005` fires on its case. A status edit does not change the digest. A task edit does | Fails: validator absent | Passes, including through the MCP validate tool |
| T27 | `Arbiter/ArbiterDefaultRulesTests`, `Arbiter/ArbiterEvaluatorTests`, new `Arbiter/ArbiterPlanReadRuleTests`, `Arbiter/ArbiterLensCleanTreeTests` | PRODUCT-RUN `-class KyberWeave.Tests.Arbiter.ArbiterDefaultRulesTests -class KyberWeave.Tests.Arbiter.ArbiterEvaluatorTests -class KyberWeave.Tests.Arbiter.ArbiterPlanReadRuleTests -class KyberWeave.Tests.Arbiter.ArbiterLensCleanTreeTests` | A prompt naming a planning path escalates; the routing header alone does not; `docs-dev` is exempt. On a committed tree `correctness` and `test-adequacy` still apply | Fails: rule absent; lens skipped on a clean tree (or the test shows it is not, which is reported) | Passes |
| T28 | New `RunStartResumeTests` | PRODUCT-RUN `-class KyberWeave.Tests.RunStartResumeTests` | `--resume` lists completed tasks and prior results from `Task:` commit lines | Fails: no resume reader | Passes on a real temporary repository |
| T29 | `SquadCanonicalContentTests`, `SquadPerformanceContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadPerformanceContractTests` | Architect saves the file at finalization; conductor pastes and fills slots; `docs-dev` list holds `git rm`; session group rule present | Fails: old text | Passes |
| T30 | `Arbiter/ArbiterEvaluatorTests` | PRODUCT-RUN `-class KyberWeave.Tests.Arbiter.ArbiterEvaluatorTests` | `git.changed-paths.since-dispatch` still reports committed paths, so per-task commits do not hide scope drift | Fails if commits hide paths, otherwise records a passing proof | Passes |
| T31 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T32 | Not applicable | Not applicable | RED authoring task; proof in T33's row | New rows fail | Not applicable |
| T33 | `ReviewFullSuiteGuardTests`, `ReviewGatesCouncilCommandTests`, new `ReviewGatePrerequisiteTests`, `ReviewGateFullSuiteConfigTests` | PRODUCT-RUN `-class KyberWeave.Tests.ReviewFullSuiteGuardTests -class KyberWeave.Tests.ReviewGatesCouncilCommandTests -class KyberWeave.Tests.ReviewGatePrerequisiteTests`, and POLICY-RUN `-class KyberWeave.Tests.ReviewGateFullSuiteConfigTests` | `--all` with a refusal starts nothing and exits 1; scoped still runs the rest; `--gate` selects; a missing path reports `KW-REVIEW-033` and stays blocking | Fails: gates run; no `--gate` | Passes with no full run started |
| T34 | `SquadCanonicalContentTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests` | Plans name no review command; reviewer runs once and uses `--gate`; review runs alone; `COUNCIL_SEATS_NEEDED` is routed | Fails: old text | Passes |
| T35 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T36 | Not applicable | Not applicable | RED authoring task; proof in T37 to T40's rows | New classes fail for the named reason | Not applicable |
| T37 | `GateCatalogueRenderTests`, `DocsInitReviewGatesTests`, `GateCatalogueToolchainTests` | PRODUCT-RUN `-class KyberWeave.Tests.GateCatalogueRenderTests -class KyberWeave.Tests.DocsInitReviewGatesTests -class KyberWeave.Tests.GateCatalogueToolchainTests` | Whole-suite gates carry `full-suite: true`; targeted gates do not; workflow, shell and config gates carry `os` and an install path | Fails: key absent | Passes; `docs validate` gives no `KW-CONFIG-002` |
| T38 | New `OnboardingCommandTests`, `RunStartOnboardingTests`, `ConfigRegTests` | PRODUCT-RUN `-class KyberWeave.Tests.OnboardingCommandTests -class KyberWeave.Tests.RunStartOnboardingTests -class KyberWeave.Tests.ConfigRegTests` | Unset stops first; set continues; the CLI preserves comments and re-splices the block; autonomous writes nothing; a new technology gives a doctor warning | Fails: setting absent | Passes |
| T39 | `KyberStandardsTemplatesTests`, new `PortableContentTests`, new `StandardsCompletionSectionTests` | PRODUCT-RUN `-class KyberWeave.Tests.KyberStandardsTemplatesTests -class KyberWeave.Tests.PortableContentTests`, and POLICY-RUN `-class KyberWeave.Tests.StandardsCompletionSectionTests` | All 14 templates and this repository's standards have `## Completion`; `products/kyber-squad` holds no `KyberWeave.Tests` or `src/KyberWeave` | Fails: section absent; 11 lines found | Passes |
| T40 | `ReviewGateFullSuiteConfigTests`, new `TaskGatesRepositoryConfigTests` | POLICY-RUN `-class KyberWeave.Tests.ReviewGateFullSuiteConfigTests -class KyberWeave.Tests.TaskGatesRepositoryConfigTests` | `onboarding: done` set; a task editing only workflow, shell or config files selects at least one gate; `sql-database-architect` and `github-devops` have their own sets | Fails: selection empty | Passes |
| T41 | Not applicable | Not applicable | Scratch workflow on Linux and Windows: `dotnet format whitespace`, `style` and `analyzers` with a `.csproj` and `.props` in `--include` | Not applicable | Output recorded in the todo's closeout |
| T42 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |
| T43 | Not applicable | Not applicable | RED authoring task; proof in T44 to T47's rows | New classes fail for the named reason | Not applicable |
| T44 | New `ReviewSuppressionsCommandTests` | PRODUCT-RUN `-class KyberWeave.Tests.ReviewSuppressionsCommandTests` | Added suppressions fail, existing ones do not, `reason:` passes | Fails: command absent | Passes on a real temporary repository |
| T45 | New `Arbiter/ArbiterGateReportRuleTests` | PRODUCT-RUN `-class KyberWeave.Tests.Arbiter.ArbiterGateReportRuleTests` | Missing report and a line disagreeing with the report escalate; a matching report allows | Fails: rule absent | Passes |
| T46 | `TaskGateSelectionTests`, `ReviewGatesAgentCommandTests` | PRODUCT-RUN `-class KyberWeave.Tests.TaskGateSelectionTests -class KyberWeave.Tests.ReviewGatesAgentCommandTests` | `{tests}` expands to fully qualified existing classes; nothing mapped gives `KW-REVIEW-026` | Fails: token unknown | Passes; task-gate fast path measured under a few minutes |
| T47 | `SquadCanonicalContentTests`, new `SquadWorkerBaselineInjectionTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadWorkerBaselineInjectionTests` | Each worker's render carries the single source once: Discovery order, the ban, batched reads | Fails: no injection | Passes for all 13 |
| T48 | Not applicable | Not applicable | Harness matrix filled per "Other harnesses"; `docs_validate` zero findings | Not applicable | Every row verified or marked `[NOT LIVE-VERIFIED]` with its fallback |
| T49 | Not applicable | Not applicable | `docs/kyber-squad/worker-rule-map.md` has one row per removed sentence; nothing deleted yet | Not applicable | `docs_validate` and `docs_drift` zero findings |
| T50 | `SquadPerformanceContractTests`, `SquadCanonicalContentTests`, `ArbiterTestDevContractTests`, new `WorkerRuleSingleHomeTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadPerformanceContractTests -class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.ArbiterTestDevContractTests -class KyberWeave.Tests.WorkerRuleSingleHomeTests` | Pinned strings hold at their new home; a moved rule in two files fails | Fails against current text | Passes after T52, no pinned string weakened |
| T51 | Not applicable | Not applicable | Seeded-defect trial: nine defects, two arms, per D37 | Not applicable | Table recorded in the rule map. Each defect is caught at the same stage or the row names the later check. T52 does not start until this passes |
| T52 | The T50 classes | Same as T50 | Text removed per the rule map | Fails until the text moves | All T50 classes pass; byte counts recorded against the estimate |
| T53 | Not applicable | Not applicable | Closeout, and the follow-up todo for watching real runs | Not applicable | `docs_validate` and `docs_drift` zero findings |

## Tasks

Each task names files, skills, acceptance, dependencies and the row above that holds its command. `Depends on` lists tasks inside the same slice only. Slice order is in Delivery slices. Child plans copy these tasks unchanged.

**Slice 1: clean start.**

### T1: Repair the test that reads the real home folder

- **Files:** `tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** `AnotherTargetIsDeployed_UnreadableState_ReportsNoOtherTargetWithoutThrowing` sets `_isolated.Value = true`, as the class's other isolated tests do. T1's row holds. Report any other test in the class that reaches the real home.

### T2: RED for clean start

- **Files:** `tests/KyberWeave.Tests/RunSettingsConfigTests.cs`, `tests/KyberWeave.Tests/ConfigRegTests.cs`, `tests/KyberWeave.Tests/RunStartPlannerTests.cs`, `tests/KyberWeave.Tests/RunStartCommandTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterClassificationTests.cs`, `tests/KyberWeave.Tests/SquadHarnessTokenRenderTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/PrNamingScriptTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/PrNamingWorkflowTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** every row T3 to T8 is written and fails for the reason it names. No product file changes. Command tests use real temporary git repositories.

### T3: Settings: `stop-on-tool-failure` and `run:`

- **Files:** `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfig.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfigLoader.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ConfigRegMarkdown.cs`, `src/KyberWeave.Core.Docs/Validation/ConfigRegValidator.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2
- **Acceptance:** D33. A missing `stop-on-tool-failure` means stop in code, not only in text. T3's row holds.

### T4: The `run start` command

- **Files:** `src/KyberWeave.Core/RunStart/RunStartPlanner.cs` (new, pure decisions), `src/KyberWeave.Core/RunStart/RunStartRunner.cs` (new, git through `IProcessExecutor`), `src/KyberWeave.Cli/Commands/Run/RunStartCommand.cs` (new), `src/KyberWeave.Cli/Commands/Run/RunStartSettings.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/Arbiter/Facts/TriggerClassifier.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2, T3
- **Acceptance:** the six steps under fix 8 except tool testing, which slice 2 adds. D21 and D42. `--json` prints base, branch, worktree, revision, versions and the server-alignment facts the architect's packet needs. T4's row holds, and `ProcessSpawnSitesTests` stays green.

### T5: Harness token in the render pipeline

- **Files:** `src/KyberWeave.Core.Squad/Rendering/SquadHarnessRenderer.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2
- **Acceptance:** D41. One replacement point covers agent bodies, skills and resources. T5's row holds.

### T6: Declare the `github-actions` standard

- **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`, `docs/standards/github-actions/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** none
- **Acceptance:** `github-actions` joins `ontology.technologies`. The standard comes from `products/kyber-squad/standards/github-actions/README.md`, adapted to this repository's rules. The Config Reg block lists it, and only the two lines change. T6's row holds.

### T7: Naming check in CI

- **Files:** `scripts/check-pr-name.sh` (new), `.github/workflows/ci.yml`, `.github/workflows/pr-naming-recheck.yml` (new)
- **Required skills:** `github-devops`
- **Depends on:** T2, T6
- **Acceptance:** D14 and D42. `pr-naming` has `pull-requests: read`, skips on non-pull-request events, and `CI Summary` needs it and reports it. Actions are pinned by digest like the rest. `ci.yml` gains no `edited`. Live check on a scratch pull request: a bad title fails, a corrected title turns `CI Summary` green through the recheck, and a description edit starts nothing; if `actions: write` proves insufficient, report that and leave the recheck's own failure visible. T7's row holds.

### T8: Entry points use `run start`

- **Files:** `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/conductor/references/intake-path.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/agents/bug-crusher-investigator.md`, `products/kyber-squad/agents/github-devops.md`, `products/kyber-squad/skills/create-pull-request/SKILL.md`, `CONTRIBUTING.md`
- **Required skills:** `docs-dev`
- **Depends on:** T4, T5
- **Acceptance:** a run fetches and branches before investigating or planning. The shell-or-dispatch route resolves the `conductor.md:62` conflict. The three versions reach the investigator's prompt. The title and branch rules are in `create-pull-request`. `CONTRIBUTING.md` no longer names `develop`. T8's row holds.

### T9: Docs and closeout for slice 1

- **Files:** `docs/cli-command-surface.md`, `docs/configuration.md`, `docs/ci-pipelines/workflows-runbook.md`, `docs/adr/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T3, T4, T5, T6, T7, T8
- **Acceptance:** harvest for this slice, the first new ADR, child plan archived. `docs validate --merge-ready` and `docs drift` report zero findings.

**Slice 2: code index where workers run.**

### T10: RED for the code index

- **Files:** `tests/KyberWeave.Tests/RepositoryDoctorTests.cs`, `tests/KyberWeave.Tests/DoctorCommandTests.cs`, `tests/KyberWeave.Tests/DoctorEnsureTests.cs`, `tests/KyberWeave.Tests/McpLiveProbeTests.cs`, `tests/KyberWeave.Tests/McpCodeGraphRefreshTests.cs`, `tests/KyberWeave.Tests/McpToolFailureTests.cs`, `tests/KyberWeave.Tests/CorpusProvenanceTests.cs`, `tests/KyberWeave.Tests/CommandCodeRendererContractTests.cs`, `tests/KyberWeave.Tests/SquadUniversalMcpGrantContractTests.cs`, `tests/KyberWeave.Tests/SquadCodeGraphGrantContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadWorktreeMcpContractTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T11 to T14 are written and fail for the reason they name. `SquadCanonicalContentTests:1680` and `:1696` are rewritten to the stop rule, and the report names that as the owner's reversal, not a weakening.

### T11: `doctor --ensure` and `sync`

- **Files:** `src/KyberWeave.Core/Doctor/CodeGraphProvisioner.cs`, `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`, `src/KyberWeave.Core/Doctor/DoctorMode.cs`, `src/KyberWeave.Core/Doctor/McpLiveProbe.cs`, `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`, `src/KyberWeave.Cli/Commands/Doctor/DoctorSettings.cs`, `src/KyberWeave.Core/RunStart/RunStartRunner.cs`, `.apm/skills/codegraph/SKILL.md`
- **Required skills:** `csharp-dev`
- **Depends on:** T10
- **Acceptance:** D22. `Provision` takes a lock-wait argument. `McpLiveProbe.TrailingMetadataRegex` handles the new provenance fields, and `mcp.root` still catches another checkout. The `--ensure` restore never touches a file that was already dirty. `run start` gains its tool test step. The `--provision` description and the codegraph skill say `sync`. T11's row holds.

### T12: Docs server refresh and failure

- **Files:** `src/KyberWeave.Mcp/CodeGraphFreshness.cs` (new), `src/KyberWeave.Mcp/DocsTools.cs`, `src/KyberWeave.Mcp/Program.cs`, `src/KyberWeave.Mcp/RepositoryRootResolver.cs`, `src/KyberWeave.Mcp/CorpusProvenance.cs`, `src/KyberWeave.Mcp/DocsServerInstructions.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T10, T11
- **Acceptance:** D11 and D23. It reuses `CodeGraphProvisioner` and its cross-process lock, with no new way to start a process. One gate at the top of each of the 8 tools. Settings are read at each refresh, so an edit takes effect without a restart. Nothing reaches stdout. T12's row holds. Measure: with a CodeGraph server running, a worktree `--ensure` and repeated docs calls produce no "auto-sync is DISABLED" banner; report what happens if they do.

### T13: Grant `codegraph` in the renderers

- **Files:** `src/KyberWeave.Core.Squad/Rendering/CommandCodeRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/OpenCodeRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/KiloRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/PiRenderer.cs`, `src/KyberWeave.Core.Squad/Rendering/CapabilityDegradations.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T10
- **Acceptance:** D26. Names come from the toolchain roster, not a local list. The `permission-not-expressible` records stay truthful. The Command Code class remark is rewritten. Whether Pi needs a change is settled and reported. T13's row holds.

### T14: Agent instructions: stop rule, first command, worktree rule

- **Files:** `products/kyber-squad/agents/*.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor/references/execution-and-review.md`, `products/kyber-squad/agents/conductor/references/autonomy.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/skills/code-review/SKILL.md`
- **Required skills:** `docs-dev`
- **Depends on:** T11, T12
- **Acceptance:** D1, D24, D25. The role tables above are stated in each role's own file. `conductor.md:62`, `:73` and `bug-crusher/SKILL.md:43` are replaced. `STATUS: TOOL_FAILURE` is routed. The architect's `root=` rule becomes the `repo=` rule, with the `DOCS_CHECKS` return path. T14's row holds.

### T15: This repository's settings and root rules

- **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`
- **Required skills:** `docs-dev`
- **Depends on:** T11, T12
- **Acceptance:** `codegraph` is turned on under `mcp.required-servers`. `AGENTS.md:105-111` and `:117-119` follow D24. T15's row holds.

### T16: Docs and closeout for slice 2

- **Files:** `docs/docgraph/mcp-runbook.md`, `docs/docgraph/architecture.md`, `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/kyber-squad/requirements.md`, `docs/configuration.md`, `docs/cli-command-surface.md`, `docs/ci-pipelines/rule-reference.md`, `src/KyberWeave.Mcp/AGENTS.md`, `docs/adr/0050-universal-kyber-weave-mcp-grant.md`, `docs/adr/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T11, T12, T13, T14, T15
- **Acceptance:** harvest, two new ADRs including the reversal, ADR 0050 amended, child plan archived, zero findings.

**Slice 3: standards through the tool.**

### T17: RED for the standards tool

- **Files:** `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs`, `tests/KyberWeave.Tests/SquadStandardsToolContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T18 and T19 are written and fail for the reason they name.

### T18: Topic matching and the safe cut

- **Files:** `src/KyberWeave.Core.Docs/Search/DocumentIndex.cs`, `src/KyberWeave.Mcp/DocsTools.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T17
- **Acceptance:** fix 4's topic rule. The 12,000-character default stays. The answer says what was cut and that this is expected. T18's row holds.

### T19: Workers use the tool

- **Files:** `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/dal-dev.md`, `products/kyber-squad/agents/github-devops.md`, `products/kyber-squad/agents/go-dev.md`, `products/kyber-squad/agents/maui-dev.md`, `products/kyber-squad/agents/pulumi-dev.md`, `products/kyber-squad/agents/python-dev.md`, `products/kyber-squad/agents/react-dev.md`, `products/kyber-squad/agents/rust-dev.md`, `products/kyber-squad/agents/sql-database-architect.md`, `products/kyber-squad/agents/tauri-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/typescript-dev.md`, `products/kyber-squad/skills/test-dev/SKILL.md`
- **Required skills:** `docs-dev`
- **Depends on:** T18
- **Acceptance:** D4. "Read the path declared as" becomes a tool call, and a file read is allowed only when the tool is missing. The registry property names remain so a draft or missing standard still returns its question. T19's row holds.

### T20: Docs and closeout for slice 3

- **Files:** `docs/docgraph/mcp-runbook.md`, `docs/standards/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T18, T19
- **Acceptance:** harvest, child plan archived, zero findings.

**Slice 4: bug-crusher on the shared rules.**

### T21: RED for the shared skill

- **Files:** `tests/KyberWeave.Tests/SquadDeliveryExecutionSkillTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** T22's row is written and fails. Pins that name the old path move to the new one with their strings unchanged.

### T22: Move the execution contract to a canonical skill

- **Files:** `products/kyber-squad/skills/delivery-execution/SKILL.md` (new), `products/kyber-squad/agents/conductor/references/execution-and-review.md` (removed), `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/agents/conductor/references/spec-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/agents/bug-crusher.md`
- **Required skills:** `docs-dev`
- **Depends on:** T21
- **Acceptance:** D20. Bug-crusher §5 loads the skill and keeps no second copy. It stops editing plan status itself. `delegates-to` lists `architect`, `test-dev`, every developer agent, and `code-reviewer`. T22's row holds, and `skill validate`, `lint` and `scan` pass on the new skill.

### T23: Live check of the Claude conductor entry point

- **Files:** `.agent_scratchpad/T23/conductor-live-check.md`
- **Required skills:** `test-dev`
- **Depends on:** T22
- **Acceptance:** the checks in T23's row run in a live Claude Code session and the output is returned for T24 to record. Bug-crusher's escalated path also starts a specialist on a real run. If this run cannot reach a live session, the slice stays open on this task and the owner is told exactly what to run, because this fix relies on it.

### T24: Docs and closeout for slice 4

- **Files:** `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/todo/claude-conductor-live-verification.md`, `docs/todo/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T22, T23
- **Acceptance:** harvest, ADR waiver recorded, T23's evidence recorded, onboarding states only observed behaviour, the live-verification todo archived, child plan archived, zero findings.

**Slice 5: ready-to-paste tasks.**

### T25: RED for the task file

- **Files:** `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`, `tests/KyberWeave.Tests/PlanExecutionDigestTests.cs`, `tests/KyberWeave.Tests/OpenWorkValidatorTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterPlanReadRuleTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterLensCleanTreeTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterDefaultRulesTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs`, `tests/KyberWeave.Tests/RunStartResumeTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T26 to T30 are written and fail for the reason they name. If `ArbiterLensCleanTreeTests` shows the lens is not skipped, report that and amend D44's task.

### T26: Task file model and validator

- **Files:** `src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs`, `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs` (new), `src/KyberWeave.Core.Docs/Validation/PlanTaskFile.cs` (new), `src/KyberWeave.Core.Docs/Validation/PlanReadInstruction.cs` (new), `src/KyberWeave.Core.Docs/Validation/OpenWorkValidator.cs`, `src/KyberWeave.Core.Docs/Validation/DocsSchemaGate.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T25
- **Acceptance:** D18, D19, D39. Reachable through `docs validate` and the MCP validate tool. T26's row holds.

### T27: New Arbiter rule and the lens-skip repair

- **Files:** `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`, `src/KyberWeave.Core/Arbiter/ArbiterTriggerCatalog.cs`, `src/KyberWeave.Arbiter/Hooks/HookGitFacts.cs`, `src/KyberWeave.Arbiter/Hooks/HookPlanReader.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T25, T26
- **Acceptance:** fix 10 and D44. The new id is added and none renumbered, and the rule count in the file header is updated. T27's row holds. `SquadAutonomousGatesInventoryTests` stays green.

### T28: Resume from the branch

- **Files:** `src/KyberWeave.Core/RunStart/RunStartRunner.cs`, `src/KyberWeave.Core/RunStart/RunStartPlanner.cs`, `src/KyberWeave.Cli/Commands/Run/RunStartCommand.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T25
- **Acceptance:** D38. T28's row holds.

### T29: Architect, conductor, docs-dev and session rules

- **Files:** `products/kyber-squad/agents/architect.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/product-owner.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/agents/conductor/references/spec-path.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/agents/docs-dev.md`
- **Required skills:** `docs-dev`
- **Depends on:** T26, T28
- **Acceptance:** fixes 3 and 6. `plan-authoring.md:25` asks for path plus symbol, full test class names and exact commands, and for a `session` group. Prompts tell workers to commit with a `Task:` line. The conductor validates the file, pastes prompts unchanged and fills only the two slots. `git rm` joins the closed list. T29's row holds.

### T30: Prove per-task commits do not hide scope drift

- **Files:** `tests/KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** T25
- **Acceptance:** `GitFacts.GetChangedPathsSinceSnapshot` is proven to include committed paths, so `KW-ARB-DIFF-001` still sees them. If it does not, return the failure rather than changing the rule. T30's row holds.

### T31: Docs and closeout for slice 5

- **Files:** `docs/kyber-squad/architecture.md`, `docs/kyber-arbiter/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/adr/0053-kyber-arbiter-three-step-decision-gates.md`, `docs/adr/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T26, T27, T28, T29, T30
- **Acceptance:** harvest, new ADR, ADR 0053 amended, the plan index's policy text covers the task file, child plan archived, zero findings.

**Slice 6: review finishes.**

### T32: RED for the review changes

- **Files:** `tests/KyberWeave.Tests/ReviewFullSuiteGuardTests.cs`, `tests/KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs`, `tests/KyberWeave.Tests/ReviewGatePrerequisiteTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T33 and T34 are written and fail for the reason they name.

### T33: `--all` stop, `--gate`, `requires-paths`

- **Files:** `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs`, `src/KyberWeave.Core/Review/GateRunner.cs`, `src/KyberWeave.Core/Review/ReviewModel.cs`, `src/KyberWeave.Core/Configuration/ReviewConfig.cs`, `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs`, `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs`, `.kyber-weave/kyber-weave.yml`
- **Required skills:** `csharp-dev`
- **Depends on:** T32
- **Acceptance:** D15, D16, D17. A not-started gate has a result the verdict treats as failed, and the report separates it from a code failure. T33's row holds.

### T34: Reviewer and plan instructions

- **Files:** `products/kyber-squad/agents/code-reviewer.md`, `products/kyber-squad/skills/code-review/SKILL.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/bug-crusher.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`
- **Required skills:** `docs-dev`
- **Depends on:** T33
- **Acceptance:** fix 5's list. `delegates-to` on conductor and bug-crusher gains `review-lens` and `review-triage`, used only on `COUNCIL_SEATS_NEEDED`. The docs-impact review and any edit finish before the council starts. T34's row holds.

### T35: Docs and closeout for slice 6

- **Files:** `docs/code-review/architecture.md`, `docs/configuration.md`, `docs/cli-command-surface.md`, `docs/adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md`, `AGENTS.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T33, T34
- **Acceptance:** harvest, ADR 0059 amended, `AGENTS.md:78-80` still true and now names the `--all` stop, child plan archived, zero findings.

**Slice 7: project knowledge and onboarding.**

### T36: RED for project knowledge

- **Files:** `tests/KyberWeave.Tests/GateCatalogueRenderTests.cs`, `tests/KyberWeave.Tests/DocsInitReviewGatesTests.cs`, `tests/KyberWeave.Tests/GateCatalogueToolchainTests.cs`, `tests/KyberWeave.Tests/OnboardingCommandTests.cs`, `tests/KyberWeave.Tests/RunStartOnboardingTests.cs`, `tests/KyberWeave.Tests/ConfigRegTests.cs`, `tests/KyberWeave.Tests/KyberStandardsTemplatesTests.cs`, `tests/KyberWeave.Tests/PortableContentTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/StandardsCompletionSectionTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T37 to T40 are written and fail for the reason they name.

### T37: Catalogue repair and new gate entries

- **Files:** `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`, `src/KyberWeave.Core/Review/Catalogue/GateCatalogueFamily.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ReviewBlockWriter.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T36
- **Acceptance:** D32. Entries for a workflow linter, `shellcheck`, and a load check of the review config, each with its `os` restriction and install path. T37's row holds.

### T38: The `onboarding` setting and flow

- **Files:** `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfig.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfigLoader.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ConfigRegMarkdown.cs`, `src/KyberWeave.Core/Docs/Scaffolding/HostConfigYaml.cs`, `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/RunStart/RunStartPlanner.cs`, `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T36
- **Acceptance:** D6, D33, D34, D40. `run start` checks onboarding before anything else. T38's row holds.

### T39: `## Completion` and portable clean-up

- **Files:** `products/kyber-squad/standards/*/README.md`, `docs/standards/*/README.md`, `AGENTS.md`, `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md`, `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/docs-dev.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/skills/test-dev/SKILL.md`, `products/kyber-squad/skills/task-gate/SKILL.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/agents/conductor/references/autonomy.md`
- **Required skills:** `docs-dev`
- **Depends on:** T37, T38
- **Acceptance:** D5, D6, D7, D31. All 11 project-path lines leave portable files, and their meaning moves to a `Completion` section. The test standard does not cite the default fast loop for the tray until the [fast-loop todo](../../todo/fast-loop-default-tray-cargo-test.md) is decided. `docs-dev`'s list gains the onboarding command. `autonomy.md:17` and `:54` carry the exception. The onboarding skill passes `skill validate`, `lint` and `scan`. T39's row holds.

### T40: This repository's gate choices

- **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`, `docs/code-review/architecture.md`
- **Required skills:** `docs-dev`
- **Depends on:** T37, T38, T39
- **Acceptance:** `onboarding: done`. `sql-database-architect` and `github-devops` get gate sets that match the files they edit. Shell, workflow and config edits select a gate. T40's row holds.

### T41: Confirm `dotnet format --include` on Linux and Windows

- **Files:** `.agent_scratchpad/T41/format-include-check.md`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** a throwaway workflow on a `scratch/` branch, triggered on push and never merged, runs T41's row on `ubuntu-latest` and `windows-latest`. Output is returned for T42 to record, and the scratch branch is named in the pull request so the owner can delete it.

### T42: Docs and closeout for slice 7

- **Files:** `docs/configuration.md`, `docs/standards/README.md`, `docs/kyber-squad/onboarding.md`, `docs/cli-command-surface.md`, `docs/adr/README.md`, `docs/todo/task-gate-config-review.md`, `docs/todo/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T37, T38, T39, T40, T41
- **Acceptance:** harvest, new ADR, the three todos archived (the third only with T41's evidence), child plan archived, zero findings.

**Slice 8: shrink what workers read.**

### T43: RED for the new checks

- **Files:** `tests/KyberWeave.Tests/ReviewSuppressionsCommandTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterGateReportRuleTests.cs`, `tests/KyberWeave.Tests/TaskGateSelectionTests.cs`, `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs`, `tests/KyberWeave.Tests/SquadWorkerBaselineInjectionTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T44 to T47 are written and fail for the reason they name.

### T44: Suppression gate

- **Files:** `src/KyberWeave.Cli/Commands/Review/ReviewSuppressionsCommand.cs` (new), `src/KyberWeave.Core/Review/SuppressionScan.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`, `.kyber-weave/kyber-weave.yml`
- **Required skills:** `csharp-dev`
- **Depends on:** T43
- **Acceptance:** D28. Declared in `review.task-gates` and offered by the csharp catalogue family. T44's row holds.

### T45: Verify the gate report

- **Files:** `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`, `src/KyberWeave.Core/Arbiter/ArbiterTriggerCatalog.cs`, `src/KyberWeave.Core/Arbiter/Facts/GateReportFacts.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T43
- **Acceptance:** D29. T45's row holds.

### T46: `{tests}` token

- **Files:** `src/KyberWeave.Core/Review/TaskGateSelection.cs`, `src/KyberWeave.Core/Configuration/ReviewConfig.cs`, `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`, `.kyber-weave/kyber-weave.yml`
- **Required skills:** `csharp-dev`
- **Depends on:** T43, T44
- **Acceptance:** D30. This repository's `csharp-dev` and `test-dev` task gates run mapped tests. T46's row holds, including the measured fast path.

### T47: Single source for always-on worker text

- **Files:** `products/kyber-squad/shared/worker-baseline.md` (new), `src/KyberWeave.Core.Squad/Deployment/SquadWorkerBaselineInjector.cs` (new), `src/KyberWeave.Core.Squad/Rendering/SquadHarnessRenderer.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T43
- **Acceptance:** follows the `SquadResponsePreferencesInjector` pattern. Holds the Discovery order, the full-suite ban line and the batched-reads line. Agents switch to it in T52, not here. T47's row holds.

### T48: Check the harness behaviour this plan relies on

- **Files:** `docs/kyber-squad/requirements.md`
- **Required skills:** `docs-dev`
- **Depends on:** none
- **Acceptance:** every row of "Other harnesses" is confirmed against the installed harness or vendor documentation, or marked `[NOT LIVE-VERIFIED]` with its fallback. Includes Pi, Codex, Cursor and Warp subagent access to `codegraph_explore`, and a co-deployment of Antigravity with Pi, ZCode and Command Code. T48's row holds.

### T49: Build the rule map

- **Files:** `docs/kyber-squad/worker-rule-map.md` (new)
- **Required skills:** `docs-dev`
- **Depends on:** T44, T45, T46, T47
- **Acceptance:** one row per sentence to be removed, across the agents and the conductor text, and across all 27 files holding the ban. The 5 standard templates keep their line, because a standard is host-owned project content, and the map says so. A rule with no enforcing check stays and is marked so. Nothing is deleted.

### T50: Move the contract tests

- **Files:** `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/ArbiterTestDevContractTests.cs`, `tests/KyberWeave.Tests/WorkerRuleSingleHomeTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** T49
- **Acceptance:** each pin touching moved text, such as the `RED_EVIDENCE` template and the digest fields, points at its new home with the same string. The classes fail against today's text and pass after T52.

### T51: Seeded-defect trial

- **Files:** `.agent_scratchpad/T51/seeded-defect-trial.md`
- **Required skills:** `test-dev`
- **Depends on:** T50
- **Acceptance:** D37. The candidate text lives on a scratch branch for the "new" arm. Each of the nine defects has a row giving the stage caught on each arm, returned for T52 to record in the rule map. This is the gate for T52: any row caught later and not named in the map stops T52, and the text for that rule stays.

### T52: Remove the text

- **Files:** `docs/kyber-squad/worker-rule-map.md`, `products/kyber-squad/agents/*.md`, `products/kyber-squad/skills/task-gate/SKILL.md`, `products/kyber-squad/skills/task-gate/references/rework.md` (new), `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/skills/test-dev/SKILL.md`, `products/kyber-squad/skills/kilo-review-loop/SKILL.md`, `products/kyber-squad/skills/typescript-dev/references/esm-and-gates.md`, `products/kyber-squad/skills/rust-dev/references/gates-and-platforms.md`, `products/kyber-squad/skills/go-dev/references/gates-and-platforms.md`, `products/kyber-squad/skills/csharp-dev/references/build-commands.md`
- **Required skills:** `docs-dev`
- **Depends on:** T51
- **Acceptance:** only rows the trial passed are applied. D27, D43. The full-suite ban stays as one always-on line. `dotnet format analyzers` leaves the task-end text. The 2.7 KB duplicate leaves `conductor.md`. Baseline diagnostics apply only where warnings do not fail the build. Scope, weakened-test and masked-output rules stay in the agent file. T52's row holds, and byte counts are reported.

### T53: Docs and closeout for slice 8

- **Files:** `docs/kyber-squad/architecture.md`, `docs/code-review/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/todo/README.md`, `docs/todo/watch-worker-text-trim-in-real-runs.md` (new), `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T48, T52
- **Acceptance:** harvest, ADR waiver recorded, the test mapping todo archived, a new todo for comparing findings over the next few runs (this plan authorizes it), child plan archived, this file brought in and archived with its mapping to the eight children, zero findings on `--merge-ready`.

## Dependency graph

Slices land in order: 1, 2, 3, 4, then 5 and 6 (built in parallel, merged one at a time), then 7, then 8.

Inside each slice:

- Slice 1: T1 and T6 stand alone. T2 unlocks T3 and T5. T3 unlocks T4. T6 and T2 unlock T7. T4 and T5 unlock T8. T9 is last.
- Slice 2: T10 unlocks T11 and T13. T11 unlocks T12. T11 and T12 unlock T14 and T15. T16 is last.
- Slice 3: T17, then T18, then T19, then T20.
- Slice 4: T21, then T22, then T23, then T24.
- Slice 5: T25 unlocks T26, T28 and T30. T26 unlocks T27. T26 and T28 unlock T29. T31 is last.
- Slice 6: T32, then T33, then T34, then T35.
- Slice 7: T36 unlocks T37 and T38. T37 and T38 unlock T39, then T40 (both edit `AGENTS.md`). T41 stands alone. T42 is last.
- Slice 8: T43 unlocks T44, T45 and T47. T44 unlocks T46 (both edit `.kyber-weave/kyber-weave.yml`). All four unlock T49. T48 stands alone. T49, then T50, then T51, then T52, then T53.

Workers run in parallel up to `review.max-concurrent-workers` (4) where file scopes allow. Each slice ends with one council, then its closeout.

## Risks

- **More writers on the CodeGraph lock.** This session's own calls show auto-sync disabled because a lock was held past the retry budget. Adding `sync` from the docs server and workers could make this worse, and could leave the CodeGraph MCP server's index frozen. Mitigation: busy is a note, not a failure (D23); the once-a-minute limit; the zero-wait lock; T12 measures it; if the banner appears, raise the interval or leave the sync to whichever process holds the watcher.
- **A stop blocks work on a machine with a broken tool.** That is the owner's decision (D1). `false` is the escape, the failure names the fix, and read-only runs are unaffected. Other repositories with governed docs and no CodeGraph stop until they install it or set `false`: this is called out in the release notes.
- **Docs tools fail for roles that need them to report the failure.** `docs_get_source_repository_and_revision` always answers (D23).
- **Task file and plan disagree.** `KW-DOC-TASKS-002` and `-003` catch it. A digest that ignores status edits could miss an edit to prose outside the parsed fields, which is acceptable because the Arbiter reads the same fields.
- **Commit per task changes what the Arbiter sees.** T30 and D44 cover scope drift and lens applicability. `.agent_scratchpad` stays out of commits.
- **A prompt richer than the plan's task text** could trip `KW-ARB-SCOPE-002` (`adds-work`), because the decision model compares prompt and task. The architect writes both from the same content, and the plan's task text keeps the acceptance criteria.
- **Fix 7 loses a check.** The rule map, the trial and T52's gate exist for this. T53's todo watches real runs. A raw `dotnet test` is still possible, and the always-on line stays.
- **The trial is only as good as its packets.** Same harness, model and packet on both arms (D37), and only stages are compared, not wording.
- **Slices interleave with other work.** `run start` re-fetches for each slice, and a child plan is carved from the design at that slice's start, so files named here are checked by path plus symbol then.
- **Open pull requests at slice 1's landing fail the naming job.** Rename the branch through the GitHub API, which keeps the pull request, and edit the title.
- **`[commandcode]`-style suffixes land in the changelog.** Accepted (D42).
- **Disk use.** About 223 MB per worktree until cleanup. Cleanup is already guaranteed on all exit paths.

## Out of scope

- Fix 9, routing each request with the decision model: [issue 535](https://github.com/dpalfery/kyber-weave/issues/535).
- Any change to model assignment or model profiles. The owner handles model targeting separately.
- Collapsing RED and GREEN into one dispatch. The adversarial split stays.
- A size limit on plans.
- A shell for the architect, and the fact-finder agent in issue 221.
- The per-harness shell hook for the full-run guard: that todo stays open.
- Repairing the `KW-ARB-MODE-001` boolean comparison noted in the findings: reported, not fixed here.
- Closing the review-council Rust, Tauri and Go standards todo, which looks finished: left for the owner.
- Widening `context7` grants.

## Verification gates

- Each task's row in the Test contract, with a non-zero test count. No task runs the full suite.
- `docs_validate` (mergeReady false) and `docs_drift` after every plan save and every docs task, with provenance `root=` equal to the checkout.
- Each slice: `task-gate` per developer task, one council with `--council`, CI on the pull request, and `docs validate --merge-ready` and `docs drift` after its child plan is archived.
- After slice 1: a scratch pull request proves the naming job. After slice 2: a real run on Command Code reports `codegraph` above 0 and fewer direct reads. After slice 6: a real run reaches a verdict with one suite run. After slice 8: compare a bug-crusher run of similar size against the table above and report the time and tokens, as an estimate is not a result.

## Review

Each slice's pull request goes through one `code-reviewer` council over its whole change, after the draft is pushed, with nothing else editing that tree. The verdict goes in the pull request description. Human and bot review follow once CI passes.

## Closeout

**The programme delivered across eight slices, each in its own pull request on its own child plan,
and this last pull request archived the parent.** `docs-dev` closed each child in its slice's pull
request, harvesting that slice's Design harvest rows and writing or amending its ADRs. The final slice
— slice 8, `factory/feat/conductor-run-speed-shrink-reads` — copied this file out of
`claude/docs/conductor-bug-crusher-run-speed` with `git show` rather than merging that branch, edited
the copy in place for the archive's extra depth, added the [Closeout mapping](#closeout-mapping) below,
and moved this row to Archived Plans in the [plan inventory](../../plans/README.md). The design branch
is never merged into any delivery branch; D61 records the mechanism.

**Each child's state, verified in this tree rather than assumed.**

| Slice | Child plan | Delivery branch | State verified here |
|---|---|---|---|
| 1 | `2026-11-07-conductor-bug-crusher-run-speed-clean-start.md` | `factory/feat/conductor-run-speed-clean-start` | Archived in `docs/archive/plans/`, closed 2026-11-07. ADR 0060 written; the settings, `run start` and the `pr-naming` job harvested |
| 2 | `2026-11-07-conductor-run-speed-code-index.md` | `factory/feat/conductor-run-speed-code-index` | Archived, closed 2026-11-07. ADRs 0061 and 0062 written and ADR 0050 amended in place |
| 3 | `2026-11-07-conductor-run-speed-standards-tool.md` | `factory/feat/conductor-run-speed-standards-tool` | Archived, closed 2026-11-08. No new ADR — waiver, recorded in its own closeout |
| 4 | `2026-11-07-conductor-run-speed-shared-execution.md` | `factory/feat/conductor-run-speed-shared-execution` | **Still open** in this tree's `docs/plans/`: its archive commit exists only on its own branch, which has not merged. The `delivery-execution` skill it creates is in the tree and documented |
| 5 | `2026-11-07-conductor-run-speed-ready-tasks.md` | `factory/feat/conductor-run-speed-ready-tasks` | Archived, closed 2026-11-08. ADR 0063 written and ADR 0053 amended in place |
| 6 | `2026-11-07-conductor-run-speed-review-finishes.md` | `factory/feat/conductor-run-speed-review-finishes` | Archived, closed 2026-11-07. ADR 0059 amended in place |
| 7 | `2026-11-07-conductor-run-speed-project-knowledge.md` | `factory/feat/conductor-run-speed-project-knowledge` | **Still open** in this tree's `docs/plans/` and marked **Review required**: it does not close as delivered, and its own row is missing from the plan inventory. ADR 0064 is written and the standards harvest landed |
| 8 | `2026-11-07-conductor-run-speed-shrink-reads.md` | `factory/feat/conductor-run-speed-shrink-reads` | Archived by this pull request, beside this file |

**Residuals carried out of the programme, not closed by it.** Two slices remain open in this tree, and
three pieces of work a slice recorded rather than completed: the seeded-defect trial that gates slice
8's text removals never ran; the `review suppressions` command from slice 8 ships without the task-gate
declaration and catalogue entry its acceptance names; and the harness behaviours no tree can settle are
recorded as `[NOT LIVE-VERIFIED]` with their fallbacks. Each has a home in the [todo
inventory](../../todo/README.md), and none of them is described here as delivered.

## Closeout mapping

Every decision in this design's ledger — D1 to D44 and AP1 — maps to the child that delivered it and to
the canonical destination that now carries it, or to a recorded waiver. This design wrote no code
itself: it was the approved design, and each child is the unit of delivery, so a row that names a child
also names the durable page that child harvested into. Documentation-impact for every row:
`extends-component`. Last reviewed: 2026-11-08. Status: archived.

**By child.** Each row maps one child's decisions, harvest rows and ADRs to the live page that carries
them, so the destination column is the evidence and the third column says what it holds.

| ID | Destination | Child, and what it carries |
|---|---|---|
| D8, D9, D14, D21, D33, D41, D42, A1, H1, H15 | [`run start`](../../cli-command-surface.md#run--run-start), [the harness token](../../kyber-squad/architecture.md#the-harness-token-is-substituted-once-in-the-render-pipeline) and [the CI naming check](../../ci-pipelines/workflows-runbook.md#the-pull-request-naming-check-and-its-recheck) | Slice 1, the clean start: the run-start verb and its exit codes, the harness name in branch and title, the settings, `{{harness}}`, and the `pr-naming` job with its recheck |
| D1, D10, D11, D22, D23, D24, D25, D26, A2, A3, A4, H3, H4, H5, H6 | [DocGraph runbook](../../docgraph/mcp-runbook.md) | Slice 2, the code index where workers run: `doctor --ensure`, `sync`, the docs server's refresh, `STATUS: TOOL_FAILURE`, the provenance fields, the worktree binding, the `codegraph` grant and the stop rule that replaced "warn and continue" |
| D4, H7 | [Coding standards, a standard is read through the tool](../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) | Slice 3, standards through the tool: the tool as the only route to a standard, the repaired topic match and the documented cut |
| D20, H8 | [Kyber-Squad architecture](../../kyber-squad/architecture.md) | Slice 4, bug-crusher on the shared rules: one `delivery-execution` skill both entry points load. The child's own archive and the rest of its harvest complete on its branch, which has not merged |
| D2, D18, D38, A5, A6, H9 | [the tracked task file](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) | Slice 5, ready-to-paste tasks: the task file and its execution digest, the plan as authority, the paste-and-fill dispatch and the `Task:` trailer |
| D15, D16, D17, A7, H11 | [Review council architecture, reaching a verdict in one pass](../../code-review/architecture.md#reaching-a-verdict-in-one-pass) | Slice 6, review finishes: the `--all` stop, `review gates --gate`, `requires-paths`, `COUNCIL_SEATS_NEEDED`, and the review running alone last |
| D5, D6, D7, D31, D32, D34, D40, A8, H2, H12, H13 | [Coding standards](../../standards/README.md) and [Configuration, intake assessment](../../configuration.md#intake-assessment) | Slice 7, project knowledge and onboarding: `## Completion` in every standard, the `onboarding` setting and its values, the catalogue's `full-suite` key and plumbing gates. **Two verified gaps:** `docs onboarding` is not in the [`docs` verb table](../../cli-command-surface.md#docs--docgraph-governance), and this child's plan is still open and marked Review required |
| D3, D27, D28, D29, D30, D36, D37, D43, H14 | [Worker rule map](../../kyber-squad/worker-rule-map.md) | Slice 8, shrink what workers read: no plan size limit, the full-suite ban from one injected source, the suppression gate, `KW-ARB-GATE-REPORT-001`, `{tests}`, the rule map, the trial that has not run, and `cleanupcode` off in the task-end text |
| D19 | [Rule reference, plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) | Slice 5: `KW-DOC-TASKS-001` to `-006` and `KW-DOC-LIFECYCLE-005` |
| D39, D44, H10 | [Kyber Arbiter architecture, the rules](../../kyber-arbiter/architecture.md#the-rules) and [rule reference, shipped rules](../../ci-pipelines/rule-reference.md#shipped-rules--default-rulesyml) | Slices 5 and 8: the specification form of the task file (a verified gap in the [specification index](../../specs/README.md)), the committed-plus-dirty lens basis, `KW-ARB-PLANREAD-001` and `KW-ARB-GATE-REPORT-001` at 20 shipped rules |
| D12 | [Plan inventory](../../plans/README.md#plan-decisions-and-closeout-policy) | All eight: one plan per slice, each archived in its own pull request, and this file archived by the last one |
| D35 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md) | All eight: the ADR plan, delivered as ADRs 0060 to 0064 with 0050, 0053 and 0059 amended in place — see [ADR disposition](#adr-disposition) |
| D13 | Documentation waiver — the build order across the eight slices. It records sequencing, not behaviour, and every slice it orders has its own archived plan | All eight |
| AP1 | Documentation waiver — approval provenance for the Test contract and the run's execution. No lasting artefact cites the approval itself, and each child recorded its own carry of it | All eight |

**One row per decision.** The same mapping, read against the ledger row by row; the destination column
of the summary above is what the closeout gate reads.

| ID | Child | Canonical destination |
|---|---|---|
| D1 | 1, 2 | [ADR 0061](../../adr/0061-code-editing-runs-stop-on-unusable-tools.md); [Configuration, stop on tool failure](../../configuration.md#stop-on-tool-failure) |
| D2 | 5 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md); [Kyber-Squad architecture, the tracked task file](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch); [plan inventory policy](../../plans/README.md#plan-decisions-and-closeout-policy) |
| D3 | 8 | [Worker rule map](../../kyber-squad/worker-rule-map.md) — regrowth is stopped by one home per rule and `WorkerRuleSingleHomeTests`, not by a size limit, which is this decision's substance |
| D4 | 3 | [Coding standards, a standard is read through the tool](../../standards/README.md#a-standard-is-read-through-the-tool-not-as-a-file) |
| D5 | 7 | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md); [Coding standards](../../standards/README.md) and each standard's `## Completion` |
| D6 | 7 | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md); [Configuration, intake assessment](../../configuration.md#intake-assessment) |
| D7 | 7 | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md); [Kyber-Squad onboarding, intake assessment](../../kyber-squad/onboarding.md#intake-assessment-opt-in-per-repository) |
| D8 | 1 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md); [`run start`](../../cli-command-surface.md#run--run-start) |
| D9 | 1 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md); [the harness token](../../kyber-squad/architecture.md#the-harness-token-is-substituted-once-in-the-render-pipeline); [CI runbook, the naming check](../../ci-pipelines/workflows-runbook.md#the-pull-request-naming-check-and-its-recheck) |
| D10 | 2 | [ADR 0061](../../adr/0061-code-editing-runs-stop-on-unusable-tools.md); [DocGraph runbook](../../docgraph/mcp-runbook.md) |
| D11 | 2 | [ADR 0062](../../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md); [DocGraph runbook](../../docgraph/mcp-runbook.md) |
| D12 | all eight | [Plan inventory](../../plans/README.md) — one plan per slice, each archived in its own pull request, and this file archived by the last one |
| D13 | all eight | Documentation waiver — the build order across the slices. It records sequencing, not behaviour, and every slice it orders has its own archived plan |
| D14 | 1 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md); [CI runbook, the naming check and its recheck](../../ci-pipelines/workflows-runbook.md#the-pull-request-naming-check-and-its-recheck) |
| D15 | 6 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 6; [Review council architecture, reaching a verdict in one pass](../../code-review/architecture.md#reaching-a-verdict-in-one-pass) |
| D16 | 6 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 7; [`review gates --gate`](../../cli-command-surface.md#review--the-deterministic-review-half) |
| D17 | 6 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 8; [Configuration, review](../../configuration.md#review) |
| D18 | 5 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md); [the tracked task file](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) |
| D19 | 5 | [Rule reference, plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) — `KW-DOC-TASKS-001` to `-006` and `KW-DOC-LIFECYCLE-005` |
| D20 | 4 | [Kyber-Squad architecture](../../kyber-squad/architecture.md) — the `delivery-execution` skill both entry points load. Slice 4's own archive and harvest complete on its branch, which has not merged |
| D21 | 1 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md); [`run start`](../../cli-command-surface.md#run--run-start) — its flags and four exit codes |
| D22 | 2 | [DocGraph runbook](../../docgraph/mcp-runbook.md) — `doctor --ensure`, `sync` as the routine verb and `index` as the single repair |
| D23 | 2 | [ADR 0062](../../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md); [DocGraph runbook](../../docgraph/mcp-runbook.md) — the 60-second interval, the 30-second bound and the busy-lock note |
| D24 | 2 | [DocGraph runbook, git worktrees and MCP binding](../../docgraph/mcp-runbook.md#git-worktrees-and-mcp-binding); [Kyber-Squad onboarding, worktree binding](../../kyber-squad/onboarding.md#worktree-mcp-setup-and-canonical-binding) — the `branch=` and `repo=` fields and the compare-the-repository rule |
| D25 | 2 | [ADR 0061](../../adr/0061-code-editing-runs-stop-on-unusable-tools.md); [DocGraph runbook](../../docgraph/mcp-runbook.md) — a worktree indexes itself and never shares the main checkout's index |
| D26 | 2 | [ADR 0050](../../adr/0050-universal-kyber-weave-mcp-grant.md) amended in place — `codegraph` granted through the orchestrator-excluded predicate, `context7` not widened |
| D27 | 8 | [Worker rule map, the full-suite ban](../../kyber-squad/worker-rule-map.md#the-full-suite-ban); [the shared worker baseline](../../kyber-squad/architecture.md#the-shared-worker-baseline-injected-once-per-dispatched-worker) |
| D28 | 8 | [Review council architecture, the suppression gate](../../code-review/architecture.md#the-suppression-gate); [rule reference](../../ci-pipelines/rule-reference.md#suppressions--review-suppressions); [`review suppressions`](../../cli-command-surface.md#review--the-deterministic-review-half). **Residual:** no task gate selects it — [todo](../../todo/review-suppressions-gate-not-selected.md) |
| D29 | 8 | [Kyber Arbiter architecture, the rules](../../kyber-arbiter/architecture.md#the-rules); [rule reference, shipped rules](../../ci-pipelines/rule-reference.md#shipped-rules--default-rulesyml) — `KW-ARB-GATE-REPORT-001`, its three answers and the count at 20 |
| D30 | 8 | [Review council architecture, the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs); [Configuration, review](../../configuration.md#review) — `{tests}` and `review.test-map` |
| D31 | 7 | [Test standard, `## Completion`](../../standards/test/README.md#completion); [Coding standards](../../standards/README.md) |
| D32 | 7 | [Review council architecture, the shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue); [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 5 |
| D33 | 1, 7 | [Configuration, run controls](../../configuration.md#run-controls), [stop on tool failure](../../configuration.md#stop-on-tool-failure) and [intake assessment](../../configuration.md#intake-assessment); [DocGraph architecture, mode properties](../../docgraph/architecture.md#mode-properties-are-not-paths) |
| D34 | 7 | Child: slice 7, whose closeout owns the harvest. **Verified gap:** `docs onboarding` is absent from the [`docs` verb table](../../cli-command-surface.md#docs--docgraph-governance), recorded here rather than claimed as delivered |
| D35 | all eight | The ADR plan itself, delivered as [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md), [0061](../../adr/0061-code-editing-runs-stop-on-unusable-tools.md), [0062](../../adr/0062-the-docs-server-refreshes-codegraph-and-fails-its-tools.md), [0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) and [0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md), with [0050](../../adr/0050-universal-kyber-weave-mcp-grant.md), [0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) and [0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) amended in place. Fixes 2, 6 and 7 carried a waiver, as this row planned |
| D36 | 8 | [Worker rule map](../../kyber-squad/worker-rule-map.md) — a permanent reference, not a file in a plan folder that an archive would take away |
| D37 | 8 | **Residual:** the trial has not run — [run the seeded-defect trial](../../todo/seeded-defect-trial-never-ran.md) carries its two-arm design, its nine defects and what to compare |
| D38 | 5 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md); [`run start --resume`](../../cli-command-surface.md#run--run-start) |
| D39 | 5 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the specification form of the task file. **Verified gap:** the [specification index](../../specs/README.md) does not yet state it |
| D40 | 7 | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md); [Configuration, intake assessment](../../configuration.md#intake-assessment) — `declined` behaves as `done` |
| D41 | 1, 8 | [The harness token](../../kyber-squad/architecture.md#the-harness-token-is-substituted-once-in-the-render-pipeline) and [the harness behaviour matrix](../../kyber-squad/requirements.md#harness-behaviour-the-product-relies-on), which records the co-deployment limit as `[NOT LIVE-VERIFIED]` |
| D42 | 1 | [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md); [CI runbook, the naming check](../../ci-pipelines/workflows-runbook.md#the-pull-request-naming-check-and-its-recheck) — the branch format, the title suffix and the Dependabot exemption |
| D43 | 8 | [Worker rule map, the task-end fix pass](../../kyber-squad/worker-rule-map.md#the-task-end-fix-pass-and-build-isolation) — `cleanupcode` off by default in the task-end text, the skill kept for InspectCode review work |
| D44 | 5 | [ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) amended in place, decision 9 — committed plus dirty changed paths, and an empty set no longer a Markdown-only skip |
| AP1 | all eight | Documentation waiver — approval provenance for the Test contract and the run's execution. No lasting artefact cites the approval itself, and each child recorded its own carry of it |
| Harvest | 1–8 | Every row of the [Design harvest](#design-harvest) above was delivered by the child named beside it: 1 for `run start` and the naming job, 2 for the runbook and the settings, 3 for the standards tool, 4 for the `delivery-execution` skill, 5 for the task file and its rule ids, 6 for the review changes, 7 for `## Completion` and onboarding, and 8 for the rule map, the harness matrix and the new task gates |
| ADRs | 1–8 | **No new ADR beyond D35's plan — each slice's disposition is recorded in its own closeout.** Slices 1, 2, 5 and 7 wrote ADRs 0060 to 0064; slices 6 and 4 amended ADR 0059 and stayed inside ADR 0036; slice 3 and slice 8 took an ADR waiver with its reason. 0064 is the highest number in the corpus and no number was consumed twice |

