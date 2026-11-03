---
id: plans/2026-10-26-issue-436-squad-preset-canonical-profiles
title: "Squad preset verbs resolve canonical model profiles everywhere and never rewrite skills (issue #436)"
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-26
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberSquad]
---

# Squad preset verbs resolve canonical model profiles everywhere and never rewrite skills (issue #436)

**Status: Archived — complete 2026-10-26 (T1–T5 test-first, closeout mapping in the Closeout mapping section).**
**Approve-and-execute (AP1): approved — autonomous: recommendation accepted.**

**Development mode:** `test-first` (default; no opt-out supplied). **Autonomy:** `autonomous`
(packet; matches Config Reg `<autonomy>`). Changing autonomy later does not reopen approval.

**Intake:** diagnosed defect escalated by `bug-crusher` (verdict REPRODUCED, three-component
root cause, tripwires fired: multi-file multi-layer, design decision, public exit-code
contract, new tests, same symptom in `preset clear`). Plan authored from the investigator's
findings plus independent discovery; no fix was attempted before escalation.

> Discovery: CodeGraph index present, `codegraph_explore` ran first for all code questions
> (preset commands, composition/source resolution, lifecycle download+extract flow, receipt
> model, renderers, validator/diff/resolution/stores). The Kyber-Weave docs MCP
> (`docs_explore`) answered the documented preset contract, exit-code surface, and home-state
> topology. Direct reads, each for something an index does not cover: the plan index
> (`docs/plans/README.md`, twice — inventory format and closeout policy), the architect
> references under `products/kyber-squad/agents/architect/references/` (product tree, not
> `docs/`), one archived plan's frontmatter (exact governed shape for `docs validate`), one
> grep over `src/KyberWeave.Core/Squad/Rendering` confirming every renderer's skills layout,
> and one glob for existing preset test files. `Discovery: codegraph 6, docs 2, direct reads 5`.

---

## 1. Problem and goal

### 1.1 Problem

With an **installed** CLI (0.1.8-rc.7, synthetic `$HOME`), `kyber-weave squad preset use
<name> --target opencode --global --dry-run` plans `general` for every agent **and skill**
row and exits 0, and `squad preset show` exits 1 with "Could not resolve Squad model profiles
from canonical source." A repo-built binary resolves agents but still plans a model for the
skill `bug-crusher`. Three defects (verified in source):

