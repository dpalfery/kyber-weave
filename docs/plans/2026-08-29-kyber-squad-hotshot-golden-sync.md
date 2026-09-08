---
id: plans/2026-08-29-kyber-squad-hotshot-golden-sync
title: Kyber-Squad Hotshot Golden Copy Synchronization
doc-type: plan
status: current
lifecycle: approved
approval: approved
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-30
---

# Kyber-Squad Hotshot golden copy synchronization

**Status:** Approved — current HEAD baseline recorded
**Approval:** Approved by the user on 2026-08-29 through the instruction to update and execute from current HEAD
**Execution model:** Test-first; establish the golden-output contract before changing canonical source or rendering
**Goal:** Make Kyber-Squad's GitHub Copilot deployment reproduce the current
`hotshot-logistics-demo/.github/agents` and `.github/skills` trees, except for one approved
transformation: every emitted agent tool list uses one deterministic cross-agent order.

## 1. Scope and fixed decisions

The user fixed these boundaries before planning:

1. `/Users/dave/git/kyber-weave2` is the only repository that may change.
2. `/Users/dave/git/personal/hotshot-logistics-demo` is a read-only golden source. No command in
   this plan may modify it, including checkout, pull, reset, format, generation, or test output.
3. Implementation starts from the current Kyber-Weave HEAD
   `477ce9575e7408175a0fd8d1437bf4bc46506714` on `feature/update-squad-for-copilot`. Do not fetch,
   pull, merge, rebase, stash, reset, restore, or reconcile historical dirty paths as part of this
   plan. The worktree was clean when this baseline was recorded; the revised plan and plan-index
   edits are the only expected pre-implementation changes. If HEAD changes or any unrelated path
   appears before implementation, stop and rebaseline the plan to the new current state rather than
   trying to reconstruct an older one.
4. The rendered Copilot tree is the parity boundary: its 24 agent files and 24 skill
   `SKILL.md` files mirror the golden tree exactly, except for normalized agent tool ordering.
   Supplemental canonical skill resources are retained as packaged knowledge and are not emitted
   by the Copilot renderer.
5. Canonical source and package authority lives under `products/kyber-squad/`; normal packing and
   rendering consume that source. Kyber-Weave separately and intentionally tracks a Copilot
   self-deployment under root `.github/agents` and `.github/skills`, with deployment state in
   `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json`. That self-deployment is
   stale by design during this work and is explicitly out of scope: this plan must not edit,
   regenerate, delete, or reconcile any of those paths. A human will refresh them with a fresh
   Kyber-Weave release candidate after this change is delivered.
6. Complete every implementation task and the full local verification matrix before delivery.
   After that local completion boundary, the user authorizes committing and pushing the current
   feature branch, opening or updating the pull request needed for review, watching its GitHub CI
   checks, fixing in-scope failures, and requesting `@coderabbitai` review. This authorization
   supersedes the earlier no-push/no-PR boundary. It does not authorize tagging, releasing, or
   publishing an RC; the human-owned self-deployment refresh remains separate.

The golden agent frontmatter contains `name`, `description`, optional `model`, `tools`, optional
`agents`, optional `user-invocable`, and `metadata`. It contains no top-level `title` or
`permissions` key. Therefore this plan treats `name` as the deployed title/identity and treats
the exact tool allow-list plus `metadata.capability-profile` and the instruction body as the
permission contract. It does not invent absent golden fields.

## 2. Grounded discovery

### Current execution baseline

| Repository | Current state |
|---|---|
| Kyber-Weave | `/Users/dave/git/kyber-weave2`; branch `feature/update-squad-for-copilot`; HEAD `477ce9575e7408175a0fd8d1437bf4bc46506714`. The worktree was clean before this revision; only this plan and `docs/plans/README.md` are expected to differ at approval time. This commit is the implementation baseline. No origin synchronization or historical worktree reconciliation is part of this plan. |
| Golden demo | `/Users/dave/git/personal/hotshot-logistics-demo`; branch `epam-demo/4-kyber`; HEAD `677c3a876ba9c62f1083608596b238c9deaff167`. The repository has unrelated dirty files, but `.github/agents` and `.github/skills` are clean and are the only golden surfaces read by this plan. |

