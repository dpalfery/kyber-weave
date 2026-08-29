---
id: plans/2026-08-29-kyber-squad-hotshot-golden-sync
title: Kyber-Squad Hotshot Golden Copy Synchronization
doc-type: plan
status: current
lifecycle: draft
approval: pending
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-29
---

# Kyber-Squad Hotshot golden copy synchronization

**Status:** Draft — post-approval T0 reconciliation required
**Approval:** Pending renewed user approval after the 2026-08-29 upstream conflict
**Execution model:** Test-first; establish the golden-output contract before changing canonical source or rendering
**Goal:** Make Kyber-Squad's GitHub Copilot deployment reproduce the current
`hotshot-logistics-demo/.github/agents` and `.github/skills` trees, except for one approved
transformation: every emitted agent tool list uses one deterministic cross-agent order.

## 1. Scope and fixed decisions

The user fixed these boundaries before planning:

1. `/Users/dave/git/kyber-weave2` is the only repository that may change.
2. `/Users/dave/git/personal/hotshot-logistics-demo` is a read-only golden source. No command in
   this plan may modify it, including checkout, pull, reset, format, generation, or test output.
3. Before implementation, Kyber-Weave must be fast-forwarded to the latest `origin/main`. The
   pull is performed only in Kyber-Weave and stops on divergence or a local-overwrite conflict.
4. The golden agent and skill sets are recursive exact mirrors, including additions and
   deletions. The only allowed content difference is normalized agent tool ordering.
5. Canonical source under `products/kyber-squad/` is updated first. Normal packing and rendering
   consume that source; generated harness trees remain untracked, consistent with current
   repository policy.
6. Delivery ends with fully verified local working-tree changes. Do not commit, push, tag,
   release, publish, or open a pull request.

The golden agent frontmatter contains `name`, `description`, optional `model`, `tools`, optional
`agents`, optional `user-invocable`, and `metadata`. It contains no top-level `title` or
`permissions` key. Therefore this plan treats `name` as the deployed title/identity and treats
the exact tool allow-list plus `metadata.capability-profile` and the instruction body as the
permission contract. It does not invent absent golden fields.

## 2. Grounded discovery

### Repository state before the required network refresh

| Repository | Local state |
|---|---|
| Kyber-Weave | Exists at `/Users/dave/git/kyber-weave2`; clean `main`; HEAD `769b18d3f592efeca8e550fb20bb40e72a4b78e8`; upstream `origin/main`; `0` ahead / `0` behind the local tracking ref. The local tracking ref was last fetched on 2026-08-22, so this does not establish live-origin freshness. |
| Legacy path | `/Users/dave/git/kyber-weave` does not exist. |
| Golden demo | Exists at `/Users/dave/git/personal/hotshot-logistics-demo`; branch `epam-demo/4-kyber`; HEAD `677c3a876ba9c62f1083608596b238c9deaff167`; no configured upstream and no local `origin/epam-demo/4-kyber` ref. The worktree is intentionally dirty. The golden `.github` surfaces are tracked and unchanged except for the untracked `.github/skills/code-review/scripts/check_verdict.py`, which is part of the approved current-working-tree golden source. |

No fetch, pull, checkout, generation, or source edit was performed during the initial discovery.

### Post-approval T0 conflict

After the plan was approved, the target-only synchronization check fetched live origin state and
found a material overlap that invalidated the approved T0 procedure:

- pre-refresh Kyber-Weave HEAD: `769b18d3f592efeca8e550fb20bb40e72a4b78e8`;
- live `origin/main`: `d601dd05273868afce405ef42531fadb859d762f`;
- a direct fast-forward-only pull was attempted and aborted before changing HEAD or working-tree
  content because upstream overlaps 11 tracked dirty paths and would overwrite three untracked
  archive paths;
- the golden-sync plan is a fourth untracked file that must be preserved outside the worktree while
  establishing the clean precondition for the pull.

The 11 tracked dirty paths are fixed and exhaustive:

