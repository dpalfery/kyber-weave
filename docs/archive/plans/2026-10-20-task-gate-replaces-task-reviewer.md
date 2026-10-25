---
id: plans/2026-10-20-task-gate-replaces-task-reviewer
title: "Replace the per-task task-reviewer with a deterministic agent exit gate"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-20
development-mode: test-first
---

# Replace the per-task task-reviewer with a deterministic agent exit gate

**Status: Complete 2026-10-20.** T1-T6 delivered; the closeout section at the end maps decisions
D1-D19 to where each landed. The work is uncommitted at archive time; the verification evidence
below is what the closeout pass ran.

**Development mode:** `test-first` (packet-supplied). **Autonomy:** `autonomous`
(packet-supplied; the repository Config Reg also declares `<autonomy>: autonomous`).

---

## 1. Problem and goal

### 1.1 Problem

[ADR 0005](../../adr/0005-task-level-fast-review.md) puts an LLM `task-reviewer` audit, up to
three passes, after every worker completion. Since that audit was added, task completion
takes about three times as long. The part of it that matters, the deterministic checks
(format, Roslyn analyzers, linters), does not need a model. Those checks belong in a script
that the worker runs before it reports done. `code-reviewer`, the end-of-run council, already
reviews the whole run and stays the single review.

### 1.2 Goal

1. Remove `task-reviewer` from the product, conductor, docs and tests.
2. Extend `kyber-weave review gates` with per-agent **task gates**
   (`review.task-gates` in `.kyber-weave/kyber-weave.yml`, `--agent`, `--paths`/`--base`,
   `--task`, `--fix`, and the `{changed}` / `{task}` argv tokens). The argv-only injection
   contract stays as it is.
3. Add a `task-gate` skill that every developer agent runs before it reports done. It is a
   fix-and-rerun loop with a retry cap, and the gate result goes in the completion digest. The
   conductor rejects a developer digest that has no gate result.
4. Amend ADR 0005 in place so it states only the current decision (owner override, D2). Reword
   the ADR index's never-edit rule to the in-place rule (owner direction, D17).

### 1.3 Out of scope

- Hooks of any kind (packet decision 2).
- ReSharper `inspectcode` in the per-task path. It stays the end-of-run `inspectcode` gate.
- A generic changed-file to test-project mapping (deferred, D9).
- Changes to the verdict engine, the council lenses, or `review.gates` semantics.
- Commit, push, or PR.

---