T0 is now a read-only baseline assertion, not a synchronization operation. It verifies the exact
Kyber-Weave HEAD and requires the pre-implementation diff to contain only this plan plus the plan
index. It captures the golden HEAD plus scoped `.github` status and proceeds directly to the
test-first work. It does not contact origin or preserve, restore, or merge paths from an earlier
repository state. If the baseline changes, update this section to the new current state and review
the resulting diff; do not resurrect the removed reconciliation flow.

### Golden inventory

The golden tree has 24 agents:

`architect`, `architect-v3`, `azure-reader`, `bug-crusher-investigator`, `code-reviewer`,
`conductor`, `conductor-v3`, `csharp-dev`, `dal-dev`, `docs-dev`, `github-devops`, `maui-dev`,
`product-owner`, `pulumi-dev`, `python-dev`, `react-dev`, `research-agent`, `review-lens`,
`review-triage`, `sql-database-architect`, `task-reviewer`, `task-reviewer-v3`, `tauri-dev`, and
`test-dev`.

The golden tree has 24 skill identities and 24 files: one `SKILL.md` for each identity and no
additional files beneath `.github/skills`:

`app-docs-standard`, `architecture-decision-record`, `azure-cli`, `azure-naming`, `bug-crusher`,
`code-review`, `create-pull-request`, `create-pull-request-github`, `csharp-dev`, `csp-security`,
`dal-dev`, `dp-code-reviewer`, `github-cli`, `github-devops`, `lm-studio-cli`, `maui-dev`,
`pr-review-fix-comments`, `product-owner`, `python-dev`, `resharper-clt`, `second-brain`,
`security-review`, `setup-dev-environment`, and `test-dev`.

This absence is a golden-source defect rather than evidence that the resources are disposable:
61 of the 66 canonical-only files are directly referenced by retained golden `SKILL.md` files.
The two create-PR scripts also carry reusable behavior, and the setup environment agent resource
remains retained by default. The golden deployment therefore contains dangling resource references
that canonical source and packages must not reproduce.

### Delta from current canonical source

- Canonical source currently has 23 agents. `task-reviewer` already exists; only
  `task-reviewer-v3` is golden-only.
- Of the 23 common agents, 12 instruction bodies already match the golden bodies after removing
  YAML frontmatter and 11 differ. Descriptions, model projections, tool membership, and delegation
  metadata drift independently of body parity and still require exact reconciliation.
- Canonical source currently has 26 skills. `conductor` and `conductor-v3` are absent from the
  golden skill set and must be removed from the canonical skill inventory.
- All 24 common `SKILL.md` raw files differ from the golden files because their frontmatter bytes
  differ. After stripping YAML frontmatter, 22 instruction bodies match; only the `bug-crusher`
  and `resharper-clt` instruction bodies differ. Relative to the baseline HEAD, all 24 common
  `SKILL.md` files are raw byte replacements.
- The canonical skill tree currently has 90 files: 26 `SKILL.md` files and 64 supplemental
  resources. Only `conductor/SKILL.md` and `conductor-v3/SKILL.md` are deleted. The 61 referenced
  resources, both create-PR scripts, and `setup-dev-environment/agents/openai.yaml` remain canonical
  and recursively packaged until their knowledge is migrated through the governed follow-up todo.
- The current target repository intentionally tracks a stale Copilot self-deployment under root
  `.github/agents` and `.github/skills`, plus `.kyber-weave/squad.lock.yml` and
  `.kyber-weave/squad.receipt.json`. Those paths are not canonical or package authority and are
  outside this plan's diff. The human refreshes them from a fresh Kyber-Weave RC after delivery.

### Current source and rendering seams

- `SquadPackSourceLocator` resolves only `products/kyber-squad` as canonical source.
- `SquadPacker` already archives `skills/` recursively.
- `SquadSourceLoader` currently loads only `skills/<name>/SKILL.md` into AgentIR.
- `CopilotRenderer` currently emits `.github/skills/<name>/SKILL.md`, which is the complete current
  golden skill-file shape.
- `CopilotRenderer` computes tools from a lossy capability-to-tool map and one fixed order. That
  model cannot preserve the golden architect granular edit tools or the per-agent tool sets.
