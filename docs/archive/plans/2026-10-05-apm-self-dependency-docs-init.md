---
id: plans/2026-10-05-apm-self-dependency-docs-init
title: "docs init: remove the apm.yml self-dependency (#285)"
doc-type: plan
status: complete
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-05
development-mode: test-first
---

# docs init: remove the apm.yml self-dependency (#285)

**Status: Complete, archived 2026-10-05.** Decisions Q1–Q4 are locked as A3–A6
(conductor-relay answers, 2026-10-05 — the harness could not prompt the user, so the
recommended option was adopted for each). Approve-and-execute is recorded as A7
(2026-10-05): the user's entry prompt pre-authorized treating the decision-complete
Draft as approved ("Hal approves" gate), adopted by the conductor in this non-interactive
session. Implementation may proceed on T1.

This plan fixes GitHub issue
[#285](https://github.com/dpalfery/kyber-weave/issues/285): repo-root `apm.yml` lists
`dpalfery/kyber-weave` as a dependency of itself, so the `apm install` that `docs init`
shells out to aborts with a circular dependency in every consuming repository, and the
`kyber-weave-docs` authoring skill is never deployed.

**Development mode:** `test-first` — the repository default (`TreatWarningsAsErrors`,
`AnalysisMode=all`), explicit in the conductor dispatch; no opt-out was supplied. Every
implementation task defines a failing contract test before production state changes.

---

## 1. Problem and Goal

### 1.1 What the consumer sees

Onboarding any consuming repository (the issue reproduces on `dpalfery/buzz-router`,
kyber-weave `0.1.7-rc.15`/`0.1.7-rc.16`, APM `0.28.0`, macOS arm64) with:

```bash
kyber-weave docs init . --docs-root docs --target claude,agent-skills
```

scaffolds the corpus, then fails to deploy the skill. The APM subprocess aborts:

```text
[x] Circular dependencies detected:
[x]   dpalfery/kyber-weave -> dpalfery/kyber-weave
[i] Removed apm.yml created by the failed install.
[x] Failed to install APM dependencies: Cannot install packages with circular dependencies
```

`docs init` itself still exits 0: `DeploySkill` treats any non-zero APM exit as the
deliberate degraded path (`SkillUnavailable`, `DocsInitCommand.cs:147`) — the corpus is
scaffolded and the skill silently never lands, with only the skip notice telling the
operator why. The skill deployment is the broken half, and because the failing manifest
ships on GitHub `main`, every consumer of every release since the entry landed is
affected, whatever the CLI version.

### 1.2 Root cause

Repo-root `apm.yml:24` declares the package as a dependency of itself:

```yaml
dependencies:
  apm:
    - dpalfery/kyber-weave
  mcp: []
```

The entry came in with #33 (`c8a0f400`, 2026-08-22), probably from running
`apm install dpalfery/kyber-weave` inside this repository. Because the shorthand is
unpinned, APM resolves it from GitHub `main`, and the dependency graph walk — which
happens inside the external APM CLI — sees the package depending on itself and aborts
before anything is written in the consumer.

### 1.3 Goal

Every consuming repository can install the package and get the `kyber-weave-docs` skill:
remove the self-dependency from the manifest (keeping the `targets: [agent-skills]` pin
untouched), remove the tracked self-install lock state, pin the invariant with a guard
test, and verify the install path live in a scratch directory.

---

## 2. Investigation Findings

Discovery notes: no usable CodeGraph query was available to this session, so code paths
were traced by targeted reading of named files; the Kyber-Weave MCP `docs_*` tools are
not reachable in this harness, so documentation discovery used the root-`AGENTS.md`
fallback (the documentation index and direct reads), stated here as that fallback
requires. External APM facts in F7 are **self-gathered** — the research-agent delegation
is unavailable here (subagent depth limit reached).

### F1. `docs init` only shells out — there is no in-repo loader seam

Skill deployment is delegated to APM, not reimplemented:

```22:src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs
    private const string SkillPackage = "dpalfery/kyber-weave";
```

`DeploySkill` (`DocsInitCommand.cs:108-153`) starts `apm` with
`install`, `SkillPackage`, `--target`, `settings.Target` passed through `ArgumentList`
(never a shell string), and treats a missing or failing APM as a degraded, printed,
non-failing path — a design the CLI `AGENTS.md` holds up as the rule for shelling out.
**No code in `src/`, `tests/`, or `.github/` reads `apm.yml`'s `dependencies:`** — a
repo-wide search finds the manifest referenced only by itself, the root `AGENTS.md`
targets-pin note, and `.gitignore`'s `apm_modules/` line. There is nothing in this
repository's code that could "skip self": the dependency walk lives entirely inside the
external APM binary.

### F2. The abort is the external loader's, and it is consumer-side

APM 0.28.0's install pipeline walks `dependencies.apm`/`devDependencies.apm` and their
transitives in its Resolve phase; an edge from a package to itself is a cycle, and APM
aborts with "Cannot install packages with circular dependencies" (issue #285 transcript
above). The reproducing repository (`dpalfery/buzz-router`) is a *different* repository:
APM fetches kyber-weave's manifest from GitHub `main`, reads the self-edge there, and
aborts before writing anything in the consumer (it even removes the `apm.yml` it had
created). Two consequences decide the fix:

1. An own-repo special case in a loader — even if one existed in some APM version; none
   is documented — would not repair consumers, because the cycle is read from the
   *package's* manifest, wherever it is installed from.
2. Within this repository, the only candidate seam (`DocsInitCommand`) does not resolve
   dependencies at all; it delegates precisely so that harness resolution lives in one
   place.

**The fix therefore belongs in the manifest, not in any loader code (A1).** A package can
never usefully depend on itself; the loader's refusal is correct, the manifest entry is
the defect.

### F3. The manifest state

`apm.yml` carries the self-entry at line 24 under `dependencies.apm`. The
`targets: [agent-skills]` pin (lines 19-20) and its why-comment (lines 9-18) are a
deliberate, documented non-negotiable (root `AGENTS.md`: do not remove that pin) and are
**untouched** by this fix. The unpinned shorthand means the broken manifest ships on every
release from #33 onward.

### F4. The tracked lock is self-install state, and nothing reads it

`apm.lock.yaml` is **tracked on `main`** (GitHub contents API, 2026-10-05, blob
`838f716`; `.gitignore` lists `apm_modules/` and `.agents/` but not the lock). Its entire
content describes installing the package into itself: `dependencies[0].repo_url:
dpalfery/kyber-weave` (resolved commit `5b00f0e`, version 0.1.1), `deployments` whose
owners are `dpalfery/kyber-weave` and `.` with `active_owner: .`, and
`local_deployed_files` under `.agents/skills/` — a gitignored deploy target that does not
even exist in a fresh clone of this repository. **Nothing in `src/`, `tests/`, or
`.github/` reads it** (repo-wide search; `ci.yml`'s only APM reference is the skill gate
over `.apm/skills/kyber-weave-docs`, line 456). It exists only because of the
self-dependency — exactly the state the issue's Fix §2 says to clean up. No `apm_modules/`
and no `.agents/` exist in this clone.

### F5. The skill's source of truth is tracked elsewhere

`.apm/skills/kyber-weave-docs/` is the authored, tracked source (CI's skill gate dogfoods
it; `KyberWeaveDocsSkillVocabularyTests` reads it through `KyberWeaveTestPaths.ToolRoot`).
The self-install serves no function in this repository; the issue's workaround —
hand-copying `.apm/skills/kyber-weave-docs/` into a consumer — confirms where the bytes
live. Nothing about the skill itself changes in this fix.

### F6. The guard test has an established pattern to follow

`KyberWeaveTestPaths` walks up from test output to `KyberWeave.sln` to locate the
repository root, and `YamlDotNet.RepresentationModel` is how root-level YAML is parsed in
tests (`FactoryRendererContractTests`, `SquadRenderingContractTests`,
`HotshotGoldenContractTests`). A dedicated `ApmManifestGuardTests` following both is the
natural home for the manifest invariant.

### F7. APM external facts (self-gathered 2026-10-05)

From the APM docs (microsoft.github.io/apm), consulted directly:

- `apm install` accepts `owner/repo` shorthand with an optional `#ref` (branch, tag, or
  commit SHA), full git URLs, and **local paths** (`./`, `../`, `/` — development-only).
  Pre-merge verification can therefore install *this branch* as
  `dpalfery/kyber-weave#hal.hermes.opencode/issue-285-apm-self-dependency` (the issue's own
  Verify bullet; needs the branch pushed) or the clone itself by local path (no network).
- The install pipeline is Resolve → Policy gate → Scan → Integrate → Lockfile, each phase
  gating the next; the lockfile records pinned versions and content hashes.
- "What to commit" says commit `apm.yml`, `apm.lock.yaml`, and target-owned outputs, and
  keep `apm_modules/` gitignored — guidance addressed at consumers *with dependencies*.
  This manifest will have none, so there is nothing for a lockfile here to pin (Q2,
answered as A4).
- `targets:` controls which outputs are generated at compile/install/pack;
  `agent-skills` deploys Agent Skills under `.agents/skills/`, which is what
  `docs init --target agent-skills` relies on and what the pin comment already explains.

### F8. This authoring session's harness constraints (why Q4 was asked; answered as A6)

The architect session has no process/shell tool, subagent delegation is blocked (depth
limit), and no Kyber-Weave MCP server is reachable, so `docs validate .` and
`docs drift .` **could not be executed on this Draft from this session**. The Draft was
authored to conform to the ontology (plan doc-type: `component` required; closed status
vocabulary; index reachability per `KW-DOC-LIFECYCLE-001`) by mirroring the corpus's plan
format, but the two documentation checks still need a runner — Q4, answered as A6: the
conductor runs both from the shell and relays findings, and that run is the evidence
source for the Ready marker's documentation-check claims.

---

## 3. Explicitly Approved Decisions

| Id | Decision | Approval provenance |
|---|---|---|
| **A1** | Fix at the manifest, not in any loader: remove the self-dependency entry from repo-root `apm.yml`, leaving `dependencies.apm: []`, keeping the `targets: [agent-skills]` pin and its comment untouched. No `DocsInitCommand` or other code change. | Issue [#285](https://github.com/dpalfery/kyber-weave/issues/285) body, "Fix" §1 (owner-authored directive); conductor dispatch 2026-10-05 ("decide from the code"); confirmed from evidence F1/F2 — the dependency walk lives in the external APM loader, the abort is consumer-side, and no in-repo code reads or filters `dependencies:`. An own-repo special case would have to live inside APM and would still not repair consumers. |
| **A2** | Development mode is `test-first`, with no opt-out. | Conductor dispatch, 2026-10-05 (explicit); repository default. |
| **A3** | Q1=(a): the regression guard (issue Fix §3) is the xUnit manifest-guard test `tests/KyberWeave.Tests/ApmManifestGuardTests.cs` — no self-dependency in `dependencies.apm`/`devDependencies.apm` (string or object form, `#ref`-tolerant) and `targets == [agent-skills]`; no CI workflow change. | Conductor relay 2026-10-05: harness could not prompt the user; recommended option adopted. |
| **A4** | Q2=(a): `git rm apm.lock.yaml`; `.gitignore` unchanged. | Conductor relay 2026-10-05: harness could not prompt the user; recommended option adopted. Evidence F4: the tracked lock is self-install state nothing reads. |
| **A5** | Q3=(a): live scratch-dir verification is a plan task (T5) — branch-ref and/or local-path `apm install --target agent-skills` asserting the deployed skill with no circular error, plus the post-merge owner gate; recorded fallback to the owner gate if `apm` is unavailable in the implementing session. | Conductor relay 2026-10-05: harness could not prompt the user; recommended option adopted. Issue #285 "Verify" §1–2. |
| **A6** | Q4=(a): the conductor runs `docs validate .` and `docs drift .` on the saved plan from the shell and relays findings; this session reconciles any findings before `PLAN_READY`/`PLAN_FINALIZED`. The documentation-check evidence in the Ready/Finalized markers comes from that conductor run. | Conductor relay 2026-10-05: harness could not prompt the user; recommended option adopted. F8 is the reason a runner was needed. |
| **A7** | Approve-and-execute: finalize this plan Draft → Ready — frontmatter `status: current`, plans-index row Ready, Draft-only decision ledger removed (its answers are A3–A6 above), implementation open on T1. | Conductor FINALIZE relay 2026-10-05, carrying the user's entry-prompt pre-authorization of the decision-complete Draft as approved ("Hal approves" gate); adopted by the conductor in this non-interactive session. |

---

## 4. Test Contract

Per the test-first contract, every implementation task carries a row before
implementation starts. Changing an approved row — including weakening an assertion to
reach green — is a scope change that returns this plan to Draft.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| **T1** | `tests/KyberWeave.Tests/ApmManifestGuardTests.cs` (new; parses repo-root `apm.yml` via `KyberWeaveTestPaths.ToolRoot` + `YamlDotNet.RepresentationModel`, pattern per F6) | `dotnet build KyberWeave.sln -c Release --no-restore` then `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ApmManifestGuardTests"` | The repository manifest declares no `dpalfery/kyber-weave` self-dependency (string entries with or without `#ref`, object-form `git:` entries, in both `dependencies.apm` and `devDependencies.apm`), and pins `targets: [agent-skills]` exactly — the #285 invariant is a contract, not an accident. | Focused run on the branched, **unfixed** manifest fails `TheRepositoryManifestDeclaresNoApmSelfDependency` (the self-entry is present), full output captured; `TheRepositoryManifestPinsAgentSkillsTargets` is green and is not the RED driver. | Same tests pass with no weakened assertion; the targets-pin fact stays green throughout. |
| **T2** | Same file (T1's contract) | Same focused command | `apm.yml`'s `dependencies.apm` is empty (`apm: []`), `targets: [agent-skills]` and its comment untouched, the why-comment added. | T1's RED (driven by the manifest entry) — the fix task consumes it. | Both facts green; focused suite and then the full suite green; `dotnet format` whitespace/style verification clean (the new test file must build warning-free under `TreatWarningsAsErrors`/`AnalysisMode=all`). |
| **T3** | **Explicitly no automated test.** Git-state change. | `git status --short` and `git diff --cached --stat` | The self-install lock state no longer ships: `D apm.lock.yaml` staged, `.gitignore` unchanged (A4); no reader breaks (F4 evidence stands: repo-wide search found none). | n/a — replaced by read-only verification: the staged deletion and a repo-wide search for `apm.lock` consumers, recorded in the closeout. | Full suite (T4) green with the lock deleted; `docs validate .` zero findings. |
| **T4** | **Explicitly no automated test beyond T1's.** Full gate run. | §7 gate list | The declared repository gates pass on the finished branch: build, format, full test suite, skill gates, `docs validate .` zero findings, `docs drift .` zero findings (or skipped-no-index per the root `AGENTS.md` rule, never reported passed). | n/a — gate run; `docs validate . --merge-ready` is **expected** to fail `KW-DOC-LIFECYCLE-003` while this plan is in `docs/plans/` — that is the merge gate T6 clears by archiving, not a defect. | Every gate in §7 reports its result honestly, including skips. |
| **T5** | **Explicitly no automated test.** External-tool live verification. | See T5 commands | The real consumer flow works against this branch's manifest: scratch-dir `apm install` exits 0, deploys `.agents/skills/kyber-weave-docs/`, and produces no circular-dependency error. | Optional pre-fix capture on trunk (expected circular abort) if `apm` is present before T2 — evidence, not a gate. | Captured scratch-run output: exit 0 + deployed skill; if `apm`/network is unavailable in the implementing session, the A5 fallback is recorded as an open owner gate — never claimed shipped. |
| **T6** | **Explicitly no automated test.** Governance and closeout. | The §7 gate list + review council | Plan body, frontmatter, and index row reconciled in one save; the plan is archived in the finishing PR so `docs validate . --merge-ready` passes; disclosure accurate. | n/a — replaced by the council pass and the §7 gate run. | Council verdict recorded; PR opened; archive move clears `KW-DOC-LIFECYCLE-003`. |

---

## 5. Tasks

### T1 — Branch from trunk and write the RED guard test

- **Objective.** Establish the work branch and pin the #285 invariant as a failing
  contract before any state changes.
- **Branch.** `git switch -c hal.hermes.opencode/issue-285-apm-self-dependency` from
  current trunk (`main`).
- **Files / symbols.** New `tests/KyberWeave.Tests/ApmManifestGuardTests.cs` — two
  `[Fact]`s: `TheRepositoryManifestDeclaresNoApmSelfDependency` (parses `apm.yml` at
  `KyberWeaveTestPaths.ToolRoot`; for each entry in `dependencies.apm` and
  `devDependencies.apm`: string entries must not equal `dpalfery/kyber-weave` after
  stripping an optional `#ref` suffix; object entries must not carry
  `git: dpalfery/kyber-weave`; failure message names the circular-dependency consequence)
  and `TheRepositoryManifestPinsAgentSkillsTargets` (`targets` present and exactly
  `[agent-skills]`). `///` doc comments per house style; no production code.
- **Acceptance criteria.** Focused run RED per the T1 contract row, output captured; the
  test file builds warning-free.
- **Dependencies.** None.
- **Required skills.** `test-dev`.

### T2 — GREEN: remove the self-dependency from `apm.yml`

- **Objective.** Make every consumer install of the package resolvable, per A1.
- **Files.** `apm.yml` only — replace the entry with an empty list and extend the comment
  block with the why (comments explain why, not what):

```diff
 dependencies:
+  # Deliberately empty: this repository is the package, not a consumer of it. A
+  # self-entry here makes `apm install dpalfery/kyber-weave` abort on a circular
+  # dependency in every consuming repo (#285), which is how docs init's skill
+  # deployment broke. Pinned by ApmManifestGuardTests.
   apm:
-    - dpalfery/kyber-weave
+    []
   mcp: []
```

  The `targets: [agent-skills]` pin and its existing comment block stay untouched
  (root-`AGENTS.md` non-negotiable; A1).
- **Acceptance criteria.** T1's focused run GREEN, both facts; REFACTOR step: no
  production code exists to refactor — re-run focused then the full suite, and the
  `dotnet format` verifications, all green/clean.
- **Dependencies.** T1 (consumes its RED).
- **Required skills.** None — a YAML data edit verified by T1's contract.

### T3 — Remove the self-install lock state (A4)

- **Objective.** Stop shipping state that exists only because of the self-dependency
  (issue Fix §2; evidence F4).
- **Files.** `apm.lock.yaml` — `git rm apm.lock.yaml` only. `.gitignore` stays unchanged
  (A4): the lock pins nothing once `dependencies.apm` is empty, and a future real
  dependency wants its lock committed per APM's own guidance (F7).
- **Acceptance criteria.** Per the T3 contract row: staged deletion recorded, no reader
  breaks (F4 evidence re-checked), full suite green in T4.
- **Dependencies.** T2 (the fix lands first so the tree is final before gates).
- **Required skills.** None.

### T4 — Full verification gates

- **Objective.** The declared repository gates pass on the finished branch.
- **Files.** None changed; gate run only.
- **Acceptance criteria.** §7 gate list executed with results recorded — including
  `docs drift .` reported **skipped** if no usable CodeGraph index exists in the checkout
  (root `AGENTS.md` rule), never reported passed without a run.
- **Dependencies.** T2, T3 (tree final).
- **Required skills.** None.

### T5 — Live APM end-to-end verification (A5) and the post-merge owner gate

- **Objective.** Prove the consumer flow on this branch's manifest, in disposable scratch
  space, exactly as the issue's Verify section asks.
- **Procedure** (scratch directories under the session's approved temp area; nothing
  written into the clone):

```bash
# Branch-ref form (issue Verify bullet 1; requires the branch pushed to origin):
SCRATCH="$(mktemp -d)" && cd "$SCRATCH"
apm install dpalfery/kyber-weave#hal.hermes.opencode/issue-285-apm-self-dependency --target agent-skills
test -f "$SCRATCH/.agents/skills/kyber-weave-docs/SKILL.md"   # must exist, exit 0

# Local-path form (no network; APM's development-only local dependency form):
SCRATCH2="$(mktemp -d)" && cd "$SCRATCH2"
apm install /absolute/path/to/this/clone --target agent-skills
test -f "$SCRATCH2/.agents/skills/kyber-weave-docs/SKILL.md"
```

  An optional pre-fix RED capture (trunk manifest, expected circular abort) may be
  recorded if run before T2. The post-merge owner gate (issue Verify bullet 2) is
  recorded in the closeout: in an empty repo, `kyber-weave docs init . --target
  agent-skills` deploys the skill with no error.
- **Acceptance criteria.** Per the T5 contract row: captured output, exit 0, deployed
  skill, no circular-dependency error; or the recorded fallback (A5) naming the gap.
- **Dependencies.** T2 (fixed manifest); branch-ref form additionally needs the branch
  pushed (any time after T2).
- **Required skills.** None.

### T6 — Review, PR, lifecycle, and closeout

- **Objective.** Review the change, land it, and keep the plan lifecycle honest.
- **Files.** This plan; [`docs/plans/README.md`](README.md) (Active Plans row);
  relocation to `docs/archive/plans/` in the finishing PR per `KW-DOC-LIFECYCLE-003`.
- **Acceptance criteria.** `code-review` council pass on the branch (the
  `static-analysis-triage` lens reads `artifacts/inspectcode.xml` — a `.cs` file is
  touched: the guard test); PR to `main` via `create-pull-request`; body status,
  frontmatter, and index row reconciled in the same save; `docs validate . --merge-ready`
  passes in the archiving PR; closeout disclosure records every gap (F8 session limits,
  T5 fallback if taken, `docs drift` skip if applicable).
- **Dependencies.** T4, T5.
- **Required skills.** `code-review`, `create-pull-request`, `app-docs-standard`.

---

## 6. Dependency Graph and Concurrency

```text
T1 (branch + RED) → T2 (GREEN) → T3 (lock removal) ─┐
                                  └── T5 (live e2e) ─┼→ T4 (full gates) → T6 (review/PR/closeout)
```

- `T1 → T2`: the RED exists before the fix it pins.
- `T2 → T3`, `T2 → T5`: both consume the fixed manifest; `T5`'s branch-ref variant needs
  the push, which can happen any time after `T2`.
- `T3 ∥ T5`: disjoint scopes (git state vs. scratch directories) — may run concurrently.
- `T4` after `T3`/`T5`: the gates run on the final tree; `T5` writes nothing to the tree.
- `T6` consumes the concrete outputs of `T4` and `T5`.

**MAX_CONCURRENCY: 2** — `T3` and `T5` in parallel after `T2`; every other edge is
sequential.

---

## 7. Verification Gates

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ApmManifestGuardTests"
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

Or the equivalent declared gate suite:
`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`.

`docs validate . --merge-ready` is expected to fail with `KW-DOC-LIFECYCLE-003` while
this plan sits in `docs/plans/` — that is the merge gate, cleared by T6's archive move.
`docs drift .` requires a usable CodeGraph index; if none exists in the checkout it is
reported skipped, not passed (root `AGENTS.md`). The skill gates dogfood an unchanged
skill — they should pass unchanged, and a failure there means something else broke.

---

## 8. Risks, Out of Scope, Review, and Closeout

### Risks

| Id | Risk | Mitigation |
|---|---|---|
| **R1** | Something reads `apm.lock.yaml` that the repo-wide search missed, and removing it breaks a flow. | F4 evidence (no reader in `src/`, `tests/`, `.github/`); T4 full gates + T5 live e2e would surface any miss. |
| **R2** | `apm` absent from `PATH`, or network blocked, in the implementing session. | A5 fallback: local-path form needs no network; otherwise defer to the post-merge owner gate and record the gap — never claim an unrun check. |
| **R3** | The guard test is weakened to reach green (asserting only the targets pin, skipping the self-dependency fact). | Test contract forbids it; RED evidence pins the self-dependency fact; any contract change returns the plan to Draft for reapproval. |
| **R4** | `docs drift .` cannot run without a usable CodeGraph index. | Report skipped per the root `AGENTS.md` rule; never passed. |
| **R5** | Recurrence: someone re-runs `apm install dpalfery/kyber-weave` inside this repo (the #33 route). | APM's own circular abort and failed-install cleanup stop the in-repo route at the tool; the guard test fails the next suite run if an entry ever re-enters the manifest; the why-comment explains the invariant at the manifest itself. |
| **R6** | T5's branch-ref form needs the branch pushed before verification. | Push any time after T2, or use the local-path form (no network). |

### Out of scope

- Any change to `DocsInitCommand` / `DeploySkill` — the degraded, non-failing path for a
  missing or failing APM is deliberate design (CLI `AGENTS.md`, "Shelling out").
- Any change to the external APM loader, or requests to it.
- The skill source `.apm/skills/kyber-weave-docs/` — untouched; only the manifest that
  ships it is fixed.
- Tracking `.agents/` deploy output, or bumping the manifest's `version` (release
  management owns versioning; the issue does not ask).
- Adding any real dependency to `apm.yml`.
- An ADR — a reversible data fix with no architectural constraint; A1's evidence is
  recorded here and the invariant is enforced by the guard test plus the manifest's own
  comment.

### Review

A `code-review` council pass on the branch before the PR. The diff is small
(`apm.yml`, the `apm.lock.yaml` deletion, one new test file); the
`static-analysis-triage` lens should attribute any InspectCode findings from the new test
file to this change, and a reviewer must be able to state from the diff alone that the
`targets: [agent-skills]` pin and its comment survive byte-for-byte.

### `docs-dev` closeout

No canonical document describes the manifest's dependency list, so nothing is harvested
outward: the invariant lives in the guard test and the manifest's why-comment. Body
status, frontmatter, and the plans index row are reconciled in one save; the plan is
archived in the finishing PR per `KW-DOC-LIFECYCLE-003` so
`docs validate . --merge-ready` passes. No ADR.

### Closeout disclosure

Record, never fix, environment gaps: F8 (this plan's authoring session could not run the
two documentation checks — A6 records that the conductor's shell run is the evidence
source), any T5 fallback taken (live
verification deferred to the post-merge owner gate if `apm` was unavailable), and a
`docs drift` skip if no usable CodeGraph index existed in the checkout.