## 2. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Keep or remove `task-reviewer`? | Remove it entirely: agent, `agents/task-reviewer/` references, `migration/task-reviewer.md`, bundle entry, README, conductor ladder/ESCALATION/reviewer-session text, architect plan-authoring, bug-crusher skill, code-reviewer mentions, docs, tests. | Packet decision 1. The loss of the per-task RED/GREEN-ordering audit is accepted. The council's `test-adequacy` lens covers part of it, and the conductor still checks mechanically that a RED digest exists before it dispatches GREEN (§4.3). | ANSWERED — packet agreed decision 1 (user) |
| D2 | Superseding ADR, or amend ADR 0005? | **Amend ADR 0005 in place.** It states only the current decision: a deterministic agent exit gate through the `task-gate` skill and `review.task-gates`, `code-reviewer` as the single review, `task-reviewer` removed, and the per-task RED/GREEN-ordering audit loss accepted. Every contradictory passage is rewritten or removed. Add an `Amended 2026-10-20: <one-line reason>` note (ADR 0016 precedent, lines 16/28), retitle the ADR (for example "Deterministic Agent Exit Gate Ahead of the Council"), and sync the `docs/adr/README.md` row. **No new ADR.** | This overrides the packet's "add a new ADR superseding 0005". ADR 0016 is the precedent for in-place amendment. The deterministic-fix-pass half of 0005 survives and is reworded as the exit gate. | ANSWERED — owner decision relayed by conductor 2026-10-20 |
| D3 | Enforcement mechanism? | No hooks. A `task-gate` skill plus a rule in each developer agent: run the gate, fix failures, rerun, paste the result in the digest. The conductor rejects a developer digest that has no `TASK_GATE:` line. | Packet decision 2. | ANSWERED — packet agreed decision 2 (user) |
| D4 | Which agents? | Developer agents only: csharp-dev, dal-dev, python-dev, react-dev, maui-dev, tauri-dev, test-dev, pulumi-dev, sql-database-architect, github-devops. Not architect, research-agent, azure-reader, product-owner, docs-dev, bug-crusher(-investigator), conductor, or reviewers. | Packet decision 3. | ANSWERED — packet agreed decision 3 (user) |
| D5 | Runner and config shape? | Extend `review gates`. `review.task-gates:` maps agent name (ConfigSlug) to an ordered gate list. Each gate has `{id, run: [argv], blocking (default true), timeout-seconds (default 900), os?: windows\|unix, include?: [globs], fix?: [argv]}`. `--agent <name>` selects only that agent's task gates (repo-wide `review.gates` do not run). Changed set: `--paths <p>...`, or else `git diff --name-only --diff-filter=ACMRT <--base, default HEAD>` ∪ `git ls-files --others --exclude-standard`. Same `kyber-weave.review-gates/v1` JSON report, and a non-zero exit on a blocking failure. | Packet decision 4. `include`, `fix` and `{task}` are refinements: D6, D7, D8. | ANSWERED — packet agreed decision 4 (user); refinements per D6–D8 |
| D6 | How `{changed}` expands, and how it stays injection-safe | `{changed}` must be a **whole** argv element. It expands to one argv entry per changed file that matches the gate's `include` globs (PathGlob; all files when `include` is absent). An empty expansion, or no changed file matching `include`, skips the gate (`KW-REVIEW-026`, Info, not in `Gates[]`). Each path is normalized to forward slashes and must resolve under the root. A path that is absolute, escapes the root with `..`, or begins with `-` is refused, the run exits 1, and no process starts (`KW-REVIEW-028`). The loader rejects `{changed}` embedded in a larger string (`--include={changed}`) and any unknown `{identifier}` token, with a nearest-token hint. A gate whose `os` does not match the host is skipped (`KW-REVIEW-026`). | Keeps ADR 0002's argv-only contract: nothing is a shell string, and a filename cannot become an option. A leading `-` is refused because `dotnet format --include` would otherwise read `--x` as an option. Fail-closed matches `PathGlob`'s posture. Cheap to reverse: config and runner are local to T1G/T2G. | ANSWERED — autonomous: recommendation accepted (source: ADR 0002, `ReviewGate` remarks, `PathGlob`) |
| D7 | Who runs the fixing form of the tools? | An optional per-gate `fix: [argv]` and a `--fix` flag. With `--fix`, each non-skipped gate runs `fix` (same token expansion) before its `run`. A failed fix is a Warning (`KW-REVIEW-029`) and does not fail the gate. The verify `run` decides. The skill passes `--fix` on the first run of every dispatch. | Packet decision 5 says the worker runs the fixing form first. Putting it in host config keeps the portable skill free of repo-specific fixer commands. It is additive and optional, so cheap to reverse. | ANSWERED — autonomous: recommendation accepted (source: ADR 0005 fix-pass, packet decision 5) |
| D8 | Parallel workers share `obj/`/`bin/`. How does the build gate isolate output? | A `{task}` token, allowed embedded in a string, expands from `--task <id>` (ConfigSlug-validated). A gate that uses `{task}` without `--task` exits 1 (`KW-REVIEW-028`). This repo's build gate: `dotnet build KyberWeave.sln -c Release --artifacts-path .agent_scratchpad/{task}/artifacts`. `.agent_scratchpad/` is gitignored. | csharp-dev's existing completion-gate rule already requires per-task artifacts paths. A slug cannot carry a separator or option. | ANSWERED — autonomous: recommendation accepted (source: csharp-dev.md completion gate, `.gitignore:14`) |
| D9 | Changed-file to test-project mapping | **Defer.** This repo's task gates run no tests. The worker's Test-contract command (RED/GREEN evidence) stays the test evidence, and the council's `test` gate runs the full suite once. Record the follow-up as a todo in T6. | This repo has one .NET test project (full suite about 25 min with coverage). "Touched test project" would mean the whole suite on every test change, which brings back the slowdown this plan removes. `fast-loop` is barred as evidence (AGENTS.md). Cheap to reverse: config-only addition later. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D10 | Per-agent gate content for this repo | Individually anchored gates (`&fmt-whitespace`, `&fmt-style`, `&fmt-analyzers`, `&dotnet-build`, `&dash-typecheck`, `&dash-lint`, `&tray-ui-typecheck`, `&cargo-fmt`, `&cargo-clippy`) referenced by alias per agent. **.NET** (include `**/*.cs`, `**/*.csproj`, `**/*.props`, `**/*.targets` as applicable): `dotnet format KyberWeave.sln whitespace\|style\|analyzers --verify-no-changes [--severity warn] --include {changed}`, each with its `fix` form (same without `--verify-no-changes`), plus the Release `dotnet-build` with `{task}` artifacts. **dash** (include `dash/**`, excluding the tray when its own gates cover it): `npm run --prefix dash typecheck`, `npm run --prefix dash lint`. **tray**: `npm run --prefix dash/tray/ui typecheck`, `cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check` (fix: without `--check`), `cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings`. Assignment: csharp-dev, dal-dev, maui-dev, pulumi-dev, sql-database-architect, github-devops → .NET set; react-dev → dash + tray-ui; tauri-dev → cargo + dash + tray-ui; test-dev → .NET + dash; **python-dev → no entry** (the repo has no ruff/mypy config). ReSharper `cleanupcode` is not a configured gate. The skill allows it as an optional scoped fix pass, per `resharper-clt`. | Packet decision 5. `include` filtering lets shared anchors sit on every agent without false work. YAML aliases of individual gate mappings work with typed deserialization. List concatenation would need a custom node parser, which this plan rejects. | ANSWERED — packet decision 5 (user); anchoring form autonomous: recommendation accepted |
| D11 | Retry cap and conductor rejection | Inside one dispatch the skill allows the first run plus at most **2** fix-and-rerun cycles (3 runs, inside the worker's existing 3-iteration cap). If the gate still fails, the worker returns `STATUS: ESCALATION`, `CIRCUIT_BREAKER_TRIGGER: ITERATION_CAP_EXCEEDED`, `FAILURE_CLUSTER: task-gate/<gate-id>`. The conductor treats a developer digest with a missing or `FAIL` `TASK_GATE` as rework for the same worker session. The rework counts in the existing per-cluster tally, keyed `task-gate/<gate-id>` (`task-gate/missing` when absent), max 2 rework dispatches, then the breaker trips. | Packet decision 2. The token set stays closed (`AssertClosedCircuitBreakerTriggerSet`). | ANSWERED — packet decision 2 (user); cluster keying autonomous: recommendation accepted |
| D12 | Digest format and "CLI unavailable" | Line: `TASK_GATE: PASS \| FAIL \| NONE_DECLARED \| UNAVAILABLE — agent=<name> gates=<n> failed=<ids\|none> skipped=<n> report=<path> runs=<k>`. `NONE_DECLARED` (KW-REVIEW-027) is a valid result. `UNAVAILABLE` (no `kyber-weave` on PATH) is accepted by the conductor and recorded in the run's findings for the council gates, never treated as a pass. The skill invokes `kyber-weave` on PATH. Inside the Kyber-Weave checkout the fallback is `dotnet run --project src/KyberWeave.Cli -c Release --` (see R3). | A gate result line that is always present makes the conductor's rejection mechanical. | ANSWERED — autonomous: recommendation accepted |
| D13 | Report schema | Keep `kyber-weave.review-gates/v1` unchanged. Skipped gates do not appear in `Gates[]`. They appear as Info diagnostics `KW-REVIEW-026`. New rule ids **KW-REVIEW-026** (gate skipped: os/no matching changed files), **027** (no task gates declared for agent), **028** (changed-set or token resolution refused/failed), **029** (fix command failed). These ids are not used anywhere today (rule-reference lists 001–012, 020–025, 030–032). | Rule ids are permanent (AGENTS.md), so add, never reuse. Unchanged schema means `review verdict` readers are unaffected. | ANSWERED — autonomous: recommendation accepted |
| D14 | Hotshot golden fixture | Leave `Fixtures/kyber-squad-hotshot-golden.json` byte-identical (SHA-pinned historical snapshot of an external tree, `ExpectedManifestSha256`, opt-in external provenance check). Instead, add `task-reviewer` to `RetiredAgentIdentities` and remove it from `EvolvedAgentIdentities` in `HotshotGoldenContractTests`. Add `task-gate` to `CanonicalOnlySkillIdentities`. `ValidateTaskReviewer`/`ValidateConductorDelegation` keep validating the historical manifest. In `SquadCanonicalContentTests`, drop the `task-reviewer` rows from `ExpectedSourceHashManifest`, `ExpectedMigratedAgents` and `SourceAgentName`. | Precedent: `create-pull-request-github` in `RetiredSkillIdentities`, and `*-v3` agents in `RetiredAgentIdentities`. The packet lists the fixture among "tests/fixtures referencing it". The fixture is still handled, through the retired-identity mechanism that exists for this purpose. Rewriting it would break its provenance pin. Cheap to reverse (one test task). | ANSWERED — autonomous: recommendation accepted (source: HotshotGoldenContractTests retired-identity precedent) |
| D15 | Compile-state sequencing in the shared test project | Only T1R is compile-RED (new config API). T2R and TSR are runtime-RED (CLI boundary and squad content), so the project still compiles. TSR and T2R record RED **before** T1R breaks compilation. Anything whose acceptance builds the test project (T3G) waits for T1G. | Precedent: archived plan 2026-09-21 "Compile-state rule" (one compile-RED pair at a time). | ANSWERED — autonomous: recommendation accepted |
| D17 | ADR editing rule | Reword `docs/adr/README.md` lines 13–14 ("An ADR is never edited to say something else — a decision that changes is recorded in a new ADR that supersedes the old one.") to say that ADRs are edited in place to state only the current decision. Outdated or superseded content is removed, not kept as history, because git holds history. An `Amended <date>` note with a one-line reason is optional. A sweep of `docs/` (non-archive), `AGENTS.md` and `REVIEW.md` found no other current text repeating the never-edit rule. `docs/archive/specs/kyberdash-context-surfaces/README.md:49` repeats it, but archived material is historical and not edited. | The owner's direction makes D2 consistent with the index's own policy. Folded into T5 because it is the same file (`docs/adr/README.md`). | ANSWERED — owner decision relayed by conductor 2026-10-20 |
| D18 | Should the portable `architecture-decision-record` skill's "NEVER edit the decision out of an accepted ADR … write a new ADR … supersedes" section (`products/kyber-squad/skills/architecture-decision-record/SKILL.md:93–97`) also change? | **Yes. Reword it in T5** to match D17: ADRs are edited in place to state only the current decision, outdated content is removed (git holds history), and an `Amended <date>` note with a one-line reason is optional. Rewrite the "Superseding" section accordingly. `supersedes` stays only for an ADR that genuinely replaces another subject's record. The skill's rendered Copilot body is SHA-pinned by the Hotshot golden manifest (`.github/skills/architecture-decision-record/SKILL.md`), so TSR adds `architecture-decision-record` to `EvolvedSkillIdentities` in `HotshotGoldenContractTests`, and the golden fixture stays byte-identical (D14). The skill has no migration record (migration records are agents only). Its inventory lists in `SquadCanonicalContentTests`, `SquadPackAndReleaseTests` and `FakeSquadRenderer` hold only its name and do not change. | The skill now agrees with this repository's ADR index. Consumer hosts receive the new rule on their next `squad update`. | ANSWERED — owner decision relayed by conductor 2026-10-20 |
| D19 | Approve and execute? | **Approved.** The plan is set to Ready and execution is authorized. | Conductor supplied `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous`, 2026-10-20. The conductor reports `docs validate` and `docs drift` at 0 findings on the Draft. The post-finalization edits (D18, T5/TSR scope, status) need a re-run (see the status block). | ANSWERED — autonomous: recommendation accepted (AUTONOMOUS_APPROVAL: approve-and-execute, 2026-10-20) |
| D16 | Developer-agent wording vs `resharper-clt` | Developer agents keep "run the resharper-clt fix pass". The skill already forbids InspectCode only "from inside a per-task completion gate" and allows scoped `cleanupcode`. `resharper-clt/SKILL.md` changes only if T3G finds wording that contradicts the `task-gate` flow. | Packet decision 5. | ANSWERED — autonomous: recommendation accepted |

No `NEEDS_DECISION` escalation. Every open item was covered by a packet decision, the owner
override, or repository precedent, or was cheap to reverse.

---

## 3. Investigation findings

**Discovery: codegraph 3, docs 4 (docs_explore 1, docs_for_symbol 1, agent_coding_standards 1, docs_status via provenance 1), direct reads 22, grep/glob 18.** Direct-read reasons:
`ReviewSettings.cs` (indexed code that codegraph did not return for the settings query, read
to confirm the flags). `SquadCanonicalContentTests.cs` and `HotshotGoldenContractTests.cs`
line ranges (large test files: needed the exact fixture lists, the migration-digest assertion,
and the retired-identity mechanism). `ReviewConfigTests.cs` head (test style). Everything else
read directly is product markdown, config, or plan/ADR files the docs index does not cover, or
covers only partly. CodeGraph and the kyber-weave docs server were available. Docs provenance
root matched this checkout (rev 187280f, clean).

- **Runner today.** `GateRunner.Run(ReviewConfig, root, stopOnBlockingFailure)` runs every
  `config.Gates` entry through `ProcessRunner` (argv, no shell). `RunOne` builds
  `ProcessStartInfo.ArgumentList`. There are no unit tests for `GateRunner` or
  `ReviewGatesCommand`, and codegraph reports no covering tests.
- **Config today.** `ReviewYamlSection` has `Gates/Coverage/Duplicates/Policy`.
  `ReviewConfigLoader.Merge`/`ParseGates` validate slug ids, duplicates, non-empty argv and
  timeout. Errors are `YamlException`s (tested in `ReviewConfigTests` through
  `KyberWeaveConfigLoader.LoadFromYaml`).
- **CLI today.** `ReviewGatesSettings` has `--out` and `--stop-on-failure`. Rule ids in use:
  KW-REVIEW-020..025 and 030..032.
- **Docs owning the symbols.** `docs/code-review/architecture.md` declares `GateRunner`,
  `ReviewConfig` and `ReviewConfigLoader` in code-refs. `docs/cli-command-surface.md`,
  `docs/ci-pipelines/rule-reference.md` and `docs/code-review/README.md` document
  `review gates`.
- **task-reviewer footprint (non-archive).** 29 files. Product: `agents/task-reviewer.md`,
  `agents/task-reviewer/references/{test-first,standard}-evidence.md`,
  `migration/task-reviewer.md`, `bundles/full.yml`, `README.md`, `agents/conductor.md` (line 56,
  delegates-to), `conductor/references/execution-and-review.md` (Task session continuity,
  "Three-pass task audit", Findings), `agents/code-reviewer.md:24`,
  `architect/references/plan-authoring.md:28`,
  `architect/references/standard-verification.md:10` ("task audit"),
  `skills/bug-crusher/SKILL.md`. Repo-local: `.apm/skills/spec-conductor/SKILL.md:46`
  ("three-pass limit"). Docs: `docs/code-review/{README,architecture}.md`,
  `docs/kyber-squad/{architecture,onboarding}.md`, `docs/adr/{0005,README}.md`, `REVIEW.md`,
  `docs/plans/README.md`, root `AGENTS.md:23` (cites ADR 0005 for fast-loop evidence; the link
  survives the amendment). ADRs 0021/0028/0040 mention task-reviewer historically. They are
  left as is because they record past decisions about other subjects. Tests:
  `SquadSourceTests` (250, 271), `SquadCanonicalContentTests` (40–73, 300–311, 389, 400–415,
  593–603, 840, 953–954), `HotshotGoldenContractTests` (58, 116–121, 244–247),
  `SquadPackAndReleaseTests:48`, `AntigravityRendererContractTests:1082`,
  `Squad/CopilotRendererTests` (221, 287, 630), `Fakes/FakeSquadRenderer:35`.
- **Migration digests.** `LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest`
  asserts `migration/<agent>.md` `final-body-sha256 == agent.BodyDigest`. Every edited migrated
  agent body (all ten developer agents, conductor, code-reviewer, architect if its body
  changes) needs its migration record's `final-body-sha256` updated in the same task.
- **Developer agents** already carry the 3-iteration JEV cap, the closed
  `CIRCUIT_BREAKER_TRIGGER` set, and a `Completion gate` / `READY_FOR_REVIEW` digest
  (csharp-dev, dal-dev, react-dev, pulumi-dev, maui-dev, test-dev, github-devops, python-dev;
  tauri-dev and sql-database-architect have no `READY_FOR_REVIEW` digest line and need one).
  All ten have `capability-profile: worker` or `publishing-worker`, so they can execute
  processes. The conductor is `process.execute: deny` and cannot run the gate itself, which is
  why the result goes in the digest.
- **Policy.** `.kyber-weave/kyber-weave.yml` and `products/kyber-squad/agents/**` are
  `always-human`, so the end-of-run verdict will be `NEEDS_HUMAN` (`KW-REVIEW-008`) by design
  (R1).
- **dash scripts.** `typecheck` = three `tsc --noEmit` runs. `lint` = `eslint .`
  (project-wide; `{changed}` is not passed). No `pyproject.toml`/ruff/mypy anywhere.

---

## 4. Contracts

### 4.1 Config and CLI contract (normative for T1–T2)

```yaml
review:
  task-gates:
    csharp-dev:
      - &fmt-whitespace
        id: format-whitespace
        include: ["**/*.cs"]
        fix: [dotnet, format, KyberWeave.sln, whitespace, --include, "{changed}"]
        run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes, --include, "{changed}"]
      - &dotnet-build
        id: build
        include: ["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets"]
        run: [dotnet, build, KyberWeave.sln, -c, Release, --artifacts-path, ".agent_scratchpad/{task}/artifacts"]
    dal-dev: [*fmt-whitespace, *dotnet-build]
```

`kyber-weave review gates <path> --agent <name> [--paths <p>...] [--base <ref>] [--task <slug>] [--fix] [--stop-on-failure] [--out <file>]`.
`--paths`/`--base`/`--task`/`--fix` without `--agent` is a client-input error (exit 2,
following the CLI exit-code contract in `docs/cli-command-surface.md`; T2G confirms the code).
Without `--agent`, behaviour is byte-for-byte today's.

### 4.2 Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ReviewTaskGatesConfigTests.cs` (new) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.ReviewTaskGatesConfigTests` (MTP filter syntax as the repo uses) | `LoadFromYaml` exposes `Review.TaskGates` keyed by agent, gates in declaration order with defaults (blocking true, timeout 900); `os`, `include`, `fix` parsed; YAML aliases of anchored gate mappings resolve; rejected with `YamlException` naming the field: invalid agent slug, duplicate gate id within an agent, empty/missing `run`, empty argv element, `{changed}` embedded in a larger element (in `run` or `fix`), unknown `{token}` (message offers nearest known token), invalid `os` value, timeout ≤ 0; absent section → empty map; existing `review.gates` parsing unchanged. Plus `RepositoryConfigDeclaresDeveloperTaskGates`: the repo's own `.kyber-weave/kyber-weave.yml` declares entries for the nine D10 agents, none for python-dev, and no entry for any non-developer agent (stays RED until T2C). | Compile failure naming missing `TaskGates`/task-gate types (D15), recorded with the error lines. | All tests in the class pass except `RepositoryConfigDeclaresDeveloperTaskGates`, which passes after T2C. `ReviewConfigTests` still passes. |
| T2 | `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs` (new; drives a real `CommandApp` against a temp git repo with its own `.kyber-weave/kyber-weave.yml`, using `git`/`dotnet --version` as portable gate programs) | same runner, `--filter-class KyberWeave.Tests.ReviewGatesAgentCommandTests` | (1) `--agent X` runs only X's task gates, not `review.gates`; (2) `{changed}` expands to one argv per `--paths` entry filtered by `include` (proved, for example, by `git ls-files --error-unmatch {changed}` passing or failing for tracked/untracked paths); (3) without `--paths`, the changed set is the git diff vs `--base` (default HEAD) plus untracked, deletions excluded; (4) no matching file, or `os` mismatch → gate skipped, `KW-REVIEW-026`, absent from report `gates`, exit 0; (5) agent with no entry → `KW-REVIEW-027`, empty `gates`, exit 0; (6) path that is absolute, escapes the root, or starts with `-` → `KW-REVIEW-028`, exit 1, no gate process started (no report gate entries); (7) `{task}` used without `--task`, or invalid slug → `KW-REVIEW-028`, exit 1; (8) `--fix` runs `fix` before `run` (observable ordering: fix makes the verify pass); fix failure → `KW-REVIEW-029` Warning, verify still decides; (9) blocking failure → exit 1, report schema `kyber-weave.review-gates/v1`; (10) `--paths` without `--agent` → client-input error; (11) without `--agent`, the existing behaviour is unchanged (`KW-REVIEW-020` when no `review.gates`). | Runtime failure: unknown option `--agent` / unexpected exit codes, on a compiling tree, recorded **before** T1R lands (D15). | All tests in the class pass on macOS **and** Linux. |
| T3 | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `SquadPackAndReleaseTests.cs`, `Fakes/FakeSquadRenderer.cs`, `HotshotGoldenContractTests.cs` (edits by TSR) | `dotnet test … --filter-class` for each of the four classes plus `SquadSourceTests`, `AntigravityRendererContractTests`, `Squad.CopilotRendererTests` | `task-gate` is a canonical skill in `ExpectedSkills`, the full bundle and pack inventory; Hotshot treats it as canonical-only; each of the ten D4 agents' instruction body names the `task-gate` skill and a `TASK_GATE:` digest line; no non-developer agent body contains `TASK_GATE:`; the skill body contains `review gates`, `--agent`, `--fix`, the 2-cycle cap, `ITERATION_CAP_EXCEEDED`, `task-gate/<gate-id>` and the D12 line shape; the closed trigger set holds. | Runtime assertion failures (skill missing, bodies lacking text), recorded on a compiling tree before T1R. | All named classes pass after T3G. |
| T4 | same files as T3 plus `SquadSourceTests.cs`, `AntigravityRendererContractTests.cs`, `Squad/CopilotRendererTests.cs` (edits by TSR) | as T3 | No `task-reviewer` agent, reference folder, or migration record in the source; not in `ExpectedMigratedAgents`, bundle, pack, fake renderer, reviewer-agent arrays, Copilot tool/model rows, or conductor `delegates-to`; `task-reviewer` is in Hotshot `RetiredAgentIdentities` (rendered output lacks `.github/agents/task-reviewer.agent.md`); the conductor contract contains `TASK_GATE`, "reject", `task-gate/` and the circuit-breaker strings it has today, and no longer contains `reviewer-session-id`, "Three-pass task audit" or `task-reviewer`; `ConductorRetainsTaskSessionsForIndependentReviewAndRework` is rewritten to the worker-session-only contract; `LoadTaskReviewerDefinesThreePassesForBothDevelopmentModes` is deleted; architect plan-authoring and code-reviewer bodies do not mention `task-reviewer`; migration digests match. | Runtime assertion failures on a compiling tree, recorded before T1R. | All named classes pass after T4G; the full suite passes in T6. |
| T2C | `ReviewTaskGatesConfigTests.RepositoryConfigDeclaresDeveloperTaskGates` (from T1R) plus manual runs | runner as T1; plus `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --agent csharp-dev --paths src/KyberWeave.Core/Review/GateRunner.cs --task t2c --fix --out artifacts/task-gate-t2c.json` | Repo config declares D10 gates; real runs pass on a clean tree for csharp-dev, react-dev (`--paths dash/src/...`), tauri-dev (`--paths dash/tray/src-tauri/src/...`), and `python-dev` returns `KW-REVIEW-027`. | RED already recorded by T1R for the named test. | Test passes; four manual runs recorded with exit codes on macOS and Linux. |
| T5 | `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` (evolved-skill entry from TSR) plus docs/skill checks | `dotnet test … --filter-class KyberWeave.Tests.HotshotGoldenContractTests`; `docs validate . --merge-ready`; `docs drift .`; `skill validate/lint/scan products/kyber-squad/skills/architecture-decision-record` | ADR 0005 amended per D2; ADR index rule and the ADR skill's Superseding section state the D17 rule; the index row is synced; the golden snapshot bytes are unchanged while the skill body differs. | TSR's run with `architecture-decision-record` declared evolved against the unchanged body (RED if the guard covers skills; recorded either way). | Hotshot class green, skill checks pass, docs zero findings (except `KW-DOC-LIFECYCLE-003` for this open plan under `--merge-ready`). |
| T6 | — (no-test task: docs closeout) | full AGENTS.md "Before opening a PR" list, `skill validate/lint/scan` on `products/kyber-squad/skills/task-gate`, and `review gates . --out artifacts/gates.json` | Docs match shipped behaviour; plan archived. | Explicit no-test task. | Every gate green on macOS and Linux (dash gates included); docs zero findings. |

### 4.3 Conductor contract after the change (normative text for T4G)

The conductor drops the "Three-pass task audit" section, the `reviewer-session-id`, and the
`task-reviewer` delegation. In their place it gets a **"Developer exit gate"** section:

- A developer completion digest must carry a `TASK_GATE:` line (D12).
- A missing line, or `FAIL`, is rejected. Rework goes to the original worker session and counts
  in the cluster tally `task-gate/<gate-id>` / `task-gate/missing`, max 2 rework dispatches,
  then the breaker trips.
- `UNAVAILABLE` is recorded in the findings collection.
- In test-first mode, the conductor checks mechanically that the RED digest exists before it
  dispatches GREEN. It does no LLM audit.

The circuit-breaker section, the findings collection, and the single `code-reviewer` run stay
as they are, with `ESCALATION: end-of-run` removed from the reviewer-sourced paths.

---

## 5. Tasks

Required skills are named, not owning agents. Each packet is self-contained, and workers do
not read this plan.

### TSR — RED: squad content tests for task-gate wiring and task-reviewer removal
- **Objective:** Write the T3 and T4 contract tests (§4.2), plus the D18 test change: add `architecture-decision-record` to `EvolvedSkillIdentities` in `HotshotGoldenContractTests`. This is RED until T5 changes the skill body, if the evolved-identity guard covers skills; otherwise it is a no-op RED that T5 keeps green. Tests only.
- **Files:** `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `SquadPackAndReleaseTests.cs`, `SquadSourceTests.cs`, `HotshotGoldenContractTests.cs`, `AntigravityRendererContractTests.cs`, `Squad/CopilotRendererTests.cs`, `Fakes/FakeSquadRenderer.cs`. `Fixtures/kyber-squad-hotshot-golden.json` stays byte-identical (D14).
- **Acceptance:** The project compiles. The new and changed assertions fail for the intended reason. RED log saved under `.agent_scratchpad/tsr/`.
- **Depends on:** none. **Skills:** test-dev.

### T2R — RED: `review gates --agent` CLI-boundary tests
- **Objective:** Write the T2 contract (§4.2) through `CommandApp` only, so the file compiles against today's API.
- **Files:** `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs` (new).
- **Acceptance:** The project compiles. Runtime RED is recorded. Temp repos are cleaned up, and nothing writes outside the temp root.
- **Depends on:** none. **Skills:** test-dev.

### T1R — RED: task-gate config tests (compile-RED)
- **Objective:** Write the T1 contract (§4.2).
- **Files:** `tests/KyberWeave.Tests/ReviewTaskGatesConfigTests.cs` (new).
- **Acceptance:** RED is the compile error naming the missing API.
- **Depends on:** TSR, T2R (build-state ordering, D15). **Skills:** test-dev.

### T1G — GREEN (a): config schema and loader
- **Objective:** `ReviewTaskGate` record (`Id, Run, Blocking, TimeoutSeconds, Os?, Include, Fix?`), `ReviewConfig.TaskGates` (`IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>>`, ordinal), YAML classes, and parsing/validation per D5/D6/D8, with why-comments and hinted errors.
- **Files:** `src/KyberWeave.Core/Configuration/{ReviewConfig.cs,ReviewYamlSection.cs,ReviewConfigLoader.cs}` (and `KyberWeaveYamlDocument.cs` only if the section key needs it).
- **Acceptance:** T1 GREEN, except `RepositoryConfigDeclaresDeveloperTaskGates`. `ReviewConfigTests` green. Release build clean (warnings-as-errors). Format verify clean.
- **Depends on:** T1R. **Skills:** csharp-dev, resharper-clt.

### T2G — GREEN (b): runner and CLI
- **Objective:** Changed-set resolution (`--paths` or git through `ProcessRunner` argv), token expansion (`{changed}`, `{task}`), include/os skipping, `--fix`, new settings, KW-REVIEW-026..029, reuse of `GateRunner.RunOne`-style execution and `GateReport`. Keep the no-`--agent` path unchanged.
- **Files:** `src/KyberWeave.Core/Review/GateRunner.cs` (and, if the split earns it, a new `src/KyberWeave.Core/Review/TaskGateSelection.cs`), `src/KyberWeave.Cli/Commands/Review/{ReviewGatesCommand.cs,ReviewSettings.cs}`.
- **Acceptance:** T2 GREEN on macOS and Linux. T1 is still green. Build and format clean.
- **Depends on:** T1G, T2R. **Skills:** csharp-dev, resharper-clt.

### T2C — Repository task-gate configuration
- **Objective:** Add `review.task-gates` per D10 with why-comments. Existing `review.gates` unchanged.
- **Files:** `.kyber-weave/kyber-weave.yml`.
- **Acceptance:** T2C row (§4.2). Then `docs init` (or check `KW-CONFIG-REG-002`), so that the Config Reg block is not stale. Expected: no change, because the block does not render review config.
- **Depends on:** T2G. **Skills:** csharp-dev.

### T3G — GREEN (c): `task-gate` skill and developer-agent wiring
- **Objective:** Write `products/kyber-squad/skills/task-gate/SKILL.md` (trigger-style description, ≥70 lint score). It covers the invocation `kyber-weave review gates . --agent <self> --paths <own changed files> --task <task-id> --fix --out .agent_scratchpad/<task-id>/task-gate.json`, then reruns without `--fix`. It passes only the worker's own files under parallel workers, an optional scoped `cleanupcode`, the loop with 2-cycle cap (D11), the escalation block, and the D12 digest line. Add it to `bundles/full.yml` `skills`. In each of the ten D4 agents: add a Skills line plus a hard rule ("run `task-gate` and fix failures before reporting done; never report done without a `TASK_GATE:` line"), and add `TASK_GATE:` to the completion digest (tauri-dev and sql-database-architect gain a digest if they lack one). Keep csharp-dev's InspectCode prohibition. Update each edited agent's `migration/<agent>.md` `final-body-sha256`.
- **Files:** `products/kyber-squad/skills/task-gate/SKILL.md` (new), `products/kyber-squad/bundles/full.yml` (skills list only), `products/kyber-squad/agents/{csharp-dev,dal-dev,python-dev,react-dev,maui-dev,tauri-dev,test-dev,pulumi-dev,sql-database-architect,github-devops}.md`, matching `products/kyber-squad/migration/*.md`. Optionally `skills/resharper-clt/SKILL.md` (D16).
- **Acceptance:** T3 GREEN. `skill validate|lint --min-desc-score 70|scan --fail-on critical products/kyber-squad/skills/task-gate` pass. Renders for all squad targets succeed (existing renderer contract tests green).
- **Depends on:** TSR, T1G (test project must compile, D15). **Skills:** writing-trigger-descriptions, test-dev (for running the squad suites), docs authoring via app-docs-standard.

### T4G — GREEN (d): remove task-reviewer
- **Objective:** Delete `agents/task-reviewer.md`, `agents/task-reviewer/`, `migration/task-reviewer.md`. Remove the bundle `agents` entry and the README mentions. Rewrite `agents/conductor.md` (line 56, `delegates-to`) and `conductor/references/execution-and-review.md` per §4.3. Edit `code-reviewer.md:24`, `architect/references/plan-authoring.md:28` (replace "Each task has its own `task-reviewer`…" with the exit-gate sentence), `architect/references/standard-verification.md:10` ("task audit" → "the developer exit gate"), `skills/bug-crusher/SKILL.md`, and `.apm/skills/spec-conductor/SKILL.md:46`. Update the migration digests of the edited agents. Update `REVIEW.md`, `docs/kyber-squad/{architecture,onboarding}.md` (session-continuity text near 767–777), `docs/code-review/{README,architecture}.md` (three layers become deterministic exit gate plus council, and the Modes text), and the `docs/plans/README.md` policy mention, if any.
- **Files:** as listed. Not ADRs (T5) and not the CLI/rule docs (T6).
- **Acceptance:** T4 GREEN. A non-archive grep for `task-reviewer` matches only ADRs 0021/0028/0040 (historical), the amended ADR 0005 statement of removal, this plan, and the test retired-identity lists. `docs validate`/`docs drift` zero findings. A render to a scratch target shows no task-reviewer file for any target.
- **Depends on:** T3G (replacement text, `bundles/full.yml` overlap). **Skills:** app-docs-standard, writing-trigger-descriptions.

### T5 — Amend ADR 0005 in place, and reword the ADR editing rule (e, part 1)
- **Objective:** (1) Reword `docs/adr/README.md` lines 13–14 per D17: ADRs are edited in place to state only the current decision; outdated or superseded content is removed, not kept as history (git holds history); an `Amended <date>` note with a one-line reason is optional. Re-check `docs/` (non-archive) and `AGENTS.md` for any other text that repeats the never-edit rule and reword it the same way. None was found at planning time. Do not edit `docs/archive/`. (2) Reword the "Superseding" section of `products/kyber-squad/skills/architecture-decision-record/SKILL.md` (about lines 93–97, plus any other line in that skill repeating the never-edit rule) per D18. (3) Amend ADR 0005 per D2. Retitle, rewrite Status/Context/Decision/Alternatives/Consequences to state only the current decision. Add `Amended 2026-10-20: <one-line reason — the per-task LLM audit roughly tripled task time; deterministic exit gate replaces it>`. Keep the deterministic fix-pass decision. State the accepted RED/GREEN-ordering audit loss and the part the `test-adequacy` lens covers. Bump `last-reviewed`. Sync the `docs/adr/README.md` row (title and summary). Check the AGENTS.md:23 link text still fits.
- **Files:** `docs/adr/0005-task-level-fast-review.md` (keep the filename; the id is cited), `docs/adr/README.md` (the rule text and the 0005 row), `products/kyber-squad/skills/architecture-decision-record/SKILL.md`, and any other current `docs/` or `AGENTS.md` passage the re-check finds. Not the test files (TSR owns the `EvolvedSkillIdentities` edit).
- **Acceptance:** The ADR index and the ADR skill both state the D17 rule. A non-archive grep of `docs/`, `AGENTS.md` and the skill for the never-edit wording finds nothing. No passage contradicts D1–D18. `skill validate|lint --min-desc-score 70|scan --fail-on critical products/kyber-squad/skills/architecture-decision-record` pass. `HotshotGoldenContractTests` is green (the evolved-skill entry from TSR now matches a changed body). `docs validate . --merge-ready` is clean except `KW-DOC-LIFECYCLE-003` for this open plan, and `docs drift .` is clean.
- **Depends on:** TSR (the evolved-skill test entry), T1G (its Hotshot verification run needs a compiling test project, D15). The doc and skill edits can be drafted earlier, but the task completes only after its verification run. **Skills:** architecture-decision-record, app-docs-standard, writing-trigger-descriptions.

### T6 — Docs closeout (e, part 2)
- **Objective:** Document `--agent/--paths/--base/--task/--fix`, the tokens and the exit codes in `docs/cli-command-surface.md`. Add KW-REVIEW-026..029 to `docs/ci-pipelines/rule-reference.md`. Add `review.task-gates` to the configuration section of `docs/code-review/architecture.md` and its code-refs, if new symbols are added. Create the D9 todo `docs/todo/task-gate-test-project-mapping.md` plus its index row. Run the full declared suite on macOS and Linux. Closeout mapping of D1–D18 to destinations. Archive this plan and update the index.
- **Files:** those docs, `docs/todo/`, `docs/plans/README.md`, `docs/archive/plans/`.
- **Acceptance:** T6 row (§4.2).
- **Depends on:** T2C, T4G, T5. **Skills:** app-docs-standard (docs-dev closeout).

### Dependency graph

```text
TSR ─┬────────────────► T1R ─► T1G ─┬─► T2G ─► T2C ─┐
T2R ─┘                              │   (T2R ──► T2G)│
                                    └─► T3G ─► T4G ──┼─► T6
            (TSR ──► T3G)                            │
                                    └─► T5 ──────────┘
            (TSR ──► T5)
```

Ready at start: **TSR, T2R**. After T1G: **T2G ∥ T3G ∥ T5**.

---

## 6. Risks

- **R1 — always-human paths.** `.kyber-weave/kyber-weave.yml` and `products/kyber-squad/agents/**` force `NEEDS_HUMAN` (`KW-REVIEW-008`) at the council. This is expected; follow the autonomy contract.
- **R2 — `dotnet format` shares `obj/`.** `format` takes no `--artifacts-path`, so concurrent workers may still contend during design-time builds. Mitigation: `--include` scoping, the build gate's per-task artifacts path, and rerun-on-transient guidance in the skill. Watch the T3G/T6 logs.
- **R3 — CLI self-hosting.** In this repo the CLI under change is also the gate runner. Workers prefer the installed `kyber-weave`. The fallback `dotnet run --project src/KyberWeave.Cli -c Release --` builds into shared output (R2). An `UNAVAILABLE` result is never a pass (D12).
- **R4 — stale installs.** Hosts that already have `task-reviewer` deployed keep the file until `squad update` reconciles receipts. T4G checks the scratch render, and the risk is listed in T6 docs.
- **R5 — Lost per-task audit.** Accepted (D1). Completion-claim honesty now rests on the digest's mechanical checks and the council.

## 7. Verification gates, review, closeout

- Every C# task: Release build warnings-as-errors and format verify. T2 tests run on macOS and Linux.
- T6: the full AGENTS.md list, `skill validate/lint/scan` on `.apm/skills/kyber-weave-docs`, `.apm/skills/kyber-weave-cli`, `.apm/skills/spec-conductor` and `products/kyber-squad/skills/task-gate`, the dash gates, and `docs validate . --merge-ready` / `docs drift .`, on Linux too.
- Review: one `code-reviewer` council over the accumulated change (there is no per-task reviewer under this plan's own outcome; the conductor applies the D11/D12 digest rule to its own workers as soon as T4G lands, and the existing contract before that).
- Closeout: `docs-dev` per T6. Durable decisions go to ADR 0005 (amended), `docs/code-review/*`, `docs/cli-command-surface.md`, `docs/ci-pipelines/rule-reference.md` and `docs/kyber-squad/*`.

---

## 8. Closeout

Decision destinations. File paths are relative to the repository root.

| Decision | Where it landed |
|---|---|
| D1 remove `task-reviewer` | Deleted `products/kyber-squad/agents/task-reviewer.md` and `agents/task-reviewer/`, its migration note and bundle entry. Conductor, architect, code-reviewer and README text rewritten. The only surviving mentions are the test retired-identity lists, the Hotshot golden fixture, ADRs 0021/0028/0040 as history, and this plan. |
| D2 amend ADR 0005 in place | `docs/adr/0005-task-level-fast-review.md`, retitled and stating only the exit-gate decision. |
| D3 enforcement by skill and rule, no hooks | `products/kyber-squad/skills/task-gate/SKILL.md` and the rule in each developer agent; the conductor rejects a missing `TASK_GATE:` line (`products/kyber-squad/agents/conductor/references/execution-and-review.md`). |
| D4 developer agents only | The ten agents named in D4 carry the skill and the rule; no others do. |
| D5 runner and config shape | `src/KyberWeave.Core/Configuration/{ReviewConfig,ReviewConfigLoader,ReviewYamlSection}.cs`; `src/KyberWeave.Cli/Commands/Review/{ReviewGatesCommand,ReviewSettings}.cs`; documented in `docs/code-review/architecture.md` (Task gates) and `docs/cli-command-surface.md`. |
| D6 `{changed}` expansion and path safety | `src/KyberWeave.Core/Review/{TaskGateSelection,ChangedFiles}.cs`; loader token validation; the unsafe-input tests in `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs`. |
| D7 `fix` and `--fix` | `GateRunner.RunTaskGates`; `KW-REVIEW-029` in `docs/ci-pipelines/rule-reference.md`. |
| D8 `{task}` and per-task artifacts | Loader and `TaskGateSelection`; the build gate in `.kyber-weave/kyber-weave.yml`. |
| D9 test mapping deferred | `docs/todo/task-gate-test-project-mapping.md`. |
| D10 per-agent gates | `review.task-gates` in `.kyber-weave/kyber-weave.yml`. Follow-up on the shared .NET set: `docs/todo/task-gate-config-review.md`. |
| D11 retry cap and rework | `task-gate` skill and the conductor's execution-and-review reference. |
| D12 digest line and `UNAVAILABLE` | `task-gate` skill; conductor reference. |
| D13 report schema and rule ids | `KW-REVIEW-026` to `029` in `docs/ci-pipelines/rule-reference.md`; schema `kyber-weave.review-gates/v1` unchanged. |
| D14 Hotshot fixture untouched | Fixture byte-identical; `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` lists `task-reviewer` as retired. |
| D15 compile-state sequencing | Followed during execution; no residual artifact. |
| D16 resharper-clt wording | Developer agents keep the `resharper-clt` fix pass; the skill needed no change. |
| D17 ADR editing rule | `docs/adr/README.md`. Older ADRs still holding superseded history: `docs/todo/adr-in-place-cleanup.md`. |
| D18 ADR skill reworded | `products/kyber-squad/skills/architecture-decision-record/SKILL.md`. |
| D19 approve and execute | Recorded in the decisions ledger above. |

Verification evidence is recorded in the task report that closed T6, not here, because it
changes with every re-run. Open follow-ups: the three todos named above.