- Kyber-Squad declares nine targets. Five renderers are implemented and registered: `copilot`,
  `cursor`, `claude`, `codex`, and `antigravity`. The four unsupported targets — `opencode`,
  `kilo`, `warp`, and `factory` — fail coverage preflight and are outside this synchronization
  plan.
- The current renderer suppresses `conductor` and `conductor-v3` skills because canonical source
  models them as shared agent/skill identities. Removing those two golden-absent skills changes
  them to ordinary agent identities and requires the fallback and validation contracts to be
  updated accordingly.

## 3. Technical design

### 3.1 Canonical agent representation

Keep target-neutral lifecycle fields (`invocation`, `capability-profile`, `delegates-to`,
`fallback`, and `aliases`) because the deployment engine needs them. Replace each canonical
description and instruction body with the golden value, without paraphrase.

Add an ordered Copilot tool projection to the canonical agent schema and AgentIR. The field holds
the exact golden tool identifiers as a set; the renderer emits them through one global order that
contains every golden identifier. The order is frequency-descending, where frequency is the
number of the 24 golden agent files whose `tools` list contains the identifier. Equal-frequency
tools retain their relative order from the plan's prior deterministic order:

| Tool | Golden agent frequency |
|---|---:|
| `vscode` | 24 |
| `read` | 24 |
| `todo` | 24 |
| `codegraph/*` | 22 |
| `kyber-weave/*` | 22 |
| `context7/*` | 22 |
| `search` | 22 |
| `execute` | 16 |
| `web` | 12 |
| `edit` | 11 |
| `agent` | 6 |
| `edit/createDirectory` | 2 |
| `edit/createFile` | 2 |
| `edit/editFiles` | 2 |
| `edit/rename` | 2 |
| `vscodeGeneral/rename` | 1 |

```text
vscode
read
todo
'codegraph/*'
'kyber-weave/*'
'context7/*'
search
execute
web
edit
agent
edit/createDirectory
edit/createFile
edit/editFiles
edit/rename
vscodeGeneral/rename
```

For each agent, the renderer filters this global sequence against that agent's exact golden tool
membership. Normalization changes order, never membership, and has no per-agent ordering
exceptions. Validation rejects unknown identifiers, duplicates, or a source order that the
normalizer cannot project deterministically. Across all 276 pairs of golden agents, this order
produced an aggregate pairwise common-prefix proxy of 1,947 tool identifiers, compared with 1,118
for the prior plan order. That measurement motivates the deterministic ordering policy; it is a
structural proxy for shared serialized prefixes, not a claim or guarantee about production cache
hit rates.

Continue using model profiles for cross-harness abstraction where the mapping is lossless. Add a
profile for `MAI-Code-1.1-Flash (copilot)` and assign it to `test-dev`, `task-reviewer`, and
`task-reviewer-v3`. Preserve the existing exact Copilot mappings for GPT-5.6 Sol, GPT-5.6 Luna,
and Grok 4.5. Conductors continue to omit `model`, matching the golden files.

The capability lattice remains the cross-harness permission authority. Align its decisions with
the golden tool membership, including the architect execution/write/delegation surface, and add a
validator that prevents a Copilot tool projection from exceeding its capability profile. MCP
wildcards remain gated and quoted exactly. If exact golden membership cannot be represented by a
shared profile, add a narrower internal profile while preserving the golden deployed metadata
value; never silently widen a shared role.

### 3.2 Golden roster and orchestration graph

Update the existing `task-reviewer` and add `task-reviewer-v3` as subagents with the golden
descriptions, models, tools, metadata, and instruction bodies. Update both conductor delegation
rosters exactly:

- `conductor` delegates per-task completion audits to `task-reviewer`.
- `conductor-v3` delegates Red/Green completion audits to `task-reviewer-v3`.

Update `bundles/full.yml`, canonical counts, schema fixtures, migration records, and roster tests
to 24 agents.

### 3.3 Deployed skill fidelity and canonical knowledge preservation

Reconcile deployed skill identities and bytes without discarding canonical knowledge:

- replace all 24 common `SKILL.md` files with the exact golden raw bytes; 22 preserve their
  instruction bodies while receiving golden frontmatter, and `bug-crusher` and `resharper-clt`
  also replace their instruction bodies;
