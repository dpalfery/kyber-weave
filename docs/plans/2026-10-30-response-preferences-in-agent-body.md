---
id: plans/2026-10-30-response-preferences-in-agent-body
title: Response preferences injected into conductor and bug-crusher; response-format skill removed
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-30
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad]
---

# Response preferences injected into conductor and bug-crusher; response-format skill removed

**Status:** Ready — decision-complete as of revision 3 (2026-10-30): the human answered D-B, D-C, D-D and OQ1. Approve-and-execute gate passed: approved and executing, human, 2026-10-30, on branch `feat/response-preferences-in-agent-body` with draft PR #475.

**Autonomy change (D-E, 2026-10-30):** the human switched this run to `autonomous` — verbatim: "you need to go into autonomous mode you are bugging me with issue you or the architect can solve." Frontmatter now reads `autonomy: autonomous`, matching the Config Reg default; D-D's supervised override is **superseded**. From this point every gate is answered by recommendation (recorded as `autonomous: recommendation accepted`), and only a hard block returns to the human.

**Branch:** implementation starts from a **fresh branch off the default branch** (`main`). The checkout this plan was written from (`fix/skill-provenance-metadata`) is unrelated and must not carry this work.

## Goal

`conductor` and `bug-crusher` must have the user's response guidance in context on every turn. A skill that the agent is told to load is not reliable: in a real session an agent loaded `response-format` and still did not read the home guidance file. So the text is copied into the agent itself. `kyber-weave squad install` / `squad update` copy the text of `~/.kyber-weave/response-guidance.md` into a section named `<response-preferences>` in the rendered `conductor` and `bug-crusher` entry file for every deployment target.

**What is deleted and what is not (H6):** only the `response-format` **skill** (`products/kyber-squad/skills/response-format/` and its deployed copies) is removed. The home preferences file `~/.kyber-weave/response-guidance.md` is **not** deleted, renamed or moved: `squad install` creates it with the shipped default when it is absent (new, D-C), `squad update` never overwrites it once the user has changed it (D-C, already true in source — F0), and it stays the user's one place to change their preferences. What moves is only the *canonical source of the shipped default text* inside this repository (from the skill directory to `products/kyber-squad/response-guidance.default.md`, D5); the embedded resource and the home file are unchanged.

## Decided by the human (not reopened)

| ID | Decision |
|---|---|
| H1 | Remove the `response-format` skill entirely. |
| H2 | Add a section literally named `<response-preferences>` to `conductor` and `bug-crusher`. It holds the text of `~/.kyber-weave/response-guidance.md`, filled in by `squad install` and `squad update`. |
| H3 | All 13 targets. The text goes into each target's **entry file** for `conductor` and `bug-crusher`, whatever form the target lowers the agent to (native agent, lowered skill, command, canonical skill, or `role-` skill — see F1 table). *Revision 2: H3 originally said "five targets lower an agent to a skill (Cursor, Pi, ZCode, Devin, Warp)"; that was wrong for ZCode (command) and for bug-crusher on Cursor/ZCode/Pi/Devin (canonical skill) and Warp (`role-` skill). The human's intent — "the entry point carries the text" — is unchanged.* |
| H4 | If the file is missing or empty, the section gets the shipped default text. |
| H5 | Paste the file verbatim, with no ~8 KB cap and no "style-only" guard line. **Accepted risk, recorded once:** the full file now goes into every turn's system context unfiltered. A large or instruction-bearing file costs context and is obeyed as agent text. The operator owns that file. |
| H6 | The home preferences file is **not** deleted. It is created by Squad and becomes the user's place to change their preferences; only the `response-format` skill is removed. Name: the file is `response-guidance.md` (see F0) and stays so (OQ1 answered: no rename). |

## Investigation findings

