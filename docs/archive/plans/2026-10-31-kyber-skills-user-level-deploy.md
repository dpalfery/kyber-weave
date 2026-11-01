---
id: plans/2026-10-31-kyber-skills-user-level-deploy
title: Kyber-Squad ships the Kyber-Weave skills, so they deploy at the same level as the Squad skills
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-31
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad, Distribution, DocGraph, ContextHygiene]
---

# Kyber-Squad ships the Kyber-Weave skills, so they deploy at the same level as the Squad skills

**Status: Archived 2026-10-31; closeout not complete until `docs drift .` is verified (no CodeGraph index in this worktree).** Revision 1 executed (T0 to T2, then closeout) on branch `fix/kyber-skills-ship-with-squad`, draft pull request #494. Approve-and-execute recorded as D15; the human's request ("kyber skills should be deployed at the same level as the squad") is the scope. Closeout and mapping are in the Closeout section at the end.

**Branch:** a **new feature branch cut from `origin/main`** (`b25cb70` at planning time), suggested name `fix/kyber-skills-ship-with-squad`. The checkout this plan was written from (`fix/opencode-v2-capture`) is unrelated OpenCode work and must not carry this change.

## Problem

The human, verbatim, after being told where the skills live: "ok that is a bug. kyber skills sould be deployed at the same level as the squad. questions?"

What they were told, and what this plan re-verified:

- `kyber-weave squad install --global` deploys the 28 Squad skills from `products/kyber-squad/skills/` into every selected harness's user-level root. On the human's machine that is all 13 targets, including `~/.claude/skills/` (`~/.claude/skills/task-gate/SKILL.md` is present).
- The six Kyber-Weave skills under `.apm/skills/` — `kyber-docs-authoring`, `kyber-docs-integrity-review`, `kyber-docs-onboarding-and-assessment`, `kyber-weave-cli`, `kyber-weave-model-profile`, `spec-conductor` — are absent from `~/.claude/skills/`. They ship only through APM, by `kyber-weave docs init`, which is project-scoped (default target `agent-skills`, i.e. `<repo>/.agents/skills/`) and has no global option.

So a Claude Code user who installs the Squad globally never gets the Kyber-Weave skills at user level.

## Goal

Every `kyber-weave squad` deployment — install, update, status, doctor, uninstall, at global and project scope, on every target — carries the six Kyber-Weave skills beside the 28 Squad skills, exactly as it carries the Squad's own skills. For the human: after the next release and `kyber-weave update` (or `kyber-weave squad update --global`), `~/.claude/skills/kyber-docs-authoring/SKILL.md` and the other five sit next to `~/.claude/skills/task-gate/SKILL.md`, tracked by the Squad receipt.