- remove only `conductor/SKILL.md` and `conductor-v3/SKILL.md`;
- restore and retain all 64 supplemental canonical resources from the baseline: the 61 files
  referenced by retained skills, both create-PR scripts, and
  `setup-dev-environment/agents/openai.yaml`.

`SquadSourceLoader` and `CopilotRenderer` continue to treat only `SKILL.md` as deployable skill
content, so canonical resource retention does not alter the exact 48-file Copilot render.
`SquadPacker` remains recursive and carries the retained resources. Regression coverage proves
that the Copilot render contains only the 24 golden `SKILL.md` paths with identical bytes, that the
package contains every retained resource, and that relative resource references from retained
skills resolve both in canonical source and in the package.

The deferred migration of this knowledge into `products/kyber-squad/standards/*`, governed
documentation baselines, and appropriate durable homes for procedural or lens material is tracked
in [migrate-skill-resources-into-standards](../todo/migrate-skill-resources-into-standards.md).
Skill routing changes only after migrated content is verified; resources stay packaged until then.

Removing the two conductor skills makes the agent/skill namespace intersection seven
distinct-body collisions rather than nine identities with two shared bodies. Update fallback
profiles and render validation so a fallback-only target would lower each conductor agent to its
unoccupied same-name role skill. Do not retain hidden conductor skill source merely to satisfy the
old invariant; exact rendered parity governs the deployed set while supplemental canonical
resources remain packaged.

### 3.4 Exact update and deletion behavior

Rendered Copilot output consists of exactly:

- 24 `.github/agents/<name>.agent.md` files;
- 24 `.github/skills/<name>/SKILL.md` files.

Add lifecycle regression coverage proving an update deletes receipt-owned files that disappeared
from a newer render while preserving unmanaged and locally edited files under existing conflict
rules. Roster removal must flow through the normal deployment plan and transaction engine; do not
add ad hoc recursive deletion.

### 3.5 Golden parity verification without coupling CI to a personal path

Add an opt-in local integration test that accepts the golden repository root through an explicit
environment variable. It renders Copilot output in memory and compares it to the external golden
tree:

- the rendered skill path set must contain exactly the 24 golden `SKILL.md` paths, with identical
  bytes; supplemental canonical and package resources are outside external golden parity;
- agent frontmatter keys and values must be identical;
- instruction bodies must be byte-identical after existing LF normalization;
- tool membership must be identical, and rendered order must equal the single approved order;
- no extra or missing path is allowed.

The ordinary test suite must not require `/Users/dave/...` or skip its core assertions. Keep
in-repository roster, schema, renderer, package, and lifecycle tests that prove the same contracts
from canonical source. The external-path test is the final local provenance check that the imported
source still matches the designated golden working tree.

### 3.6 Documentation and tracked-output boundary

Update canonical documentation that currently states 23 agents, 25/26 skills, nine namespace
intersections, or shared conductor skill identities. At minimum this includes:

- `products/kyber-squad/README.md`;
- `docs/README.md`;
- `docs/kyber-squad/README.md`, `architecture.md`, `onboarding.md`, and `requirements.md`;
- `docs/context-hygiene/agents.md` and `skills.md`;
- any current distribution or verification document found by the implementation count sweep.

Update all affected migration records so they name the Hotshot golden source and record normalized
body digests; add records for both task reviewers. The root `.github` Copilot self-deployment and
its tracked `.kyber-weave` state are intentionally committed repository content, but they are not
outputs of this plan and must remain byte-for-byte untouched. A human will regenerate that
self-deployment after a fresh Kyber-Weave RC exists. The tracked outputs of this work are
canonical/package source, schemas/profiles, implementation, tests, migration evidence, and
governed documentation.

## 4. Test-first task graph

