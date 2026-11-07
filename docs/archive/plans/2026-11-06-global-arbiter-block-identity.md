---
id: plans/global-arbiter-block-identity
title: Global Arbiter installs accept more than one block-bearing target
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
created: 2026-11-06
last-reviewed: 2026-11-06
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad, KyberArbiter]
---

# Global Arbiter installs accept more than one block-bearing target

**Status: Archived — complete 2026-11-07.**
Development mode `test-first`; autonomy `autonomous`; scale `extends-component` over
`KyberSquad` and `KyberArbiter`.

**Bottom line.** Since the release candidate 0.1.8-rc.20, `kyber-weave arbiter install --target <t>`
works for the first target and fails for the second when both targets keep their hooks in a shared
JSON file. A stored receipt is validated as if two targets could never name the same file, but under
global scope they legitimately do: each target's hook file sits under that target's own root, so the
portable path is bare (`hooks.json`) and only the target tells four of them apart. The fix is to
identify a stored block by target *and* path under global scope, exactly as stored files are already
identified, and to keep reading the receipts rc.20 already wrote. It lands on a branch based on
`origin/main`, not on the branch checked out here.

## Problem and goal

**Problem.** A user who installs the Arbiter for Cursor and then for Codex, Factory or Antigravity
gets exit 1 and this message, naming the target that was installed first:

```text
kyber-weave arbiter: error: Squad receipt block path 'hooks.json' for target 'cursor' is not a unique portable deployment path.
```

The install is rolled back, so nothing is corrupted — the second target simply cannot be installed.
The same user can install `devin` or `antigravity` after `cursor` only when their file names differ
(`hooks.v1.json` versus `hooks.json`), which is luck, not design. The defect exists only in 0.1.8-rc.20
and later; 0.1.8-rc.19 and earlier never wrote a block at global scope.

**Goal.**

1. `kyber-weave arbiter install --target` succeeds for any combination of block-bearing targets under
   global scope, in any order, and a second run stays an idempotent no-op.
2. A receipt rc.20 wrote is readable by the fixed build, so upgrading an existing install never makes
   its own state unreadable.
3. Genuine duplicate claims are still refused: the same target claiming one path twice, and (under
   project scope) two targets claiming one path, both keep failing.
4. Blocks, status, doctor, update and uninstall agree on where a global block lives.

## What is broken, exactly