1. `docs/plans/2026-08-16-coding-standards-and-config-reg.md`
2. `docs/plans/2026-08-20-code-review-council.md`
3. `docs/plans/2026-08-21-complete-recommended-inspectcode-improvements.md`
4. `docs/plans/2026-08-21-duplication-and-prior-art-lenses.md`
5. `docs/plans/2026-08-21-inspectcode-warning-fixes-and-suggestion-triage.md`
6. `docs/plans/2026-08-22-resolve-inspectcode-warnings-and-actionable-suggestions.md`
7. `docs/plans/README.md`
8. `docs/standards/csharp/README.md`
9. `docs/todo/embeddings-endpoint-loopback-check.md`
10. `docs/todo/kyber-weave-docs-skill-vocabulary.md`
11. `docs/todo/portable-artifacts-carry-project-standards.md`

The four untracked files to move into the backup, and no others, are:

1. `docs/plans/2026-08-29-kyber-squad-hotshot-golden-sync.md`
2. `docs/archive/plans/2026-08-16-coding-standards-and-config-reg.md`
3. `docs/archive/plans/2026-08-21-inspectcode-warning-fixes-and-suggestion-triage.md`
4. `docs/archive/plans/2026-08-22-resolve-inspectcode-warnings-and-actionable-suggestions.md`

#### Recoverable path-scoped reconciliation

T0 must use this exact reconciliation instead of a broad stash, reset, checkout, clean, or
worktree-wide restore:

1. Reconfirm that HEAD is the recorded pre-refresh commit and that `git status --short` names only
   the 15 paths above. If either fact differs, stop and return the new evidence for another review.
2. Create a task-specific backup directory with `mktemp -d` outside the repository. Record its
   absolute path in the run log; do not use a repository directory or an unresolved environment
   variable.
3. Populate the backup with all of the following:
   - a binary Git diff from the old HEAD for exactly the 11 tracked paths;
   - exact copies of every present dirty tracked file and all four untracked files, preserving their
     repository-relative paths;
   - an old-HEAD base copy for each tracked path plus an explicit deletion manifest for the three
     locally absent active-plan paths;
   - the old HEAD, fetched `origin/main`, `git status --short`, and path inventories as text evidence;
   - a SHA-256 manifest for every copied file and the binary diff.
4. Verify the backup before touching the worktree: recompute every SHA-256, compare each exact copy
   with its source, confirm the deletion manifest names only the three absent tracked plans, and
   confirm the binary diff enumerates only the 11 allowed tracked paths. A failed verification is
   terminal; do not continue with a partial backup.
5. Restore only the 11 tracked paths above to
   `769b18d3f592efeca8e550fb20bb40e72a4b78e8`, and move only the four listed untracked files into
   their path-preserving backup locations. Do not invoke broad `git reset`, `git restore .`, stash,
   clean, or recursive deletion.
6. Require `git status --porcelain` to be empty and HEAD to remain the old commit, then run the
   target-only `git pull --ff-only origin main`. Require the resulting HEAD and local
   `origin/main` to equal `d601dd05273868afce405ef42531fadb859d762f`; if origin moved again, stop
   and re-review rather than mixing upstream snapshots.
7. Reapply exact bytes from the verified backup for these eight local-reviewed artifacts:
   - `docs/plans/2026-08-20-code-review-council.md`
   - `docs/plans/2026-08-21-complete-recommended-inspectcode-improvements.md`
   - `docs/plans/2026-08-21-duplication-and-prior-art-lenses.md`
   - `docs/standards/csharp/README.md`
   - `docs/archive/plans/2026-08-16-coding-standards-and-config-reg.md`
   - `docs/archive/plans/2026-08-21-inspectcode-warning-fixes-and-suggestion-triage.md`
   - `docs/archive/plans/2026-08-22-resolve-inspectcode-warnings-and-actionable-suggestions.md`
   - `docs/plans/2026-08-29-kyber-squad-hotshot-golden-sync.md`
8. Remove only these three upstream archive copies, because their locally reviewed plans remain
   active at their original paths:
   - `docs/archive/plans/2026-08-20-code-review-council.md`
   - `docs/archive/plans/2026-08-21-complete-recommended-inspectcode-improvements.md`
   - `docs/archive/plans/2026-08-21-duplication-and-prior-art-lenses.md`
9. Manually three-way merge these four files from old-HEAD base, backed-up local version, and new
   upstream version; never apply either side wholesale:
   - `docs/plans/README.md`
   - `docs/todo/embeddings-endpoint-loopback-check.md`
   - `docs/todo/kyber-weave-docs-skill-vocabulary.md`
   - `docs/todo/portable-artifacts-carry-project-standards.md`

   The merged result must preserve upstream's newer index/todo structure and 23-agent baseline,
   while also preserving the locally reviewed lifecycle state and canonical documentation links.
   The plans index must keep this golden-sync plan's row in `Draft` / pending-approval state until
   the user approves the revised plan.