| Task | Owner | Depends on | RED contract | GREEN work and acceptance |
|---|---|---|---|---|
| T0 — Assert the current baseline | github-devops | Approval of this revised plan | None; this is a read-only precondition check. | Require Kyber-Weave HEAD `477ce9575e7408175a0fd8d1437bf4bc46506714` and require the pre-implementation diff to name only this plan and `docs/plans/README.md`. Record the Hotshot HEAD and scoped status for `.github/agents` and `.github/skills`, require those golden surfaces to be clean, and proceed without fetch, pull, merge, backup, restore, or reconciliation. The demo repository remains untouched. |
| T1 — Pin the golden contracts | test-dev | T0 | Add failing tests for the 24-agent roster, 24-skill roster, both task reviewers, conductor delegation edges, exact model values, exact tool membership, normalized order, exact bodies/descriptions, and the 24 rendered golden `SKILL.md` paths and bytes. Separately pin retention of all 64 canonical resources, their recursive package presence, resolution of retained skills' relative references in canonical and package trees, removal of only the two conductor skills, and the exact 48-file Copilot render. Add the opt-in external-golden parity test. | The new tests fail for the current 23-agent/26-skill source and pre-amendment rendering/package contracts for the expected reasons, with no production edits mixed into RED. |
| T2 — Extend AgentIR for lossless Copilot tools | csharp-dev | T1 | Use the T1 schema/model/renderer failures. | Extend agent schema, model, loader, validation, model profiles, capability checks, and deterministic tool ordering. Preserve all existing non-broadening checks and diagnostics with actionable hints. The focused source/renderer tests pass. |
| T3 — Import the golden agents and graph | docs-dev for artifact content; csharp-dev for schema fallout | T2 | T1 roster/body/delegation tests remain red until import. | Mirror all 24 golden agents into canonical source, add migration records for `task-reviewer` and `task-reviewer-v3`, refresh the 20 existing records, update the bundle and profiles, and preserve all fields exactly except tool order. Focused agent tests and the external parity agent comparison pass. |
| T4 — Reconcile skills without knowledge loss | csharp-dev + docs-dev for artifact content | T1 | Add/retain failing tests showing two extra conductor skill identities, raw-byte drift in all 24 common `SKILL.md` files, body drift only in `bug-crusher` and `resharper-clt`, and the need to distinguish 64 retained canonical/package resources from the exact 24-file rendered skill surface. Tests must fail if a retained relative reference is unresolved in canonical source or package output. | Keep the current working-tree replacements that make all 24 common `SKILL.md` files exact golden raw bytes, restore all 64 supplemental resources from the baseline, delete only the two conductor `SKILL.md` identities, update fallback/shared-identity contracts, and prove: canonical inventory is 24 golden-raw-byte skills plus 64 retained resources; Copilot output is exactly 24 golden skill files within the 48-file render; the recursive package contains all retained resources; and every retained relative reference resolves. Focused skill, source, renderer, package, and reference-integrity tests pass. |
| T5 — Prove exact deployment deletion | test-dev + csharp-dev | T3, T4 | Add a lifecycle test whose previous receipt owns a file absent from the next render, alongside unmanaged and locally edited neighbors. | Normal update planning removes only obsolete receipt-owned paths, preserves conflicts under current policy, and leaves a receipt matching exactly the 48 rendered files. Full Squad lifecycle tests pass cleanly. |
| T6 — Reconcile governed documentation | docs-dev | T3, T4, T5 | `docs validate`/`docs drift` and count sweeps expose stale roster, lowering, and renderer-coverage claims. | Update the canonical documents and product README listed in §3.6, with 24-agent/24-skill counts, seven collision identities, conductor unoccupied fallback behavior, the exact 24-file rendered skill shape, retained canonical/package resources, and accurate renderer coverage: nine declared targets; five implemented and registered (`copilot`, `cursor`, `claude`, `codex`, `antigravity`); four unsupported targets that fail coverage preflight (`opencode`, `kilo`, `warp`, `factory`). Explain that `products/kyber-squad/` is canonical/package authority while the intentional root `.github` Copilot self-deployment and `.kyber-weave` state are out of scope, remain untouched, and will be refreshed by a human after a fresh RC. Both documentation gates return zero findings. |
| T7 — Full verification, delivery, and review | code-reviewer + github-devops | T2–T6 | Any failing required gate or golden parity mismatch is blocking. | First run the complete local verification matrix in §5, inspect the scoped diff and deletion set, confirm the demo repository status/HEAD are unchanged, confirm there are no changes under root `.github/agents`, root `.github/skills`, `.kyber-weave/squad.lock.yml`, or `.kyber-weave/squad.receipt.json`, and obtain a local APPROVE verdict with zero open findings. Only after every local task and gate is complete, commit and push the current feature branch, open or update its pull request, watch GitHub CI, fix in-scope failures and re-run the affected local gates, and request `@coderabbitai` review. Do not tag, release, publish, or refresh the root self-deployment. |

