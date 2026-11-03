---
id: plans/2026-10-27-declare-rust-tauri-go-standards
title: "Declare Rust, Tauri and Go coding standards so the review council can cite them"
doc-type: plan
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-10-27
development-mode: test-first
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Declare Rust, Tauri and Go coding standards

**Status: Archived — complete 2026-10-27.** Closeout mapping is in the Closeout section. Approved for execution 2026-10-27 (AP1, `autonomous: recommendation accepted`). Frontmatter `status: archived` follows the closed status vocabulary (`KW-DOC-SPEC-002`).

**Development mode:** `test-first` (packet value; no user opt-out). **Autonomy:** `autonomous` (packet value and repository Config Reg `<autonomy>`).

**Scale:** `extends-component`, component `ReviewCouncil`.

**Source todo:** [review-council-rust-tauri-go-standards.md](../todo/review-council-rust-tauri-go-standards.md).

**Branch:** `feat/declare-rust-tauri-go-standards`, stacked on PR #440's branch `feat/council-catches-kilo-findings` (D1). This plan relies on #440's checklists (`products/kyber-squad/skills/code-review/references/{rust,tauri,go}.md`) and its `ReviewGuidanceCouncilContractTests`.

> Discovery (authoring pass 1): codegraph 2, docs 1, direct reads 24, greps 14.
> - CodeGraph returned `KyberStandardsTemplates` (the template roster includes `rust`, `tauri`, `go`) and `DocsScaffolder` (`WriteHostConfig`, `WriteConfigReg`, `Write` with `force: false` skips existing files).
> - Docs: `docs_explore` returned `docgraph/onboarding.md` (what `docs init` touches), `documentation-ontology.md` (technologies legalize the `technology` key), and `docgraph/governance.md` (`KW-DOC-SPEC-004` owner check, `KW-CONFIG-REG-001/002`).
> - Direct reads, each for something the indexes do not return verbatim: the architect references (4); `AGENTS.md` and `.kyber-weave/kyber-weave.yml` (Config Reg and gate config, not indexed code); the source todo; `docs/plans/README.md` (index shape); `docs/standards/README.md` and `docs/standards/typescript/README.md` (precedent); the four #440 checklists; the three templates `products/kyber-squad/standards/{rust,tauri,go}/README.md`; `ReviewGuidanceCouncilContractTests.cs` (full, the file to extend); heads of `McpAgentCodingStandardsTests.cs`, `DocGovernanceTests.cs` and `DocsInitCommandTests.cs` (to place the host-level assertion, D2); `SquadTypeScriptSpecialistTests.cs` lines 95-139; `docs/catalog.md` lines 30-44 (owner column); tray `Cargo.toml`, `tauri.conf.json` and `capabilities/tray.json` (grounding, D5-D6); `REVIEW.md` lines 320-369 and `docs/code-review/architecture.md` lines 140-164 (stale prose); the archived council plan head (frontmatter and runner form).
> - Greps (self-gathered, narrow): tray source for `#[tauri::command]`, `Command::new`, `unwrap`/`expect` counts; Glob for `Cargo.toml`, `go.mod`, `Cargo.lock`, tray source files.
>
> Validation note: the planner harness has no process tool, so `docs-dev` ran both checks for it. `docs validate .` and `docs drift .` were clean on the Draft, both before and after the source todo was promoted to `superseded`. `docs-dev` reruns both on the Ready transition.

---

## 1. Problem and goal

### 1.1 Problem

PR #440 added review checklists for Rust, Tauri and Go. Each one is a pointer: it names `<rust-coding-standard>`, `<tauri-coding-standard>` or `<go-coding-standard>` and states no rule of its own. None of those three properties is declared in this repository. So the council can only say "no standard declared" and fall back on non-authoritative Review traps. Kilo findings on Rust and Tauri code (PRs #407-#439) cannot be cited against a rule.

### 1.2 Goal