10. Recompute hashes and require every exact-reapplied artifact to match its backup digest. Review
    the four manual merges line by line, require `git status --short` to contain only the explicit
    reconciliation paths, run `git diff --check`, `docs validate`, and `docs drift`, and retain the
    backup until the user accepts the reconciled result. The backup is the recovery source if any
    check fails; do not delete it as part of T0.

Every command in this reconciliation runs in `/Users/dave/git/kyber-weave2` or the task-specific
backup directory. The Hotshot Logistics demo remains untouched and is not part of the pull,
backup, restore, merge, or cleanup scope.

### Golden inventory

The golden tree has 24 agents:

`architect`, `architect-v3`, `azure-reader`, `bug-crusher-investigator`, `code-reviewer`,
`conductor`, `conductor-v3`, `csharp-dev`, `dal-dev`, `docs-dev`, `github-devops`, `maui-dev`,
`product-owner`, `pulumi-dev`, `python-dev`, `react-dev`, `research-agent`, `review-lens`,
`review-triage`, `sql-database-architect`, `task-reviewer`, `task-reviewer-v3`, `tauri-dev`, and
`test-dev`.

The golden tree has 24 skill identities and 25 files: one `SKILL.md` for each identity plus
`code-review/scripts/check_verdict.py`:

`app-docs-standard`, `architecture-decision-record`, `azure-cli`, `azure-naming`, `bug-crusher`,
`code-review`, `create-pull-request`, `create-pull-request-github`, `csharp-dev`, `csp-security`,
`dal-dev`, `dp-code-reviewer`, `github-cli`, `github-devops`, `lm-studio-cli`, `maui-dev`,
`pr-review-fix-comments`, `product-owner`, `python-dev`, `resharper-clt`, `second-brain`,
`security-review`, `setup-dev-environment`, and `test-dev`.

### Delta from current canonical source

- Canonical source currently has 22 agents. `task-reviewer` and `task-reviewer-v3` are golden-only.
- All 22 common agent instruction bodies differ from the golden bodies after removing YAML
  frontmatter. The golden descriptions and delegation rosters also contain material changes.
- Canonical source currently has 26 skills. `conductor` and `conductor-v3` are absent from the
  golden skill set and must be removed from the canonical skill inventory.
- All 24 common `SKILL.md` bodies differ from the golden bodies.
- The golden skill tree adds `code-review/scripts/check_verdict.py` and omits 61 files currently
  present under canonical skills, including reference, provider, script, and agent-resource files.
  Exact recursive mirroring therefore authorizes those 61 deletions.
- The current target repository has no tracked `.github/agents` or `.github/skills` tree. Its
  tracked authority is `products/kyber-squad/`; generated deployment trees are runtime output.

### Current source and rendering seams

- `SquadPackSourceLocator` resolves only `products/kyber-squad` as canonical source.
- `SquadPacker` already archives `skills/` recursively, so release packaging can carry resources.
- `SquadSourceLoader` currently loads only `skills/<name>/SKILL.md` into AgentIR.
- `CopilotRenderer` currently emits only `.github/skills/<name>/SKILL.md`; it cannot deploy the
  golden `check_verdict.py` resource.
- `CopilotRenderer` computes tools from a lossy capability-to-tool map and one fixed order. That
  model cannot preserve the golden architect granular edit tools or the per-agent tool sets.
- `SquadCommandComposition` registers only `CopilotRenderer`. The other nine declared targets fail
  coverage preflight and are outside this synchronization plan.
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

Add `task-reviewer` and `task-reviewer-v3` as subagents with the golden descriptions, models,
tools, metadata, and instruction bodies. Update both conductor delegation rosters exactly:

- `conductor` delegates per-task completion audits to `task-reviewer`.
- `conductor-v3` delegates Red/Green completion audits to `task-reviewer-v3`.

Update `bundles/full.yml`, canonical counts, schema fixtures, migration records, and roster tests
to 24 agents.

### 3.3 Recursive skill fidelity

Replace `products/kyber-squad/skills/` with the golden recursive tree:

- copy all 24 golden `SKILL.md` files byte-for-byte;
- copy `code-review/scripts/check_verdict.py` byte-for-byte, preserving its executable intent;
- remove `conductor` and `conductor-v3` skill directories;
- remove every reference, provider, script, or agent resource absent from the golden tree.

Extend Squad skill loading to retain every regular file beneath each declared skill directory,
including relative path and exact bytes. Reject symlinks, traversal, duplicate portable paths,
case aliases, and files outside the declared skill root through the existing Squad path policy.
`CopilotRenderer` copies `SKILL.md` and all resources to `.github/skills/<name>/...` without
re-serializing or rewriting bytes. `SquadPacker` remains the recursive package path and gains
regression coverage rather than a second implementation.

Removing the two conductor skills makes the agent/skill namespace intersection seven
distinct-body collisions rather than nine identities with two shared bodies. Update fallback
profiles and render validation so a fallback-only target would lower each conductor agent to its
unoccupied same-name role skill. Do not retain hidden conductor skill source merely to satisfy the
old invariant; exact recursive mirroring governs the canonical set.

### 3.4 Exact update and deletion behavior

Rendered Copilot output consists of exactly:

- 24 `.github/agents/<name>.agent.md` files;
- 24 `.github/skills/<name>/SKILL.md` files;
- `.github/skills/code-review/scripts/check_verdict.py`.

Add lifecycle regression coverage proving an update deletes receipt-owned files that disappeared
from a newer render while preserving unmanaged and locally edited files under existing conflict
rules. Roster removal must flow through the normal deployment plan and transaction engine; do not
add ad hoc recursive deletion.

### 3.5 Golden parity verification without coupling CI to a personal path

Add an opt-in local integration test that accepts the golden repository root through an explicit
environment variable. It renders Copilot output in memory and compares it to the external golden
tree:

- skill paths and bytes must be identical recursively;
- agent frontmatter keys and values must be identical;
- instruction bodies must be byte-identical after existing LF normalization;
- tool membership must be identical, and rendered order must equal the single approved order;
- no extra or missing path is allowed.

The ordinary test suite must not require `/Users/dave/...` or skip its core assertions. Keep
in-repository roster, schema, renderer, resource, package, and lifecycle tests that prove the same
contracts from canonical source. The external-path test is the final local provenance check that
the imported source still matches the designated golden working tree.

### 3.6 Documentation and tracked-output boundary

Update canonical documentation that currently states 22 agents, 25/26 skills, nine namespace
intersections, or shared conductor skill identities. At minimum this includes:

- `products/kyber-squad/README.md`;
- `docs/README.md`;
- `docs/kyber-squad/README.md`, `architecture.md`, `onboarding.md`, and `requirements.md`;
- `docs/context-hygiene/agents.md` and `skills.md`;
- any current distribution or verification document found by the post-pull count sweep.

Update all affected migration records so they name the Hotshot golden source and record normalized
body digests; add records for both task reviewers. Generated `.github` trees are not committed.
The tracked outputs of this work are canonical source, schemas/profiles, implementation, tests,
migration evidence, and governed documentation.

## 4. Test-first task graph