T3 and T4 may proceed in parallel after T2 only if their file scopes remain disjoint. T5 waits for
both because it verifies the combined rendered tree. T6 follows implementation so documentation
describes the verified behavior rather than an intended design.

## 5. Verification matrix

After implementation, run:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
./scripts/update-loop.sh
```

Also run focused validation for every canonical golden skill, the external golden parity test with
the demo root supplied explicitly, a local `squad pack` into a disposable directory outside the
repository, archive-content and retained-reference inspection, `git diff --check`, and a final source/file count
sweep. Before and after the parity test, record the demo repository HEAD and `git status --short`
and require them to be identical.

Expected final invariants:

1. Canonical bundle: 24 agents and 24 skills; canonical skill storage also retains 64 supplemental
   resources, for 88 files beneath `products/kyber-squad/skills/`.
2. Copilot render: exactly 48 files total — 24 agents and the 24 golden `SKILL.md` paths, with no
   supplemental resource paths.
3. Recursive package: all 24 skills and all 64 retained resources are present, and every retained
   skill-relative reference resolves in canonical source and package output.
4. Golden render differences: tool ordering only; no other agent field/body difference and no
   rendered skill path/byte difference.
5. The intentional tracked self-deployment at root `.github/agents` and `.github/skills`, plus
   `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json`, has no diff. It is not the
   canonical/package authority and remains stale until a human refreshes it from a fresh RC.
6. No mutation of the demo repository.
7. All local tasks and gates pass before delivery; then the current feature branch is committed
   and pushed, its pull request is opened or updated, GitHub CI is green after any in-scope fixes,
   and `@coderabbitai` review is requested. No tag, release, RC publication, or self-deployment
   refresh occurs.

## 6. Risks and stop conditions

- **Baseline drift:** if Kyber-Weave HEAD changes or the pre-implementation diff includes a path
  other than this plan and `docs/plans/README.md`, stop and update the plan to the new current state.
  Do not fetch or merge an unrelated branch as a substitute for rebaselining.
- **Golden mutability:** use the demo HEAD plus working-tree status captured after T0 as the source
  identity. If a golden `.github` file changes during the run, stop rather than mixing snapshots.
- **Permission broadening:** exact golden tool membership is authorized, but any additional tool is
  not. The renderer and validator must fail closed on an unmapped or profile-incompatible tool.
- **Knowledge loss:** delete only `conductor/SKILL.md` and `conductor-v3/SKILL.md`. The other 64
  canonical-only resources are deliberately retained and packaged until the governed migration
  todo proves their content has a durable replacement.
- **Golden dangling references:** exact golden `SKILL.md` bytes remain the Copilot parity contract,
  but the golden repository omits 61 resources those files reference. Do not copy that defect into
  canonical source or packages; require reference-resolution tests while keeping Copilot output at
  the exact 48-path boundary.
- **External path coupling:** no production code, canonical content, or ordinary test may contain
  `/Users/dave`. The local parity test receives the path only through its environment.
- **Self-deployment drift:** root `.github/agents`, root `.github/skills`,
  `.kyber-weave/squad.lock.yml`, and `.kyber-weave/squad.receipt.json` are an intentional stale
  self-deployment, not canonical input. Any diff in those paths is blocking for this plan; do not
  refresh or reconcile it. The human performs that operation after a fresh RC.

## 7. Closeout

After T7 completes local verification and the authorized branch/PR delivery, report the baseline
target HEAD, exact changed/deleted path summary, golden parity result, retained-resource and
package-reference results, local gate results, commit and pull-request identifiers, GitHub CI
results, the `@coderabbitai` review request, and any intentionally untracked verification output.
Confirm the intentional root self-deployment and its `.kyber-weave` state remained untouched and
that
[migrate-skill-resources-into-standards](../todo/migrate-skill-resources-into-standards.md) remains
indexed as authorized deferred work. Its completion does not block this synchronization plan, but
the resources remain packaged until that todo's migration and routing acceptance criteria pass.
Do not archive this plan until the implementation is complete and canonical documentation has
absorbed the final behavior.