- `.kyber-weave/kyber-weave.yml` declares `rust`, `tauri` and `go` in `ontology.technologies`. `docs init` regenerates the Config Reg block.
- `docs/standards/{rust,tauri,go}/README.md` exist, `status: current`, owner `dpalfery`. Each one is grounded in what this repository actually has: the tray crate, its gates, and the display-layer rule. Go has no code yet.
- The three checklists name their in-repo standard path, as the TypeScript checklist already does.
- `ReviewGuidanceCouncilContractTests` pins all of this.
- `docs validate` and `docs drift` are clean.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | Which technologies are declared? | All three: `rust`, `tauri`, `go`. The Go standard's scope line says there is no Go code in this repository yet and that the standard applies when Go is introduced. | Rejected: declaring only `rust` and `tauri`. The Go checklist would keep reporting "no Go standard declared", and the `go-dev` agent already resolves `<go-coding-standard>` (`SquadRustGoSpecialistTests`). | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| Q2 | Which status do the standards carry? | All three `status: current`, grounded in real gates. Tray Rust gates: `cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test` (root `AGENTS.md`). Tauri also binds the display-layer rule. | Only `current` outranks portable defaults (`docs/standards/README.md`). A draft would make the checklist report "standard is a draft", which is no better than today. The grounding rule is D6. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| Q3 | How is the declaration made? | Edit `ontology.technologies` in `.kyber-weave/kyber-weave.yml`, then run `docs init . --no-skill` to regenerate the Config Reg. Never hand-edit the block. Never pass `--kyber-standards`. | `--kyber-standards` would declare all 14 template technologies and scaffold 11 unwanted standards. Hand edits are reported as `KW-CONFIG-REG-002`. `--no-skill` stops `docs init` from trying an APM skill deploy, which is not part of this change. `docs init` does not overwrite existing files (`DocsScaffolder.Write`, `force: false`) and rewrites only the block between the Config Reg markers. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| Q4 | Where do repository-specific review rules live? | Review rules stay in `REVIEW.md`. Standards say how code is written. No rule text is duplicated between them. `REVIEW.md` is not edited by this plan. | Its technology map already routes `rust` and `tauri` (`tauri` loads the `rust` and `react` rows). A standard may link to `REVIEW.md` but does not restate its rules. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| D1 | Branch and PR | Own PR from `feat/declare-rust-tauri-go-standards`, targeting `feat/council-catches-kilo-findings`. Retarget to `main` after #440 merges. | Stacking keeps #440 reviewable alone. The plan depends on #440's checklists and test class. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| D2 | Which test class holds the host-level assertion (Config Reg declares the three properties, docs exist, status current)? | `ReviewGuidanceCouncilContractTests`. | `McpAgentCodingStandardsTests` and `DocGovernanceTests` test engine behaviour over temporary `6-Docs` fixtures (`DocFixture`, `CsharpStandardPath = "6-Docs/..."`). Pinning this repository's configuration there would couple engine tests to one host. `ReviewGuidanceCouncilContractTests` already reads real repository files at `KyberWeaveTestPaths.ToolRoot` (`REVIEW.md`, `products/kyber-squad/...`), and the todo names it. Source: the code read in discovery. | autonomous: decided, source = repository test structure |
| D3 | Who owns the standards? | `owner: dpalfery`. No `component` key. | `KW-DOC-SPEC-004` checks `owner` against the catalog's Owner column (index 6). `dpalfery` is the owner on every catalog row (`docs/catalog.md`, verified). `docs/standards/typescript/README.md` uses the same owner and omits `component`. The templates' `owner: unassigned` would fail validation. | autonomous: decided, source = `docs/catalog.md` and the TypeScript standard |
| D4 | How do the Rust and Tauri standards split? | The Rust standard governs language-level Rust everywhere in this repository, including `dash/tray/src-tauri`. That crate is the only first-party Rust. The Tauri standard governs the Tauri layer and defers language rules to the Rust standard. The Tauri layer is IPC commands, capabilities, CSP and window config, the engine child process, and the display layer. | The template excludes `src-tauri` from the Rust standard. Following it would leave the Rust standard governing nothing here. Consistent with `REVIEW.md`'s map, where `tauri` also loads the `rust` row. `dash/tray/vendor/glib-0.18.5` is vendored third-party code and is out of scope for both. Cheap to reverse: one scope paragraph per document. | autonomous: low-confidence, no authoritative source |
| D5 | How is the Tauri template adapted to the tray? | Drop what is not true of the tray. Template assumptions and what replaces them:<br>- **Python sidecar, `bundle.externalBin`, PyInstaller.** The tray supervises the `kyberdash` CLI engine as a child process (`supervisor.rs`). It bundles no sidecar.<br>- **Mobile entry point.** Desktop only: bundle targets `app`, `dmg`, `nsis` (`tauri.conf.json`).<br>- **`commands/` module.** Commands live in `lib.rs` and are registered with `generate_handler!`.<br>- **Isolation Pattern and `npm run tauri build`.** Dropped.<br><br>Add the tray's real contracts:<br>- Each command is granted in `capabilities/tray.json`, and `tests/capability_contract.rs` pins it.<br>- The CSP denies the webview any network. Rust does all HTTP, and only to the loopback engine.<br>- The tray is a display layer: [`docs/rules/kyberdash-display-layer.md`](../../rules/kyberdash-display-layer.md), enforced by `dash/src/architecture/display-layer.test.ts`. | Verified by reading `Cargo.toml`, `tauri.conf.json`, `capabilities/tray.json` and greps of `src/` (9 `#[tauri::command]` hits, all in `lib.rs`, `Command::new` in `supervisor.rs`). Anything the author keeps from the template must pass D6. | autonomous: decided, source = tray source and config |
| D6 | Grounding rule for a `current` standard | Every factual claim must be checkable in the repository when it is written: a gate command, a setting, a file, an MSRV (`rust-version = "1.77"`), `Cargo.lock` committed. Template claims that are false here are dropped or reworded. Examples: `rust-toolchain.toml`, a Cargo workspace, `--locked`, nextest, `--all-targets --all-features`, rustls (the tray's `reqwest` has `default-features = false` and no TLS feature). A prescriptive rule that existing code does not fully meet may stay only as a rule for new and changed code. Example: no `unwrap`/`expect` on external input. The docs-dev digest lists every known existing exception, with file and line, for the human. Those exceptions are not fixed in this plan. | Prevents a `current` standard that the council would cite against code the repository's own gates accept. Rejected: fixing existing exceptions here. That is Rust code change and out of scope (D10). | autonomous: low-confidence, no authoritative source |
| D7 | What changes in the three checklists? | Add the in-repo path in parentheses after the token, in the TypeScript checklist's form: "(in this repository, `docs/standards/<tech>/README.md`)". Keep the not-declared and draft fallback text, the `standard declared` wording, and the Review traps. | The checklists are portable: they deploy to host repositories that may not declare these standards, so the fallback must stay. The TypeScript checklist is the precedent. The existing theory `ALanguageChecklistExistsAndPointsAtItsDeclaredStandard` keeps passing. | autonomous: decided, source = `references/typescript.md` |
| D8 | Are specialist reviewers (`rust-dev`, `tauri-dev`) needed? | No. No task changes Rust code. Accuracy is held by D6's evidence list in the docs-dev digest and by the whole-change council at the end. | Plan-authoring rule: no agent reviews a single task. | autonomous: decided, source = `plan-authoring.md` |
| D9 | Expected review outcome | The council returns `NEEDS_HUMAN` under `KW-REVIEW-008`, because `.kyber-weave/kyber-weave.yml` is in `review.policy.always-human`. This is expected and is not a defect. The owner reviews before merge. | Same outcome as the archived plans for 2026-10-15 and 2026-10-25. | autonomous: decided, source = `.kyber-weave/kyber-weave.yml` policy |
| D10 | Out of scope | Not changed by this plan:<br>- Rust or tray code.<br>- `REVIEW.md` (Q4).<br>- The hand-authored "Where to go next" table in `AGENTS.md`.<br>- New `review.task-gates` entries, including `cargo test` and any Go gate.<br>- The templates under `products/kyber-squad/standards/`.<br>- `docs/kyber-squad/skill-resource-dispositions.md` (its rows describe the conditional fallback, which still exists). | Each would widen the PR beyond the todo's "Done when". | autonomous: decided, source = the todo's "Done when" |
| D11 | What does the Go standard say with no Go code to ground it? | Scope line: no Go code in this repository yet; applies when Go is introduced. Keep the template's language-level decisions that do not depend on existing code: modules and `go.sum` committed, errors checked and wrapped, context propagation, goroutine ownership, `CGO_ENABLED=0` for shipped binaries. State that no Go gate is declared today. The change that introduces Go must add `gofmt` / `go vet` / `go test` gates to `review.task-gates` for `go-dev` and `test-dev`. Owner `dpalfery`. | Claims nothing that is false today, which keeps it compatible with D6. Cheap to reverse when Go arrives. | autonomous: low-confidence, no authoritative source |
| AP0 | Phase gates before execution: intake recommendation (PLAN), mode (test-first), scale (`extends-component`, ReviewCouncil), Test contract (§5) | All accepted as recommended. | Conductor `AUTONOMOUS_APPROVAL` under resolved autonomy `autonomous`, 2026-10-27. `docs validate .` and `docs drift .` ran clean on the Draft (run by `docs-dev`). | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |
| AP1 | Approve and execute | Approved. The plan is Ready, to execute as T1 → (T2 ∥ T3) → T4. | Conductor `AUTONOMOUS_APPROVAL` for the final gate. The `KW-REVIEW-008` NEEDS_HUMAN result at review is expected (D9); the owner reviews before merge. | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |

## Design harvest

Scale: `extends-component`; impact component `ReviewCouncil`.

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| H1 | ReviewCouncil | architecture | [`docs/code-review/architecture.md`](../../code-review/architecture.md) (Review guidance, Technology checklists bullet) | Replace "Rust, Tauri and Go have no declared standard in this repository" with: all three are declared; each checklist names its standard; the Go standard applies when Go is introduced. |
| H2 | ReviewCouncil | coding-standard | `docs/standards/{rust,tauri,go}/README.md` | The standards themselves, including the D4 split and the D5 adaptations. |
| H3 | ReviewCouncil | index | [`docs/standards/README.md`](../../standards/README.md) | The declared list names C#, test, TypeScript, Rust, Tauri and Go. |

No ADR expected. ADR waiver: this declares standards through the existing mechanism (`ontology.technologies` plus `docs init`), with no new boundary, dependency or schema.

## 4. Investigation findings

- **Mechanism.** `ontology.technologies` is the single list. It creates `docs/standards/<tech>/`, publishes `<tech-coding-standard>` in the Config Reg, and legalizes `technology: <tech>`. Without it a standard fails `KW-DOC-SPEC-002`. `docs init` writes a declared technology's README only when the file is absent. `--force` would overwrite scaffolded docs and must not be used. The registry path must exist (`KW-CONFIG-REG-001`), and `docs init` creates it.
- **Templates exist.** `KyberStandardsTemplates` embeds `rust`, `tauri` and `go`. All three are `status: draft`, `owner: unassigned`, and written for a generic repository.
- **Rust reality.** The only first-party crate is `dash/tray/src-tauri`:
  - `kyberdash-tray`, edition 2021, `rust-version = "1.77"`;
  - lib plus bin; no workspace; `Cargo.lock` committed; no `rust-toolchain.toml`;
  - `tokio` plus `reqwest` (blocking, no TLS);
  - `thiserror` and `anyhow` both present;
  - glib patched to a vendored copy for an advisory.
  
  The gates are `cargo fmt --check`, `cargo clippy -- -D warnings` and `cargo test`. `review.task-gates.tauri-dev` runs `cargo-fmt` and `cargo-clippy`.
- **Tauri reality.**
  - Tauri `2.11`; desktop bundles only; one `popover` window.
  - CSP `default-src 'self'; connect-src ipc: http://ipc.localhost`.
  - Capability `tray.json` lists the tray's own commands plus event listen and unlisten.
  - Contract tests `tests/{capability_contract,runtime_contract,login_lifecycle_contract}.rs`.
  - `supervisor.rs` spawns the engine.
- **Go reality.** No `go.mod` and no `.go` files in the repository.
- **Test placement.** See D2. The existing theory `ALanguageChecklistExistsAndPointsAtItsDeclaredStandard` checks only that a checklist has a pointer token, and it already passes. The new assertion has to read the real Config Reg and the real standards.
- **Stale canonical prose.** `docs/code-review/architecture.md` lines 153-154 and `docs/standards/README.md` lines 17-18 go stale once the declaration lands. `REVIEW.md` line 346-348 stays correct.

## 5. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs`, new theory `ADeclaredLanguageStandardIsRegisteredCurrentAndNamedByItsChecklist`, rows `typescript`, `rust`, `tauri`, `go` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class "*ReviewGuidanceCouncilContractTests"` | For each technology:<br>(a) The root `AGENTS.md`, between `<!-- KYBER_WEAVE_CONFIG_REG_START -->` and `<!-- KYBER_WEAVE_CONFIG_REG_END -->`, contains ``- **<tech-coding-standard>**: `docs/standards/<tech>/README.md` ``.<br>(b) That file exists.<br>(c) Its frontmatter block only, not the body, has `doc-type: coding-standard`, `technology: <tech>` and `status: current`.<br>(d) `references/<tech>.md` contains `docs/standards/<tech>/README.md`.<br>Each assertion message names the missing token or file. | The `rust`, `tauri` and `go` rows fail on (a), the missing Config Reg line. The `typescript` row passes, which proves the assertion is satisfiable. Record the three failure messages. | Same theory, all four rows pass, assertions unchanged. The whole class still passes. |
| T2 | Same theory, rows `rust`, `tauri`, `go`, clauses (a)-(c) | Same command | The Config Reg declares the three properties, and the three standards exist and are current. | T1's RED | (a)-(c) pass for all three rows. (d) may still fail until T3 lands. |
| T3 | Same theory, clause (d), and the existing theory `ALanguageChecklistExistsAndPointsAtItsDeclaredStandard` | Same command | Each checklist names its in-repo path and keeps its `coding-standard>` and `standard declared` tokens. | T1's RED, clause (d), for the rust, tauri and go rows | (d) passes. The existing theory is unchanged and passing. |
| T4 | No test: documentation-only verification | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` and `... -- docs drift .` | Zero findings. | n/a (verification task) | Both report zero findings. Canonical prose matches H1 and H3. |

## 6. Tasks

### T1: RED contract test (`test-dev`)

- **Objective.** Add the theory from §5 to `ReviewGuidanceCouncilContractTests`.
- **Files.** `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs` only.
- **Acceptance.**
  - Stable-token assertions in the file's existing style. Path constants come from `KyberWeaveTestPaths.ToolRoot`.
  - The frontmatter check reads only the leading `---` block.
  - Update the class `<summary>` to mention the declared-standard pin.
  - Do not change the existing tests.
  - Run the command, then record RED for `rust`, `tauri` and `go` and the pass for `typescript`.
  - `task-gate` passes.
- **Dependencies.** None.
- **Skills.** `test-dev`, `task-gate`.

### T2: Declare and author the three standards (`docs-dev`)

- **Objective.** Declare `rust`, `tauri` and `go`, and write grounded `current` standards.
- **Files.**
  - `.kyber-weave/kyber-weave.yml`: `ontology.technologies` gains `rust`, `tauri`, `go`, and the comment above the list says what each covers.
  - `AGENTS.md`: the Config Reg block only, changed by the tool.
  - `docs/standards/rust/README.md`, `docs/standards/tauri/README.md`, `docs/standards/go/README.md`.
  - `docs/standards/README.md` (H3).
- **Steps.**
  1. Edit the yml.
  2. Run `dotnet run --project src/KyberWeave.Cli -c Release -- docs init . --no-skill` (Q3). Do not use `--force` or `--kyber-standards`.
  3. Run `git status --short` and confirm that only the files above changed. Revert anything else `docs init` touched.
  4. Write each standard. Start from the template under `products/kyber-squad/standards/<tech>/README.md` and apply D3-D6 and D11.
     - Frontmatter: `id: standards/<tech>`, `doc-type: coding-standard`, `status: current`, `technology: <tech>`, `owner: dpalfery`, `last-reviewed` set to the edit date.
     - Remove the template's "Template." callout.
     - Opening paragraph in the TypeScript standard's form: the document resolves as `<tech-coding-standard>` in the root `AGENTS.md`, and states its scope.
     - The tray gate commands exactly as in the root `AGENTS.md`.
     - Tauri links the display-layer rule and defers language rules to the Rust standard.
     - No rule text copied from `REVIEW.md` (Q4).
- **Acceptance.**
  - T2's GREEN clauses (a)-(c).
  - `docs validate .` zero findings for these files.
  - The digest carries the D6 grounding list (each factual claim → file, line or command) and the known existing exceptions.
- **Dependencies.** T1 (RED recorded first).
- **Skills.** `docs-dev`, `app-docs-standard`.

### T3: Point the checklists at the declared standards (`docs-dev`)

- **Objective.** Add the D7 parenthetical to the three checklists and update the H1 prose.
- **Files.**
  - `products/kyber-squad/skills/code-review/references/rust.md`
  - `products/kyber-squad/skills/code-review/references/tauri.md`
  - `products/kyber-squad/skills/code-review/references/go.md`
  - `docs/code-review/architecture.md` (Technology checklists bullet only)
- **Acceptance.**
  - T3's GREEN.
  - The fallback text, `standard declared` wording and Review traps are unchanged.
  - The H1 sentence no longer says the three are undeclared.
- **Dependencies.** T1.
- **Skills.** `docs-dev`.

### T4: Verify and close out (`docs-dev`)

- **Objective.** Run the full verification gates (§9) and complete closeout.
- **Steps.**
  1. Run `docs validate .` and `docs drift .`, then the T1 runner, all green.
  2. Closeout: map Q1-Q4, D1-D11, AP0-AP1 and H1-H3 in a Closeout mapping.
  3. Archive this plan to `docs/archive/plans/` and move its index row to Archived Plans.
  4. Archive the source todo and remove its row from the todo index's Open table. Do this only at closeout, after T1-T3 are done and verified. Until then the todo stays `superseded`, links to this plan, and keeps its Open-table row.
  5. Run `docs validate . --merge-ready` clean.
- **Dependencies.** T2, T3.
- **Skills.** `docs-dev`.

## 7. Dependency graph

```text
T1 (RED) ──┬─> T2 ──┐
           └─> T3 ──┴─> T4
```

T2 and T3 have disjoint file scopes and run in parallel.

## 8. Risks

- **`docs init` side effects.** It may touch more than the Config Reg: `.kyber-weave/.gitignore`, or an `AGENTS.md` newline style. T2 step 3 checks `git status` and reverts.
- **A canonical-content digest pin over code-review references.** One may fail when the checklists change. `SquadCanonicalContentTests` and `SkillResourceDispositionAuditTests` run in the full suite (§9). If they fail, update the pin in the same PR. That is not weakening a contract: the content change is intended.
- **A `current` standard stating a rule that existing tray code breaks.** Mitigated by D6: rules for new and changed code, with exceptions disclosed.
- **Stacked PR drift.** If #440 changes the checklists or the test class before merging, T1 and T3 rebase onto it.

## 9. Verification gates

- `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json` (the declared suite), or the "Before opening a PR" list in the root `AGENTS.md`.
- `docs validate . --merge-ready` after archive, and `docs drift .`.
- The tray Rust gates are not required: no Rust changed.

## 10. Review and closeout

- Whole-change `code-reviewer` council at the end. Expected verdict: `NEEDS_HUMAN` (`KW-REVIEW-008`, D9). The owner reviews before merge.
- `docs-dev` closeout in T4. It maps every decision and harvest id to a governed document or a documentation waiver.

## Closeout

Closeout by `docs-dev`, 2026-10-27. Scale `extends-component` (ReviewCouncil). The `## Decisions`
table is unchanged in content; the AP0 and AP1 rows were outside the table because a blank line
split them, and they are now inside it. Each harvested item below has `documentation-impact:
extends-component`, impact component ReviewCouncil, `last-reviewed` 2026-10-27 on its destination,
and a status. Paths are relative to this archived location, `docs/archive/plans/`.

**Archive status: archived 2026-10-27.** The plan was staged with `git add` (authorized by the
coordinator), then moved with `git mv` to `docs/archive/plans/`. Its source todo moved with it.

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| Q1 | [`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml) `ontology.technologies` declares `rust`, `tauri`, `go`; [coding standards index](../../standards/README.md) "This repository declares" paragraph | ReviewCouncil | 2026-10-27 | archived |
| Q2 | [Rust](../../standards/rust/README.md), [Tauri](../../standards/tauri/README.md) and [Go](../../standards/go/README.md) standards, frontmatter `status: current`; grounding per D6 | ReviewCouncil | 2026-10-27 | archived |
| Q3 | [Coding standards index, "Declaring a technology"](../../standards/README.md#declaring-a-technology): edit the list, then `docs init`, never hand-edit the registry block | ReviewCouncil | 2026-10-27 | archived |
| Q4 | [Coding standards index, "What belongs in a standard"](../../standards/README.md#what-belongs-in-a-standard): review rules stay in REVIEW.md and are not restated. [REVIEW.md](../../REVIEW.md) unchanged | ReviewCouncil | 2026-10-27 | archived |
| D1 | Documentation waiver: the stacked PR target and retarget to `main` are transient branch mechanics. No surviving invariant | ReviewCouncil | 2026-10-27 | archived |
| D2 | Documentation waiver: test-class placement is test-fixture scoped. The invariant it pins (declared standards are current and named by their checklists) is documented at [coding standards index](../../standards/README.md) and [review guidance](../../code-review/architecture.md#review-guidance) | ReviewCouncil | 2026-10-27 | archived |
| D3 | Owner `dpalfery` on each standard's frontmatter, [Rust](../../standards/rust/README.md), [Tauri](../../standards/tauri/README.md), [Go](../../standards/go/README.md); the owner check is `KW-DOC-SPEC-004` | ReviewCouncil | 2026-10-27 | archived |
| D4 | [Rust standard, intro and crate layout](../../standards/rust/README.md) (Rust governs the tray crate); [Tauri standard, "Authority and scope"](../../standards/tauri/README.md#authority-and-scope) (Tauri defers language rules to Rust) | ReviewCouncil | 2026-10-27 | archived |
| D5 | [Tauri standard](../../standards/tauri/README.md): Stack, Layout (no sidecar, desktop only, commands in `lib.rs`), Network and the webview boundary, IPC contract, Capabilities, Window and configuration | ReviewCouncil | 2026-10-27 | archived |
| D6 | [Coding standards index, "What belongs in a standard"](../../standards/README.md#what-belongs-in-a-standard) (grounding rule; existing exceptions listed with location). Known exceptions in [Rust standard, "Existing exceptions"](../../standards/rust/README.md#existing-exceptions) | ReviewCouncil | 2026-10-27 | archived |
| D7 | Checklists `products/kyber-squad/skills/code-review/references/{rust,tauri,go}.md` name the in-repo standard path. [Review guidance](../../code-review/architecture.md#review-guidance) (H1) | ReviewCouncil | 2026-10-27 | archived |
| D8 | Documentation waiver: no specialist reviewer per task is a planning choice for this change. No surviving invariant | ReviewCouncil | 2026-10-27 | archived |
| D9 | Documentation waiver: the expected `NEEDS_HUMAN` (`KW-REVIEW-008`) is a transient review outcome, recorded in Closeout evidence below | ReviewCouncil | 2026-10-27 | archived |
| D10 | Documentation waiver: the out-of-scope list is transient to this change. The unchanged items are still described by their own canonical docs | ReviewCouncil | 2026-10-27 | archived |
| D11 | [Go standard](../../standards/go/README.md): "Status: no Go code yet", "Modules and build" (`go.sum` committed, `CGO_ENABLED=0` for shipped binaries), and "Gates" (no Go gate exists today) | ReviewCouncil | 2026-10-27 | archived |
| AP0 | Documentation waiver: a pre-execution approval gate, transient. Provenance is the Decisions table | ReviewCouncil | 2026-10-27 | archived |
| AP1 | Documentation waiver: the execution approval gate, transient. Provenance is the Decisions table | ReviewCouncil | 2026-10-27 | archived |
| H1 | [Review guidance](../../code-review/architecture.md#review-guidance), Technology checklists bullet: all three declared, each checklist names its standard, Go applies when Go is introduced | ReviewCouncil | 2026-10-27 | archived |
| H2 | [Rust](../../standards/rust/README.md), [Tauri](../../standards/tauri/README.md), [Go](../../standards/go/README.md) standards (coding-standard doc type) | ReviewCouncil | 2026-10-27 | archived |
| H3 | [Coding standards index](../../standards/README.md): declared list names C#, test, TypeScript, Rust, Tauri and Go | ReviewCouncil | 2026-10-27 | archived |

**ADR disposition:** no ADR — ADR waiver: declares standards through the existing mechanism
(`ontology.technologies` plus `docs init`), with no new boundary, dependency, schema or rule id.

### Documentation-impact review (DOCS_REVIEW)

Run record: each changed file was reviewed by reading it, and each standard claim was checked
against source (Cargo manifests, `tauri.conf.json`, `capabilities/tray.json`, `permissions/tray.toml`,
`lib.rs`, `supervisor.rs`, `ipc.rs`, `api.rs`, `tray_badge.rs`, `capability_contract.rs`).

| Change | DOCS_REVIEW | RECOMMENDATIONS |
|---|---|---|
| `.kyber-weave/kyber-weave.yml` | update | Done: three technologies declared with a comment on each |
| `AGENTS.md` (Config Reg block only) | update | Done: regenerated by `docs init`; three registry rows |
| `docs/standards/rust/README.md`, `tauri/README.md`, `go/README.md` (new) | update | Done: H2. Go gained "Modules and build" to match D11 |
| `docs/standards/README.md` | update | Done: H3; template count corrected from 11 to 14 with the three new technologies; grounding rule and REVIEW.md boundary added |
| `products/kyber-squad/skills/code-review/references/{rust,tauri,go}.md` | update | Done: D7 path, wrapped |
| `docs/code-review/architecture.md` | update | Done: H1 (stale "no declared standard" sentence removed) |
| `docs/plans/README.md` | update | Done: the plan moved to Archived Plans; Active reads "None" |
| `docs/todo/README.md` and `docs/todo/review-council-rust-tauri-go-standards.md` | update | Done: todo moved to `docs/archive/todo/`, Open row moved to Closed |
| `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs` | no doc change with justification | Test code, test-dev scope |
| `REVIEW.md` lines 346-348 (technology map) | no doc change with justification | Still correct: rust, tauri and go rows route the same way |
| `products/kyber-squad/agents/tauri-dev.md` line 20 | missing (non-doc, reported) | Says the standard covers "sidecar packaging". The tray has no sidecar. Product agent prompt, not docs scope: needs an owner decision |

### Closeout evidence

- **Known existing exceptions (D6, for the owner).** Not fixed here (D10):
  - `dash/tray/src-tauri/src/api.rs:71` `report_url` and `ipc.rs:183` `open_view_url` return `anyhow::Result` from public library functions.
  - `ipc.rs:102` `expect` in `view_path_patterns`.
  - `lib.rs:459` `expect` on the popover window lookup, and `lib.rs:505` `expect` on the `tauri::generate_context!` build.
  - `tray_badge.rs:11` `unsafe` call with no `// SAFETY:` comment. The crate root has no `#![deny(unsafe_op_in_unsafe_fn)]`.
  - Clippy does not deny `unwrap_used`, `expect_used` or `undocumented_unsafe_blocks`; the gate is `-D warnings` only.
- **Targeted test:** `ReviewGuidanceCouncilContractTests`, 26 of 26 passed on 2026-10-27 (docs-dev run).
- **Docs gates:** `docs validate . --merge-ready` and `docs drift .` are recorded in the run digest. Merge-ready validation fails only while this plan is in `docs/plans/` (`KW-DOC-LIFECYCLE-003`).
