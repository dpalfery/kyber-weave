---
id: plans/codex-primary-entrypoint
title: Restore Codex conductor skill discovery
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
autonomy: autonomous
---

# Restore Codex conductor skill discovery

Status: Archived — complete 2026-10-21. Development mode: test-first. Autonomy: autonomous.

## Problem and goal

The installed Squad receipt correctly owns `agents/conductor.toml`, but Codex skill
discovery has no `conductor/SKILL.md` entry point. Native delegated-agent availability
is present in the current session; the reproduced defect is missing `$conductor`
discovery in the main conversation. Restore that entry point through the renderer
and normal deployment transaction, preserving the native agent.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Development mode and autonomy | Test-first; autonomous. | No explicit test-first opt-out. Current root Config Reg declares autonomous; the conductor confirmed its earlier supervised snapshot was superseded. | ANSWERED — user request, live Config Reg, and conductor confirmation 2026-10-21. |
| D2 | Entry-point design | Add a derived skill for every primary Codex agent without a same-name canonical entry-point skill; retain native TOML. | Existing Claude and CommandCode projections establish the pattern. Product-owner and bug-crusher already have canonical entry-point skills; reuse them once. Avoid adding a conductor-only canonical duplicate or changing all harnesses. | ANSWERED — autonomous: recommendation accepted; established renderer and documented primary-identity pattern. |
| D3 | Body, resources, and paths | Derive skill body and three-key skill frontmatter from the agent; project its resource closure beside it. Respect project/global prefix resolution. | Match the existing skill serializer and resource projection rather than handwritten deployment files. Native agent resources remain intact. | ANSWERED — autonomous: recommendation accepted; existing native entry-point and resource-closure contract. |
| D4 | Scope and recovery | Fix renderer, contract tests, and canonical harness documentation; update this installation through the local fixed CLI and existing receipt. | No installer redesign, new dependency, model-policy change, or new canonical conductor skill. Preserve existing deployment target roster; do not replace it with a Codex-only roster. | ANSWERED — autonomous: recommendation accepted; reversible defect repair within user request. |
| D5 | Test contract | Use the contract below, proving entry point, unchanged native agent, exact resource closure, shared-primary reuse, and project/global paths. | Assert emitted behavior with positive and negative controls; retain corpus-count integrity rather than deleting the old assertion. | ANSWERED — autonomous: recommendation accepted; test-first default and documented projection contracts. |
| D6 | ADR and closeout | no ADR — ADR waiver: applies the established native-plus-entry-point pattern to one existing renderer. Harvest into Squad architecture and onboarding. | No new lasting architectural boundary or dependency. No documentation waiver: invocation behavior survives the task. | ANSWERED — autonomous: recommendation accepted; existing architecture pattern and plan closeout policy. |
| AP1 | Approve and execute | Approve this plan and its Test contract D5 for execution. | Conductor explicitly returned `AUTONOMOUS_APPROVAL: AP1` on 2026-10-21 under resolved autonomous mode. | ANSWERED — autonomous: recommendation accepted; explicit conductor gate AP1. |
| D7 | Global dry-run output count and receipt paths | Extend the existing global-root contract to count Codex native-plus-entry-point projection and independently assert all six additional conductor skill artifacts. | Global dry-run expected 124 and emitted 130 after T2. The difference is exactly the approved principal plus five resources; native TOML and its resources remain. Preserve exact count assertions, root confinement, bare relative paths, and all other target rows. No production change is required. | ANSWERED — autonomous: recommendation accepted; AP1 D2/D3 design and current renderer/source resource closure. |
| AP2 | Approve amended Test contract and resume execution | Approve T2a, its amended Test contract, and the updated dependency graph. | Conductor explicitly returned `AUTONOMOUS_APPROVAL: AP2` on 2026-10-21 under resolved autonomous mode. The bounded integration assertion preserves the approved design; AP1 and completed work remain recorded. | ANSWERED — autonomous: recommendation accepted; explicit conductor gate AP2. |
| D8 | Exact resource receipt path formula after T2a escalation | Project each resource as `dirname(principal.RelativePath) + / + resource.RelativePath`. A conductor resource already starts `conductor/references/`; native expected path is `agents/{resource.RelativePath}`, and skill expected path is `skills/conductor/{resource.RelativePath}`. | Confirmed from source loader and shared projection implementation. Current unverified test edit matches this formula. Earlier AP2 explicit skill path list omitted an existing `conductor/` segment; correct the plan, without changing renderer behavior or weakening receipt assertions. | ANSWERED — autonomous: recommendation accepted; source-verified clarification of D3/D7. |
| AP3 | Approve factual Test-contract path correction and bounded verification | Approve D8's corrected resource paths and bounded verification of the current paused test edit, without further code or test changes. | Conductor explicitly returned `AUTONOMOUS_APPROVAL: AP3` on 2026-10-21 under resolved autonomous mode. No output-policy change; failed-attempt history and AP1/AP2 remain intact. | ANSWERED — autonomous: recommendation accepted; explicit conductor gate AP3. |
| D9 | Recover a zero-test isolated-host handshake failure | Use the normal Release build and sequential approved class runs; if discovery/handshake fails again before any tests execute, fall back once to the exact unfiltered declared full coverage gate. Require actual execution of both affected classes. | The isolated build passed with zero warnings/errors, but the combined-filter launch exited 5 with zero tests. This supplies no assertion evidence. No source/test edit, assertion weakening, test omission, or timeout change is authorized. | ANSWERED — autonomous: recommendation accepted; bounded verification-environment recovery under AP3 with unchanged observable Test contract. |