| Task | Owner | Depends on | RED contract | GREEN work and acceptance |
|---|---|---|---|---|
| T0 — Refresh and rebaseline | github-devops | Renewed user approval of the post-conflict plan | None; repository synchronization precedes test changes. | Execute only the recoverable path-scoped reconciliation in §2, preserve and verify the backup, fast-forward to the pinned live origin commit, reapply/merge only the named paths, pass status/hash/diff/docs checks, rerun CodeGraph and the roster/resource inventory, and amend this plan again if upstream materially changed the implementation seams. The demo repository remains untouched. |
| T1 — Pin the golden contracts | test-dev | T0 | Add failing tests for the 24-agent roster, 24-skill roster, 25 recursive skill files, both task reviewers, conductor delegation edges, exact model values, exact tool membership, normalized order, exact bodies/descriptions, raw skill bytes/resources, and removal of conductor skills. Add the opt-in external-golden parity test. | The new tests fail for the current 22-agent/26-skill source for the expected reasons, with no production edits mixed into RED. |
| T2 — Extend AgentIR for lossless Copilot tools | csharp-dev | T1 | Use the T1 schema/model/renderer failures. | Extend agent schema, model, loader, validation, model profiles, capability checks, and deterministic tool ordering. Preserve all existing non-broadening checks and diagnostics with actionable hints. The focused source/renderer tests pass. |
| T3 — Import the golden agents and graph | docs-dev for artifact content; csharp-dev for schema fallout | T2 | T1 roster/body/delegation tests remain red until import. | Mirror all 24 golden agents into canonical source, add task reviewer migration records, refresh the 22 existing records, update the bundle and profiles, and preserve all fields exactly except tool order. Focused agent tests and the external parity agent comparison pass. |
| T4 — Make skills recursively lossless | csharp-dev + docs-dev for artifact content | T1 | Add/retain failing tests showing resources are not loaded/rendered and the canonical skill tree has 61 extra files plus two extra identities. | Mirror the 24 golden skill directories exactly, extend skill AgentIR/loading/rendering for raw recursive resources, update fallback/shared-identity contracts, and prove pack output includes the script. Focused skill, source, renderer, and pack tests pass. |
| T5 — Prove exact deployment deletion | test-dev + csharp-dev | T3, T4 | Add a lifecycle test whose previous receipt owns a file absent from the next render, alongside unmanaged and locally edited neighbors. | Normal update planning removes only obsolete receipt-owned paths, preserves conflicts under current policy, and leaves a receipt matching exactly the 49 rendered files. Full Squad lifecycle tests pass cleanly. |
| T6 — Reconcile governed documentation | docs-dev | T3, T4, T5 | `docs validate`/`docs drift` and count sweeps expose stale roster and lowering claims. | Update the canonical documents and product README listed in §3.6, with 24-agent/24-skill counts, seven collision identities, conductor unoccupied fallback behavior, raw resource deployment, Copilot-only renderer coverage, and no claim that generated `.github` output is tracked. Both documentation gates return zero findings. |
| T7 — Full verification and review | code-reviewer | T2–T6 | Any failing required gate or golden parity mismatch is blocking. | Run the complete verification matrix in §5, inspect the scoped diff and deletion set, confirm the demo repository status/HEAD are unchanged, and obtain an APPROVE verdict with zero open findings. Leave changes local and uncommitted. |

T3 and T4 may proceed in parallel after T2 only if their file scopes remain disjoint. T5 waits for
both because it verifies the combined rendered tree. T6 follows implementation so documentation
describes the verified behavior rather than an intended design.

## 5. Verification matrix

After the required target-only pull and implementation, run:

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
repository, archive-content inspection, `git diff --check`, and a final source/resource count
sweep. Before and after the parity test, record the demo repository HEAD and `git status --short`
and require them to be identical.

Expected final invariants:

1. Canonical bundle: 24 agents, 24 skills.
2. Copilot render: 49 files total — 24 agents, 24 `SKILL.md` files, one skill script.
3. Golden differences: tool ordering only; no other agent field/body difference and no skill
   path/byte difference.
4. No tracked generated `.github/agents` or `.github/skills` tree in Kyber-Weave.
5. No mutation of the demo repository.
6. No commit, push, tag, release, publication, or pull request.

## 6. Risks and stop conditions

- **Upstream drift:** if `origin/main` changes any canonical Squad schema, renderer, source, or
  plan/index path in a way that invalidates this design, stop and revise the plan before coding.
- **Golden mutability:** use the demo HEAD plus working-tree status captured after T0 as the source
  identity. If a golden `.github` file changes during the run, stop rather than mixing snapshots.
- **Permission broadening:** exact golden tool membership is authorized, but any additional tool is
  not. The renderer and validator must fail closed on an unmapped or profile-incompatible tool.
- **Destructive mirror mistakes:** resolve and report the exact 61-file deletion set before applying
  it. Do not use a broad recursive delete; apply explicit file deletions through the patch and
  verify no path outside `products/kyber-squad/skills/` is removed.
- **Broken skill references:** exact golden content wins. If a golden `SKILL.md` references a file
  absent from the golden tree, report the golden defect rather than retaining a target-only file.
- **External path coupling:** no production code, canonical content, or ordinary test may contain
  `/Users/dave`. The local parity test receives the path only through its environment.

## 7. Closeout

After T7 approves the local diff, report the refreshed target HEAD, exact changed/deleted path
summary, golden parity result, gate results, and any intentionally untracked verification output.
Do not archive this plan until the implementation is complete and canonical documentation has
absorbed the final behavior.
