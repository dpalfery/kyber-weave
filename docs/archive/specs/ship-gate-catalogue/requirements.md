---
id: archive/specs/ship-gate-catalogue/requirements
title: Shipped review-gate catalogue requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-10-27
documentation-impact: extends-component
impact-components:
  - ReviewCouncil
  - DocGraph
  - KyberSquad
  - CI Pipelines
  - Distribution
component: ReviewCouncil
keywords:
  - gate catalogue
  - deterministic checks
  - review gates
  - task gates
  - per-technology checks
  - docs init review block
---

# Requirements Document

**Status:** Archived
**Archive Date:** 2026-10-27
**Autonomy:** autonomous
**Development mode:** test-first

## Introduction

The owner asked that installing Kyber-Weave into any repository brings the per-technology
command-line checks with it: the deterministic gates the review council runs, the per-task
gates each developer agent runs before handing off, and a CI pipeline that runs them. The
request assumed C# already works this way. It does not. Verified against the code at
`1d2439ae`:

- `ReviewConfig.ProductDefaults` is empty: no `Gates`, no `TaskGates`
  (`src/KyberWeave.Core/Configuration/ReviewConfig.cs`). `ReviewConfigLoader.Merge` returns
  those defaults when a host has no `review:` section.
- `docs init` writes a host configuration holding only `ontology` and a commented
  `config-reg` (`DocsScaffolder.HostConfig`). It writes no `review:` section, with or without
  `--kyber-standards`.
- `--kyber-standards` ships 14 coding-standard templates (`KyberStandardsTemplates`), but no
  gates come with them.
- `templates/github-actions/` holds the docs, skill and agent gate workflows; none runs
  `kyber-weave review gates`. No Kyber-Weave command writes a workflow into a host's
  `.github/workflows/`; the templates are copy-ready samples.
- `squad install` deploys agent and skill files only, so the `task-gate` skill in an
  installing repository reports that no gates are declared.
- A council gate (`ReviewGate`) has only `id`, `run`, `blocking` and `timeout`. It has no
  `include` or `os`, so it always runs. The YAML loader ignores unknown keys, so an `include`
  written on a council gate is silently dropped.
- Nothing links a gate to `ontology.technologies`. `cargo`, `go`, `tsc` and `dotnet` take a
  project root rather than a file list, and this repository's gates hard-code its own roots
  (`KyberWeave.sln`, `dash`, `dash/tray/src-tauri/Cargo.toml`).

This repository's own gates in [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml)
are the reference set for .NET, TypeScript, and Rust/Tauri. Go and Python have none yet.

This feature adds a **gate catalogue**: per-technology council gates and task gates shipped
inside the release. `docs init` writes them as an ordinary, editable `review:` block for the
technologies the host declares, so the host owns them from then on. "Catalogue" is used
rather than "defaults" because nothing is applied at load time: a gate runs only if it is
written in the host's file.

**Version 1 technologies** (each a slug in `KyberStandardsTemplates`): `csharp`,
`typescript`, `react`, `rust`, `tauri`, `go`, `python`. The other declared slugs (`azure`,
`data-access-layer`, `github-actions`, `maui`, `pulumi`, `sql`, `test`) have no catalogue
entry of their own in version 1; the .NET-based ones are covered through `csharp` (see
Requirement 6).

## Requirements

### Requirement 1 — A shipped gate catalogue

**User Story:** As a developer installing Kyber-Weave, I want per-technology checks to come
inside the product, so that I don't have to research and hand-write format, lint, build and
test commands for each stack.

#### Acceptance Criteria

1.1. The release SHALL contain a gate catalogue entry for each version 1 technology, readable
without network access.
1.2. Each catalogue entry SHALL declare council gates (`review.gates`) and task gates
(`review.task-gates`, keyed by developer agent slug) for its technology.
1.3. Each catalogue entry SHALL cover, where the technology's standard toolchain provides
one, a format check, a lint or static-analysis check, a type check or build, and a test run.
1.4. Every command in the catalogue SHALL be argv, never a shell command line, and SHALL pass
`ReviewConfigLoader` validation unchanged.
1.5. Every command in the catalogue SHALL be checked against the tool vendor's current
documentation, with the source recorded in the design. WHERE that documentation does not state
the command's exit behaviour THEN the command SHALL count as confirmed only after the R9.6
Linux run shows it passing on clean code and failing on its seeded defect. A command confirmed
by neither SHALL NOT ship in the release.
1.6. Every catalogue gate id SHALL be unique across all catalogue entries, so that any
combination of technologies can be written into one `review:` block without a duplicate id.
1.7. Each catalogue gate SHALL either run on Linux, macOS and Windows without a shell, or
declare `os` for the platforms it runs on.
1.8. IF a declared technology has no catalogue entry THEN `docs init` SHALL report that no
gates are shipped for it and SHALL NOT fail.