## Investigation findings

- Investigator reproduced absent global `conductor/SKILL.md` under both Codex skill
  discovery locations while `agents/conductor.toml` exists. `squad status --global`
  reports deployed files match the receipt. The native conductor is available to
  delegation in this session.
- `CodexRenderer.RenderAsync` emits every native agent and canonical skills, but
  derives no skill from `invocation: primary`. `RenderSkill` already implements
  name, single-line description, and MIT-license frontmatter.
- `CodexRendererContractTests` expects native principals/resources plus canonical
  skills/resources only. Its output count must grow by derived primary principals
  and their closures, while assertions remain exact.
- Squad architecture documents native-plus-entry-point projection for Claude,
  canonical reuse for primary shared identities, and no canonical conductor skill.
  `docs_for_symbol(CodexRenderer)` identifies Squad architecture as formal owner.
- Documentation provenance root matches this checkout at revision `6b30f53`.
- Discovery: codegraph 2, docs 4, direct reads 5 unique repository files (instruction
  and skill resources excluded). Two CodeGraph calls named the
  target but returned unrelated `Render` symbols rather than its source; narrow
  self-gathered fallback read only `CodexRenderer.cs` and the first contract-test
  section. Other direct reads: root Config Reg, plans index, and catalog. Installed
  architect references and the frontmatter skill were also read. The index/catalog reads confirmed
  exact row placement and legal owner/component; instruction resources are outside
  the indexed source. Do not repeat broad exploration during execution.

### Global dry-run gate amendment, 2026-10-21

The declared full test gate failed with one failure: 3,023 passed out of 3,024;
exit code 2. `SquadGlobalRootTests.InstallAsync_GlobalScopeDryRun_PlansEveryFileUnderTheResolvedTargetRootWithBareRelativePaths`
for Codex expected 124 receipt files and observed 130. This is an exact-count
contract failure, not a timeout. The helper `ExpectedRenderedFileCount` adds
primary entry-point artifacts for Claude and Factory only. Current Codex renderer
now correctly emits conductor's skill plus the five canonical resources. Its
`NoPrimaryAgent == skill` condition and canonical shared-identity reuse remain the
approved design.

The six additional global receipt paths are exactly:

- `skills/conductor/SKILL.md`
- `skills/conductor/conductor/references/autonomy.md`
- `skills/conductor/conductor/references/plan-path.md`
- `skills/conductor/conductor/references/spec-path.md`
- `skills/conductor/conductor/references/intake-path.md`
- `skills/conductor/conductor/references/execution-and-review.md`