`.apm/skills/` stays the single source. The APM package (`apm install dpalfery/kyber-weave`), `apm.yml`, and `kyber-weave docs init` are unchanged.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | What does "same level as the squad" mean? Three readings: (A) the same user-level folders; (B) the same mechanism, so Squad installs, updates, packs and tracks them; (C) `docs init` writes the per-harness project folders Squad uses. | **(B), which gives (A) as a consequence.** Squad itself deploys the six skills, so they land wherever Squad lands: the same scope (global or project), the same 13 targets, the same receipt, `squad update` and `squad uninstall`. | The human was told "squad skills are in `~/.claude/skills`, these six are not" and called that the bug: the target is the squad's own level. (A) alone, via a new `docs init --global`, gives the folders but a second mechanism: a runtime APM dependency, a package resolved from GitHub `main` rather than the version-locked Squad release (KS-005), no receipt, no update or uninstall, and a new public CLI flag. (C) is project-only and puts nothing in `~/.claude/skills`, so it does not fix the reported case. | autonomous: recommendation accepted (architect, 2026-10-31; sources: the human's words and the context relayed by the conductor; `docs/kyber-squad/requirements.md` KS-004, KS-005) |
| D1 | Where do the six skills live? | **They stay in `.apm/skills/`, their only source.** Squad adds them when it builds its source tree: `squad pack` (the release archives) and the checkout release source copy every `.apm/skills/<name>/**` into the Squad tree's `skills/<name>/**`. Nothing is moved or duplicated. | Moving them into `products/kyber-squad/skills/` would break the APM package: `apm install dpalfery/kyber-weave` reads `.apm/` (`includes: auto`), `docs init` deploys from it, and #285/#289 pin the package shape (`ApmManifestGuardTests`: no self-dependency, `targets: [agent-skills]`). A mirrored copy with a drift test means two sources. A symlink breaks on Windows checkouts and under the loader's path-discovery guard. Only composition keeps one source and leaves APM consumers untouched. | autonomous: recommendation accepted (architect, 2026-10-31; sources: `apm.yml` comments, issues #285 and #289, `docs/docgraph/onboarding.md` "APM is an expected dependency") |
| D2 | Which skills ship with Squad: only the three `kyber-docs-*`, an explicit list, or every `.apm/skills` skill? | **Every directory under `.apm/skills/` that holds a `SKILL.md`, discovered** — the six today. | The human said "kyber skills", not "docs skills". `kyber-weave-model-profile` was added as a Squad feature (commit 54ec5a4) yet placed in `.apm/skills`; `kyber-weave-cli` and `spec-conductor` are Kyber-Weave operational skills; nothing documents why those three sit outside Squad. Discovery means no second list to drift. The tests name the six explicitly, so a silent removal still fails. | autonomous: recommendation accepted (architect, 2026-10-31; source: the investigator's verdict, `products/kyber-squad/README.md:163-165`, which names only the three `kyber-docs-*` skills) |
| D3 | Does `kyber-weave docs init` change: a new flag, a new default target, or a skip? | **No change.** No flag, no default, no behaviour change. Its APM deploy stays the path for repositories that use Kyber-Docs without Kyber-Squad. | This keeps the public `docs init` contract intact and removes the tripwire about a CLI flag or default change. The overlap in a project that uses both is handled by documentation (D10). | autonomous: recommendation accepted (architect, 2026-10-31; source: `src/KyberWeave.Cli/Commands/Docs/DocsInitSettings.cs`, `docs/docgraph/onboarding.md`) |
| D4 | Does `products/kyber-squad/bundles/full.yml` list the six? | **No.** The bundle stays the Squad canonical roster of 28, validated against `products/kyber-squad/skills/`. The six join the built tree at composition, not in the bundle. | `SquadSourceValidator` only checks that bundle names exist as skills; renderers render every loaded skill, not the bundle list (no use of `Bundle.SkillNames` outside validation). Listing the six in `full.yml` would make every products-only load fail with "unknown skill": `squad pack`'s own validation, `TestSquadCorpus` (116 callers) and `NoKyberDocsSkillLivesInSquad`. KS-001 (28 canonical skills under `products/kyber-squad/`) stays true. | autonomous: recommendation accepted (architect, 2026-10-31; source: `SquadSourceValidator.cs:150-180`, `SquadSourceLoader.LoadSkills`) |
| D5 | What happens when an `.apm/skills` name equals a Squad skill or agent name? | **Fail closed.** `squad pack` exits 1, writes no archive, and names the clashing skill and both paths. A name matches ignoring letter case. The checkout release source fails the same way. Neither side silently wins. *Extended by review round 2026-11-01.* | Silent shadowing is the worst outcome: one skill would vanish from every harness without a signal. An agent-name match would change the Squad's skill/agent collision lowering, which the README pins to exactly eleven names. The check costs one directory lookup. | autonomous: recommendation accepted (architect, 2026-10-31; source: `products/kyber-squad/README.md` §4 "Namespace Collision and Fallback Lowering") |
| D6 | Does the Agent Plugins archive (`kyber-squad-plugin-<version>.zip`) carry them too? | **Yes**, in both archives. | KS-006 defines the plugins archive as "the complete recursive portable skill tree". The six are portable skills. "Same level" includes every channel Squad ships skills through. | autonomous: recommendation accepted (architect, 2026-10-31; source: `docs/kyber-squad/requirements.md` KS-006) |
| D7 | How does `KyberDocsSkillSetTests.NoKyberDocsSkillLivesInSquad` change? Its comment says "never through Squad". | **Assertions unchanged; the XML doc comment is corrected.** The test still asserts that no `kyber-docs-*` skill lives in `products/kyber-squad/`, which is now the single-source guard. The new test class adds the same guard for every `.apm/skills` name (T1 case h). | No assertion is weakened, so this is not a Test-contract weakening. Only the stated reason changes, from "never deployed by Squad" to "never duplicated into the Squad source tree; Squad adds it when it builds the tree". | autonomous: recommendation accepted (architect, 2026-10-31; source: `tests/KyberWeave.Tests/KyberDocsSkillSetTests.cs:85-101`) |
| D8 | How is the recorded decision at `products/kyber-squad/README.md:163-165` ("intentionally managed separately … not part of Kyber-Squad") amended? | **Amend it in place, and record the reversal in a new ADR** (the next free number, 0049 at planning time), harvested by `docs-dev` at closeout. | The ADR bar is met: the change reverses a recorded product boundary, had viable rejected alternatives (move, mirror, symlink, `docs init --global`, per-harness `docs init`), and constrains future work (every `.apm/skills` skill ships with Squad; the fail-closed name rule). No existing ADR covers the Kyber-Docs/Squad split (ADR index 0001–0048 checked on `origin/main`). | autonomous: recommendation accepted (architect, 2026-10-31; source: `docs/adr/README.md` on `origin/main`; `architecture-decision-record` skill criteria) |
| D9 | Is the "29 canonical skills" versus 28 count mismatch fixed here? | **Yes, only in the sections this change already edits.** `docs/kyber-squad/onboarding.md` (intro, line 31; Packaging, line 1100), `docs/kyber-squad/architecture.md` §1 (lines 147–149), `docs/distribution.md` "Squad release assets and packaging" (lines 102–103, 122) and `docs/context-hygiene/skills.md` §"Deployment control plane" (lines 145, 154) say 29 skills and 87 resources. `bundles/full.yml`, `products/kyber-squad/README.md` and KS-001 say 28 and 88 (116 files). | All five places are edited anyway to describe the composed tree, so the fix costs nothing extra. Closeout counts the files on disk before writing a number: expected 28 Squad `SKILL.md` + 88 resources = 116 files in the canonical tree, plus 6 `SKILL.md` + 3 resources from `.apm/skills`, so 34 skills and 125 skill-tree files in each archive. | autonomous: recommendation accepted (architect, 2026-10-31; source: the grep in Investigation findings F6) |
| D10 | A project that uses Squad at project scope and also runs `docs init` gets two writers. With the Antigravity target, both write `.agents/skills/<name>`. | **Document it; no code change.** Squad onboarding and DocGraph onboarding say: where Kyber-Squad is deployed, it already ships these skills, so run `kyber-weave docs init --no-skill`. | A `docs init` that auto-skips when a Squad receipt covers the project would be a CLI behaviour change; it is out of scope and **not** filed as a todo, because no owner has accepted it. The collision fails safe: Squad's transaction refuses to overwrite an unmanaged file and reports it, and `squad doctor` reports a managed file changed by APM. | autonomous: low-confidence, no authoritative source (architect, 2026-10-31). Cheap to reverse: a later plan can add the auto-skip. |
| D11 | Where does the composition logic live, and how does each caller find `.apm/skills`? | **One Core type**, `SquadCompanionSkills` in `src/KyberWeave.Core/Squad/Packaging/`, used by `SquadPacker` (both archive formats) and by the checkout release source. It resolves `<repo>/.apm/skills` from a Squad source root shaped `<repo>/products/kyber-squad`, the layout `SquadPackSourceLocator` and `TryFindLocalProductSquadRoot` already require. `SquadPacker` takes the root as an explicit optional parameter, so the existing direct callers and tests are unchanged. | One function means pack and checkout cannot disagree. The layout assumption is not new. Exact signatures are left to `csharp-dev` within the acceptance criteria in T2. | autonomous: low-confidence, no authoritative source (architect, 2026-10-31). Local to T2, cheap to reverse. |
| D12 | The checkout release source (`LocalCheckoutSquadReleaseSource`) is a `private` nested class, so a RED test cannot reach it. | **T0, a behaviour-preserving refactor, moves it to `internal sealed class` in `src/KyberWeave.Cli/Commands/Squad/Infrastructure/LocalCheckoutSquadReleaseSource.cs` before T1.** The single-argument constructor `(string sourceRoot)` is kept, so T1 compiles against today's behaviour and fails for the missing skills, not for a missing type. | A compile-error RED would stop every other T1 case from running. Tests already reach `internal` CLI members (for example `SquadInstallCommand`'s internal constructor), so no new `InternalsVisibleTo` is needed. | autonomous: recommendation accepted (architect, 2026-10-31; source: `docs/standards/test/README.md` "Internals are in scope") |
| D13 | When does the human actually get the skills? | **After a release that contains this change, followed by `kyber-weave update` or `kyber-weave squad update --global`.** Installed CLIs fetch the version-locked Squad archive from GitHub Releases. A CLI run from a checkout gets them through the checkout source as soon as T2 lands. | KS-005 version lockstep. Stated so nobody expects the fix from a merge alone. The human's global receipt covers all 13 targets, so the six land in all 13 user-level roots. No target writes `~/.agents/skills` globally, so the stale hand-installed copies there (retired names) do not collide. | autonomous: recommendation accepted (architect, 2026-10-31; source: `SquadCommandComposition.ResolveDefaultReleaseSource`, `SquadGlobalRoots.cs`, `~/.kyber-weave/squad/global/squad.receipt.json`) |
| D14 | Development mode and autonomy | `development-mode: test-first`; `autonomy: autonomous`, both from the conductor's packet; they match the Config Reg default `<autonomy>: autonomous`. | — | autonomous: recommendation accepted (packet, 2026-10-31) |
| D15 | Approve and execute this plan? | **Yes.** Execute T0 → T1 → T2 → gates → whole-change review → T3 on a new branch cut from `origin/main`; deliver as a draft pull request; never merge. | The human reported the gap as a bug and named the target ("kyber skills should be deployed at the same level as the squad"); D0–D14 are decided and no open question or hard block remains. The change reverses a recorded product boundary (D8), so the ADR and the in-place README amendment are part of the delivery. | autonomous: recommendation accepted (conductor, 2026-10-31) |
| D16 | What happens when a companion skill's directory, or its `SKILL.md`, is a symbolic link? | **Fail closed.** A link at any of these makes `squad pack` exit 1 with a `SquadSourceValidationException` that names the link, and writes no archive: the `.apm` or `.apm/skills` root, a companion skill's directory, its `SKILL.md`, or any file or folder inside a companion skill, at any depth. The checkout release source throws the same way. *Extended by review round 2026-11-01.* | A skipped skill would ship a release without a skill the operator authored, with no signal. A linked skill could pull content in from outside the tree. This is the D5 rule applied to links: refuse, do not guess. | autonomous: recommendation accepted (conductor, 2026-10-31; source: whole-change review finding 3) |
| D17 | Must an unreferenced symbolic link inside `products/kyber-squad/` that points outside the repository be closed in this change? | **No. Known gap, out of scope.** The pack walk follows links, and the loader checks only the files it reads, so such a link is still packed. The gap predates this change and is not introduced by it. A follow-up is proposed to the conductor; no todo is filed here. | Closing it changes the Squad source walk and loader, which this plan does not touch. Recorded as a known limitation in the Squad architecture (section 1), the Squad onboarding Packaging section, and ADR 0049. | autonomous: low-confidence, no authoritative source (conductor, 2026-10-31) |

No open questions remain. No hard block.

## Investigation findings

Discovery: codegraph 4, docs 7 (`docs_status` 1, `docs_explore` 3, `docs_for_symbol` 2, `agent_coding_standards` 1, plus the `docs_validate` and `docs_drift` gates after each save), direct reads 14. Five of those reads are indexed code, each for a reason: `KyberDocsSkillSetTests.cs` (codegraph returned the call path, not the body); `SquadPacker.cs` (not in codegraph's output; read to confirm what `CollectApmEntries` and `CollectPluginsEntries` enumerate); `SquadSourceValidator.cs` 135–195 (the bundle check body was not returned); `SquadPackAndReleaseTests.cs` 1–345 (existing archive assertions that T2 must keep green); `Fakes/CorpusSquadReleaseSource.cs` 1–60 (the fake's shape, for T1(e)). The other nine are not indexed code: `AGENTS.md`, the plans index, one archived plan (format), `apm.yml`, `bundles/full.yml`, `toolchain.yml`, `release.yml` 555–595, and doc excerpts of `products/kyber-squad/README.md`, `docs/kyber-squad/onboarding.md` and `docs/context-hygiene/skills.md`.

**origin/main verification (self-gathered).** This harness gave the architect no shell, so `git fetch` / `git show origin/main:<path>` could not run. Instead: the GitHub API reports `main` at `b25cb70` (2026-11-01T00:21Z), equal to the local `refs/remotes/origin/main`. Raw `main` copies of `apm.yml`, `bundles/full.yml`, `SquadPacker.cs`, `DocsInitSettings.cs`, `KyberDocsSkillSetTests.cs`, `products/kyber-squad/README.md`, `docs/adr/README.md` and the `.apm/skills` listing were fetched and match the working-tree reads below. The working branch's own commits touch only `dash/` and OpenCode files.

### F1 — How Squad gets and deploys skills

- `SquadSourceLoader.LoadSkills` (`SquadSourceLoader.cs:919-958`) loads every `skills/<name>/SKILL.md` under the Squad source root. Renderers render the loaded `source.Skills`; the bundle is a validated roster (`SquadSourceValidator.cs:170-180`), not a filter.
- Installed CLIs get the source from the release archive (`GitHubSquadReleaseSource`, `kyber-squad-<version>.zip`), extract it, and `SquadLifecycleService.TryResolveSquadSource` loads it. A CLI whose binary sits inside a checkout uses `LocalCheckoutSquadReleaseSource` (`SquadCommandComposition.cs:965-1006`), which copies `products/kyber-squad/**` verbatim (`ResolveDefaultReleaseSource`, `:323-350`).
- `SquadPacker.CollectApmEntries` (`SquadPacker.cs:100-136`) zips everything under the Squad source root except `.git/`, `migration/` and the target trees. `CollectPluginsEntries` (`:138-175`) zips `plugin.json`, `mcp.json` and `skills/**`. Neither file mentions `.apm` (confirmed on `main`). Release CI runs `squad pack --format all` from the repository root (`release.yml:567-572`; `scripts/release-local.sh:336`), so the pack command covers the release.
- `SquadPackCommand.Execute` validates with `SquadSourceLoader.Load(sourcePath)` on the products tree only (`SquadPackCommand.cs:58`).

### F2 — How the six skills ship today

- `.apm/skills/` holds exactly six skills (confirmed on `main` through the contents API): 6 `SKILL.md` and 3 resources (`kyber-docs-authoring/references/{retrofit,rules}.md`, `kyber-docs-onboarding-and-assessment/references/templates.md`).
- Their frontmatter keys are `name`, `description`, `license`, `metadata`, all inside the Squad loader's allow-list (`SquadSourceLoader.ParseSkill`, `:964-968`). Whether they pass the Squad loader's resource-closure rules and all 13 renderers is not yet proven; T1(c) and T1(d) prove it.
- `docs init` runs `apm install dpalfery/kyber-weave --target <t>` in the project (`DocsInitCommand.cs`), default `agent-skills`, no `--global` (`DocsInitSettings.cs`, confirmed on `main`).

### F3 — Constraints that must hold

- `apm.yml`: `targets: [agent-skills]` pinned, `dependencies.apm: []` (#285, #289, `ApmManifestGuardTests`). This plan does not touch `apm.yml` or `.apm/`.
- `products/kyber-squad/README.md:163-165` records the split as intentional; `KyberDocsSkillSetTests.cs:85-101` says "never through Squad" in its comment, while its assertions only forbid a copy under `products/kyber-squad/` (D7).
- KS-001 (28 canonical skills under `products/kyber-squad/`) stays true. KS-006 (pack contents) must be amended.
- `docs/distribution.md` "Verifying a release locally": changes to the Squad release path must run `./scripts/update-loop.sh`.

### F4 — The human's machine

- `~/.claude/skills/task-gate/SKILL.md` is present; none of the six are present. The global receipt covers all 13 targets.
- Global roots (`SquadGlobalRoots.cs`) never resolve to `~/.agents`, so the stale `~/.agents/skills` copies left by a hand-run `apm install -g` (retired `kyber-weave-docs`, `doc-corpus-integrity-review`, plus `spec-conductor`) do not collide. Claude Code does not read that folder. Cleaning it up is the human's choice and outside this plan.

### F5 — Tests affected

- Existing pack tests run on stub repositories (`QualifiedSquadRepoFixture`, `SquadRepoFixture`) with no `.apm/skills`, or call `SquadPacker.PackApm(productRoot, …)` with three arguments. With an optional companion root, they keep passing unchanged. That includes the hard-coded 28-skill roster assertion (`SquadPackAndReleaseTests.cs:161-166`), which uses a stub repository.
- `TestSquadCorpus` (116 callers) and every renderer contract test load `products/kyber-squad` only, so their counts do not move (D4).
- No existing test detects the missing user-level deploy.

### F6 — Count mismatch (D9)

`grep "29 canonical"` finds `docs/kyber-squad/onboarding.md:31`, `:1100`, `docs/context-hygiene/skills.md:145`, plus the 29/87 form in `docs/kyber-squad/architecture.md:147-149` and `docs/distribution.md:102-103`, `:122`. `products/kyber-squad/README.md:4`, `:62`, `:127` and `docs/kyber-squad/requirements.md` KS-001 say 28 and 88.

## Test contract (test-first)

Test surface: new file `tests/KyberWeave.Tests/Squad/SquadKyberWeaveSkillsTests.cs`, class `KyberWeave.Tests.Squad.SquadKyberWeaveSkillsTests`. The six names are pinned explicitly, and the "every `.apm/skills` skill" rule is checked by enumerating `.apm/skills/*/SKILL.md` at test time.

Runner (from the repository root, through the watchdog):

```bash
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "KyberWeave.Tests.Squad.SquadKyberWeaveSkillsTests" -reporter quiet
```

RED proofs run on a clean base (T0 applied, T2 not), in an isolated worktree or throwaway copy, never with `git stash`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1(a) | `SquadKyberWeaveSkillsTests` | class filter above | `squad pack --format apm` run from the real repository root (`SquadPackCommand`, `workingDirectory` = repository root, `--out` = temp dir) writes an archive containing `skills/<name>/SKILL.md` and every file under `.apm/skills/<name>/` at `skills/<name>/<rel>`, with LF-normalized bytes equal to the source, for each of the six named skills and for every `.apm/skills/*` skill; the 28 Squad skills are still present; the archive's `SKILL.md` count equals 28 plus the number of `.apm/skills` skills. | Fails: `skills/kyber-docs-authoring/SKILL.md` (and the other five) absent from the archive. | Same test passes unchanged. |
| T1(b) | same | same | Same as (a) for `--format plugins` (`kyber-squad-plugin-*.zip`); still no `agents/` entries. | Fails: the six absent. | Passes unchanged. |
| T1(c) | same | same | The APM archive from (a), extracted to a temp dir, loads with `SquadSourceLoader.Load`; `source.Skills` contains the six by name, and its count equals 28 plus the `.apm/skills` count. This proves the install-time loader accepts them (frontmatter allow-list, resource closure, no duplicates, reserved prefixes). | Fails: the six absent from `source.Skills`. | Passes unchanged. |
| T1(d) | same | same | That loaded source renders without error on all 13 `SquadTarget`s at global scope, and each target's render contains a principal file for each of the six. For Claude the path is exactly `skills/<name>/SKILL.md` relative to the Claude global root; for other targets, the target's own skill path for `<name>`. | Fails: no rendered file for the six on any target. | Passes unchanged. |
| T1(e) | same | same | User-level lifecycle: `squad install --global --target claude --yes --no-capture-plugin` through `SquadInstallCommand`'s internal constructor, with `FakeUserPaths` and a global-root resolver under a temp home, and a release source that extracts the APM archive from (a). Afterwards `<home-claude-root>/skills/<name>/SKILL.md` exists for all six beside `skills/task-gate/SKILL.md`; the global receipt lists them; a following `squad update --global` changes nothing; `squad uninstall --global --yes` removes them with the Squad skills. | Fails: the six files absent after install. | Passes unchanged. |
| T1(f) | same | same | Fail-closed names (D5): in a temp repository (sln marker, real `products/kyber-squad` copied, `.apm/skills/task-gate/SKILL.md` written), `squad pack --format all` exits 1, leaves no `kyber-squad-*.zip` in `--out`, and the output names `task-gate` and both source paths. The same holds for an `.apm/skills` name equal to an agent (for example `architect`). | Fails: pack exits 0 today. | Passes unchanged. |
| T1(g) | same | same | Checkout parity: `new LocalCheckoutSquadReleaseSource(<temp repo>/products/kyber-squad)` with `<temp repo>/.apm/skills/<valid skill>/SKILL.md` present extracts `skills/<valid skill>/SKILL.md` into the destination and lists it in `SquadReleaseResult.ExtractedFiles`. A clashing name makes the extraction fail with the same diagnostic as (f), and nothing partial is reported as success. | Fails: `skills/<valid skill>/` absent from the destination (the T0 type compiles; the behaviour is missing). | Passes unchanged. |
| T1(h) | same | same | Single-source guard (D1, D7): for every `.apm/skills/<name>`, `products/kyber-squad/skills/<name>` does not exist, and the products-only `SquadSourceLoader.Load` carries no skill of that name. | **Characterization, declared:** passes at RED by design and pins the invariant (same pattern as `NoKyberDocsSkillLivesInSquad`). No failing run required. | Still passes. |
| T0 | — (refactor) | `scripts/proc-guard.sh run -- dotnet build KyberWeave.sln -c Release`, then the direct runner with `-class "KyberWeave.Tests.SquadCliCommandTests"` | **No new test.** A behaviour-preserving move; the existing Squad CLI tests are its verification. | n/a — declared no-test task | Build passes with warnings as errors; `SquadCliCommandTests` passes unchanged. |
| T1 (comment edit) | `tests/KyberWeave.Tests/KyberDocsSkillSetTests.cs` | `-class "KyberWeave.Tests.KyberDocsSkillSetTests"` | XML doc comment of `NoKyberDocsSkillLivesInSquad` corrected (D7); **assertions untouched**. | n/a — comment only, declared | Class still passes. |
| T3 | — (docs) | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `-- docs drift .` | **No test.** Documentation, ADR and archive. | n/a — declared no-test task | 0 findings on both. |

Changing any row above, including weakening an assertion to reach green, is a scope change: the plan returns to Draft for re-approval.

## Tasks

| ID | Objective | Files / symbols | Acceptance criteria | Depends on | Required skills |
|---|---|---|---|---|---|
| T0 | Make the checkout release source testable without changing behaviour (D12). | Move `LocalCheckoutSquadReleaseSource` out of `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs` (`:965-1006`) into `src/KyberWeave.Cli/Commands/Squad/Infrastructure/LocalCheckoutSquadReleaseSource.cs` as `internal sealed class` in namespace `KyberWeave.Cli.Commands.Squad.Infrastructure`. Keep the `(string sourceRoot)` constructor and the method body byte-for-byte. `ResolveDefaultReleaseSource` keeps constructing it. | Build clean (`TreatWarningsAsErrors`, `AnalysisMode=all`); `SquadCliCommandTests` green; no behaviour change; `task-gate` passes. | — | `csharp-dev`, `task-gate` |
| T1 | Write the RED contract (T1 a–h) and correct the D7 comment. | New `tests/KyberWeave.Tests/Squad/SquadKyberWeaveSkillsTests.cs`; a local fixture that copies the real `products/kyber-squad` and, where a case needs it, `.apm/skills` into a temp repository with a `KyberWeave.sln` marker; a small archive-extracting `ISquadReleaseSource` fake for (e) in the test file or `tests/KyberWeave.Tests/Fakes/`. Edit only the XML comment of `KyberDocsSkillSetTests.NoKyberDocsSkillLivesInSquad`. | Cases (a)–(g) fail on the clean base for the reason stated in the contract, recorded verbatim (first failing assertion per case); (h) passes and is labelled characterization; no existing test edited apart from that comment; the six names are hard-coded; real-repository cases resolve the root with `KyberWeaveTestPaths.ToolRoot`; temp directories are cleaned up; no real home directory is touched. `task-gate` passes on the test files. | T0 | `test-dev`, `task-gate` |
| T2 | GREEN: Squad builds its tree from `products/kyber-squad` plus `.apm/skills` in pack and in the checkout source. | New `src/KyberWeave.Core/Squad/Packaging/SquadCompanionSkills.cs` (D11): resolve the companion root from a Squad source root shaped `<repo>/products/kyber-squad` (or none); enumerate `<root>/<name>/**` for each `<name>` holding `SKILL.md`; map to `skills/<name>/<rel>`; fail closed on a name equal to `products/kyber-squad/skills/<name>` or `agents/<name>.md` (D5). `SquadPacker.PackApm` / `PackPlugins` / `PackAll`: optional companion-root input; the entries join the existing collection before sorting and response-preferences filling. `SquadPackCommand.Execute`: pass the root resolved from the working directory; validate the **composed** tree (for example, stage it and run `SquadSourceLoader.Load`), and on any failure exit 1 with the diagnostic and leave no archive. `LocalCheckoutSquadReleaseSource.DownloadAndExtractAsync`: copy the companion skills into `skills/` and list them in `ExtractedFiles`, using the same type. | T1 (a)–(h) pass with no assertion edited; the full suite stays green, including `SquadPackAndReleaseTests`, `SquadCliCommandTests`, `SquadPackResponsePreferencesTests`, `PullRequestSkillConsolidationTests`, `KyberDocsSkillSetTests`, `ApmManifestGuardTests`; archives stay deterministic (two packs hash-equal); no change to `apm.yml`, `.apm/`, `bundles/full.yml`, `DocsInitCommand` or `DocsInitSettings`; `task-gate` passes. | T1 | `csharp-dev`, `task-gate` |
| T3 | `docs-dev` closeout: harvest, ADR, archive. | See Design harvest H1–H9 and Documentation impact. Create the ADR (next free number) and its index row; amend `products/kyber-squad/README.md:163-165`; fix the counts per D9 after counting files on disk; archive this plan with `git mv` to `docs/archive/plans/`, move its index row to **Archived Plans** with the closeout mapping. | Every Design-harvest row mapped (`MAPPED:` / `IMPACT:` / `DOCS_REVIEW:`); `docs validate . --merge-ready` and `docs drift .` at 0 findings after the archive; the plan carries a closeout mapping for D0–D14 and H1–H9. | T2, review | `app-docs-standard`, `architecture-decision-record` |

### Dependency graph

```mermaid
flowchart LR
    T0["T0 extract checkout source (csharp-dev)"] --> T1["T1 RED contract (test-dev)"]
    T1 --> T2["T2 GREEN composition (csharp-dev)"]
    T2 --> G["Gates G1-G5"]
    G --> R["Whole-change review council (code-reviewer)"]
    R --> T3["T3 docs-dev closeout"]
```

T0 is small and goes first only because T1(g) must compile against its seam. T1 and T2 are separate workers, so the RED and GREEN roles stay apart (adversarial RED/GREEN boundary).

## Verification gates

- **G1** — full suite: `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`.
- **G2** — `dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json`.
- **G3** — `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `-- docs drift .`, 0 findings (after T3's archive).
- **G4** — local release loop, required for Squad release-path changes (`docs/distribution.md#verifying-a-release-locally`): `./scripts/update-loop.sh --no-kyberdash --keep`. Then, in the kept sandbox: the published `kyber-squad-<version>.zip` holds 34 `skills/*/SKILL.md` (28 plus 6); the sandbox's Claude global root holds `skills/<name>/SKILL.md` for the six beside `skills/task-gate/SKILL.md`; the sandbox global receipt lists them. If the loop's `squad install` case does not select `claude`, run `squad install --global --target claude --yes --no-capture-plugin` with the sandbox binary, sandbox `HOME` and the loop's `KYBER_WEAVE_RELEASE_ORIGIN`. Never against the real home.
- **G5** — Linux: the pull request's CI (ubuntu) runs G1 and the gates. If the conductor has a Linux host, G4 runs there too; if not, the closeout says G4 ran on macOS only.
- **Not live-verified by this plan:** the human's own `~/.claude/skills`. That happens after the release, with `kyber-weave update` (D13).

## Review

One whole-change `code-reviewer` council after the draft pull request is pushed. Lenses to stress: the D5 fail-closed path (no partial archive, no partial extraction reported as success), determinism of both archives, path handling on Windows (`Path` APIs, LF normalization of companion files), and that no assertion in an existing test was weakened.

## Risks

- **R1** — A user with an unmanaged copy at a new path (a hand copy in `~/.claude/skills/kyber-docs-authoring`, say) sees `squad update` refuse to overwrite it: the transaction's existing conflict preservation. Remedy: remove the copy, or `--adopt` if it is byte-identical. Documented in Squad onboarding (H2).
- **R2** — Squad at project scope with the Antigravity target, plus `docs init` with the default `agent-skills` target, write the same `.agents/skills/<name>` (D10). Documented; fails safe.
- **R3** — Version skew: `docs init` installs from GitHub `main` through APM, while Squad installs the version-locked release, so one project can carry two versions of a skill at different levels. Documented in the ADR consequences.
- **R4** — Discovery (D2) means any skill later added to `.apm/skills` ships with Squad automatically. Mitigated by the composed-tree validation in pack (T2) and by T1(c)/(d); stated in the ADR.
- **R5** — A harness that reads both user and project skill folders may list a name twice when Squad is global and `docs init` targets that harness in a project. Low impact; documented with R2.

## Out of scope

- Any change to `kyber-weave docs init` (flags, defaults, auto-skip), `apm.yml`, `.apm/` contents or the APM package shape.
- Moving, mirroring or symlinking the six skills into `products/kyber-squad/`.
- Adding the six to `bundles/full.yml` or to the skill-resource dispositions audit, whose scope stays the Squad canonical tree.
- New assertions in `scripts/update-loop.sh`.
- The CI "Skill and docs gate" (`docs/distribution.md:579`), which validates five of the six `.apm` skills and skips `spec-conductor`. Not changed or filed here; the composed-tree validation in T2 now covers `spec-conductor` at pack time.
- Cleaning the stale `~/.agents/skills` copies on the human's machine.

## Design harvest

Scale: `extends-component`; components KyberSquad, Distribution, DocGraph, ContextHygiene.

| ID | Item | Target component | Target doc type | Destination |
|---|---|---|---|---|
| H1 | The Squad source tree is `products/kyber-squad` plus every `.apm/skills` skill, built at pack and in the checkout source; fail-closed name rule; bundle stays the 28-skill roster; corrected counts | KyberSquad | architecture | `docs/kyber-squad/architecture.md` §1 "Canonical Source Normalization and IR Construction" |
| H2 | What `squad install` / `update` deploy (28 Squad + the Kyber-Weave skills, same scope and targets, same receipt); `docs init --no-skill` where Squad is deployed; R1 remedy; Packaging section contents and counts | KyberSquad | onboarding | `docs/kyber-squad/onboarding.md` intro, Packaging (`squad pack`), and the install/update workflow |
| H3 | KS-006 pack contents amended; KS-001 unchanged | KyberSquad | requirements | `docs/kyber-squad/requirements.md` KS-006 |
| H4 | The recorded split replaced: authored once in `.apm/skills/`, shipped by both the Kyber-Docs APM package and Kyber-Squad | KyberSquad | reference (product README) | `products/kyber-squad/README.md:163-165` |
| H5 | Release asset contents (both archives) and counts | Distribution | reference | `docs/distribution.md` "Squad release assets and packaging" |
| H6 | `docs init`'s APM deploy stays for repositories without Squad; Squad already ships these skills; `--no-skill` where Squad is deployed | DocGraph | onboarding | `docs/docgraph/onboarding.md` "APM is an expected dependency" |
| H7 | The authoring skill ships through APM **and** Kyber-Squad; corrected counts | ContextHygiene | reference | `docs/context-hygiene/skills.md` (lines 133–139 and §"Deployment control plane with Kyber-Squad") |
| H8 | Retired-skills and APM notes: `kyber-docs-onboarding-and-assessment` is deployed by `docs init` and by `squad install`/`update` | Distribution | runbook | `docs/install.md` "Retired skills" (line 497 onward) and the APM section (line 576) |
| H9 | The decision itself: D0, D1, D2, D4, D5, D10, R3, R4, the rejected alternatives, and the superseded README statement | KyberSquad | ADR | New ADR (next free number, 0049 at planning) plus its `docs/adr/README.md` row; `decided-by` added to `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md` |

## Documentation impact

Every row in Design harvest, plus:

- `docs/cli-command-surface.md`: **no change**. No flag or exit code changes (checked: it does not state the skill roster).
- `docs/code-review/architecture.md:549` and `docs/distribution.md:579` (APM skill gates): **no change**; the gates still apply to `.apm/skills`.
- `docs/kyber-squad/skill-resource-dispositions.md`: **no change** (scope is the Squad canonical tree; see Out of scope).

## docs-dev closeout

T3 runs after the review. Closeout migrates H1–H9, maps D0–D14 to their destinations, creates the ADR (no ADR waiver: D8), runs `docs validate . --merge-ready` and `docs drift .`, archives this plan to `docs/archive/plans/` with `git mv`, moves its index row to **Archived Plans**, and ends in `STATUS: CLOSEOUT_COMPLETE`.

## Closeout

Closed 2026-10-31 by docs-dev on branch `fix/kyber-skills-ship-with-squad` (draft pull request #494, head `90167b6`). The documentation changes are left uncommitted for the conductor.

### Acceptance check (T3)

- Every Design-harvest row H1 to H9 is mapped below. Each row names its destination, documentation-impact, impact components, last-reviewed date, and status.
- Decisions D0 to D17 are mapped below. D12, D14 and D15 carry documentation waivers, each for a transient or test-seam reason with no surviving invariant. No ADR waiver is used anywhere in this plan (D8).
- ADR 0049 was created at the next free number. On `origin/main` (`f5322ff`), ADR 0048 was the last record and 0049 was free. Its row is in the ADR index.
- `products/kyber-squad/README.md` lines 163 to 165 are amended in place.
- Counts were fixed in the sections this change edits (D9), after counting files on disk. The canonical Squad tree holds 28 `SKILL.md` files and 88 resources (116 files). The `.apm/skills/` tree holds 6 `SKILL.md` files and 3 resources (9 files). Each release archive therefore holds 34 skills and 125 skill-tree files (34 `SKILL.md` and 91 resources).
- Behaviour was checked against the code as built, not the planned text: `SquadCompanionSkills.cs`, `SquadPacker.cs`, `SquadPackCommand.cs` and `LocalCheckoutSquadReleaseSource.cs`. Differences from the plan text are recorded: the fail-closed rule covers symbolic links (D16), pack validates both the Squad tree and the composed tree, and a refused pack keeps the previous archive.
- Code and test gates (G1, G2, G4, G5) were not re-run by this closeout. The conductor's gate evidence on the pull request applies to them.

### Verification

Run from the worktree root after the archive move.

```text
$ dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready --base origin/main --format table
No findings.

0 critical, 0 error, 0 warning, 0 info.

$ dotnet run --project src/KyberWeave.Cli -- docs drift .
1 critical, 0 error, 0 warning, 0 info.
KW-DOC-DRIFT-001: Drift cannot be verified: No CodeGraph index at <worktree>/.codegraph/codegraph.db.
```

`docs drift .` is not verified in this worktree. The worktree has no CodeGraph index, and the closeout did not build one. The drift check, including the `code-refs` added to ADR 0049 and the architecture and onboarding documents, must be run where an index exists before the closeout counts as complete.

### Documentation review

```text
DOCS_REVIEW: update
RECOMMENDATIONS: updated in this change: docs/adr/0049-kyber-squad-ships-the-kyber-weave-skills.md (new), docs/adr/README.md, products/kyber-squad/README.md, docs/kyber-squad/architecture.md, docs/kyber-squad/onboarding.md, docs/kyber-squad/requirements.md, docs/distribution.md, docs/docgraph/onboarding.md, docs/context-hygiene/skills.md, docs/install.md. Not written, outside the D9 scope, for the conductor to route: docs/README.md line 120 ("29 skills"); docs/kyber-squad/README.md lines 28, 42, 56, 72 and 73 (Squad skill and resource counts, and a Copilot render of 149 files); docs/context-hygiene/agents.md line 58 ("29-skill namespace"); docs/kyber-squad/architecture.md line 909 and docs/kyber-squad/onboarding.md line 1164 (a Copilot render of 149 files, built on the 29-skill count, not re-measured).
```

### Follow-ups

- D17 (known gap, out of scope): a symbolic link inside `products/kyber-squad/` that points outside the repository is still packed. A follow-up is proposed to the conductor. No todo or issue was filed by this closeout.
- The release loop and the human's own `~/.claude/skills` are verified after the next release, as D13 states.

## Closeout mapping

Each decision and Design-harvest item maps to its destination. Documentation-impact is `extends-component` for every row, and last-reviewed is 2026-10-31 for every row. Status is archived for every row.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D0 | [ADR 0049, Decision 1](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (Squad deploys the six at its own level); [architecture §1, composed Squad tree](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction) | extends-component | KyberSquad | 2026-10-31 | archived |
| D1 | [ADR 0049, Decision 2](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (`.apm/skills/` stays the only source); [architecture §1](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction) | extends-component | KyberSquad | 2026-10-31 | archived |
| D2 | [ADR 0049, Decision 3](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (discovery, not a list); [onboarding, Kyber-Weave skills in a Squad deployment](../../kyber-squad/onboarding.md#kyber-weave-skills-in-a-squad-deployment) | extends-component | KyberSquad | 2026-10-31 | archived |
| D3 | [ADR 0049, Decision 1](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (`docs init` unchanged); [DocGraph onboarding, APM is an expected dependency](../../docgraph/onboarding.md#apm-is-an-expected-dependency); [install, APM](../../install.md#apm) | extends-component | KyberSquad, DocGraph | 2026-10-31 | archived |
| D4 | [ADR 0049, Decision 4](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (bundle stays the 28 Squad skills); [architecture §1, composed Squad tree](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction) | extends-component | KyberSquad | 2026-10-31 | archived |
| D5 | [ADR 0049, Decision 5](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision); [architecture §1, fail-closed names and links](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction); [onboarding, Packaging](../../kyber-squad/onboarding.md#packaging-squad-pack) | extends-component | KyberSquad | 2026-10-31 | archived |
| D6 | [ADR 0049, Decision 4](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision); [distribution, Squad release assets and packaging](../../distribution.md#squad-release-assets-and-packaging); [requirements KS-006](../../kyber-squad/requirements.md) | extends-component | KyberSquad, Distribution | 2026-10-31 | archived |
| D7 | [ADR 0049, Decision 2](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) (the single-source rule behind the guard); the corrected XML comment on `NoKyberDocsSkillLivesInSquad` in `tests/KyberWeave.Tests/KyberDocsSkillSetTests.cs` (assertions unchanged; test-dev owns the file) | extends-component | KyberSquad | 2026-10-31 | archived |
| D8 | [ADR 0049, Status and Decision 2](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#status); [products/kyber-squad/README.md, lines 163 to 165, amended in place](../../../products/kyber-squad/README.md) | extends-component | KyberSquad | 2026-10-31 | archived |
| D9 | Counts in [architecture §1](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction), [onboarding intro and Packaging](../../kyber-squad/onboarding.md#packaging-squad-pack), [distribution table](../../distribution.md#squad-release-assets-and-packaging) and [context-hygiene, deployment control plane](../../context-hygiene/skills.md#deployment-control-plane-with-kyber-squad). Verified on disk: 28 skills, 88 resources, 116 files canonical; 34 skills, 125 files per archive. | extends-component | KyberSquad, Distribution, ContextHygiene | 2026-10-31 | archived |
| D10 | [onboarding, Where Kyber-Squad is deployed, skip the docs-init skill](../../kyber-squad/onboarding.md#where-kyber-squad-is-deployed-skip-the-docs-init-skill); [DocGraph onboarding](../../docgraph/onboarding.md#apm-is-an-expected-dependency); [install, APM](../../install.md#apm); [ADR 0049, Consequences](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#consequences). No code change; no todo filed. | extends-component | KyberSquad, DocGraph | 2026-10-31 | archived |
| D11 | [architecture §1, composed Squad tree](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction) (`SquadCompanionSkills` is the one type both callers use); [ADR 0049, Decision 2](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision) | extends-component | KyberSquad | 2026-10-31 | archived |
| D12 | Documentation waiver: a visibility change on one nested class (`private` to `internal`), with no behaviour change and a test-seam purpose. The surviving invariant, that checkout and pack share one type, is recorded at D11's destination. | extends-component | KyberSquad | 2026-10-31 | archived |
| D13 | [onboarding, Kyber-Weave skills in a Squad deployment](../../kyber-squad/onboarding.md#kyber-weave-skills-in-a-squad-deployment) ("They arrive with a release that carries them"); [ADR 0049, Consequences](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#consequences) | extends-component | KyberSquad | 2026-10-31 | archived |
| D14 | Documentation waiver: development-mode and autonomy metadata of this plan. Process only and transient. | extends-component | KyberSquad | 2026-10-31 | archived |
| D15 | Documentation waiver: approval to execute and the delivery rules (draft pull request, never merged by this plan). A transient process decision. | extends-component | KyberSquad | 2026-10-31 | archived |
| D16 | [ADR 0049, Decisions 6 and 7](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#decision); [architecture §1, fail-closed names and links, and pack validation](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction); [onboarding, Packaging](../../kyber-squad/onboarding.md#packaging-squad-pack) | extends-component | KyberSquad | 2026-10-31 | archived |
| D17 | [architecture §1, known limitation](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction); [onboarding, Packaging, known gap](../../kyber-squad/onboarding.md#packaging-squad-pack); [ADR 0049, Consequences](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#consequences). Out of scope; follow-up proposed to the conductor; no todo filed. | extends-component | KyberSquad | 2026-10-31 | archived |
| H1 | [architecture §1, Canonical Source Normalization and IR Construction](../../kyber-squad/architecture.md#1-canonical-source-normalization-and-ir-construction) | extends-component | KyberSquad | 2026-10-31 | archived |
| H2 | [onboarding, Kyber-Weave skills in a Squad deployment](../../kyber-squad/onboarding.md#kyber-weave-skills-in-a-squad-deployment), intro, and [Packaging](../../kyber-squad/onboarding.md#packaging-squad-pack) (includes R1, the unmanaged-copy remedy, and R2 and R5, the overlap) | extends-component | KyberSquad | 2026-10-31 | archived |
| H3 | [requirements, KS-006](../../kyber-squad/requirements.md) (KS-001 unchanged) | extends-component | KyberSquad | 2026-10-31 | archived |
| H4 | [ADR 0049, Status and Decision 2](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md#status) (the replaced split and the new rule); [products/kyber-squad/README.md, lines 163 to 165, amended in place](../../../products/kyber-squad/README.md) | extends-component | KyberSquad | 2026-10-31 | archived |
| H5 | [distribution, Squad release assets and packaging](../../distribution.md#squad-release-assets-and-packaging) | extends-component | Distribution | 2026-10-31 | archived |
| H6 | [DocGraph onboarding, APM is an expected dependency](../../docgraph/onboarding.md#apm-is-an-expected-dependency) | extends-component | DocGraph | 2026-10-31 | archived |
| H7 | [context-hygiene skills, Kyber-Weave ships a skill of its own](../../context-hygiene/skills.md#kyber-weave-ships-a-skill-of-its-own) and [deployment control plane](../../context-hygiene/skills.md#deployment-control-plane-with-kyber-squad) | extends-component | ContextHygiene | 2026-10-31 | archived |
| H8 | [install, Retired skills](../../install.md#retired-skills) and [install, APM](../../install.md#apm) | extends-component | Distribution | 2026-10-31 | archived |
| H9 | [ADR 0049](../../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md) and its [ADR index row](../../adr/README.md); decided-by added in [architecture](../../kyber-squad/architecture.md) and [onboarding](../../kyber-squad/onboarding.md) | extends-component | KyberSquad | 2026-10-31 | archived |

ADR position: ADR 0049 (decision recorded in full; no ADR waiver, D8).