### Requirement 2 — `docs init` writes the gates into the host's configuration

**User Story:** As a developer adopting Kyber-Weave, I want `docs init` to write the checks for
my stacks into my configuration, so that I can see exactly what runs and change it.

#### Acceptance Criteria

2.1. WHEN `docs init` runs AND the host configuration has no `review:` section AND
`ontology.technologies` declares at least one technology with a catalogue entry THEN
`docs init` SHALL write a `review:` section holding the catalogue gates for those
technologies.
2.2. WHEN `docs init` creates a new host configuration THEN the `review:` section SHALL be
part of that file; WHEN the host configuration already exists without a `review:` section
THEN `docs init` SHALL add the section and SHALL leave every other byte of the file unchanged.
2.3. WHEN `docs init --kyber-standards` declares technologies THEN the gates written SHALL be
those of the technologies declared after that merge, subject to Requirement 4.
2.4. The written section SHALL mark each gate with a comment naming its technology, so the
host can find and remove a stack's gates.
2.5. WHEN `docs init` writes gates THEN its output SHALL list, per technology, the gate ids it
wrote and the gates it wrote commented out, with the reason for each.
2.6. IF `ontology.technologies` declares no technology with a catalogue entry THEN `docs init`
SHALL write no `review:` section, so behaviour for such a host is unchanged.
2.7. WHERE the operator passes the opt-out option THEN `docs init` SHALL write no gates and
SHALL say so in its output.
2.8. The configuration `docs init` writes SHALL load through `KyberWeaveConfigLoader` without
a configuration error.

### Requirement 3 — Re-running and upgrading never overwrite the host's gates

**User Story:** As a developer who has edited the gates, I want re-running `docs init` and
upgrading Kyber-Weave to leave my gates alone, so that my changes are never lost and my CI
never changes behind my back.

#### Acceptance Criteria

3.1. WHEN `docs init` runs AND the host configuration already has a `review:` section THEN
`docs init` SHALL NOT change any byte of that section, with or without `--force`.
3.2. WHEN `docs init` leaves an existing `review:` section in place AND a declared technology
has catalogue gate ids absent from it THEN `docs init` SHALL list those ids as information
and SHALL exit with the same code as if none were missing.
3.3. WHEN `docs init` runs twice in a row on the same repository THEN the second run SHALL
leave the host configuration byte-identical to the first run's result.
3.4. The operator SHALL be able to print a technology's catalogue gates as YAML to standard
output without writing any file, so that gates added in a later release can be copied in by
hand.
3.5. `ReviewConfig.ProductDefaults` SHALL remain empty: upgrading Kyber-Weave SHALL NOT change
which gates run for a host whose configuration did not change.

### Requirement 4 — Gates point at the host's real project roots

**User Story:** As a developer whose projects do not sit at the repository root, I want the
written gates to target my actual solution, package or crate, so that they run against my
code rather than failing on a path from another repository.

#### Acceptance Criteria

4.1. Each catalogue gate that needs a project root SHALL state which marker file identifies
that root for its technology (for example a solution file, `package.json`, `Cargo.toml`).
4.2. WHEN `docs init` finds exactly one root for a technology THEN it SHALL write that
technology's gates targeting that root.
4.3. WHEN `docs init` finds more than one root for a technology THEN it SHALL write one copy of
each root-bound gate per root, each with a distinct id derived from the root's path.
4.4. IF `docs init` finds no root for a declared technology THEN it SHALL write that
technology's root-bound gates commented out, with a comment stating the marker it looked for.
4.5. Root detection SHALL skip dependency and build-output directories and directories ignored
by version control, and SHALL order roots by ordinal path, so the same tree always yields the
same output.
4.6. A written task gate bound to a root SHALL limit its `include` globs to files under that
root.
4.7. IF a catalogue tool cannot be given its project root as an argument THEN the gate schema
SHALL provide an optional, repository-relative working directory for the gate, AND the loader
SHALL refuse a working directory that is absolute, leaves the repository, or does not exist.
4.8. No written gate SHALL contain a path taken from this repository's own layout.

### Requirement 5 — Council gates can be scoped to the files a change touches

**User Story:** As a developer with several stacks in one repository, I want a council gate to
run only when its stack changed, so that a documentation-only change does not wait for every
toolchain to build and test.