`agents/conductor.toml` and its resource closure must remain present. The receipt
paths must remain bare relative paths confined to the resolved Codex global root.
Compute expected counts independently from canonical source and documented target
policy; do not obtain the expected value from renderer output or replace the
assertion with a hard-coded 130.

Amendment discovery: codegraph 2, docs 1, direct reads 5 unique repository files.
Both CodeGraph attempts again returned unrelated symbols. Narrow self-gathered
fallback read only the failing test/helper, the changed renderer branch, and the
canonical conductor metadata; directory enumeration confirmed its five resource
names. Plan/index direct reads checked the current artifact and exact row while
preserving another active plan's concurrent row.

### T2a circuit-breaker assessment and recovery

T2a exhausted its two verification attempts. Latest recorded full gate still had
3,023 of 3,024 tests passing and exit code 2; the Codex row failed `Assert.Single`
at the native resource receipt assertion. The worker's former expected path added
the conductor name twice. Before pausing, it changed the assertion to
`agents/{resource.RelativePath}`; that edit has not yet been verified.

Source-established formula:

- `SquadSourceLoader.LoadResources` anchors closure paths to the canonical
  principal's containing directory. For `agents/conductor.md`, that root is
  `agents`; resource paths already contain `conductor/references/<file>.md`.
- `SquadResourceProjection.Append` appends this unchanged resource path to the
  rendered principal's directory. Native principal `agents/conductor.toml`
  therefore yields `agents/conductor/references/<file>.md`.
- Skill principal `skills/conductor/SKILL.md` yields
  `skills/conductor/conductor/references/<file>.md`. The repeated segment is
  intentional preservation of authored body-relative links, not a reason to strip
  or rename resource paths. Project scope adds `.codex/` to each root.

The current native assertion is statically correct. The earlier plan's explicit
skill-resource list was factually wrong and is corrected above. No production
fix or contract relaxation follows from this assessment. GREEN remains unproven.

After AP3, authorize one bounded verification dispatch of the current edit: rebuild,
run the complete global-root and Codex renderer contract suites, then rerun the
declared full coverage gate only if focused suites pass. Preserve the previous
failed-attempt tally. No further test or production edits are authorized by this
recovery; any failure returns its exact diagnostics to architect immediately.
Successful GREEN clears this specific assertion-path escalation for T5.

The first AP3 verification dispatch made no edits. Its isolated Release build
passed with zero warnings/errors, but the combined class-filter command failed
test-host handshake with exit 5 and zero tests run. Coverage and task-gate were
correctly not attempted. This is infrastructure failure, neither product RED nor
GREEN; it does not add an assertion-fix attempt or reset the existing tally.

D9 authorizes this bounded environment recovery under the unchanged AP3 contract:

1. Run the ordinary repository Release build without isolated output or intermediate
   directory overrides: `dotnet build KyberWeave.sln -c Release --no-restore`.
2. Run the two approved class selections separately against normal build output:
   `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter FullyQualifiedName~SquadGlobalRootTests`,
   then `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter FullyQualifiedName~CodexRendererContractTests`.
3. If both execute and pass, run the exact declared full coverage gate. If a
   focused launch instead fails discovery/handshake before any test execution,
   do not repeat launch experiments: fall back once to that same unfiltered full
   coverage gate, the previously executing command. Its detailed results must
   establish that both affected classes, including the Codex global dry-run row,
   actually executed and passed. A successful process with zero tests is invalid.
4. Any executed failing assertion, normal-build failure, or failure of the one
   full-gate fallback returns exact diagnostics to architect immediately. No edits,
   repeated environment experiments, or relaxed test criteria are authorized.
   Only after full GREEN may the worker run its task-gate and resume T5.

The full gate can supply affected-class execution evidence when focused launch
is unavailable; this changes launch mechanics only. No new Test-contract approval
is needed. The plan stays Ready with AP3 and the failed-attempt history intact.
Recovery discovery: codegraph 0, docs 1, direct reads 0; supplied launch evidence
was sufficient for this environment-only assessment.

