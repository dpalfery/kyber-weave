---
id: archive/plans/2026-09-26-squad-receipt-layout-marker
title: Squad receipt layout marker
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-26
development-mode: test-first
---

# Squad receipt layout marker

## Status

Complete and archived 2026-09-26. Decisions A3–A5 recorded 2026-09-26 and harvested as [ADR 0024](../../adr/0024-squad-global-receipt-layout-marker.md); see the T6 closeout below.

## Problem and goal

In [issue 98](https://github.com/dpalfery/kyber-weave/issues/98), global receipts written before #91 (v0.1.7-rc.9/rc.10) record target-prefixed paths (`.codex/agents/x.toml`) beneath the single deployment root. Since #91 the CLI resolves every global entry beneath that target's own root, using bare paths, so a legacy entry resolves to `~/.codex/.codex/...`. The effects:
- `status` reports every file missing.
- `update` drops the entries and orphans the originals.
- Same-target `install` does the same, because it routes through `CreateUpdate`.
- `uninstall` reports success with 0 files and deletes the receipt and lock. This cannot be undone.

Goal: every lifecycle command resolves a global receipt through one explicit, deterministic layout. A legacy receipt is either verified or removed where rc.9/rc.10 wrote it, or refused before any change. It is never silently disowned.

## Development mode

`test-first`. Supplied by the conductor; the user did not opt out.

## Approved decisions

| ID | Decision | Provenance |
|---|---|---|
| A1 | `development-mode: test-first` | Conductor assignment 2026-09-26; repository default, no opt-out. |
| A2 | Plan only; keep the artifact concise. | User constraint relayed by conductor 2026-09-26. |
| A3 | `kyber-squad.receipt/v2` with a required `layout: single-root \| per-target-roots`, written for global scope only; project receipts stay v1 byte-identical. Older CLIs refuse a v2 global receipt (exit 1, no changes) until upgraded. | User answer 2026-09-26, relayed by bug-crusher |
| A4 | `status` and `uninstall` resolve legacy entries under the recorded deployment root. `update` and same-target `install` refuse before any download or change, with copy-ready `squad uninstall --global` then `squad install --global` guidance. | User answer 2026-09-26, relayed by bug-crusher |
| A5 | From the paths in the receipt: every entry starts with its target's project prefix → single-root; no entry does → per-target; a mix → rejected as invalid, with guidance. Deterministic and never reads the disk. | User answer 2026-09-26, relayed by bug-crusher |

Rejected alternatives:
- Q1b: Keep v1; path spelling (Q3) stays the permanent layout rule, with no marker. Breaks the explicit marker the issue asks for.
- Q1c: v2 for all scopes. Breaks committed project receipts shared across teammates' CLI versions.
- Q1d: Record resolved absolute roots per receipt. Breaks the portable `targetRoot: "."` rule.
- Q2b: (a), plus `update` migrates in place. Needs a per-entry layout for edited legacy files, plus handling for paths that overlap at $HOME.
- Q2c: A new `squad migrate --global` command that checks digests, rewrites state, and moves files where the paths differ. Largest scope: a new command, docs and tests.
- Q2d: Refuse on all four commands. Leaves no supported way out, because install goes through update and the state would have to be deleted by hand.
- Q3b: The issue's check: treat as single-root only when the per-target roots lack the recorded bytes. Depends on the filesystem, so the same receipt can be classified differently over time.
- Q3c: The lock's `cli-version` is rc.9 or rc.10. Fails for dev builds.

## Investigation findings

- The receipt has no layout field: `SquadReceipt` (SquadDeploymentModels.cs:60-66). The schema string is duplicated (SquadStateStore.cs:17, SquadDeploymentPlan.cs:8). The reader requires the exact v1 schema and field set (SquadStateStore.cs:394-400, :602-636).
- The layout is chosen from scope plus whether a resolver was supplied (`ResolvePhysicalRoot`, SquadDeploymentPlan.cs:110-128). The CLI always supplies `SquadGlobalRoots` (SquadCommandComposition.cs:228). So the single-root fallback promised in the `ResolveOwnedFilePath` doc comment (:140-149) can never be reached from the CLI.
- Where entries are silently dropped:
  - `CreateUpdate` skips a missing previous entry with `continue` (:361-362).
  - `CreateUninstall` drops missing entries (:422-423), then deletes the lock and receipt when nothing is retained (:448-449).
  - `InstallAsync` with an existing receipt for the same targets calls `CreateUpdate` (SquadLifecycleService.cs:105-196).
- All 11 renderers strip their project prefix under Global at HEAD (`ResolvePrefixedDirectory` in each `*Renderer.cs`). The prefixes are `.github/ .cursor/ .claude/ .codex/ .opencode/ .kilo/ .factory/ .agents/ .warp/ .pi/ .zcode/`. The investigator reports that pre-#91 renderers always emitted the prefix (`83321bc`); this was not re-checked because there is no shell.
- Legacy and per-target physical paths match only for codex, claude and cursor, and only for a $HOME root with no env override. Copilot (`~/.github` vs `~/.copilot`) and antigravity (`~/.agents` vs `~/.gemini/config`) never match. So a receipt-only rewrite is not a general migration.
- The uninstall confirmation derives per-target roots from the receipt (`ResolveUninstallGlobalTargetRoots`, SquadCommandComposition.cs:118-148), so for a legacy receipt it names the wrong roots.
- `SquadDoctorCommand` does not read receipts, so it is out of scope.
- The investigator reproduced all of this outside the repo. No existing test covers a legacy single-root global receipt. Squad suite baseline: 937/937.
- Discovery fallbacks: the Kyber-Weave docs MCP was unavailable, so docs were looked up with narrow self-gathered grep under `docs/` (not `docs/archive/`). Code was explored with CodeGraph.

## Scope

In scope:
- how the receipt layout is stored (Q1) and how v1 global receipts are classified (Q3);
- legacy handling in status, uninstall, update and install (Q2);
- the root named in the uninstall confirmation;
- canonical docs.

Out of scope:
- sibling-global ownership across bindings;
- doctor;
- any project-scope behavior change;
- the lock schema;
- a `migrate` command.

## Test contract

- Every new test name contains `ReceiptLayout`.
- RED must be a runtime assertion failure through existing public surfaces: the `SquadStateStore` string APIs, `SquadLifecycleService`, the `SquadDeploymentPlan` factories, and the CLI commands. A compile error is not RED.
- Runner, called **F** below: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReceiptLayout"`.

| Task | Test file | Runner | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `SquadDeploymentStateTests.cs`, `SquadGlobalRootTests.cs`, `SquadCliCommandTests.cs` | F | See behaviors (1)–(7) below the table. | Behaviors (1)–(6) fail at HEAD on the stated defect: v1 written, v2 rejected, mixed paths accepted, 0 deletions with state deleted, update plans writes, status shows `missing` with exit 1, per-target roots echoed. Keep the log. (7) is a guard that passes at HEAD and is not RED evidence. | F passes unchanged after T3. |
| T2 | same | F, then `--filter "FullyQualifiedName~Squad"` | Behaviors (1)–(5), (7) | Uses T1's log. If already green, stop and investigate again. | These tests pass, and the full Squad filter passes. Edits to existing tests are limited to mechanical call-site updates for the new `ResolveOwnedFilePath` signature, and each one is listed. |
| T3 | `SquadCliCommandTests.cs` | F, then `--filter "FullyQualifiedName~SquadCliCommandTests"` | Behavior (6) | Uses T1's log. | Both commands pass. |
| T4 | `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md` | `docs validate .` then `docs drift .` | The docs state the layout contract, v1/v2 compatibility, that older CLIs refuse v2 global receipts, and how to recover from an rc.9/rc.10 install. | No test. Record that the docs before the edit say nothing about layout or legacy recovery. | Zero findings from both checks. |
| T5 | Read-only | `review gates . --out artifacts/gates.json` | Gates plus council review of the whole change. | No test. | All gates pass; review is approval-quality. |
| T6 | Plan, index, archive | `docs validate . --merge-ready` then `docs drift .` | Closeout and archive. | No test. `--merge-ready` fails before closeout only with `KW-DOC-LIFECYCLE-003`. | Both pass with zero findings. |

Behaviors asserted by T1:
1. A global receipt serializes as `kyber-squad.receipt/v2` with `layout: per-target-roots` for new writes. A project receipt serializes as v1 with no `layout` field, byte-identical to today.
2. A v2 global receipt round-trips. These are rejected with an `InvalidDataException` that names the fix: a v2 project receipt, a v2 receipt with a missing or unknown `layout`, and a v1 global receipt that mixes prefixed and bare paths.
3. Uninstall of a legacy v1 global receipt with codex and copilot entries, using a fake resolver that points elsewhere:
   - deletes the clean files under the recorded root;
   - keeps an edited file and rewrites the receipt as v2 `single-root` with only that entry;
   - touches nothing under the per-target roots;
   - when nothing was edited, deletes the lock and receipt only after the files are gone.
4. With a legacy receipt, `update` (real and dry-run) and same-target `install` throw `SquadDeploymentConflictException`. The message names `squad uninstall --global` followed by `squad install --global`. The release source is called zero times, and the lock, receipt and file bytes are unchanged.
5. A v1 global receipt with bare paths (written by rc.11 or later) updates normally and is rewritten as v2 `per-target-roots`.
6. CLI behavior:
   - `status --global` on a clean legacy receipt prints `ok` for each entry plus a legacy-layout notice, and exits 0;
   - an edited entry shows `drift` and exit 1;
   - the `uninstall --global` confirmation names the recorded root.
7. Guard: every registered renderer's Global output contains no path starting with that target's legacy prefix.

## Tasks

**T1 — Failing ReceiptLayout contracts.** Add the tests from the Test contract. Build fixtures with the existing `SquadReceipt` constructor and `SquadGlobalRoots(_ => env, tempHome)`, and never touch the real home directory. Depends on: none. Skills: `test-dev`.

**T2 — Core layout.** Depends on: T1. Skills: `csharp-dev`.
- `SquadDeploymentModels.cs`: add a `SquadReceiptLayout` enum. The receipt's layout is additive and the positional constructor is unchanged, so existing code compiles as is.
- `SquadStateStore.cs`:
  - keep one receipt schema source and delete the duplicate at SquadDeploymentPlan.cs:8;
  - validate the JSON shape per schema;
  - classify v1 global receipts using a fixed per-target legacy prefix table, with a `<remarks>` block that cites #91;
  - write v2 for global receipts.
- `SquadDeploymentPlan.cs`:
  - `ResolvePhysicalRoot`: a single-root layout resolves to the recorded root whether or not a resolver is supplied;
  - `ResolveOwnedFilePath`: takes the receipt instead of the `scope` parameter, and its doc comment is corrected;
  - `CreateUninstall`: resolves by layout and keeps the layout on the retained receipt;
  - `CreateUpdate`: refuses a single-root previous receipt.
- `SquadLifecycleService.cs`: `InstallAsync` (existing-receipt path) and `UpdateAsync` refuse a single-root receipt before `DownloadAndExtractAsync`.
- Check that `SquadTransaction` (:388, :482) resolves only through the plan.

**T3 — CLI.** Depends on: T2, which provides the new signature. Skills: `csharp-dev`.
- `SquadStatusCommand.Execute`: use the receipt-aware `ResolveOwnedFilePath` and print the legacy notice with copy-ready commands.
- `SquadCommandComposition.ResolveUninstallGlobalTargetRoots`: return the recorded root for a single-root receipt.

**T4 — Docs.** Update architecture.md §Lock and Receipt Files and onboarding.md §2 Global Scope. Depends on: none, since the Q answers fix the contract. Skills: `app-docs-standard`, `kyber-weave-docs`.

**T5 — Gates and review.** Run everything under Verification gates and triage each failure to T1–T4. Do not weaken a contract or add to `NoWarn`. Depends on: T3, T4. Skills: `resharper-clt`, `code-review`.

**T6 — Closeout.**
- Record the evidence, archive the plan and update the index.
- Write an ADR for the receipt layout contract (a lasting persisted-format constraint with rejected alternatives).

Depends on: T5. Skills: `app-docs-standard`, `architecture-decision-record`.

## Dependency graph

```text
T1 -> T2 -> T3 --+
T4 --------------+--> T5 -> T6
```

`MAX_CONCURRENCY: 2`. T4 runs alongside T1–T3; their file scopes don't overlap.

## Risks

- By design (Q1a), older CLIs refuse a v2 global receipt. The docs say so, and upgrading the CLI fixes it.
- The Q3 classifier depends on renderers never emitting a prefixed global path. Guard test (7) pins this.
- Legacy uninstall deletes only owned files whose digest matches, under the recorded root. These are the same ownership rules project uninstall already uses.
- The mapping of rc.9/rc.10 to "before #91" comes from commit messages; the clone is shallow and has no tags.

## Verification gates

The AGENTS.md "Commands" set: restore, format ×2, Release build, full test, skill validate/lint/scan, `docs validate`, `docs drift`. Also `./scripts/update-loop.sh` and `review gates . --out artifacts/gates.json`.

## Planning verification

Pending: `docs validate .` and `docs drift .` run by the orchestrator after save.

## Review and closeout

T5 reviews the complete change. T6 is the `docs-dev` closeout: it archives this plan and moves its index row to Archived.

## Closeout (T6, 2026-09-26)

**Implementation.** Commit `2d2a34b` delivered T1–T4, and `83fbee6` removed `var` patterns from the scope and layout parsing. `9e70c8f` keeps the console captures in `ProcessConsoleCapture.cs` free of ANSI escapes on CI runners, so the new CLI assertions hold there. The shipped contract is decisions A3–A5 as approved above.

**Test evidence.** On the branch head `cd3ccff`, the runner **F** (`FullyQualifiedName~ReceiptLayout`) passed 18/18, and the full Squad filter passed 955/955, against the 937 baseline plus the 18 new tests. CI run [36291127122](https://github.com/dpalfery/kyber-weave/actions/runs/36291127122) passed Build and test, the Squad filesystem contract on Linux, macOS and Windows, and the self-update and Squad install loop on Linux and macOS. The T1 RED log is not attached to the pull request, so this closeout does not claim it.

**Canonical documentation.** T4 added [Receipt version and layout contract](../../kyber-squad/architecture.md#receipt-version-and-layout-contract) to the architecture and the rc.9/rc.10 Global recovery note to [the onboarding guide](../../kyber-squad/onboarding.md). The architecture document now cites the ADR in `decided-by`.

**ADR.** [ADR 0024](../../adr/0024-squad-global-receipt-layout-marker.md) records the receipt layout contract: a persisted-format constraint that older CLIs enforce by refusing v2, together with the rejected alternatives Q1b–Q1d, Q2b–Q2d and Q3b–Q3c.

**Review.** No T5 council verdict is recorded on the pull request. CI's required gates are the review evidence at closeout.

**Closeout verification.** After archival, the index update and the ADR, `docs validate . --merge-ready` exited 0 with zero findings. Before this closeout, that was the one check failing in CI, with `KW-DOC-LIFECYCLE-003`. `docs drift .` did not run: this checkout has no CodeGraph index, so it stops at `KW-DOC-DRIFT-001` before checking anything, and CI defers drift until an index is provisioned. The closeout adds no `code-refs`, so it introduces no drift of its own.