Discovery (revision 3): codegraph 1, docs 2 (`docs_validate`, `docs_drift`), direct reads 7 — `SquadResponseGuidanceDeployer.cs` 111–190 (codegraph returned the class up to line 112 only; the write decision in `Deploy` was not in its output); a grep and read of `SquadDeploymentPlan.cs` 505–544 (codegraph returned `CreateUpdate`'s callers but not its stale-file loop body, needed for G2); `CursorRenderer.cs` 145–184 and `ZCodeRenderer.cs` 285–319 and 377–456 plus two greps of `Squad/Rendering` (G3 spot-check of the line ranges T2 depends on). Revision 2: codegraph 1, docs 1, direct reads 5. Revision 1: codegraph 1, docs 0, direct reads 9. Line references for `SquadRetiredSkills.cs`, the csproj and the test files were not re-read in revision 3 and are verified at implementation time.

### F0 — The home file today (new in revision 2)

- Name: `SquadResponseGuidanceDeployer.FileName = "response-guidance.md"` (line 54), under the home owned-files root (`_stateStore.ResolveHomeOwnedFilesRoot()`, i.e. `~/.kyber-weave`). Embedded default resource: `Squad.response-guidance.md` (line 57). The file name is a code contract (constant, embedded resource, `SquadHomeFile` receipt records in existing installs), so it is not renamed (OQ1 closed).
- Receipt: tracked as `SquadHomeFile(FileName, shippedDigest)` via `SquadDeploymentPlan.AttachHomeFiles`, so Squad can tell its own default from an operator edit and preserves an edit.
- **Write decision (read in revision 3, `SquadResponseGuidanceDeployer.Deploy`, lines 123–190):** absent file → write the shipped default (`:162–163`); file equals the shipped default → no write (`:169–170`); file differs and the receipt has no record → kept, with a warning (`:172–178`); file equals the recorded shipped digest, i.e. an unedited older default → replaced with the new default (`:181–183`); file differs from the recorded digest, i.e. user-edited → **kept**, with a warning (`:185–189`). So **update already preserves an edited file; it does not overwrite it.** No fix is needed; T3(h) pins it. An unedited older default is refreshed, which matches D-C ("once the user has changed it").
- **Who creates it today:** only `UpdateAsync`. The deployer is constructed at `SquadLifecycleService.cs:435–438` only when `Scope == Global && !DryRun`, and runs inside the transaction through `DeployGuidance` (`:636–644`). `InstallAsync` executes its plan at `:233` with no guidance step. So today **`squad install` does not create the file; `squad update --global` does.** D-C (yes) requires install to create it: T3(g) adds that.
- Doctor: `SquadDoctorCommand` does not read or check the file (no reference in `src/`).
- Unreadable-file path: `Deploy(previous, warnings)` adds a message to the caller's `warnings` collection on a degradable failure (`:137–140`, `:151–154`); in `UpdateAsync` that is the `List<string> warnings` built at `:434`, which is extended with transaction warnings (`:447`) and returned on `SquadLifecycleResult` for the CLI to print. The new pre-plan reader uses the same list (T1(c), T3(f)).

### F1 — Where the text can be injected (revised)

- `SquadLifecycleService` calls `_renderer.RenderAsync` in install and update. `SquadRenderResult.Files` (`SquadDeploymentFile`: `RelativePath`, `Content`, `Target`) feeds the plan, and the receipt digests come from those bytes.
- **Injection point:** a pure post-render transform, `SquadResponsePreferencesInjector`, applied to `renderResult.Files` *before* the plan is built. Renderers stay untouched; per-user state stays out of `ISquadRenderer`.
- **Entry files per `SquadTarget` member (revision 4, D7; replaces the five-row form table, which omitted `Factory`, missed every primary-agent entry-point skill, and was wrong for Warp).** Derived from the renderers, project scope; global scope strips the target's leading dot-directory (e.g. `agents/conductor.md`). Both carriers are `invocation: primary`; `bug-crusher` is a declared *primary* shared identity (`profiles/fallbacks.yml:19–21`), so its canonical skill is rendered on every target and never suppressed (suppression applies only to subagent shared identities). `conductor` is not a shared identity, so targets whose fallback branch emits an entry-point skill (`no-primary-agent: skill` in Codex, Claude, Factory, CommandCode; lowering in Cursor, Pi, Devin, Warp; command in ZCode) emit one from the agent body.

| `SquadTarget` | `conductor` entry files | `bug-crusher` entry files |
|---|---|---|
| Codex | `.codex/agents/conductor.toml`, `.codex/skills/conductor/SKILL.md` | `.codex/agents/bug-crusher.toml`, `.codex/skills/bug-crusher/SKILL.md` |
| Cursor | `.cursor/skills/conductor/SKILL.md` | `.cursor/skills/bug-crusher/SKILL.md` (canonical skill) |
| Claude | `.claude/agents/conductor.md`, `.claude/skills/conductor/SKILL.md` | `.claude/agents/bug-crusher.md`, `.claude/skills/bug-crusher/SKILL.md` |
| Copilot | `.github/agents/conductor.agent.md` | `.github/agents/bug-crusher.agent.md`, `.github/skills/bug-crusher/SKILL.md` |
| OpenCode | `.opencode/agents/conductor.md` | `.opencode/agents/bug-crusher.md`, `.opencode/skills/bug-crusher/SKILL.md` |
| Kilo | `.kilo/agents/conductor.md` | `.kilo/agents/bug-crusher.md`, `.kilo/skills/bug-crusher/SKILL.md` |
| Antigravity | `.agents/agents/conductor/agent.md` | `.agents/agents/bug-crusher/agent.md`, `.agents/skills/bug-crusher/SKILL.md` |
| Warp | `.warp/skills/conductor/SKILL.md` (unoccupied identity → agent-name skill; **no** `role-` skill) | `.warp/skills/bug-crusher/SKILL.md` (shared → reuse canonical skill; **no** `role-` skill) |
| Factory | `.factory/droids/conductor.md` (`FactoryRenderer.cs:262–303`), `.factory/skills/conductor/SKILL.md` (`:214–216`) | `.factory/droids/bug-crusher.md`, `.factory/skills/bug-crusher/SKILL.md` (entry-point skill skipped, canonical reused, `:184–198`) |
| Pi | `.pi/skills/conductor/SKILL.md` | `.pi/skills/bug-crusher/SKILL.md` (canonical) |
| ZCode | `.zcode/commands/conductor.md` (`ZCodeRenderer.cs:453–455`) | `.zcode/skills/bug-crusher/SKILL.md` (canonical) |
| Devin | `.devin/skills/conductor/SKILL.md` | `.devin/skills/bug-crusher/SKILL.md` (canonical) |
| CommandCode | `.commandcode/agents/conductor.md`, `.commandcode/skills/conductor/SKILL.md` | `.commandcode/agents/bug-crusher.md`, `.commandcode/skills/bug-crusher/SKILL.md` |

  Verification depth: Factory, Codex, Cursor, ZCode and Warp branches were read; the Claude/CommandCode entry-point skill and the Copilot/OpenCode/Kilo/Antigravity canonical-skill rows follow the same `NoPrimaryAgent` / shared-identity code paths (grep-confirmed per renderer) and are proven by T2's sentinel oracle (below), which is authoritative over this table if they disagree.
- So the block lives in three canonical sources: `agents/conductor.md`, `agents/bug-crusher.md`, `skills/bug-crusher/SKILL.md`, and every rendered file whose content derives from one of them carries it.
- **Classification rule (D7, decided from the render output, no path list):** a rendered file is an **entry file** iff it contains at least one `<response-preferences>` block after render. This is decidable because only the three carriers hold the block in source, and every renderer copies a body verbatim into its output (`InstructionBody`), so "contains a block" ⇔ "derived from a carrier body". A future target or a new derived file (another droid, command or entry-point skill) is therefore classified automatically and cannot escape injection.
- **Single-block rule (fail-closed, revised by D7):** the injector throws `SquadRenderValidationException` when (1) an entry file contains more than one block; (2) for any rendered target, either carrier identity (`conductor`, `bug-crusher`) yields **zero** entry files (the block was lost); or (3) an entry file's **identity** is not a carrier identity — identity is the path's agent/skill/command name segment (`agents/<id>.md|.toml|.agent.md`, `agents/<id>/agent.md`, `droids/<id>.md`, `skills/<id>/SKILL.md`, `commands/<id>.md`) with any `role-` prefix removed. Rule (3) replaces the old "block in a non-entry file" check: it stays an error, but only for files whose identity is not `conductor` or `bug-crusher` (leak prevention, e.g. a block copied into `skills/architect/SKILL.md`). Files of a carrier identity that inherit the body (Factory droids, ZCode commands, Codex/Claude/Factory/CommandCode entry-point skills) are entry files, not errors. Resource files projected beside an entry file never carry the block (resources are not the body) and fall under rule (3) if they ever do.
- **Effective text:** `SquadResponseGuidanceSource.ReadEffective(warnings)` reads, before the plan and read-only, the home file bytes if present and non-whitespace, else the embedded default (H4). A degradable failure (`SquadResponseGuidanceDeployer.IsDegradableFailure`) falls back to the default and adds a warning to the same lifecycle `warnings` list as F0. It never writes or deletes the home file.
- **Callers to wire:** `InstallAsync`, `UpdateAsync` (whose deployer construction at `:435–437` and `DeployGuidance` at `:637` stay — they create/preserve the home file, H6), and the `SquadDoctorCommand` re-render (~551; compares paths not bytes, injected anyway so every comparing render site sees the same bytes).
- **Scope:** per D-B (accepted), the user's effective text is injected at project scope too, on all 13 targets and every entry-file form in the table.

### F2 — Receipts, `squad update` preservation and `squad status` drift

- Owned-file digests are computed from the plan's bytes; after injection they include the guidance. With no new receipt fields:
  - `squad status` never shows the injected section as drift.
  - User edits `response-guidance.md`, runs `squad update`: the render differs, the on-disk entry file matches its recorded digest, update replaces it, agents refresh.
  - A user hand-edit to a deployed entry file is preserved as today (or `--replace-managed`); that file then stops receiving guidance changes until the edit is dropped. Stated in onboarding docs.
- The `SquadHomeFile` record for `response-guidance.md` is unchanged and the home file survives every path (H6).
- Editing the guidance without running `update` is not drift; intended (copy-on-update), documented.

### F3 — `squad pack` / APM / plugin archives

`SquadPacker.PackApm` / `PackPlugins` copy canonical source. Per D-A they run the injector with the shipped default over all three canonical block carriers.

### F4 — Existing installs (revised)

Deployed `skills/response-format/*` files are receipt-owned and absent from the new render. **Verified in source (revision 3, G2):** `SquadDeploymentPlan.CreateUpdate`, lines 505–538, skips any previous owned file still rendered (`:505–506`) or owned by a sibling receipt (`:508–509`); for the rest, an on-disk file whose digest equals the receipt's records an exact precondition and a `SquadFileMutation.Delete` (`:526–533`), and an edited file is kept and stays in the receipt (`:535–538`); a missing file is dropped (`:523–524`). So `squad update` removes unedited `response-format` files and preserves edited ones. T5 pins both. **Retired list:** `SquadRetiredSkills` (`SquadRetiredSkills.cs:7–26`) covers only skills moved to Kyber-Docs and its remedy text points at `kyber-docs-onboarding-and-assessment`; adding `response-format` would give wrong advice. Decision D3 (revised): do **not** add it there. The preserved edited copy is reported by the existing update "preserved locally edited file" output; no doctor entry. This never touches `~/.kyber-weave/response-guidance.md`, which is a home file, not a receipt-owned skill file.

### F5 — Session-title matrix

The matrix is operative only in `skills/response-format/SKILL.md`; `docs/kyber-squad/architecture.md` §8 (~1001) summarises it; nothing executes it. Disposition: the matrix moves into architecture §8 as reference; the title-line rule lives in the shipped default guidance. The guidance-file session-title guard test (`SquadCanonicalContentTests.cs` 603–661) is retargeted to `response-guidance.default.md`.

### F6 — Canonical default's new repository home

Default text moves from `products/kyber-squad/skills/response-format/user-guidance.md` to `products/kyber-squad/response-guidance.default.md`. In `src/KyberWeave.Core/KyberWeave.Core.csproj`, the comment block at lines 46–48 and the `VerifyResponseGuidanceDefault` target at 58–75 are repointed. Embedded resource name `Squad.response-guidance.md` and the home file are unchanged. The new file must be in the render/pack input set or explicitly excluded from it. This is done **before** the skill directory is deleted (T4 before T5).

### F7 — References to clean up (revised)

- Products: `agents/conductor.md` (27, 28, 33, 78), `agents/bug-crusher.md` (29, 34), `skills/bug-crusher/SKILL.md` (29, 31, its response-format section), `bundles/full.yml` (51), `products/kyber-squad/README.md` (85, 130), `migration/conductor.md` (26, historical: keep, add a note).
- Source: `SquadResponseGuidanceDeployer.cs` doc comments (15, 44–48); `KyberWeave.Core.csproj` (46–48, 58–75).
- Tests: `Fakes/SyntheticValidSquadReleaseSource.cs` (115, 140–145); `SquadCanonicalContentTests.cs` — about 20 tests: 80, 107, 114, 119, 273, 383 (skill count), 396–461, 475–481, 517–558, 603–661, 669–691, 920–952, 956–978, 993–1031, 1042–1076; `SquadGlobalHomeCliTests.cs` 671–1267 (about 10 guidance tests: must keep passing, as the home file behaviour is unchanged — only skill-path assertions change); `SquadDeploymentStateTests.cs` and `SquadResponseGuidanceDeployerTests.cs` (home-file record and deployer: expected to stay green; touched only for the default's new source path).
- **Shape-owner guard** `NoAgentOrSkillOutsideTheResponseFormatSkillRestatesItsShape` treats `skills/response-format` as sole owner of the response shape. Per D6 the owner becomes `products/kyber-squad/response-guidance.default.md`; the test is renamed (`NoAgentOrSkillRestatesTheResponseShapeOutsideTheDefaultGuidance`) and re-pointed, and the canonical `<response-preferences>` blocks are empty (D-A) so they pass it.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| H1–H5 | See "Decided by the human" | As stated (H3 wording corrected in revision 2, intent unchanged) | H5 risk recorded once | human, 2026-10-30 (relayed by conductor) |
| H6 | Is the home preferences file deleted with the skill? | **No.** Only the `response-format` skill is removed; the home file is created by Squad, preserved across `squad update`, and stays the user's place to edit preferences. Name stays `response-guidance.md` (OQ1). Creation by install → D-C. | F0 | human, 2026-10-30 (relayed by conductor) |
| D-A | What does the canonical `<response-preferences>` block hold, and what do `squad pack` archives ship? | **Option 1:** empty marker block in canonical sources; install/update fill it with the user's text; `squad pack` fills it with the shipped default. | One copy of the default; deterministic `pack` output. Now applies to three carriers (F1). | Decided: human, 2026-10-30 (relayed by conductor) |
| D-B | May the operator's personal preferences text land in committed project-scope files on all 13 targets, including canonical skill entry files, the Warp `role-` skills and ZCode commands? | Human answer, verbatim: "**yes.** The operator's personal preferences text may land in committed project-scope files on all 13 targets, including canonical skill entry files, the Warp `role-` skills and ZCode commands." | **Recorded once, applies to every task:** user-specific text is committed to project-scope files on all 13 targets; the operator owns that text; review of those files must not treat its presence or content as a defect. No project-scope warning or default-only fallback is built. | Decided: human, 2026-10-30 (relayed by conductor) |
| D-C | Should `squad install` create the home file, and may `squad update` overwrite it? | Human answer, verbatim: "**yes.** Create the default if it does not exist; once the user has changed it, update must not overwrite it." Requirements: **R-DC1** global `squad install` creates `~/.kyber-weave/response-guidance.md` with the shipped default when absent (same `Global && !DryRun` condition and `DeployGuidance` transaction step as update). **R-DC2** `squad update` leaves a user-edited file byte-identical. | Source (F0): update already preserves an edited file (`Deploy` `:185–189`); install does not create it today → T3(g) adds it; T3(h) pins R-DC2. | Decided: human, 2026-10-30 (relayed by conductor) |
| D-D | Frontmatter `autonomy: supervised` vs Config Reg `<autonomy>: autonomous`. | **Superseded by D-E (2026-10-30).** Original human answer, verbatim: "**yes.** Keep `supervised` as an explicit override of the repository's `autonomous` Config Reg default." | Reason at the time: the human was deciding this plan's items in-session. | Decided: human, 2026-10-30 (relayed by conductor); superseded by D-E |
| D1 | Injection mechanism | Post-render transform over `SquadRenderResult.Files` in install, update and doctor; entry files per F1 table; single-block fail-closed rule covers commands, canonical skills and `role-` skills. | F1 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D2 | Drift and receipts | No new receipt fields: injected bytes are the owned bytes. | F2 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D3 | Migration | Stale-owned-file removal, proven by test. **Not** added to `SquadRetiredSkills` (wrong remedy text). | F4 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D4 | Session-title matrix | Moves to architecture §8 as reference; title-line rule into the default guidance; guidance-file guard test retargeted. | F5 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D5 | Default repository home | `products/kyber-squad/response-guidance.default.md`; csproj 46–48 and 58–75 repointed; home file unchanged. | F6 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D6 | Who owns the response shape after the skill is gone? | `products/kyber-squad/response-guidance.default.md` (delivered as the home file and injected block). Shape-owner guard test renamed and re-pointed to it. | F7 | approved by the human at the approve-and-execute gate, 2026-10-30 |
| D7 | How are derived files that inherit a carrier body (Factory droids, ZCode commands, primary entry-point skills) classified, and what does fail-closed mean? | Entry file ⇔ rendered file carrying a block; exactly one block each; every carrier identity must yield ≥1 entry file per target; a block in a file whose path identity is not `conductor`/`bug-crusher` stays an error. Hard-coded `IsEntryFile` list removed. F1 per-member table is the T2 oracle, cross-checked by a sentinel render. T1 (d)/(e) amended. | F1; T2 failed 26/26 because the injector rejected `.factory/droids/*`, prefixed project paths and entry-point skills as non-entry files. Supersedes the D1 wording "entry files per F1 table" only in how entry files are identified. | autonomous: recommendation accepted (architect, 2026-10-30, under D-E) |
| D-E | Run autonomy | Switch to `autonomous`. Human, verbatim: "you need to go into autonomous mode you are bugging me with issue you or the architect can solve." Supersedes D-D. From this point gates are answered by recommendation; only a hard block returns to the human. | Config Reg `<autonomy>: autonomous` now matches frontmatter. | Decided: human, 2026-10-30 (relayed by conductor) |

## Open questions

- OQ1 — **closed**, human 2026-10-30, verbatim: "**no.** Do not rename the home file. It stays `response-guidance.md`." Reason: the file name is a code contract (`SquadResponseGuidanceDeployer.FileName`, the `Squad.response-guidance.md` resource, and `SquadHomeFile` records in existing receipts). Rename removed from scope.

No open questions remain.

## Tasks (test-first)

Each task begins with a failing test committed (Red), then the implementation (Green). No task deletes, renames or moves `~/.kyber-weave/response-guidance.md`.

| # | Owner | Red (fails first) | Green |
|---|---|---|---|
| T1 | test-dev → csharp-dev | `SquadResponsePreferencesInjectorTests`: (a) block contents replaced verbatim, no cap, bytes exact, incl. a >8 KB input and an input with tool instructions; (b) empty/whitespace/missing → shipped default; (c) `ReadEffective` on an unreadable file (degradable exception) → default plus exactly one warning appended to the supplied collection; source never writes; (d) an entry file (any rendered file carrying a block — agent, droid, command, canonical skill, entry-point skill, `role-` skill; prefixed project paths and stripped global paths) carrying two blocks → `SquadRenderValidationException`; a render in which a carrier identity yields zero block-bearing files → exception; (e) files without a block are untouched byte-for-byte; a block in a file whose identity is not `conductor`/`bug-crusher` (e.g. `.claude/skills/architect/SKILL.md`) → exception; a block in a derived carrier-identity file (`.factory/droids/conductor.md`, `.zcode/commands/conductor.md`, `.codex/skills/conductor/SKILL.md`) is injected, not rejected (D7). | `SquadResponseGuidanceSource` + `SquadResponsePreferencesInjector` in `Squad/Deployment` (classification per F1 / D7; the hard-coded `IsEntryFile` list is removed). |
| T2 | test-dev → csharp-dev | Theory over all 13 `SquadTarget`s × {conductor, bug-crusher}: render the real canonical tree, inject, assert that the set of files carrying the block equals the **expected entry-path set** from the F1 per-member table (incl. Factory droid + skill, Codex/Claude/CommandCode entry-point skills, the canonical bug-crusher skill on every target, Warp with no `role-` file), each with exactly one block holding the user text, and that no other file carries a block. **Oracle check (D7):** a second assertion renders the tree with a distinct sentinel in each carrier body and requires the sentinel-bearing path set to equal the table set, so a renderer change that adds or drops a derived file fails loudly instead of silently diverging from the table. | Canonical `agents/conductor.md`, `agents/bug-crusher.md`, `skills/bug-crusher/SKILL.md` drop the load-the-skill lines and the "after loading response-format" ordering, and gain the empty block. Line 78 relay wording → "in the shape set by `<response-preferences>`". |
| T3 | test-dev → csharp-dev | Lifecycle/CLI tests: (a) install writes injected entry files; receipt digest = disk bytes; `status` clean; (b) edit `response-guidance.md`, `update`, entry files carry new text **and the home file still exists with the edit**; (c) locally edited conductor preserved and reported; (d) doctor render matches deployed bytes; (e) project scope injects the user's effective text into every entry file on all 13 targets (D-B); (f) unreadable home file → lifecycle result warnings contain the source's message and the run succeeds with the default; (g) **R-DC1:** global `squad install` with no home file creates `~/.kyber-weave/response-guidance.md` byte-equal to the shipped default and records its `SquadHomeFile`; dry-run and project-scope install do not create it; (h) **R-DC2:** a user-edited home file is byte-identical after `squad update` and the update warnings contain "Kept the edited response guidance" (pins existing behaviour; expected green on first run, kept as a regression guard). Existing `SquadGlobalHomeCliTests` 671–1267 stay green. | Wire the injector into `InstallAsync`, `UpdateAsync` (keep `:435–437` deployer and `:637` `DeployGuidance`) and `SquadDoctorCommand`; in `InstallAsync` construct the deployer under `Global && !DryRun` and run `DeployGuidance` in its transaction (R-DC1). No change to `Deploy`'s write decision. |
| T4 | test-dev → csharp-dev | `SquadCanonicalContentTests`: build target and csproj comment name `response-guidance.default.md`; default contains the title-line rule; guidance-file session-title guard (603–661) and shape-owner guard (D6) point at the new file. `SquadResponseGuidanceDeployerTests` / `SquadDeploymentStateTests` stay green. Update `SyntheticValidSquadReleaseSource`. | Move the default to `products/kyber-squad/response-guidance.default.md` (D5), add the title-line rule, repoint csproj 46–48 and 58–75, fix deployer doc comments. *Was T5; moved first so the build target never points at a deleted file.* |
| T5 | test-dev → csharp-dev | Upgrade test: prior receipt owning `skills/response-format/*` on several targets; `update` removes unedited files (delete mutation, per `CreateUpdate` `:526–533`), preserves an edited one and keeps it in the receipt (`:535–538`), and leaves `~/.kyber-weave/response-guidance.md` byte-identical. `SquadCanonicalContentTests`: no product file mentions `response-format`; skill count drops by one; remaining ~20 dependent tests (F7 list) updated or deleted with stated reason; `factory` session-title claim retargeted or deleted. | Delete `products/kyber-squad/skills/response-format/`; remove it from `bundles/full.yml`. Do not touch `SquadRetiredSkills` (D3). *Was T4.* |
| T6 | test-dev → csharp-dev | `SquadPacker` tests per D-A: APM and plugin archives' three carriers hold the default in the block; archives deterministic. | Apply the injector in `SquadPacker` with the shipped default. |
| T7 | docs-dev | — (docs gates) | Docs impact below, then `docs validate --merge-ready` and `docs drift` at zero findings. |

## Verification

The full .NET suite, `review gates . --all`, `docs validate . --merge-ready`, `docs drift .`. Manual: `squad update --global` with a custom `~/.kyber-weave/response-guidance.md`; confirm `~/.config/opencode/agents/conductor.md`, a Cursor-lowered conductor skill and the Cursor bug-crusher canonical skill contain the text; `squad status` clean; `~/.config/opencode/skills/response-format/` gone; `~/.kyber-weave/response-guidance.md` still present and unchanged.

## Documentation impact

- `docs/kyber-squad/requirements.md`: rewrite R3 (block injected; no skill load) and R5 (the home file `response-guidance.md` stays the user's single edit point / shipped default; no cap, no guard, per H5). Remove skill references at 67–69 and 182.
- `docs/kyber-squad/architecture.md`: line 38 bundle list; §8 rewritten as "response preferences injected at deploy" (entry-file table, fail-closed rule, drift, H5 risk, shape owner D6); session-title matrix (D4); home-owned files §~420–444 (file kept, H6); lines 888, 936–1002.
- `docs/kyber-squad/onboarding.md` §"User-home response guidance" (924–965, incl. 924–930): edit the file, run `squad update`; locally edited entry files stop receiving updates; project scope commits the operator's text (D-B); install creates the file, update never overwrites an edited one (D-C).
- `docs/kyber-squad/skill-resource-dispositions.md` line 130: remove or mark retired, count updated.
- `docs/kyber-squad/README.md` line 53; `products/kyber-squad/README.md` 85, 130.
- `docs/cli-command-surface.md`: `squad install/update/pack` descriptions, if they mention guidance.

### Design harvest

| Item | Target component | Doc type | Destination |
|---|---|---|---|
| Deploy-time injection, entry-file table, fail-closed block | kyber-squad | architecture | `docs/kyber-squad/architecture.md` §8 |
| Drift and refresh semantics; home file kept (H6), created by install and preserved by update (D-C) | kyber-squad | architecture + onboarding | `docs/kyber-squad/architecture.md` §8 and home-owned files; `docs/kyber-squad/onboarding.md` §"User-home response guidance" |
| Project-scope files carry the operator's text (D-B) | kyber-squad | onboarding + requirements | `docs/kyber-squad/onboarding.md`; `docs/kyber-squad/requirements.md` R5 |
| R3/R5 rewrite | kyber-squad | requirements | `docs/kyber-squad/requirements.md` |
| Session-title matrix (reference) | kyber-squad | architecture | `docs/kyber-squad/architecture.md` §8 |
| Response-shape owner (D6) | kyber-squad | architecture | `docs/kyber-squad/architecture.md` §8 |

ADR: likely **no ADR — ADR waiver**; reverses a skill-carrier choice within the existing renderer/receipt architecture, no new boundary, schema or dependency. Closeout confirms.

## Revision 2 change log (verification-pass findings)

| Finding | Fixed in |
|---|---|
| High — ZCode lowers to `.zcode/commands` | F1 table, H3 note, T2 |
| High — shared canonical-skill identity / Warp `role-` | F1 table and single-block rule, D1, T1(d), T2 |
| High — `SquadCanonicalContentTests` undercount; shape owner | F7, D6, T4, T5 |
| Medium — retired-skill list | F4, D3, T5 |
| Medium — missed callers and tests | F1 callers, F7, T3, T4 |
| Medium — docs list (onboarding 924–930, csproj 46–48) | F6, F7, Documentation impact |
| Medium — task ordering | T4/T5 swapped |
| Medium — autonomy contradiction | D-D (decided rev. 3: deliberate override) |
| Low — stale index row | `docs/plans/README.md` row |
| Low — harvest destinations | Design harvest |
| Low — T1(c)/T3 warning path | F0, T1(c), T3(f) |
| Evidence — G1 | G1 restated |

## Gaps

- G1: Revision 1's docs-check evidence had no artifact path and is withdrawn. Revision 3's `docs_validate` / `docs_drift` verdicts are in the architect digest, not asserted here.
- G2: **Closed (revision 3).** Stale-file removal verified in `SquadDeploymentPlan.CreateUpdate` 505–538 (F4); T5 pins it.
- G3: **Partly closed (revision 3).** `CursorRenderer.cs:159–172` and `ZCodeRenderer.cs:404–444` / `:411–422` re-read and correct. Not re-read: `SquadRetiredSkills.cs:7–26`, csproj 46–48 / 58–75, and test-file line ranges in F5/F7 — verified at implementation time; T2/T4/T5 are the proof.