Recovery discovery: codegraph 3, docs 1, direct reads 4 unique repository files.
CodeGraph supplied the shared projection formula and loader owner-root logic.
Narrow direct reads inspected the current paused test edit (not returned by the
index), the renderer branch omitted from the indexed excerpt, and one loader
detail outside the excerpt; the existing plan was read to preserve decision state.

## Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1/T2 | `tests/KyberWeave.Tests/CodexRendererContractTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~CodexRendererContractTests` | Both scopes emit conductor skill and native TOML, full normalized body and exact frontmatter; resources resolve beside both projections; no duplicate shared-primary canonical skill; no derived skill for a subagent; exact corpus count and unique paths. | New regression tests fail on missing conductor entry-point path before production edit; record test names, failure, and commit/diff state. Existing native-agent assertions stay green. | Same tests pass with native body/model and degradation contracts preserved; no weakened assertions. |
| T2a | `tests/KyberWeave.Tests/SquadGlobalRootTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~SquadGlobalRootTests` | Codex global dry-run owns exactly the approved native and skill artifacts: independent source-derived total includes six new conductor artifacts; exact skill paths, native TOML/resource preservation, bare paths and root confinement remain asserted. Other target behavior stays intact. | Existing full-gate failure is valid RED before test-only repair: Codex row expected 124, actual 130, 3023/3024 passing, exit 2. Record this as integration RED after T2, not as pre-production regression RED. | Same global-root suite passes for all target rows, alongside Codex renderer contracts. Rerun the exact declared full coverage test gate after rebuilding; all tests pass and host exits cleanly. |
| T3 | Canonical Squad docs | `kyber-weave docs validate .` and `kyber-weave docs drift .` | Documentation states how Codex main-session entry points relate to native delegated agents. | No-test task: inspect affected claims against T2 output before documenting them. | Both docs checks report zero findings; claims agree with outputs. |
| T4 | Local installation and receipt | Fixed local CLI `squad update --global --source products/kyber-squad`, followed by `squad status --global` | Receipt owns conductor skill and its closure, native agent remains; existing target roster preserved. | Investigator's missing-file evidence is environmental baseline, not claimed as automated RED. | Status clean; emitted and installed skill/closure hashes agree; fresh Codex skill inventory shows conductor, or record restart-dependent verification explicitly. |

## Tasks and dependencies

T2a exact resource paths use D8: native `agents/{resource.RelativePath}`;
entry point `skills/conductor/{resource.RelativePath}`. The six concrete skill
paths are listed in the amendment findings. AP3 recovery changes no count,
resource-preservation, confinement, or other-target assertion.

### T1 — Establish RED contract

Required skills: `test-dev`, `task-gate`. File ownership:
`tests/KyberWeave.Tests/CodexRendererContractTests.cs`; extend an existing
resource-projection test file only if needed for the same observable contract.
Add project/global tests and negative controls listed above; update the exact
expected corpus count to include only eligible derived entry points and closures.
The count change is an output-contract update, not permission to remove assertions.
Capture RED before T2. Dependency: AP1 approval. Acceptance: intended missing-skill
failure reproducible and task gate reports the expected RED explicitly.

### T2 — Render primary Codex entry points

Required skills: `csharp-dev`, `task-gate`. File ownership:
`src/KyberWeave.Core/Squad/Rendering/CodexRenderer.cs`.
Use invocation metadata, same-name canonical skill/shared identity resolution,
existing serializer, prefix resolution, and resource projection. Keep every native
agent and its resource closure. Reuse canonical primary skills exactly once and
derive a skill only for a primary lacking its canonical entry point. Never derive
skills for subagents. Update renderer XML remarks to explain invocation behavior.
Dependency: T1 RED. Acceptance: same contract tests GREEN and task gate PASS.

### T2a — Align global dry-run integration contract