#### Acceptance Criteria

5.1. A `review.gates` entry MAY declare `include` globs, with the same matching rules as a task
gate's `include`.
5.2. A `review.gates` entry MAY declare `os`, with the same values and meaning as a task gate's
`os`.
5.3. WHEN `review gates` runs AND a gate's `include` matches no changed file THEN the gate SHALL
be reported skipped with `KW-REVIEW-026`, and SHALL NOT be reported passed.
5.4. The changed set for council gates SHALL be the files changed between the merge base of a
base reference and the working tree, resolved the same way as `docs validate --merge-ready
--base`.
5.5. IF the changed set cannot be determined (for example an unresolvable base or a shallow
checkout) THEN every gate with `include` SHALL run, and the report SHALL say why no gate was
skipped.
5.6. A `review.gates` entry with no `include` SHALL run on every change, as today.
5.7. WHEN a `review.gates` or `review.task-gates` entry carries a key the loader does not know
THEN the loader SHALL report a warning naming the key and the gate, rather than dropping it
silently.

### Requirement 6 — Developer agents in an installing repository get task gates

**User Story:** As a developer using Kyber Squad in my own repository, I want each developer
agent to run its stack's checks before handing off, so that the per-task exit gate works
outside the Kyber-Weave repository.

#### Acceptance Criteria

6.1. Each catalogue entry SHALL name the developer agent slugs whose task gates it supplies.
For version 1: `csharp` supplies `csharp-dev`, `dal-dev`, `maui-dev` and `pulumi-dev`;
`typescript` supplies `typescript-dev`; `react` supplies `react-dev`; `rust` supplies
`rust-dev`; `tauri` supplies `tauri-dev`; `go` supplies `go-dev`; `python` supplies
`python-dev`.
6.2. WHERE several written technologies supply the same agent slug THEN the written
`task-gates` entry for that agent SHALL contain the gates of each, with no duplicate id.
6.3. `test-dev` SHALL receive the format and lint task gates of every written technology.
6.4. WHEN a developer agent runs the `task-gate` skill in a host whose configuration
`docs init` wrote with gates for that agent's technology THEN the skill SHALL run those gates
and SHALL NOT report that no gates are declared.
6.5. WHERE a tool accepts an output-directory option THEN a catalogue task gate that builds
SHALL direct its output under the per-task `{task}` path, so parallel workers do not contend
for one output tree.
6.6. WHEN `squad install` or `squad update` deploys developer agents into a host whose
configuration declares no `review.task-gates` THEN it SHALL print one line naming the
`docs init` step that writes them, and SHALL NOT write any configuration itself.

### Requirement 7 — A copy-ready review-gate CI workflow

**User Story:** As a developer adopting Kyber-Weave, I want a CI workflow that runs the same
gates on every pull request, so that the checks hold even when no agent ran them.

#### Acceptance Criteria

7.1. The product SHALL ship `templates/github-actions/kyber-weave-review-gate.yml`, which runs
`kyber-weave review gates` against the repository.
7.2. The workflow SHALL derive `--base` from the pull-request target or the pushed range, the
same way `kyber-weave-docs-gate.yml` does, and SHALL check out full history.
7.3. The workflow SHALL install Kyber-Weave the same way the existing templates do, pin every
third-party action to a commit SHA, and grant only `contents: read`.
7.4. The workflow SHALL contain a toolchain setup step for each version 1 technology, each
disabled until the host enables it, with a comment naming the technology.
7.5. WHEN any blocking gate fails THEN the workflow job SHALL fail.
7.6. Event values SHALL reach shell steps only through `env`, never interpolated into `run:`.
7.7. `templates/github-actions/README.md` SHALL list the new workflow beside the existing
three.

### Requirement 8 — Failures are clear

**User Story:** As a developer whose gate fails, I want to know whether the check failed or the
tool is missing, so that I fix the right thing.

#### Acceptance Criteria

8.1. IF a gate's program is not found on `PATH` THEN the gate result SHALL name the missing
program and SHALL be distinguishable from the check itself failing.
8.2. Existing exit codes of `review gates` and `review gates --agent` SHALL keep their current
meanings.

### Requirement 9 — Verified by tests and a real run

**User Story:** As the maintainer, I want the catalogue proven by automated tests and by real
toolchains, so that a shipped command is known to work rather than assumed to.

#### Acceptance Criteria