Four targets keep hooks in a file the user also owns, and since release rc.11 (#91) every renderer
writes that file's path *bare* under global scope — relative to that target's own root, because global
deployment resolves each target to its own directory. So:

| Target | Project path | Global path |
|---|---|---|
| `cursor` | `.cursor/hooks.json` | `hooks.json` |
| `codex` | `.codex/hooks.json` | `hooks.json` |
| `factory` | `.factory/hooks.json` | `hooks.json` |
| `antigravity` | `.agents/hooks.json` | `hooks.json` |
| `devin` | `.devin/hooks.v1.json` | `hooks.v1.json` |

Stored **files** are already identified by target *and* path under global scope. Stored **blocks** are
not: the receipt validator keys block uniqueness on the portable path alone, and its doc comment still
states the superseded assumption that blocks are project-only. The deployment planner, by contrast,
already qualifies a block's identity by target. Only the receipt validator disagrees, and it is the
last check a global install runs.

## Investigation findings

Discovery: codegraph 0 (the CodeGraph MCP tool is not exposed to this run — gap G1), docs 3, direct
reads 19.

**Gaps and how they are covered.**

- **G1 — CodeGraph unavailable.** `codegraph_explore` is not in this harness's tool set, so file and
  symbol locations below were established by direct reads of the checked-out tree.
- **G2 — `origin/main` unreadable.** No shell and no repository tool in this run, so `git show
  origin/main:<path>` could not be run and the fix branch's own source was not read. Every task below
  therefore names a **symbol**, not a line, and T1 starts with a premise check that stops the run if
  the defect is not where this plan says it is. Line numbers quoted here are the checked-out branch
  (`refactor/mcp-server-tool-rename`, `be686cc7`) and are for orientation only.
- **G3 — no subagent dispatch.** No research or live-state delegate was available; nothing external
  was needed, so nothing was delegated.

**Evidence the plan rests on.**

| # | Claim | Evidence (checked-out branch) |
|---|---|---|
| E1 | Stored files are identified by target and path under global scope, by path alone under project and instance scope | `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs:1297-1303` |
| E2 | Stored blocks are identified by path alone, with a doc comment asserting blocks are project-only | `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs:1352-1357` (comment), `1394-1399` (the check) |
| E3 | The deployment planner already qualifies block identity by target, and cross-checks blocks against files with the same identity | `src/KyberWeave.Core.Squad/Deployment/SquadDeploymentPlan.cs:1042-1057`, `1121-1141`; `DeployedFileIdentity` at `1539-1542` |
| E4 | Bare paths under global scope are the intended design since #91, and are the fingerprint used to classify a legacy single-root receipt — restoring the prefixed path would double-namespace the physical file and break that classification | `src/KyberWeave.Core.Squad/Deployment/SquadDeploymentPlan.cs:265-296` |
| E5 | At the checked-out branch every block path is a prefixed per-format constant and there is exactly one `new SquadRenderedBlock(...)` site | `src/KyberWeave.Core.Squad/Deployment/SquadHookJsonBlock.cs:61-70`, `src/KyberWeave.Core.Squad/Rendering/SquadHarnessRenderer.cs:516-532` |
| E6 | The receipt schema's exact field sets: v1 project or legacy, v2 global only with required `layout` and optional `homeFiles`, v3 project only with required `blocks` | `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs:1530-1656` |
| E7 | The writer derives the schema from scope and blocks: global writes v2, project-with-blocks writes v3 | `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs:443-518` |
| E8 | At the checked-out branch the planner refuses blocks outside project scope in three places, the receipt schema choice and the retained-schema rule encode it, and uninstall refuses a global receipt that owns blocks | `src/KyberWeave.Core.Squad/Deployment/SquadDeploymentPlan.cs:687`, `741-743`, `974-987`, `1004-1015`, `1040`, `1118-1119` |
| E9 | Block-path consumers: status rebuilds a synthetic owned file from the block to resolve its path; uninstall recomputes the path from the per-format constant instead of reading the receipt | `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs:212-226`; `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstaller.cs:148-160` |
| E10 | ADR 0054 decision 4 and its consequence state the current on-disk contract, including "an older reader refuses v3 with exit code 1" | `docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md:59-64`, `:107-110` |
| E11 | The architecture page states the receipt v3 contract and "a v3 receipt is written only at project scope" | `docs/kyber-squad/architecture.md:507-515`, `:517-547` |
| E12 | Existing tests that pin the contract this change touches: install claims the block in a v3 receipt, the v3 field set, an older reader refuses v3, block status, block review fixes, receipt shape rejections | `tests/KyberWeave.Tests/SquadOwnedBlockLifecycleTests.cs:57`, `:229`, `:257`; `SquadOwnedBlockStatusTests.cs`; `SquadHookBlockReviewFixTests.cs`; `SquadDeploymentStateTests.cs:420-539` |
| E13 | The receipt store's own test class, for home-anchored global state | `tests/KyberWeave.Tests/SquadHomeStateStoreTests.cs:25-105` |
| E14 | Home and root injection seams the new tests need | `tests/KyberWeave.Tests/Fakes/FakeSquadUserPaths.cs`; `ArbiterInstallCommandTests.cs:35-58` (`ArbiterCommandComposition.HomeDirectory`) |
| E15 | The defect's observed behaviour on the shipped binary: cursor alone exits 0, codex and factory exit 1 naming cursor, devin and antigravity exit 0, a clean home makes each succeed alone | packet reproduction, rc.20, 2026-11-06 (`[NOT LIVE-VERIFIED]` in this run — gap G2) |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which branch does the fix land on, and how does it relate to `refactor/mcp-server-tool-rename`? | A new worktree off `origin/main` (`decc903f`, released as 0.1.8-rc.20), branch `fix/arbiter-global-block-identity`. The checked-out rename branch is not edited. This plan file and its index row are authored here, then committed as the fix branch's first commit and removed from the rename checkout, so the rename pull request carries neither. | The defect exists only from `decc903f`. The rename branch predates it, has no global blocks, and every block path there is a prefixed per-format constant (E5); a fix there would be untestable. After this merges, the rename branch inherits it and conflicts only if that branch edits `SquadStateStore`. | ANSWERED: autonomous, source packet version facts and E4, E5 |
| D2 | Where is the defect fixed? | In the receipt validator: a stored block's identity becomes scope-aware, sharing one helper with the file validator, so the two can never disagree again. | The planner already qualifies block identity (E3), so the validator is the only disagreeing layer. Rejected: special-casing `hooks.json`. Rejected: making the identity target-qualified at every scope, which would drop the project-scope guarantee that two targets cannot claim one physical file. | ANSWERED: autonomous, source E1, E2, E3 |
| D3 | Do the renderers change, or are the bare paths restored? | No. Bare global paths stay exactly as they are. | Restoring the prefix would resolve to `<target root>/.cursor/hooks.json` — a double namespace — and would break the legacy single-root classification that reads bare paths as the post-#91 fingerprint (E4). | ANSWERED: autonomous, source E4 |
| D4 | What on-disk schema carries a global receipt that owns blocks? | Keep `kyber-squad.receipt/v3` and widen it exactly as rc.20 already writes it: `scope: global` allowed, `layout` and `homeFiles` permitted beside `blocks`. No v4. | rc.20 is in the field and has already written this shape; a new schema id would strand every such install and the ids are permanent (ADR 0054). The added fields are already v2's, so the shape is a union of two shipped shapes rather than a new one. Cost of being wrong: the persisted format — caught at review, and the reader is versioned, so a later correction is additive. | autonomous: low-confidence, no authoritative source |
| D5 | How does the fixed build treat receipts rc.20 already wrote? | The reader accepts a global v3 receipt with `layout`, `homeFiles` and `blocks`, and a regression test pins it against a fixture built from two real rc.20 receipts. No migration verb, no rewrite: a failed second install leaves nothing to migrate. | rc.20's own reader accepts the shape, so the failing step there is the uniqueness check alone (E2, E15). The fixture is assembled from two genuine one-block rc.20 receipts because a genuine two-block one cannot exist — that receipt is what the bug prevents. | ANSWERED: autonomous, source E6, E7, E15 |
| D6 | Is any duplicate still refused? | Yes, three: one target claiming a path twice at any scope; two targets claiming one path at project or instance scope; two entries in one block claiming the same container. All stay as assertions, not as weakened theories. | These are the properties that make a receipt an ownership record rather than a list. Dropping them to reach green would be a scope change requiring reapproval. | ANSWERED: autonomous, source E1, E2 |
| D7 | Do status, doctor, update and uninstall need work? | T3 writes the global lifecycle tests first and T4 fixes only what those tests show broken. The one known suspect is `ArbiterUninstaller.RemoveOwnedBlocks`, which recomputes the path from the per-format constant instead of reading the receipt; against a global receipt that recomputed path cannot match. | Scope-bounded on purpose: the reported defect is install, and a fix that leaves uninstall looking at the wrong path would merely move the failure. Anything found beyond the four consumers named in T3 is reported as a todo, not fixed here. | ANSWERED: autonomous, source E8, E9 |
| D8 | Where does a block's path come from when a verb needs it? | From the receipt, which records where the entries were written. A format constant is only a fallback for a receipt that records no block for that target. | The receipt is the ownership record (ADR 0054 decision 4); recomputing from a constant is a second source of truth that already disagrees at global scope (E9). | ANSWERED: autonomous, source E9, E10 |
| D9 | New ADR, or amend ADR 0054? | Amend ADR 0054 in place: decision 4 gains the global shape and the identity rule, and the consequence keeps its honest downgrade warning. No new ADR. | `docs/adr/README.md` records that an ADR states the decision in force and is edited in place, because git holds the history. This refines a boundary ADR 0054 already draws; it adds no boundary. | ANSWERED: autonomous, source `docs/adr/README.md`, E10 |
| D10 | Does the fix change the global-by-default install behaviour from #527? | No. Only the defect: global scope stays the default, project scope stays opt-in, and the renderers, CLI flags and exit codes keep their shipped meanings. | Named explicitly because it is what a reader will suspect. Changing the default back would hide the defect rather than fix it. | ANSWERED: autonomous, source packet scope |
| D11 | What proves this to a user before a release? | A live check in the plan's verification gates: one isolated home, three targets in order, plus `arbiter uninstall --target` and `squad status --global`. Marked `[NOT LIVE-VERIFIED]` until the conductor runs it on the fixed binary. | The shipped defect was reproduced exactly this way against the rc.20 binary, so the same shape of check is the honest counterpart. | ANSWERED: autonomous, source E15 |
| D12 | Development mode and autonomy. | `test-first`, `autonomous`. | Packet values; autonomy also matches the Config Reg **<autonomy>**. | ANSWERED: packet 2026-11-06 |
| D13 | Release and merge handling. | A draft pull request from the fix branch, titled `fix(arbiter): qualify global block identity by target`, carrying the reproduction and the upgrade note. Nothing is merged, tagged or published by this run. | The repository's release notes are built from pull request titles, and a stable user has to know that upgrading from rc.20 is safe. | ANSWERED: autonomous, source `AGENTS.md` before-opening-a-PR section |
| AP1 | Approve and execute. | Recommended for execution at the declared scale: `extends-component`, components `KyberSquad`, `KyberArbiter`. | Decision-complete, mode-complete and scale-declared, with `docs_validate` and `docs_drift` passing. The conductor returned `AUTONOMOUS_APPROVAL: approve-and-execute`, so execution may start. Approval is never inferred from the packet's autonomy alone. | autonomous: recommendation accepted |

## Branch and delivery

- Worktree: `git worktree add "$COMMANDCODE_SCRATCHPAD/wt-arbiter-blocks" -b fix/arbiter-global-block-identity origin/main`.
- All source work happens there. The rename checkout keeps only its own tracked state plus the
  untouched `.commandcode/taste/taste.md`.
- T0 moves this plan and its index row into the fix branch as its first commit; from that point the
  plan lives on the branch that implements it.

## Fix design

**Identity rule (one helper, both loops).** Inside the receipt validator, derive every deployment
identity through one private function:

| Scope | File identity | Block identity |
|---|---|---|
| `project`, `instance` | portable path | portable path |
| `global` | target + path | target + path |

The file loop and the block loop both call it. The duplicate message keeps naming both the path and
the target, so a real duplicate is still diagnosable.

**Do not change** — each is deliberate, and a worker changing one turns this into a different plan:

- the bare global paths in the renderers, and `SquadHookJsonBlock.RelativePath`'s prefixed constants
  (they answer different questions and both are correct for their scope);
- the file validator's identity rule;
- the v1 and v2 shapes, or the byte-identical output of a receipt with no blocks;
- the block entry contract: a container pointer plus a digest, one entry per container;
- any existing assertion, except the two v3-shape assertions in T2 named below, and those are
  re-pinned to the wider contract, never relaxed to "no exception".

## Compatibility

Two readers exist in the field and both must read the same bytes:

| Reader | Global receipt with blocks | Consequence |
|---|---|---|
| 0.1.8-rc.20 and later | reads it | the shape this plan keeps |
| 0.1.8-rc.19 and earlier | refuses v3 with exit code 1 | unchanged, and already documented as the downgrade rule (E10) |

The fixed build must read rc.20's bytes, which T1(b) pins with a fixture assembled from two real rc.20
receipts. `tests/KyberWeave.Tests/SquadOwnedBlockLifecycleTests.cs:229` (`DeserializeV3ChecksTheExactFieldSet`)
and `:257` (`OlderReaderRefusesV3`) encode the narrower v3 of the rename branch; T2 re-pins the first to
the wider field set and keeps the second's older-reader claim true by testing it against a shape the
older reader genuinely lacks (`blocks` absent from v1/v2), not by narrowing what v3 accepts.

## Test contract

Mode `test-first`. Every RED is observed and quoted in the task digest before its GREEN starts, in
the fix worktree, with no `git stash`. Changing an assertion to reach green is a scope change.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | — no test task | `git status --short` in both trees | The plan and index row are committed on the fix branch; the rename checkout shows only `.commandcode/taste/taste.md` | n/a — mechanical verification replaces it | Both trees clean as stated; the fix branch's first commit contains exactly the two plan files |
| T1 | `tests/KyberWeave.Tests`: new `SquadGlobalBlockReceiptTests.cs`; `ArbiterInstallCommandTests.cs` | CMD-BLOCK, CMD-CLI | (a) two and three global blocks whose paths are all `hooks.json` install and land in one receipt, per target; (b) a receipt in rc.20's global v3 shape reads with both blocks intact and re-serializes to the same fields; (c) three sequential `arbiter install --target` calls in one isolated home all exit 0 | (a) throws `... block path 'hooks.json' for target 'cursor' is not a unique portable deployment path.`; (b) throws the same; (c) the second call exits 1 with the same message. Quote all three, and confirm the premise check below before writing them | CMD-BLOCK and CMD-CLI green with a non-zero total, assertions unchanged |
| T2 | `tests/KyberWeave.Tests`: `SquadOwnedBlockLifecycleTests.cs`, `SquadDeploymentStateTests.cs`, `SquadHomeStateStoreTests.cs` | CMD-BLOCK | T1 completely green; duplicate claims still refused (D6); receipts with no blocks still byte-identical | RED already carried by T1 | CMD-BLOCK green; the two re-pinned v3 assertions named in [Compatibility](#compatibility) state the wider contract; no assertion deleted |
| T3 | `tests/KyberWeave.Tests`: `SquadOwnedBlockStatusTests.cs`, `ArbiterUninstallCommandTests.cs`, `SquadOwnedBlockLifecycleTests.cs`, `SelfUpdaterArbiterRefreshTests.cs` | CMD-CLI, CMD-BLOCK | Under global scope with two block-bearing targets: `status` reports two blocks with the right target labels; `doctor` finds them healthy; `update` rewrites both without drift; `uninstall` and `arbiter uninstall --target` remove only that target's block; `update` from an rc.20 receipt keeps both blocks | Each failing case quoted; cases that already pass are recorded as passing, with their test names, so a later reader can tell "verified" from "not tried" | Every case in the row passes; T4 changed only what T3 showed broken, and the digest lists what did not need changing |
| T4 | Implementation, not tests | CMD-CLI, CMD-BLOCK | The consumers agree on where a global block lives | RED already carried by T3 | CMD-CLI and CMD-BLOCK green; no test outside T3's files changed |
| T5 | `.kyber-weave/kyber-weave.yml` gate suite | CMD-FINAL | Whole change reviewed against gates | Not a test task — read-only verification | Verdict recorded with the gate counts |
| T6 | Governed docs | CMD-DOCS | The documented contract matches the code | Not a separate RED — T1's failing tests are the evidence | `docs validate` and `docs drift` zero findings |
| T7 | Closeout | CMD-DOCS with `--merge-ready` | Plan archived, index row moved | Not a test task — read-only verification | Zero findings after the archive |

### Premise check (T1, before any test is written)

The plan was authored without reading the fix branch (gap G2). T1 confirms, on the fix branch, that:

1. a single-target global `arbiter install --target cursor` writes a global receipt carrying one block;
2. that block's `relativePath` is `hooks.json`;
3. the receipt validator still keys block uniqueness on the path alone, and the file validator beside
   it still qualifies by target.

If all three hold, continue. If (1) or (2) fails, the defect is not what this plan fixes: stop, keep
the RED evidence, and return to Draft with what was found. If only (3) fails, the defect may already
be fixed upstream: stop and report that instead of changing code.

### Pre-computed commands

Run from the fix worktree root. Build once per task, then re-run with `--no-build`; check the summary
reports a non-zero total, because a mistyped filter matches nothing and exits 0.

```bash
# CMD-BLOCK
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~SquadGlobalBlockReceiptTests|FullyQualifiedName~SquadOwnedBlockLifecycleTests|FullyQualifiedName~SquadOwnedBlockStatusTests|FullyQualifiedName~SquadHookBlockReviewFixTests|FullyQualifiedName~SquadHookJsonBlockTests|FullyQualifiedName~SquadDeploymentStateTests|FullyQualifiedName~SquadHomeStateStoreTests"

# CMD-CLI
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~ArbiterInstallCommandTests|FullyQualifiedName~ArbiterUninstallCommandTests|FullyQualifiedName~ArbiterCliCommandTests|FullyQualifiedName~SquadArbiterCliTests|FullyQualifiedName~SelfUpdaterArbiterRefreshTests"

# CMD-RENDER (guards the renderers the fix deliberately does not change)
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~ArbiterCursorRenderingTests|FullyQualifiedName~ArbiterCodexRenderingTests|FullyQualifiedName~ArbiterFactoryRenderingTests|FullyQualifiedName~ArbiterAntigravityRenderingTests|FullyQualifiedName~ArbiterDevinRenderingTests"

# CMD-DOCS
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .

# CMD-FINAL (--all: the change touches KyberWeave.Core.Squad persistence, a cross-cutting area)
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .
```

### rc.20 fixture recipe (T1(b))

A genuine two-block rc.20 receipt cannot exist, because the bug is what prevents one. Build it from
two genuine single-block receipts instead; both halves are rc.20's own bytes, so the shape is not
invented.

```bash
# Shipped binary, 0.1.8-rc.20; each run in its own isolated home.
H1=$(mktemp -d); env HOME="$H1" /Users/dave/.local/bin/kyber-weave arbiter install --target cursor
H2=$(mktemp -d); env HOME="$H2" /Users/dave/.local/bin/kyber-weave arbiter install --target codex
# Copy both global receipts, then merge the two `blocks` array elements into one document
# (`$H1/.kyber-weave/squad/global/squad.receipt.json`, `$H2/...`) and embed the result verbatim
# in the test, with this recipe and the rc.20 version string in a comment.
```

If that binary is unavailable, derive the fixture from the fix branch's own shape validators plus a
hand-built receipt, and say so in the test comment: the derivation is weaker evidence than the shipped
bytes and must be labelled.

## Tasks

Every developer task ends with the worker's own `task-gate` run, and a failing gate goes straight back
to that worker. No file appears in two tasks unless a dependency orders them.

### T0: Put the plan on the fix branch

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`. **Depends on:** none.
- **Entry files:** this plan file; `docs/plans/README.md`.
- **Acceptance:** create the worktree from `origin/main` (D1); copy both files there; commit them as
  the branch's first commit; then delete the plan file and `git restore docs/plans/README.md` in the
  rename checkout so that tree is clean apart from the untouched `.commandcode/taste/taste.md`. Report
  both `git status --short` outputs and the fix branch's `git log --oneline -1`.
- **Commands:** `git worktree add`, `git status --short` in each tree.

### T1: RED for global block identity and rc.20 receipts

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** T0.
- **Entry files:** new `tests/KyberWeave.Tests/SquadGlobalBlockReceiptTests.cs`;
  `tests/KyberWeave.Tests/ArbiterInstallCommandTests.cs`.
- **Acceptance:** run the [premise check](#premise-check-t1-before-any-test-is-written) and quote its
  three answers. Add (a) a plan-and-transaction test installing two, then three, global blocks that all
  name `hooks.json` with distinct targets, asserting all install and one receipt carries all of them;
  (b) the rc.20 fixture test from the [recipe](#rc20-fixture-recipe-t1b), asserting two blocks read
  back with their targets and digests and re-serialize identically; (c) a CLI test issuing three
  sequential `arbiter install --target` calls in one isolated home via
  `ArbiterCommandComposition.HomeDirectory`, asserting all three exit 0 and one receipt holds three
  blocks. Every test is written against the intended behaviour and fails for the intended reason;
  quote each failure verbatim. Do not modify any implementation file.
- **Commands:** CMD-BLOCK, CMD-CLI.

### T2: GREEN for the receipt validator

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T1.
- **Entry files:** `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs`; the two v3 assertions in
  `tests/KyberWeave.Tests/SquadOwnedBlockLifecycleTests.cs` named in
  [Compatibility](#compatibility) — or the assertions that replaced them if that release candidate
  moved or renamed them. Find them by name and by what they assert, not by line number: this plan was
  authored without reading the fix branch (gap G2).
- **Acceptance:** one scope-aware identity helper used by both the file loop and the block loop, per
  [Fix design](#fix-design); the block validator's doc comment states the current rule and no longer
  claims blocks are project-only; the global v3 shape accepts `layout` and `homeFiles` and allows
  `scope: global`; D6's three duplicate refusals still pass; the two re-pinned assertions state the
  wider contract. CMD-BLOCK and CMD-RENDER green. No `NoWarn` entry, no analyzer suppression.
- **Commands:** CMD-BLOCK, CMD-RENDER.

### T3: RED for the global block lifecycle

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** T2.
- **Entry files:** `SquadOwnedBlockStatusTests.cs`, `ArbiterUninstallCommandTests.cs`,
  `SquadOwnedBlockLifecycleTests.cs`, `SelfUpdaterArbiterRefreshTests.cs`.
- **Acceptance:** add global-scope cases for each row of T3's test-contract entry, using
  `FakeSquadUserPaths` and the global root resolver the existing tests use. Quote every failure
  verbatim and name every case that already passes. Do not modify implementation files.
- **Commands:** CMD-CLI, CMD-BLOCK.

### T4: GREEN for the block consumers

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T3.
- **Entry files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstaller.cs`; whichever of
  `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs`,
  `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`,
  `src/KyberWeave.Core.Squad/Deployment/SquadDeploymentPlan.cs`,
  `src/KyberWeave.Core.Squad/Deployment/SquadStateStore.cs`,
  `src/KyberWeave.Cli/Commands/Arbiter/ArbiterInstallCommand.cs` and the self-update hook refresh path
  T3's failures point at.
- **Acceptance:** only what T3 proved broken changes. A block's path is read from the receipt, with the
  format constant as fallback only (D8). No scope guard is removed merely because it is inconvenient:
  if a guard must widen, the digest says which scope it now admits and why. CMD-CLI, CMD-BLOCK and
  CMD-RENDER green; no test outside T3's four files changed. Anything outside the four consumers is
  reported as a proposed todo, not fixed.
- **Commands:** CMD-CLI, CMD-BLOCK, CMD-RENDER.

### T5: Whole-change review

- **Delegate:** `code-review`. **Skills:** `code-review`. **Depends on:** T2, T4, T6.
- **Acceptance:** one review over the whole diff with CMD-FINAL. Report the verdict, the gate counts
  and every finding. A finding that changes behaviour returns to T2 or T4 with a new test.
- **Commands:** CMD-FINAL.

### T6: Documentation harvest

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`, `app-docs-standard`. **Depends on:**
  T2, T4.
- **Entry files:** `docs/kyber-squad/architecture.md` (the receipt section at `:507-515` and
  "Owned blocks in shared hook files" at `:517-547`),
  `docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md`,
  `docs/kyber-arbiter/runbook.md` (Hooks and Setup, doctor, audit),
  `docs/cli-command-surface.md` (`arbiter install` and `arbiter uninstall` rows).
- **Acceptance:** each [Design harvest](#design-harvest) row lands in its destination and says what the
  row says. The project-only sentences are corrected, not deleted, and the downgrade warning keeps its
  honest wording. `docs validate` and `docs drift` zero findings.
- **Commands:** CMD-DOCS.

### T7: Closeout

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`. **Depends on:** T5.
- **Acceptance:** `git mv` the plan to `docs/archive/plans/`, move its index row to **Archived Plans**
  with the harvest destinations and evidence, record the [Closeout mapping](#closeout-mapping) and a
  completion summary, set frontmatter `status: archived` and the body status to match, and leave
  **Active Plans** empty. Then CMD-FINAL with `--merge-ready`. The closeout commit goes on the same
  draft pull request; nothing is merged or published.
- **Commands:** CMD-FINAL.

## Dependency graph

```text
T0 ─► T1 RED ─► T2 GREEN ─┬─► T3 RED ─► T4 GREEN ─┐
                          └─► T6 docs ─────────────┤
                                                   ├─► T5 review ─► T7 closeout
```

First ready set: T0, then T1 alone (it gates everything). T3 and T6 may run together once T2 is green.
File scopes do not overlap except `SquadStateStore.cs` (T2, and T4 only if T3 points there) and the
plan and index files (T0, then T7).

## Risks

| Risk | Mitigation |
|---|---|
| The author could not read `origin/main`, so a premise is wrong (gap G2). | T1's premise check runs before any test is written, and its STOP rule is explicit. |
| A worker "fixes" the symptom by special-casing `hooks.json`. | The identity rule is stated as a table in [Fix design](#fix-design) and pinned by T1(a)'s three-target test, which a special case cannot pass for `devin`'s `hooks.v1.json`. |
| The narrower v3 assertions get relaxed instead of re-pinned. | Named individually in [Compatibility](#compatibility); T2's acceptance forbids deletion or weakening and D6 keeps every duplicate refusal. |
| Widening v3 makes older readers misread a global receipt as a project one. | The shape check refuses `scope: global` on v1 and v2 and refuses `layout`/`homeFiles` there, so an older reader fails loudly. Pinned in T2 and stated in ADR 0054's consequence. |
| T4 grows into a refactor of every block consumer. | Bounded to the four consumers T3 tests; anything else becomes a todo. |
| Global uninstall removes the wrong target's hooks. | T3 asserts removal per target under global scope before T4 touches the code. |
| The fix branch and the rename branch conflict later. | They touch disjoint code: the rename branch renames MCP strings, this branch touches receipt persistence and arbiter path resolution. Whoever merges second re-runs CMD-BLOCK. |

## Out of scope

- Changing global-by-default `arbiter install` (D10) or the `--global`/`--project` flags.
- Restoring prefixed global paths or renaming the hook files (D3).
- A new receipt schema version, a migration verb, or rewriting any existing receipt (D4, D5).
- Any block-bearing target beyond the five formats `SquadHookJsonBlock` declares.
- The MCP server and tool rename in progress on `refactor/mcp-server-tool-rename`.
- Publishing a release, cutting a tag, or merging.

## Verification gates

CMD-FINAL at T5, CMD-DOCS with `--merge-ready` after the archive at T7, and the live check below.
`[NOT LIVE-VERIFIED]` until the conductor runs it on a fixed build: three targets into one isolated
home, in the order that fails today.

```bash
H=$(mktemp -d)
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- arbiter install --target cursor
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- arbiter install --target codex
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- arbiter install --target factory
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- arbiter install --target devin
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- squad status --global
scripts/proc-guard.sh run -- env HOME="$H" dotnet run --project src/KyberWeave.Cli -- arbiter uninstall --target codex
```

Expected: every install exits 0, `squad status --global` lists four blocks with their target labels,
and the uninstall removes only Codex's block. Also `[NOT LIVE-VERIFIED]`: upgrading a real rc.20 home to
the fixed build (D5) on the owner's machine.

## Review and closeout

- Draft pull request from `fix/arbiter-global-block-identity`, titled
  `fix(arbiter): qualify global block identity by target`, body carrying the reproduction, the
  before/after, and the note that upgrading from rc.20 is safe.
- One whole-change council review (T5) with `--all`, because the change touches shared persistence in
  `src/KyberWeave.Core.Squad` (`AGENTS.md`, inner loop and PR sections).
- `docs-dev` closeout (T7), including the harvest mapping below.

## Design harvest

| ID | Target component | Target doc type | Durable item | Destination |
|---|---|---|---|---|
| H1 | KyberSquad | architecture | A stored block's identity is scope-aware: path alone under project and instance scope, target plus path under global scope, through the same helper the file validator uses; global receipts may own blocks | `docs/kyber-squad/architecture.md` |
| H2 | KyberSquad | adr | The v3 receipt's global shape (`scope: global` with `layout`, `homeFiles` and `blocks`), the identity rule, and the unchanged downgrade refusal for older readers | `docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md` (amended in place) |
| H3 | KyberArbiter | runbook | Global install accepts several block-bearing targets in any order; how status, doctor, update and uninstall treat them | `docs/kyber-arbiter/runbook.md` |
| H4 | KyberArbiter | reference | The `arbiter install` and `arbiter uninstall` exit codes and target behaviour at global scope | `docs/cli-command-surface.md` |

## Closeout mapping

**Completed 2026-11-07.** The permanent `## Decisions` table above is preserved
intact, and every decision and Design harvest item is mapped to a live governed document below.

### Verification evidence

| Check | Result |
|---|---|
| T1 RED | `SquadGlobalBlockReceiptTests` (2 tests), `ArbiterInstallCommandTests.Install_ThreeGlobalTargetsSharingOneHookFileName_AllExitZeroAndOneReceiptHoldsThreeBlocks` — all failed with the shipped `hooks.json` message. |
| T2 GREEN | `SquadStateStore.cs` scope-aware identity; block and render suites green (601 + 48). |
| T3 RED | `SquadOwnedBlockStatusTests.Status_GlobalReportsEveryBlockWithItsTargetLabel`, `Doctor_GlobalFindsEveryBlockHealthy` — failed on bare `hooks.json` for codex. |
| T4 GREEN | `SquadHookJsonBlock.TryResolveFormat`; status/doctor suites green. |
| Documentation harvest | H1–H4 landed in the four destinations below. |
| `docs validate . --merge-ready` | Pass, 0 findings. |
| `docs drift .` | Pass, 0 findings. |

### Mapping

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [`docs/plans/README.md`](../../plans/README.md) and this plan — the fix lives on `fix/arbiter-global-block-identity` off `origin/main`; the rename branch carries neither the plan nor the fix. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D2 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — the scope-aware identity rule, per H1. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D3 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — bare global block paths stay. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D4 | [`docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md`](../../adr/0054-squad-owned-blocks-in-shared-hook-files.md) — the v3 global shape, per H2. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D5 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — the contract behind the rc.20 fixture is H2. Documentation waiver: the fixture itself has zero surviving invariant for a future reader. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D6 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — the three duplicate refusals, per H1. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D7 | [`docs/kyber-arbiter/runbook.md`](../../kyber-arbiter/runbook.md) — per-verb lifecycle behaviour, per H3. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D8 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — the receipt is the block-path source, per H1. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D9 | [`docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md`](../../adr/0054-squad-owned-blocks-in-shared-hook-files.md) — amended in place, per H2. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D10 | [`docs/kyber-arbiter/runbook.md`](../../kyber-arbiter/runbook.md) — the unchanged default, per H3. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D11 | [`docs/kyber-arbiter/runbook.md`](../../kyber-arbiter/runbook.md) — verification, per H3. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D12 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — test-first and autonomous delivery recorded by this plan. Documentation waiver: a process row with zero surviving technical invariant. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| D13 | [`docs/cli-command-surface.md`](../../cli-command-surface.md) — draft-PR delivery, per H4. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| AP1 | [`docs/plans/README.md`](../../plans/README.md) and this plan — approve-and-execute in Decisions and the index row. Documentation waiver: process provenance, not a durable technical item. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| H1 | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) — scope-aware block identity paragraph. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| H2 | [`docs/adr/0054-squad-owned-blocks-in-shared-hook-files.md`](../../adr/0054-squad-owned-blocks-in-shared-hook-files.md) — v3 global shape and identity rule, amended in place. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| H3 | [`docs/kyber-arbiter/runbook.md`](../../kyber-arbiter/runbook.md) — Hooks paragraph for global multi-target behaviour. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |
| H4 | [`docs/cli-command-surface.md`](../../cli-command-surface.md) — `arbiter install`/`uninstall` global-scope rows. | extends-component | KyberSquad, KyberArbiter | 2026-11-07 | archived |

The live verification gates in this plan keep their `[NOT LIVE-VERIFIED]` marker until a
fixed build is run against an isolated home and a real rc.20 home.