Required skills: `test-dev`, `task-gate`. File ownership:
`tests/KyberWeave.Tests/SquadGlobalRootTests.cs` only. Extend
`ExpectedRenderedFileCount`'s native-plus-entry-point branch to include Codex,
retaining source-derived eligibility, fallback policy, and resource counts.
Extend the existing global dry-run entry-point/resource assertions to include
Codex, asserting its native `agents/conductor.toml` separately from Claude's
`.md` agent and Cursor's skill-only behavior. Assert exactly the six listed skill
artifacts, preserve native resources, shared-primary reuse, root confinement,
unique output paths, and all other harness rows. Do not weaken/delete assertions
or change production code to recover the obsolete expected count.
Dependency: AP2 approval and existing T2 output. The full-gate failure already
establishes RED; no new production change or invented earlier RED is needed.
Acceptance: complete global-root suite and Codex renderer contracts GREEN,
task gate PASS, then exact full coverage gate GREEN before final review.
After the two-attempt escalation, T2a is paused pending AP3 and resumes only the
bounded verification dispatch described above; the current corrected assertion
requires no additional edit on the assessed evidence.
AP3 has been granted. D9 governs recovery from its zero-test isolated-host launch
failure; it does not authorize another assertion-fix iteration.

### T3 — Harvest canonical invocation guidance

Required skills: `app-docs-standard`, `kyber-weave-docs`, `task-gate`.
File ownership: `docs/kyber-squad/architecture.md` and
`docs/kyber-squad/onboarding.md`. Describe Codex native agents plus primary
entry-point skills, shared-primary reuse, project/global discovery paths, and fresh
session expectations grounded in actual local observation. Update the Codex target
table row only; do not repair unrelated stale target counts. Dependency: T2 output.
Acceptance: docs validate/drift clean and task gate PASS.

### T4 — Verify and repair local installation

Required skills: `task-gate`. Own only the deployment managed by the existing global
Squad receipt. Build/use the fixed local CLI, inspect `squad update --help` and the
current receipt before mutating; use supported source selection for
`products/kyber-squad`. Preserve the receipt's target roster by omitting `--target`.
Preview update when supported, run normal update, then status. Verify installed
conductor skill and closure content match rendered output. Do not hand-write the
skill or alter Codex's shared configuration. Dependency: T2. Acceptance: native
agent plus new receipt-owned skill present; status clean. A running conversation's
cached skill catalog may require a fresh Codex session; distinguish installed bytes
from live UI discoverability and do not assert an unperformed check.

### T5 — Whole-change review and documentation closeout

Required skills: `code-review`, `app-docs-standard`, `kyber-weave-docs`.
The conductor routes review through `code-reviewer` once over the accumulated
change, after all developer task gates pass. Then `docs-dev` records test/live
evidence, maps D2/D3 to architecture and D4 to onboarding, preserves all Decisions,
archives this plan under the normal closeout policy, and updates its index row.
Dependency: T1–T4 and T2a complete. Acceptance: review APPROVE, no unresolved findings,
canonical harvest recorded, merge-ready docs validation and drift pass. No commit,
push, release, or PR is authorized merely by this plan.

## Closeout

Completed 2026-10-21. The Codex renderer now derives an entry-point skill for a primary
agent that has no same-name canonical skill, while retaining its native TOML agent and
resource closure. Primary shared identities reuse their canonical skills. The global
deployment receipt is clean and owns `skills/conductor/SKILL.md`, its resource closure,
and `agents/conductor.toml` with its native resource closure. A fresh Codex session was
not used to inspect the runtime skill catalog; the installed receipt is verified, while
the active session may require a restart to refresh discovery.

Verification: the recovery run completed all declared gates (18/18 PASS). Whole-change
review returned APPROVE, risk LOW, with zero findings. `squad status --global` reports
that all deployed files match the receipt and confirms the conductor skill and native
agent artifacts are present.