9.1. A test SHALL load every catalogue entry through `ReviewConfigLoader` and fail on any
validation error.
9.2. For each version 1 technology, a test SHALL run `docs init` over a fixture repository
holding a minimal project for that technology and compare the written `review:` section with
an approved expected file.
9.3. Tests SHALL cover: no root found (4.4), several roots (4.3), an existing `review:` section
left untouched with and without `--force` (3.1), a second run byte-identical (3.3), the
opt-out (2.7), and an existing host file with no `review:` section (2.2).
9.4. Tests SHALL cover council `include`: matched, unmatched and skipped (5.3), unknown changed
set runs the gate (5.5), and `os` skipping (5.2).
9.5. Tests SHALL cover the unknown-key warning (5.7) and the missing-program result (8.1).
9.6. Each version 1 technology's catalogue gates SHALL be run at least once on Linux against
its fixture project with the real toolchain, passing on clean code and failing on a seeded
defect, before the feature is reported complete.
9.7. Each new test SHALL be written failing before the code that makes it pass.

### Requirement 10 — Documentation

**User Story:** As a developer reading the docs, I want the install and review guides to say
what gates I get and how to change them, so that I don't have to read the source.

#### Acceptance Criteria

10.1. [`docs/code-review/architecture.md`](../../../code-review/architecture.md) SHALL describe the
catalogue, the materialisation rules, and `include` and `os` on council gates.
10.2. [`docs/docgraph/onboarding.md`](../../../docgraph/onboarding.md) SHALL list the `review:`
section among the things `docs init` writes, with the opt-out and the re-run rule.
10.3. [`docs/install.md`](../../../install.md) SHALL state what an installing repository gets and
which step writes it.
10.4. [`docs/ci-pipelines/workflows-runbook.md`](../../../ci-pipelines/workflows-runbook.md) SHALL
describe the review-gate workflow.
10.5. [`docs/todo/task-gate-config-review.md`](../../../todo/task-gate-config-review.md) SHALL be
updated to say whether the catalogue's .NET agent mapping (6.1) answers its first question.

## Non-goals

- Applying gates at load time when a host declares none. Rejected in D1.
- Writing into a host's `.github/workflows/`. The workflow ships copy-ready, like the
  existing three templates, because it changes the host's CI and may need secrets or
  runners the product cannot know.
- Installing toolchains (`dotnet`, `node`, `cargo`, `go`, Python tools) on a developer's
  machine.
- Running only the tests that cover a task's changed files. That stays in
  [task-gate test-project mapping](../../../todo/task-gate-test-project-mapping.md).
- Catalogue entries for `azure`, `data-access-layer`, `github-actions`, `maui`, `pulumi`,
  `sql` and `test` as technologies of their own.
- Coverage floors, duplicate thresholds, or policy defaults in the written `review:` section.
- Azure DevOps or other CI providers.
- Changing this repository's own `review:` section beyond what the new keys allow.
- A `fix` command on council gates. The council fixes nothing.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How does an installing repository get the gates: hidden load-time defaults, or written configuration? | `docs init` writes an editable `review:` block for the declared technologies (Requirement 2); `ProductDefaults` stays empty (3.5). | Hidden defaults would change what runs on every upgrade with no diff in the host repo. Written config is visible, reviewable and host-owned, matching the rule that `docs init` never overwrites host config. Expensive to reverse: it is a configuration contract. | autonomous: recommendation accepted (architect); low-confidence, no authoritative source |
| D2 | How does a council gate avoid running for a stack the change did not touch? | Optional `include` (and `os`) on `review.gates`, same rules and `KW-REVIEW-026` skip as task gates (Requirement 5). | Reuses an existing, documented contract instead of inventing one. Fails closed: an unknown changed set runs the gate (5.5). | autonomous: recommendation accepted (architect); low-confidence, no authoritative source |
| D3 | Is writing gates the default or opt-in? | Default when technologies are declared, with an opt-out option (2.7). | The owner's request is that installing brings the checks. A host with no declared technology is unchanged (2.6). Cheap to reverse before release. | autonomous: low-confidence, no authoritative source |
| D4 | What does re-running `docs init` do to an existing `review:` section? | Leaves it byte-for-byte, even with `--force`; lists missing catalogue ids; a print command gives the YAML to copy (Requirement 3). | Matches the existing rule that `--force` never overwrites host config (DocGraph onboarding). Avoids a YAML merge that could break comments and anchors. | autonomous: low-confidence, no authoritative source |
| D5 | How do gates find a project root that differs per host? | Detect marker files at `docs init`; one gate copy per root; comment out when none found; add a working-directory key only where a tool takes no root argument (Requirement 4). | Exact mechanism (marker list, id derivation, whether a working-directory key is needed) is decided in design. | autonomous: low-confidence, no authoritative source |
| D6 | Does the CI workflow get written into the host? | No: copy-ready template only (Requirement 7, Non-goals). | Consistent with the three existing templates; no Kyber-Weave command writes `.github/workflows/` today. The owner's "when it installs its CI pipelines" maps to copying the template. | autonomous: low-confidence, no authoritative source |
| D7 | Should an unknown key on a gate be an error? | A warning, not an error (5.7). | An error would break hosts that carry extra keys today; a warning ends the silent drop. | autonomous: low-confidence, no authoritative source |
| D8 | Which Go and Python commands ship? | Not named here. Design confirms each against current vendor documentation through `research-agent` (1.5). | The architect flagged the Go (`gofmt`, `go vet`, `golangci-lint`, `go test`) and Python (`ruff`, `mypy` or `pyright`, `pytest`) candidates as unverified. | deferred to design |
| D20 | R1.5 said an unconfirmed command must not ship, while the design deferred some exit codes to the R9.6 run. How do the two fit? | R1.5 is reworded. The vendor docs confirm a command where they state its exit behaviour. Otherwise the R9.6 Linux run confirms it, and that run gates the release. A command confirmed by neither does not ship. | Raised by the docs-dev spec review on 2026-10-27. The intent is unchanged: no unproven command ships. | autonomous: recommendation accepted |
| D9 | Are these requirements approved? | Yes: requirements phase approved; proceed to design. | `docs validate` and `docs drift` reported no findings on the staged requirements. | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |

## Design harvest

The durable design answers to these requirements (H1–H10) are tabulated authoritatively in
[design](design.md#design-harvest); the rows below map each harvest id to the requirement it
answers so this file's closeout stands alone.

| ID | Answers | Lives in |
|---|---|---|
| H1 | R1: catalogue format, families, placeholders | [design](design.md#design-harvest) |
| H2 | R4: root detection and id derivation | [design](design.md#design-harvest) |
| H3 | R4, R5: `working-directory`, council `include` and `os`, changed set | [design](design.md#design-harvest) |
| H4 | R5, R8: new rule ids `KW-CONFIG-002`, `KW-REVIEW-033`, `KW-REVIEW-034` | [design](design.md#design-harvest) |
| H5 | R2, R3: `docs init` review block, `--no-review-gates`, re-run rule | [design](design.md#design-harvest) |
| H6 | R3: `review catalogue` command | [design](design.md#design-harvest) |
| H7 | R6: Squad task-gate hint | [design](design.md#design-harvest) |
| H8 | R7: review-gate CI workflow | [design](design.md#design-harvest) |
| H9 | R2: what an installing repository gets | [design](design.md#design-harvest) |
| H10 | R6, R10: answer to the .NET agent-mapping question | [design](design.md#design-harvest) |

## Closeout mapping

No ADR: these requirements refine the existing review architecture and established `docs init`
/ `review gates` patterns, so no new architectural decision constrains future work — no ADR,
ADR waiver: refines existing architecture.

| ID | Canonical destination |
|---|---|
| D1 | [DocGraph onboarding](../../../docgraph/onboarding.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| D2 | [Review council architecture](../../../code-review/architecture.md#council-gates) |
| D3 | [DocGraph onboarding](../../../docgraph/onboarding.md), [Install](../../../install.md) |
| D4 | [DocGraph onboarding](../../../docgraph/onboarding.md) |
| D5 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| D6 | [Workflows runbook](../../../ci-pipelines/workflows-runbook.md#the-review-gate-template) |
| D7 | [Rule reference](../../../ci-pipelines/rule-reference.md) |
| D8 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| D9 | Documentation waiver — requirements-phase approval record; no surviving behavioral invariant. |
| D20 | Documentation waiver — superseded wording decision; its effect is the shipped R1.5 text and the Linux confirmation run. |
| H1, H2 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H3 | [Review council architecture](../../../code-review/architecture.md#council-gates), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H4 | [Rule reference](../../../ci-pipelines/rule-reference.md), [Workflows runbook](../../../ci-pipelines/workflows-runbook.md) |
| H5 | [DocGraph onboarding](../../../docgraph/onboarding.md), [Install](../../../install.md) |
| H6 | [CLI command surface](../../../cli-command-surface.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H8 | [Workflows runbook](../../../ci-pipelines/workflows-runbook.md#the-review-gate-template) |
| H9 | [Install](../../../install.md), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H10 | [Task-gate config review](../../../todo/task-gate-config-review.md), [Windows Node gate probe](../../../todo/windows-node-gate-probe.md) |