1. **Canonical profiles are resolvable only from a checkout.**
   `SquadCommandComposition.TryResolveSquadSource()` (SquadCommandComposition.cs:377-412)
   loads `products/kyber-squad` by walking up from `AppContext.BaseDirectory` — absent for an
   installed CLI. The documented contract (onboarding: presets validate against the *active
   release's* `models.yml`) is unreachable outside a checkout. `SquadPresetShowCommand`
   (SquadPresetShowCommand.cs:80-85) turns that null into the reported exit-1 error.
2. **Null source is swallowed.** `SquadPresetUseCommand.ApplyPresetToDeployment`
   (SquadPresetUseCommand.cs:236-241) builds an empty agent→category map and a
   preset-only `SquadModelProfiles`, so every file falls back to `"general"`
   (lines 273-274) and dry-run prints a wrong plan and exits 0 instead of failing.
   `SquadPresetClearCommand` (SquadPresetClearCommand.cs:191-193) shares the defect.
3. **Every receipt `.md` is treated as an agent.** The rewrite loops match on
   `file.RelativePath.EndsWith(".md")` only (use: 258, clear: 210), so skill files — which
   never carry a model — reach `SquadFrontmatterHelper.UpdateFrontmatterModel`'s insert path
   (SquadFrontmatterHelper.cs:110-121) and gain a `model:` key. Install-time diffing is
   correct because `SquadModelDiff.Compute` iterates `source.Agents` only; the CLI rewrite
   loops have no equivalent discrimination.

### 1.2 Goal

1. `squad preset use|clear|show` resolve canonical model profiles on an installed CLI from a
   **version-keyed canonical profile snapshot** persisted in the home state tree by
   install/update, with the checkout path preserved as first choice.
2. When profiles cannot be resolved by any means, all three verbs fail fast with **exit 1**
   and an actionable remedy — before any file rewrite, lock change, or (for dry-run) any
   wrong plan printed.
3. Skill files are **never** rewritten: model rewrites apply only to deployed agent files
   whose profile category resolves; the silent `"general"` fallback is removed.
4. `preset use` and `show` enforce the documented strict preset-key validation
   (unknown category/harness → exit 1 with file path and line).
5. The documented exit-code and preset contracts are updated to match, and the home-state
   topology documents the snapshot.

## 2. Investigation findings

- **Repro** (investigator, installed 0.1.8-rc.7, synthetic `$HOME`): dry-run prints every row
  `-> general` exit 0; `preset show` exits 1 could-not-resolve. Repo-built binary resolves
  agents (checkout found above `AppContext.BaseDirectory`) but still plans a model for skill
  `bug-crusher` (defect 3: name lookup / general fallback reaches skills).
- **How install does it right:** `SquadLifecycleService.InstallAsync`/`UpdateAsync` download
  the release, resolve `SquadSource` from the extraction root
  (SquadLifecycleService.cs:130, 312), validate presets with `SquadModelPresetValidator`,
  render, and delete the temp extraction — the canonical profiles exist only transiently.
  The preset verbs never download, so they have no canonical source outside a checkout.
- **Skills layout is uniform:** all 13 renderers emit skills at
  `<prefix>/skills/<name>/SKILL.md` (grep over `src/KyberWeave.Core/Squad/Rendering`;
  `SquadRendererRegistry.cs:365-480` projects primary-agent entry-point skills there too).
  Agent-owned resources deploy as `.markdown` (ADR 0040), already excluded by the `.md` check.
- **`SquadOwnedFile`** (SquadDeploymentModels.cs:110-114) carries only
  `RelativePath/Sha256/Target/Adopted` — no agent/skill kind, so discrimination must come
  from path shape plus the canonical agent roster.
- **Agent frontmatter does not carry `model-profile`** (e.g. FactoryRenderer emits
  name/description/model/tools/...), so the `ExtractField(frontmatter, "model-profile")`
  branch is a secondary signal at best; the roster lookup is the real mechanism and must come
  from the resolved canonical profiles.
- **Existing coverage:** `SquadPresetCliTests` covers checkout-backed happy paths; codegraph
  reports **no covering tests** for `UpdateFrontmatterModel` and none for the null-source
  path (investigator: NONE).
- **Docs contract today** (onboarding): presets "strictly checked against the active release's
  `models.yml` ... exit code 1, reporting the exact file path and line number"; dry-run
  "prints each **agent** whose resolved model changes". The code violates both on installed
  CLIs; `cli-command-surface.md` has no preset exit-code rows.

## 3. Fix design

### 3.1 Canonical profile snapshot (Core)

- New record `SquadCanonicalProfiles` (`SquadModelProfiles Profiles`,
  `IReadOnlyDictionary<string,string> AgentProfiles` — agent name → profile category).
- New `SquadCanonicalProfileStore` (Core, `Squad/Presets/`): persists one YAML file per
  Squad release version at `~/.kyber-weave/squad/profiles/<version>.yml`, schema
  `kyber-squad.canonical-profiles/v1` (`squad-version`, `profiles` mirroring `models.yml`
  shape, `agents` name→category). Atomic write (temp file + rename, the
  `SquadStateMigration.WriteAtomically` pattern). `TryRead(version)` and `TryReadLatest()`;
  an unreadable or corrupt file counts as absent (never throws to callers). No pruning (D7).
- `SquadLifecycleService.InstallAsync`/`UpdateAsync` (and therefore `squad run` instance
  provisioning, which routes through the lifecycle service): after a committed transaction,
  write the snapshot built from the resolved extraction-root source, keyed by
  `releaseResult.Version`. **Not** on dry-run (dry-run modifies no state — documented). A
  snapshot write that fails after the deployment commits produces a warning naming the
  remedy (rerun `squad update`), mirroring the registry write-failure pattern; the
  deployment is never failed for it. Source resolution is restructured to run whenever the
  extraction root is available, not only when presets are loaded, while preserving the
  existing fail-closed throw when presets are requested and the source is unresolvable.

### 3.2 CLI resolution chain and null-source contract

- `SquadCommandComposition.ResolveCanonicalProfiles(preferredVersion, userPaths)`:
  (1) checkout `TryResolveSquadSource()` → (2) snapshot `TryRead(lock.SquadVersion)` →
  (3) snapshot `TryReadLatest()` → (4) null. `preset use`/`clear` pass the active lock's
  `SquadVersion`; `preset show` passes null (no deployment context). Checkout-first keeps
  repo-built and existing test behaviour unchanged (D5).
- All three preset commands get an injectable resolver seam (extend the existing internal
  constructors) so tests can force the no-checkout path; the production default is the
  composition static.
- Null result → operational error, **exit 1**, identical message shape across the verbs:
  could-not-resolve plus the remedy (`kyber-weave squad update` refreshes the snapshot;
  running from a kyber-weave checkout also works). In `use`/`clear` this fires before the
  rewrite loop in both dry-run and apply (D2). The `?? new SquadModelProfiles(...)` fallback
  (use: 239-241) and both `?? "general"` fallbacks (use: 273-274, clear: 225-226) are
  removed.

### 3.3 One rewrite helper, agent-only, shared by use and clear

- New CLI helper (`Commands/Squad/Infrastructure/`, e.g. `SquadPresetFileRewriter`)
  implementing the receipt rewrite loop once. Per receipt file in the target tokens ending
  `.md`:
  - **Skip unchanged** when the final path segment is `SKILL.md` or any path segment equals
    `skills` (case-insensitive, both separators) — covers canonical skills *and*
    primary-agent entry-point skill projections across all 13 renderers (D3).
  - Otherwise parse frontmatter; category = `model-profile` field, else
    roster[`name` field ?? `ResolveIdentity(path)`]. **Unresolvable → pass through
    unchanged, no diff row** (adopted/unknown files keep their bytes) (D4).
  - Resolve the new token with `SquadModelResolution.ResolveToken` semantics (no
    `general`-category fallback); `null` (inherit) removes the `model:` line, matching
    bundled behaviour. Preserve newline style; diff rows only for changed files.
- `SquadPresetUseCommand` validates the preset with `SquadModelPresetValidator` against the
  resolved profiles before any rewrite; a validation failure exits 1 with the validator's
  file-path/line message (D6). `show` validates the same way before printing.
- `SquadPresetShowCommand` resolves through the same chain (checkout → newest snapshot) and
  keeps its existing exit-1 error, extended with the remedy text.

### 3.4 Migration for existing deployments

Deployments installed before this change have no snapshot. After upgrading the CLI, the
first `preset use/clear/show` still fails exit 1 with the remedy; one `squad update`
(--global and/or in the project) re-renders from the release and writes the snapshot. This
is the documented migration path (D2); no implicit download is added to the preset verbs.

## Decisions

Permanent section, preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How does an installed CLI obtain canonical model profiles for the preset verbs? | Version-keyed canonical profile snapshot persisted in the home state tree at install/update; read by `preset use/clear/show`; checkout stays first choice | Alternatives rejected: embedding `models.yml` in the CLI assembly (skews from `--version`-pinned deployments; build change), on-demand release download in the preset verbs (adds network to previously offline verbs and a new failure mode), deriving from deployed files (bundled profile→model map is not present in rendered output). Snapshot matches the documented "active release's `models.yml`" contract exactly (keyed by the lock's `SquadVersion`), works offline, is a few KB. Additive state under the ADR 0037 home-state tree. Sources: SquadLifecycleService.cs:123-142, onboarding preset contract, ADR 0037 pattern. | autonomous: recommendation accepted |
| D2 | What happens when canonical profiles cannot be resolved (no checkout, no snapshot)? | Hard fail **exit 1** in `use`/`clear` (dry-run and apply) and `show`, before any file rewrite or state write, with a remedy naming `squad update` | Today dry-run prints a wrong plan and exits 0 — the reported defect. Exit 1 mirrors `preset show`'s existing behaviour and the documented validation exit code; input was valid, the environment lacks state. Existing pre-snapshot deployments migrate via one `squad update`. | autonomous: recommendation accepted |
| D3 | How are skill files kept out of the model rewrite? | Path discrimination: skip any receipt `.md` whose final segment is `SKILL.md` or whose path contains a `skills/` segment | Verified uniform across all 13 renderers (`src/KyberWeave.Core/Squad/Rendering` grep; `SquadRendererRegistry.cs:365-480` also projects primary-agent entry-point skills there, and those must not gain models either). Roster-name checking alone is insufficient (agent/skill same-name collisions, e.g. `bug-crusher`). Agent-owned resources already deploy as `.markdown` (ADR 0040). | autonomous: recommendation accepted |
| D4 | Which files may be rewritten at all? | Only non-skill `.md` files whose profile category resolves (frontmatter `model-profile`, else roster by name); unresolvable files pass through unchanged with no diff row | Removes the `?? "general"` fallback (the every-row-`general` defect). Matches install-time `SquadModelDiff.Compute`, which iterates only canonical agents. Adopted files whose identity is not a canonical agent keep their bytes. | autonomous: recommendation accepted |
| D5 | Resolution order for canonical profiles | Checkout `products/kyber-squad` → snapshot for the lock's `SquadVersion` → newest snapshot → null (`show` has no lock, uses checkout → newest) | Checkout-first preserves current repo-built behaviour and the existing checkout-backed tests; the version-keyed snapshot reflects what the deployment was actually rendered from; newest covers `show`. | autonomous: recommendation accepted |
| D6 | Do `use`/`show` strictly validate preset keys? | Yes — `SquadModelPresetValidator` against the resolved profiles before any rewrite or print; failure exits 1 with the validator's file-path/line message | Documented onboarding contract ("strictly checked ... exit code 1, reporting the exact file path and line number") is currently enforced only at install/update; the preset verbs silently omitted it. | autonomous: recommendation accepted |
| D7 | Snapshot retention | One file per distinct Squad version under `profiles/`, no pruning | Each file is a few KB; KS-005 version lockstep bounds distinct versions in practice; pruning adds failure modes for multi-version deployments. Cheap to reverse later. | autonomous: low-confidence, no authoritative source |
| D8 | Shared implementation for the near-identical `use`/`clear` rewrite loops | Extract one CLI-layer rewrite helper used by both commands | All three defects exist identically in both commands because the loops are copies (use 246-288, clear 198-240); one helper closes the defect class instead of patching it twice. | autonomous: recommendation accepted |
| D9 | Does dry-run write the snapshot? | No — dry-run modifies no state or files (documented); the snapshot is written only by committed install/update (including `squad run` instance provisioning); a post-commit write failure is a warning naming the remedy, never a failed deployment | Mirrors the registry write-failure pattern in architecture.md §4 (warning after committed transaction; deployment not rolled back). | autonomous: recommendation accepted |
| AP1 | Approve and execute this Ready plan (extends-component, KyberSquad, test-first, autonomous)? | Approved — execute T1→T5 per §7-8 with RED→GREEN and per-task task-gate; whole-change review before PR | Decision-complete with no hard-block escalations; scale and harvest declared in §5; docs validate clean at approval time. | autonomous: recommendation accepted |

No hard-block escalations: every open question had a recommendable option with repository
sources (autonomy-decisions checks 1-2 applied per row).

## Design harvest

`documentation-impact: extends-component`, `impact-components: [KyberSquad]` — behaviour fix
plus one additive state file in an existing component's home-state tree; no new component.

Design harvest (one row per durable item; harvested at closeout):

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberSquad | ARCHITECTURE (`kyber-squad/architecture.md` §4 state topology) | `profiles/<version>.yml` canonical profile snapshot: schema, atomic write on committed install/update, read order for preset verbs, warning-on-write-failure, no pruning |
| H2 | KyberSquad | ONBOARDING (`kyber-squad/onboarding.md` model-presets section) | Canonical-profile resolution order; skills and entry-point skills never rewritten; dry-run/apply exit 1 when unresolvable with the `squad update` remedy; strict validation now enforced by `use`/`show`; migration for pre-snapshot deployments |
| H3 | (aggregation index) | REFERENCE (`cli-command-surface.md` exit codes) | `squad preset use/clear/show` exit-code rows: 2 client input, 1 unresolvable canonical profiles or validation failure |

ADR disposition (closeout policy): **no new ADR — ADR waiver**: the snapshot is an additive
state file inside the existing ADR 0037 home-state tree using its ownership and atomic-write
pattern; no new boundary, dependency, or external surface. The exit-code correction aligns
code with the already-governed onboarding contract rather than changing it.

## 6. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 snapshot store | `tests/KyberWeave.Tests/SquadCanonicalProfileStoreTests.cs` (new) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadCanonicalProfileStoreTests"` | Round-trip profiles+agent map; atomic overwrite; `TryRead(version)` exact hit; `TryReadLatest()` newest; corrupt/unreadable file counts as absent (no throw) | New tests fail on current main (type missing / not writing) | Same tests pass unchanged |
| T2 lifecycle write | `tests/KyberWeave.Tests/SquadModelPresetLifecycleTests.cs` (extend) | `... --filter "FullyQualifiedName~SquadModelPresetLifecycleTests"` | Committed install **and** update write the snapshot keyed by release version; dry-run writes nothing; post-commit write failure yields a warning, deployment succeeds | Snapshot assertions fail against current lifecycle | Same tests pass |
| T3 rewrite helper | `tests/KyberWeave.Tests/SquadPresetRewriterTests.cs` (new) | `... --filter "FullyQualifiedName~SquadPresetRewriterTests"` | `skills/…/SKILL.md` (incl. entry-point projections) passed through byte-identical; roster agent rewritten to its category's token; unknown identity passed through, no diff row; `inherit` removes `model:`; CRLF preserved; no `general` fallback | Tests fail: current loops rewrite skills / fall back to general | Same tests pass |
| T4 CLI verbs | `tests/KyberWeave.Tests/SquadPresetCliTests.cs` (extend) | `... --filter "FullyQualifiedName~SquadPresetCliTests"` | With checkout forced absent and a snapshot present: agents resolve per-category, skills untouched (use/clear, dry-run and apply); with neither checkout nor snapshot: exit 1, remedy message, zero files/state touched (dry-run and apply); invalid preset key → exit 1 with path/line; `show` resolves from newest snapshot, else exit 1 with remedy | Exit-1 and snapshot-path assertions fail on current main (exit 0, all-general) | Same tests pass; pre-existing checkout-backed cases stay green |
| T5 docs | n/a (document edits) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` and `-- docs drift .` | Onboarding, cli-command-surface, architecture state the shipped contract | Marked no-test: verification is the two documentation checks plus the §3.4 wording review | Both checks zero findings |

T5 is explicitly a no-test task; its verification is the documentation validation and drift
checks named above.

## 7. Tasks

Each task is RED→GREEN under test-first and ends with the worker's own `task-gate` run;
a failing gate goes back to the same worker.

| Task | Objective and exact files/symbols | Acceptance criteria | Depends on | Skills |
|---|---|---|---|---|
| T1 | Core snapshot model + store: new `src/KyberWeave.Core/Squad/Presets/SquadCanonicalProfileStore.cs` (record `SquadCanonicalProfiles`, store with atomic write, `TryRead(version)`, `TryReadLatest()`); new `SquadCanonicalProfileStoreTests.cs` | Test contract row T1 GREEN; build zero warnings; `task-gate` PASS | — | csharp-dev, test-dev |
| T2 | Lifecycle persistence: `SquadLifecycleService.InstallAsync`/`UpdateAsync` restructure source resolution, write snapshot post-commit (not dry-run), warning on write failure; extend `SquadModelPresetLifecycleTests` | Row T2 GREEN; existing preset-fail-closed throw preserved; `task-gate` PASS | T1 (consumes store) | csharp-dev, test-dev |
| T3 | CLI resolution + rewrite helper: `SquadCommandComposition.ResolveCanonicalProfiles` chain; new `Commands/Squad/Infrastructure/SquadPresetFileRewriter`; injectable resolver seam on the three preset commands' internal ctors; new `SquadPresetRewriterTests.cs` | Row T3 GREEN; `task-gate` PASS | T1 (consumes record/store) | csharp-dev, test-dev |
| T4 | Rewire verbs: `SquadPresetUseCommand`, `SquadPresetClearCommand`, `SquadPresetShowCommand` onto the chain + helper; remove null-source and `general` fallbacks; validator in `use`/`show`; extend `SquadPresetCliTests` | Row T4 GREEN incl. exit-1 cases; existing checkout-backed tests unchanged and green; `task-gate` PASS | T2, T3 (consumes snapshot writer + helper) | csharp-dev, test-dev |
| T5 | Docs: `docs/kyber-squad/onboarding.md` (resolution order, skills-never-rewritten, exit-1 contract, `squad update` migration), `docs/cli-command-surface.md` (preset exit-code rows), `docs/kyber-squad/architecture.md` §4 (profiles node + bullet) | `docs validate . --merge-ready` and `docs drift .` zero findings on touched scope; wording matches shipped behaviour | T4 (behaviour final) | docs-dev |

## 8. Dependency graph

```
T1 ──► T2 ──┐
   └──► T3 ─┴──► T4 ──► T5
```

T2 and T3 are independent of each other (Core lifecycle vs CLI helper) and may run
concurrently; T4 consumes both outputs; T5 follows final behaviour.

## 9. Risks and out-of-scope boundaries

**Risks**

- **Observable exit-code change:** dry-run/apply of `use`/`clear` now exit 1 where they
  previously exited 0 (with a wrong plan). Mitigated: this is the defect being fixed; the
  contract is documented in T5 and mirrors `show`'s existing exit-1.
- **KS-005 version skew:** snapshot keyed by the lock's `SquadVersion`, not the CLI version,
  so `--version`-pinned deployments read their own release's profiles; newest-snapshot
  fallback only serves `show`.
- **Validator accessibility:** `SquadModelPresetValidator` must be reachable from the CLI
  project (it is Core-public today per lifecycle usage); if not, T4 makes it public — an
  additive surface change only.
- **Path separators:** skills-segment matching normalizes `\` and `/` and compares
  case-insensitively; covered by T3 tests.
- **Global receipts:** rewrite uses `file.RelativePath` for discrimination and
  `ResolveOwnedFilePath` for physical location, exactly as both commands do today; unchanged.

**Out of scope:** `preset list`/`status` behaviour; embedding `models.yml` in the CLI;
implicit release download inside preset verbs; snapshot pruning; MCP surface; changes to
renderer output (no new `model-profile` frontmatter key is introduced — the roster snapshot
carries the mapping).

## 10. Verification gates, review, closeout

- Per task: the worker's `task-gate` over its changed files; RED→GREEN evidence in the task
  digest for rows T1-T4.
- During iteration: `scripts/fast-loop.sh` (not evidence, per ADR 0005).
- Before push/PR: the declared gate suite
  `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --out artifacts/gates.json`
  plus `docs validate . --merge-ready` and `docs drift .`.
- Whole-change review (`code-review` skill) at the end of the run, before PR.
- Closeout (`docs-dev`): harvest the Design harvest table into the named documents with
  `MAPPED:`/`IMPACT:`/`DOCS_REVIEW:` rows, record the ADR waiver from the Design harvest section, archive this plan
  in the finishing PR (`KW-DOC-LIFECYCLE-003`), and update this plan's index row.

**Authoring-harness disclosure (G3, established precedent):** this plan was authored in a
harness with no process-execution tool, and the delegated gate run was blocked by a
subagent depth limit, so `docs validate` and `docs drift` were **not run at authoring
time** — the conductor runs both before execution. Frontmatter and index-row shapes were
self-verified against validated precedent (the archived 2026-10-23 pi-renderer and
2026-10-25 docs-dev-harvest plans carry the identical `documentation-impact` /
`impact-components` / `development-mode` frontmatter shape; the ontology required-key row
for `plan` is `component`, satisfied by the cataloged `KyberSquad`).

## Closeout mapping

Every durable decision below is now recorded in canonical documentation. The permanent
`## Decisions` table (including the AP1 row) is preserved intact above and was not
edited during closeout. The Design harvest table is fully landed — verified in the working
tree before archiving.

| ID | Destination |
|---|---|
| D1 | Version-keyed canonical profile snapshot in the home state tree, checkout stays first choice → [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) §4 state topology (profiles node in the diagram plus the canonical-profile-snapshots bullet: schema, atomic write, read order, warning-on-write-failure, no pruning); [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) model-presets section (resolution-order and snapshot-persistence bullets) |
| D2 | Hard fail exit 1 with the `squad update` remedy when profiles are unresolvable → [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) (unresolvable-profiles-fail-fast and pre-snapshot-migration bullets, §9 exit-code table naming unresolvable canonical profiles); [`docs/cli-command-surface.md`](../../cli-command-surface.md) exit-code rows for `squad preset use/clear/show` |
| D3 | Skill files kept out of the rewrite by path discrimination → [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) (skills-are-never-rewritten bullet: `SKILL.md` final segment or `skills/` path segment, canonical and entry-point projections alike) |
| D4 | Only non-skill `.md` files whose category resolves are rewritten; no `general` fallback → [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) (skills bullet: unresolvable identities keep their bytes, no diff row); implementation in `src/KyberWeave.Cli/Commands/Squad/Infrastructure/SquadPresetFileRewriter.cs` shared by `use`/`clear` |
| D5 | Resolution order checkout → version snapshot → newest snapshot → null → [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) §4 bullet; [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) resolution bullet (`show` has no lock context: checkout → newest) |
| D6 | `use`/`show` strictly validate preset keys via `SquadModelPresetValidator` → [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) validation paragraph (now names `use`/`show` enforcing the check before rewrite/print, exit 1 with file path and line); [`docs/cli-command-surface.md`](../../cli-command-surface.md) exit-code rows |
| D7 | One file per Squad version, no pruning (`autonomous: low-confidence, no authoritative source`) → [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) §4 bullet ("never pruned"); [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) persistence bullet. Low-confidence provenance retained from the Decisions row; cheap to reverse later per that row |
| D8 | One shared CLI rewrite helper for `use`/`clear` → observable behaviour in [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) (skills-never-rewritten and unresolvable-identity bullets); implementation record is the shared helper's location in source (`SquadPresetFileRewriter.cs` consumed by both commands — no separate canonical-doc section, no surviving standalone invariant beyond the shared location itself) |
| D9 | Dry-run writes nothing; post-commit snapshot-write failure is a warning, never a failed deployment → [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) §4 bullet; [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) persistence bullet (dry-run writes nothing; rerun-`squad update` remedy) |
| AP1 | Approve-and-execute record (`autonomous: recommendation accepted`; autonomous provenance recorded per Decisions row, D7 noting the low-confidence exception) → this plan's Decisions table as the record, indexed by the live [plan inventory](../../plans/README.md) archived row |
| H1 | Architecture harvest (snapshot schema, atomic write, read order, warning, no pruning) → [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md) §4 |
| H2 | Onboarding harvest (resolution, skills, exit-1, validation, migration) → [`docs/kyber-squad/onboarding.md`](../../kyber-squad/onboarding.md) model-presets section |
| H3 | Exit-code aggregation harvest → [`docs/cli-command-surface.md`](../../cli-command-surface.md) preset exit-code rows |

**ADR waiver:** no new ADR — restated from the Design harvest section: the snapshot is an additive state file
inside the existing ADR 0037 home-state tree using its ownership and atomic-write
pattern; no new boundary, dependency, or external surface. The exit-code correction
aligns code with the already-governed onboarding contract rather than changing it.

**Documentation waiver:** none, and none needed. Behavioural facts are in
`onboarding.md` (resolution, skills, exit-1, validation, migration), state-topology
facts in `architecture.md` §4, exit codes in `cli-command-surface.md`; D8's record is
the shared helper's location in source plus the observable bullets above.

## Completion summary

- Implementation: T1–T5 complete test-first. Build Release: 0 warnings, 0 errors.
- Tests: preset-scope filter (`SquadPreset`, `SquadCanonical`, `SquadModelPreset`, `Preset`) 296/296 pass, including the new `SquadCanonicalProfileStoreTests` (T1), extended `SquadModelPresetLifecycleTests` (T2), new `SquadPresetRewriterTests` (T3), and extended `SquadPresetCliTests` (T4) with exit-1 and snapshot-path cases.
- Docs: `docs validate .` and `docs drift .` zero findings on the working tree; `docs validate . --merge-ready` passes after this archive (no open plan remains in `docs/plans/`).
- Review: whole-change `code-review` verdict REQUEST_CHANGES parked **only** on two pre-existing `dash/` gates (ts-typecheck TS2740 in `dash/src/mcp/tools.ts`; 2 Zod-assertion failures in `dash/src/mcp/*.test.ts`) — `dash/` byte-identical to HEAD (`git diff -- dash/` empty), unrelated to this C#/docs change. Parked as [`docs/todo/dash-mcp-zod-gates.md`](../../todo/dash-mcp-zod-gates.md), not fixed per scope.
- Autonomous provenance: D1–D6, D8–D9 + AP1 each `autonomous: recommendation accepted`; D7 `autonomous: low-confidence, no authoritative source` (snapshot retention / no pruning) — the one low-confidence decision, listed for the PR description.