| Decision | Closeout mapping |
|---|---|
| D1 — test-first mode and autonomous execution | no ADR — ADR waiver: execution mode and approval are delivery-specific process choices. Documentation waiver: approval and execution mode are historical to this delivery; they establish no surviving product invariant. Recorded here with provenance. |
| D2 — derive a Codex skill for an unoccupied primary entry-point identity | [Kyber-Squad architecture](../kyber-squad/architecture.md#8-rendering) and [onboarding](../kyber-squad/onboarding.md#codex-notes) describe the Codex primary entry point and native-agent retention. no ADR — ADR waiver: applies the existing native-agent plus entry-point pattern to one renderer. |
| D3 — derive the body/frontmatter and project the resource closure | [Kyber-Squad architecture](../kyber-squad/architecture.md#8-rendering) documents the derived entry point and co-located resource closure. no ADR — ADR waiver: uses the existing skill serialization and resource-projection contracts. |
| D4 — preserve the target roster and update through the existing receipt | [Kyber-Squad onboarding](../kyber-squad/onboarding.md#detection-rules) records that updates reuse the receipt roster when no target list is supplied; the Codex path behavior is also in [architecture](../kyber-squad/architecture.md#8-rendering). no ADR — ADR waiver: follows the established deployment transaction and target-resolution policy. |
| D5 — test contract and positive/negative controls | no ADR — ADR waiver: test assertions are not an architectural decision. Documentation waiver: the assertions are test-fixture scoped and introduce no invariant beyond the renderer and receipt behaviors documented in architecture. |
| D6 — no new ADR and mandatory canonical harvest | no ADR — ADR waiver: this change refines the existing renderer pattern and creates no new architectural boundary, dependency, or policy. Durable behavior is mapped under D2–D4 and D7–D8. |
| D7 — exact global receipt paths, count, and root confinement | [Kyber-Squad architecture](../kyber-squad/architecture.md#8-rendering) and [onboarding](../kyber-squad/onboarding.md#codex-notes) document the project/global roots and resource closure. no ADR — ADR waiver: the exact six-file expected count is a test assertion over the established projection. Documentation waiver for that test-fixture detail. |
| D8 — preserve authored resource paths under both native and skill roots | [Kyber-Squad architecture](../kyber-squad/architecture.md#8-rendering) documents the retained native agent and projected skill resource closure. no ADR — ADR waiver: this is the established resource projection rule, with no new namespace or link-rewrite behavior. |
| D9 — one bounded fallback for zero-test host handshake failure | no ADR — ADR waiver: a verification launch recovery does not constrain the product. Documentation waiver: this was a one-time verification-environment recovery decision; the successful full gate superseded the fallback and left no product behavior or operating procedure to retain. |
| AP1, AP2, AP3 — approve-and-execute gates | no ADR — ADR waiver: approval provenance does not establish an architectural choice. Documentation waiver: these gates are approval provenance for this plan's execution, preserved in its decision table, with no continuing system invariant. |

The implementation, verification, and canonical-documentation mapping are complete. The
plan is archived under the normal plan lifecycle. No commit, push, release, or pull
request was part of this closeout.

Dependency graph: AP1 → T1 RED → T2 GREEN → {T3, T4};
{AP2, T2} → T2a integration GREEN; {T3, T4, T2a} → T5.
Completed T1–T4 work is not repeated merely because AP2 adds an integration test task.
Recovery dependency: AP3 → T2a bounded verification → T5.

## Verification gates and review

Each developer supplies its own `TASK_GATE:` digest; a failure returns to that
worker. Use the repository-declared suite for final review:
`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`.
Because the change affects rendered Squad release contents, also run the documented
local release loop `./scripts/update-loop.sh` during execution. Run docs validate
and drift after every docs change; use merge-ready validation only after closeout
archives the active plan. Fast-loop output is iteration feedback, not review evidence.

For T2a, rebuild before using `--no-build`, then rerun the exact failing declared
gate command:
`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --coverlet --coverlet-output-format cobertura --results-directory artifacts/coverage`.
Do not substitute timeout tuning for repairing the diagnosed assertion contract.

## Risks and out of scope

Skills and native agents differ in execution context and enforced permissions; keep
existing degradation accounting and do not invent stronger skill enforcement claims.
Avoid duplicate output paths and missing references when the same resources are
projected to both principal directories. Preserve all existing receipt targets and
unrelated local artifacts. No changes to other renderers, canonical conductor role,
dependencies, registry generation, or application behavior beyond this projection.

## Planning validation

`kyber-weave docs validate .` and `kyber-weave docs drift .` both passed on
2026-10-21 with zero findings, including after AP3 Ready finalization. AP1, AP2,
and AP3 are explicitly approved. Verification of the paused edit is authorized;
further code or test changes are not. No implementation or test edits were made
by architect.
