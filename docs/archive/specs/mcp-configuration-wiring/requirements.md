---
id: archive/specs/mcp-configuration-wiring/requirements
title: MCP configuration wiring requirements
doc-type: requirements
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-11-01
documentation-impact: extends-component
impact-components:
  - DocGraph
  - Distribution
keywords:
  - MCP wiring
  - harness configuration
  - docs init
  - MCP configuration
  - required servers
  - harness detection
  - repair receipt
---

# Requirements Document

**Status:** Archived
**Archive Date:** 2026-11-01
**Phase status:** Delivered and verified — every criterion below was checked against the shipped code and tests, not against this document. The phase approval (2026-10-31, conductor `AUTONOMOUS_APPROVAL: requirements-phase-approval`, provenance `autonomous: recommendation accepted`) is recorded as D15; the mid-execution amendment is D19. The council's re-review of the remediated change returned `APPROVE`, alongside the reserved-path `NEEDS_HUMAN` verdict that task 8 predicted and accepted (TK3). Durable content is mapped in [Closeout mapping](#closeout-mapping).
**Requirement amendment (2026-10-31, D19):** criterion 5.4 gained one clarifying sentence mid-execution, settling that a selected repair target is not gated on the in-play heuristic (2.2). Amending a requirement is a re-approval of it; the re-approval and its provenance are recorded in the Decisions table as D19 (a conductor-directed correction).
**Execution:** authorized 2026-10-31 (`AUTONOMOUS_APPROVAL: approve-and-execute`, provenance `autonomous: recommendation accepted`), recorded in [tasks.md](tasks.md) as TK7 and AP1.
**Autonomy:** autonomous
**Development mode:** test-first

## Introduction

The repository's own instruction files tell agents to use tools the host harness may not be
able to reach. The root [`AGENTS.md`](../../../../AGENTS.md) prescribes a CodeGraph-first
exploration order and the kyber-weave documentation MCP tools; every canonical agent carries
the shared discovery-order block naming `codegraph_explore` and `docs_explore` /
`docs_for_symbol` / `docs_glossary`; and the `architect` contract goes further — after every
plan write it runs exactly `docs_validate` and `docs_drift`, and states verbatim: "If either
tool is absent or the root differs, return `STATUS: BLOCKED` and name the gap"
([`products/kyber-squad/agents/architect.md`](../../../../products/kyber-squad/agents/architect.md)).
Nothing checks whether the host project actually has those MCP servers configured. The
consequence is observed, not hypothetical: an `architect` run on this repository returned
`STATUS: BLOCKED` naming the absence of `docs_validate` and `docs_drift`, because its
session had no kyber-weave MCP tools at all; two further architect sessions in the same
investigation found the docs MCP tools and the CodeGraph tool absent from their catalogs.

The repository already documents the wiring recipe per harness in
[`docs/docgraph/mcp-runbook.md`](../../../docgraph/mcp-runbook.md) — seven clients, five
incompatible document shapes — but a recipe nobody is told to follow at the moment of
adoption is not a configuration. The gap is detection and remedy, not documentation.

The feature has three parts, fixed by the owner's decisions (D1–D4 below):

1. **A declaration** of the MCP servers this repository's instructions rely on, in the host
   configuration `.kyber-weave/kyber-weave.yml` — the check needs a truth source that is
   not Squad's `toolchain.yml` roster.
2. **A check inside `docs init`**: after scaffolding, for each harness in play, whether each
   declared server is wired and launchable in that harness's repository-scope MCP
   configuration. Findings are warnings that never fail the command, printed with a
   copy-ready remedy, following the warn-with-manual-command idiom the command already owns
   (the APM-missing path, the retired-skill warnings).
3. **A repair in a separate command**, bounded: repository scope, per-target opt-in,
   insertion-only, receipted, refusal on malformed or foreign files, dry-run, and never a
   reformat of bytes it did not write.

### The interactivity tension, resolved

Owner decision D3 puts a confirmation prompt on the repair path, while `docs init` is today
a non-interactive scaffolder: it has no prompt and no consent flag anywhere (verified in
`src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs` and `DocsInitSettings.cs`). The
resolution (D5, Requirement 4) is that the prompt is **terminal-gated**:

- When the check found a fixable gap **and** stdin is an interactive terminal, `docs init`
  offers a yes/no naming exactly what would be written; confirming performs the repair.
- When stdin is not a terminal — redirected, piped, no TTY, CI — `docs init` **never
  prompts, never writes**, and the output ends with the printed manual repair command. The
  command cannot block on input it cannot receive, and automation cannot receive a surprise
  write.
- `docs init` gains no `--yes`-style consent flag in this slice. A non-interactive run that
  wants the repair invokes the dedicated repair command, whose explicit invocation is the
  consent (D1). A consent flag on a scaffolding command would silently widen a write
  surface; the rejected alternative is recorded in D5.

These requirements **are satisfiable inside `docs init` alone**: a terminal-gated prompt
that declines when stdin is redirected needs no new command and no interactive mode beyond
the prompt itself. The smallest honest alternative — `docs init` reports only and the
dedicated repair command carries the whole remedy — exists and is recorded, but is not
needed.

### Verified ground truth

- `docs init` scaffolds the host config, docs root, catalog, standards, and catalogue
  review gates, then shells out to `apm install dpalfery/kyber-weave`. It has no notion of
  harness MCP configuration.
- `src/KyberWeave.Cli/AGENTS.md` pins: "Kyber-Weave never installs external software as a
  side effect." Expected dependencies are reported with a manual command, never installed.
- `squad doctor` already contains the read-side precedent: `ReportZCodeMcpConfiguration`
  reads the toolchain's `required-mcp-tools` keys, keys on the `.zcode/` marker so
  non-users are reported `info`, and probes the `kyber-weave-mcp` binary via
  `McpProcessProbe`.
- `kyberdash kyber capture enable` is the repository's only existing writer of harness
  configuration keys: receipt-tracked with per-file digests, restores pre-enable values
  byte-for-byte on disable, refuses on drift (`drift` / `conflict` / `unparseable` /
  `unreadable`), and writes nothing in dry-run. It is the template for a safe writer.
- ADR 0026 is scoped to Kyber Utilities: deploy only Kyber-owned files, never create, edit,
  merge, back up, restore, or read a shared harness settings file "for any harness and any
  command", activation manual, the doctor never reads settings. It does not distinguish
  repository-scope from user-scope files. The boundary question this raised was decided as
  D16: the remedy writes repository-scope files under the R5 bounds, recorded in a new ADR
  before the remedy ships (D13, Requirement 8).

## Requirements

### Requirement 1 — Required servers are declared in the host configuration

**User Story:** As a repository owner, I want to declare which MCP servers my agent
instructions rely on, so that the wiring check measures against my declaration rather than
a Squad-internal roster.

#### Acceptance Criteria

1.1. The host configuration `.kyber-weave/kyber-weave.yml` SHALL provide a section
declaring the MCP servers the repository's agent instructions rely on. Each entry SHALL
name the server, its launch command as argv, and its provenance class: `first-party` (a
binary Kyber-Weave itself ships, today `kyber-weave-mcp`) or `third-party` (a binary the
user installs, today `codegraph`).
1.2. Each entry MAY declare a requirement level, `required` or `recommended`. An entry
with no level SHALL be treated as `required`.
1.3. WHEN `docs init` scaffolds a new host configuration THEN the scaffolded section SHALL
declare exactly one server: `kyber-weave`, launch `kyber-weave-mcp --repo-root .`,
first-party.
1.4. IF the section is absent or empty THEN the check SHALL report nothing and `docs init`
SHALL behave exactly as it does today.
1.5. The declaration SHALL be independent of Squad's `toolchain.yml` `required-mcp-tools`
roster and of `products/kyber-squad/mcp.json`. Neither SHALL be read by the check or the
remedy.
1.6. WHERE a repository's review policy lists the host configuration on
`review.policy.always-human` — as this repository's does — THEN any change to the
declaration SHALL escalate for human review by path alone, before any finding is weighed.
This consequence SHALL be stated plainly in the adoption documentation (Requirement 9).

### Requirement 2 — `docs init` checks harness MCP wiring after scaffolding

**User Story:** As a developer adopting Kyber-Weave, I want `docs init` to tell me which
harnesses in this repository cannot reach the servers my instructions need, so that I find
out at adoption time rather than from a blocked agent run.

#### Acceptance Criteria

2.1. WHEN `docs init` completes scaffolding successfully AND the opt-out option was not
passed THEN it SHALL check, for each harness in play, whether each declared server is wired
in that harness's repository-scope MCP configuration.
2.2. A harness SHALL count as in play when its repository-scope MCP configuration file
exists or its marker directory exists. Markers SHALL be per-harness; ZCode SHALL key on
`.zcode/` presence, the same marker `SquadTargetResolver` and `squad doctor` use.
2.3. The version 1 harness set and repository-scope files SHALL be: Factory
`.factory/mcp.json`; Claude Code `.mcp.json`; Cursor `.cursor/mcp.json`; VS Code
`.vscode/mcp.json`; Codex `.codex/config.toml`; opencode `opencode.json`; ZCode `zcode.json`
/ `.zcode/config.json`; and the Copilot coding agent, whose configuration is repository
settings, not a file.
2.4. IF a harness has neither a configuration file nor a marker THEN `docs init` SHALL
report one `info` line naming the marker it looked for and SHALL NOT raise a warning for
that harness. "Cannot detect" is a first-class outcome, not a failure: Claude Code has no
repository-scope marker other than `.mcp.json` itself, so a repository with neither is not
failed for it.
2.5. WHERE a harness's configuration is not a file in the tree (the Copilot coding agent)
THEN the check SHALL report the manual wiring snippet and the workflow-step requirement,
and SHALL NOT count that harness as fixable.
2.6. The check SHALL read repository-scope files only. It SHALL NOT read user-scope
configuration under the user's home directory for any harness, in the check or the remedy.
2.7. The check SHALL verify that a wired entry's launch command names a binary resolvable
on `PATH` at check time (the `McpProcessProbe` precedent), and SHALL report a wired but
unlaunchable entry as a finding rather than as wired.
2.8. The check SHALL write nothing and SHALL be deterministic: the same tree SHALL yield
the same report on every run, with findings ordered by harness then server.

### Requirement 3 — Report shape and exit codes

**User Story:** As a developer running `docs init`, I want each wiring gap reported with its
harness, file, server, and a copy-ready remedy, so that I can act on it without reading
source or the runbook.

#### Acceptance Criteria

3.1. Each finding SHALL name the harness, the configuration file, the server, and the gap
kind: not wired; wired with a different launch command (naming the difference); wired but
binary absent; or cannot detect.
3.2. Each warning SHALL carry a stable, permanent `KW-*` rule id and a hint naming the
remedy. New ids SHALL be added for these findings; no existing id SHALL be reused or
renumbered.
3.3. The remedy the report prints SHALL be the exact repair command to run, unfolded and
copy-ready — the same discipline as the existing cleanup-command warnings — not prose
instructions.
3.4. Wiring findings SHALL NOT change `docs init`'s exit code. A scaffold that succeeded
SHALL exit 0 with warnings, exactly like the existing degraded paths (APM missing, retired
skills). A declined, refused, or impossible repair SHALL NOT fail the command.
3.5. WHEN every declared server is wired and launchable for every harness in play THEN
`docs init` SHALL print one line confirming it.
3.6. A declaration the configuration loader rejects SHALL surface through the existing
`KW-CONFIG-001` configuration-failure path; `docs init` SHALL NOT silently ignore an
invalid declaration.

### Requirement 4 — The confirmation prompt and its non-interactive behaviour

**User Story:** As a developer at a terminal, I want to be asked before anything is written
into my harness configuration; as automation, I want `docs init` never to block on input
or write unprompted.

#### Acceptance Criteria

4.1. WHEN the check found at least one fixable gap AND stdin is an interactive terminal
AND the opt-out option was not passed THEN `docs init` SHALL offer a yes/no confirmation
that names exactly which files and which servers the repair would write.
4.2. WHEN the operator confirms THEN `docs init` SHALL perform the repair through the same
bounded write path as the dedicated repair command (Requirement 5) and SHALL report what
was written, per file, with the receipt location.
4.3. WHEN the operator declines THEN nothing SHALL be written, and the findings and the
printed manual repair command SHALL remain in the output.
4.4. WHEN stdin is not an interactive terminal — redirected, piped, or no TTY, as in CI —
THEN `docs init` SHALL NOT prompt, SHALL NOT write, and the output SHALL end with the
printed manual repair command. The command SHALL never block waiting for input it cannot
receive.
4.5. `docs init` SHALL acquire no consent flag in this slice. A non-interactive run that
wants the repair SHALL run the dedicated repair command, whose explicit invocation is the
consent.
4.6. WHERE the operator passes the opt-out option THEN `docs init` SHALL skip the check, the
prompt, and any repair, and SHALL say so in its output — the same symmetry as `--no-skill`
and `--no-review-gates`.
4.7. The prompt SHALL be offered only after scaffolding succeeded, so a failed scaffold
never reaches an interactive question.
4.8. Satisfiability: these requirements ARE satisfiable inside `docs init` alone. A
terminal-gated prompt that declines when stdin is redirected requires no new command and no
interactive mode beyond the prompt itself. Had they not been, the smallest honest
alternative would be `docs init` reporting only — the warning, the manual command, no
prompt — with the dedicated repair command carrying the whole remedy; that alternative is
not needed and is recorded here only so the boundary is explicit.

### Requirement 5 — A dedicated repair command with a bounded write policy

**User Story:** As a repository owner, I want wiring repaired by a command whose writes are
insertion-only, receipted, previewable, and removable, so that a committed configuration
file is never corrupted for my teammates and any change it makes can be undone.

#### Acceptance Criteria

5.1. The repair SHALL be a dedicated CLI command, separate from `docs init`: diagnosis and
repair are separate operations with separate commands (D1). Running it is explicit consent.
5.2. The repair SHALL write repository-scope files only. User-scope files (`~/.factory/mcp.json`,
`~/.cursor/mcp.json`, `~/.codex/config.toml`, `~/.config/opencode/opencode.json`, and ZCode
storage) SHALL be reported with the manual snippet and never written in this slice.
5.3. The repair SHALL be opt-in per target: it SHALL list the harnesses in play and the gaps
found, and SHALL write only harnesses the operator explicitly selected. Invoked with no
target selected, it SHALL write nothing.
5.4. WHERE the target file does not exist THEN the repair SHALL create it holding only the
declared servers' entries; WHERE it exists THEN the repair SHALL insert the entries
textually without touching any other content. Neither outcome is gated on the harness
being in play (2.2): the operator's explicit target selection is the consent (5.1, 5.3),
and for a harness whose only repository-scope artefact is its configuration file —
Claude Code and opencode — "not in play" and "target file absent" are the same
condition, which this criterion answers with creation (D19).
5.5. WHERE an entry with the same server name already exists in a target file THEN the
repair SHALL refuse that server for that file and report the difference. It SHALL NOT
modify, merge, or overwrite an existing entry, ever.
5.6. The repair SHALL NOT reformat, reorder, or re-serialize any byte it did not write.
Existing formatting, comments, and key order SHALL survive byte-for-byte outside the
inserted text. TOML targets SHALL be handled by textual table insertion, not by parsing and
re-serializing the whole document — both to honour this criterion and to avoid a new
dependency.
5.7. The repair SHALL refuse a target file that is malformed, unparseable as its harness's
format, or structurally foreign (the wrong top-level shape), with a diagnostic naming the
file; nothing SHALL be written to a refused file.
5.8. Every write SHALL be recorded in a receipt naming the target file, the inserted entry,
and the pre-write and post-write digests, so the change is auditable and removable. A
removal path SHALL exist that restores the pre-write state recorded in the receipt (the
`kyber capture enable` precedent).
5.9. WHERE the removal path finds the file's current digest differs from the receipt's
post-write digest — drift — THEN it SHALL refuse and report the drift rather than restore
over foreign edits.
5.10. The repair SHALL provide a dry-run option that prints the exact per-file change and
writes nothing — not even the receipt.
5.11. Each emitted entry SHALL match the harness's documented wiring recipe in the DocGraph
MCP runbook: server key, command and args shape, environment key, and path-substitution
semantics — `${workspaceFolder}` for VS Code, `.` where the runbook documents it, an
absolute path where the harness substitutes nothing (Codex).

### Requirement 6 — An absent third-party binary is never installed and never written broken

**User Story:** As a developer without CodeGraph installed, I want the gap reported with
the install pointer, so that I am never handed a configuration that fails at harness
startup and blames the configuration file.

#### Acceptance Criteria

6.1. Kyber-Weave SHALL NOT install any external binary as a side effect of the check or
the repair. CodeGraph and APM are expected dependencies, reported with a manual command,
never installed.
6.2. WHERE a declared third-party server's binary is not resolvable on `PATH` THEN the
check SHALL report the gap with the manual install pointer, and the repair SHALL refuse to
write that server's entry for every harness. A written entry whose launch command cannot
work converts a diagnosable absence into a harness-side startup failure, and SHALL NOT be
written.
6.3. WHERE the first-party `kyber-weave-mcp` binary is not resolvable THEN the finding
SHALL say the Kyber-Weave installation itself is broken and point at the documented repair
for that (`kyber-weave update` or the install script), not at harness configuration.
6.4. A third-party server's declaration MAY carry the manual install command text, so the
report can print the exact command rather than a pointer alone.

### Requirement 7 — Squad is untouched

**User Story:** As a Squad user, I want Squad's deployment behaviour unchanged by this
feature, so that the standing ownership boundary and every rendered artifact stay exactly
as they are.

#### Acceptance Criteria

7.1. This feature SHALL NOT modify Squad's renderer, its receipt, or its install or update
path. `squad install` and `squad update` SHALL continue to write no harness settings file
for any harness; D-no-settings (archived 2026-09-16 factory-renderer plan) stands.
7.2. The check and the repair SHALL NOT read Squad state: not `toolchain.yml`, not
`products/kyber-squad/mcp.json`, not rendered droid files.
7.3. `squad doctor` SHALL be unchanged by this work. Extending it toward Factory was ruled
out as D10 by the in-flight factory-mcp-server-visibility plan.
7.4. Nothing in this feature SHALL change what the Factory renderer whitelists per droid.

### Requirement 8 — The ownership boundary is recorded before the remedy ships

**User Story:** As a maintainer, I want the boundary between Kyber-Weave and the user's
harness configuration written down as an ADR before any write path exists, so that the
boundary is unambiguous rather than inferred from two documents that read differently.

#### Acceptance Criteria

8.1. The delivered change SHALL record, in an ADR, whether the repair falls under ADR
0026's scope or a separate decision. The ADR SHALL name which command writes which files,
SHALL draw the repository-scope versus user-scope distinction ADR 0026 does not draw, and
SHALL state the bounds (insertion-only, receipt, refusal, dry-run, never reformat).
8.2. The recommended form is a new ADR rather than an amendment: ADR 0026 is scoped to
Kyber Utilities and its decision stands for that component; a new ADR states the
docs-branch exception and cross-references 0026 (D13). The underlying question was decided
as D16: the remedy writes repository-scope files, bounded per R5, and the new ADR is the
record of that boundary change.
8.3. The ADR SHALL exist before the repair command writes its first file outside this
repository. This is verifiable at closeout: the ADR is in the validated corpus.

### Requirement 9 — Documentation

**User Story:** As a developer reading the docs, I want the runbook and adoption guides to
describe the check, the prompt, and the repair, so that I never have to read source to know
what `docs init` will ask and what the repair will write.

#### Acceptance Criteria

9.1. The DocGraph MCP runbook SHALL document the check-and-repair flow and the exact
wiring recipe for every harness the remedy covers — including ZCode, which has no row in
the wiring table today.
9.2. [`docs/docgraph/onboarding.md`](../../../docgraph/onboarding.md) SHALL list the check
among the steps `docs init` performs, with the opt-out, the prompt's non-interactive
behaviour (Requirement 4), and the always-human consequence of editing the declaration
(1.6).
9.3. [`docs/cli-command-surface.md`](../../../cli-command-surface.md) SHALL gain the repair
command with its verbs, flags, and exit codes, and SHALL describe `docs init`'s new check
step and opt-out.
9.4. The configuration documentation SHALL describe the required-server declaration
section and its keys.
9.5. No governed page SHALL claim, after this change, that Squad installs or owns harness
MCP configuration.

### Requirement 10 — Testability

**User Story:** As the maintainer, I want every criterion above provable by a deterministic
test, so that the write path in particular is pinned before it ever runs on a real
repository.

#### Acceptance Criteria

10.1. Tests SHALL cover, at minimum: fixture repositories for each of the five document
shapes and the non-file surface; the in-play markers, including the no-marker `info` path
(2.4); the report shape, rule ids, and unchanged exit code (3.1–3.4); the prompt via an
injected console seam and redirected stdin — confirm, decline, and non-terminal (4.1–4.4);
the opt-out (4.6); creation versus insertion (5.4); refusal of an existing entry (5.5);
byte-preservation of untouched content (5.6); refusal of malformed and foreign files
(5.7); the receipt and its removal, including drift refusal (5.8, 5.9); dry-run writing
nothing (5.10); and the absent-binary refusals (6.2, 6.3).
10.2. Every requirement in this document is testable as a deterministic test; none
requires a live harness session. Live acceptance — a real harness session reaching the
server after the repair — stays the owner's check and is recorded `[NOT LIVE-VERIFIED]`
until performed (G3).
10.3. New tests SHALL be written failing before the code that makes them pass
(`development-mode: test-first`).

## Non-goals

- Installing external software: CodeGraph, APM, toolchains, or any binary. Never a side
  effect of any command here.
- Writing user-scope configuration in this slice. `~/.factory/mcp.json`,
  `~/.cursor/mcp.json`, `~/.codex/config.toml`, `~/.config/opencode/opencode.json`, and
  ZCode storage are reported with a snippet and never written.
- Wiring the Copilot coding agent, whose configuration is repository settings rather than
  a file: snippet only.
- Modifying, merging, or "repairing" existing MCP entries. Insertion only; an existing
  entry with the same name is refused, never edited.
- Any change to Squad: renderer, receipt, install or update path, `toolchain.yml`,
  `products/kyber-squad/mcp.json`, `squad doctor`, or the Factory per-droid whitelist.
- Extending `squad doctor` toward Factory or any other harness.
- Reading user-scope files in the check, for any harness.
- A `--yes`-style consent flag on `docs init`.
- Changing the root `AGENTS.md` instruction files, any canonical agent body, or any
  capability profile.
- New ontology values, new dependencies (TOML is handled textually, 5.6), or widening the
  corpus.
- Wiring servers beyond those the repository declares.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Are diagnosis and repair one operation or two? | Two, in separate commands: the check lives in `docs init`; the repair is a dedicated command. | The existing repository pattern separates diagnosis (`squad doctor`) from action, and keeps scripted consent on an explicitly invoked command. | owner: authoritative (conductor packet, 2026-10-31) |
| D2 | Where does the check live? | Inside the existing `docs init` command. Not a new check command, not `squad doctor`. | `docs init` owns the adoption moment and already owns the warn-with-manual-command idiom (the APM-missing path prints the exact manual command; `ReportRetiredSkills` prints per-target warnings). A recipe nobody is told to follow at adoption is not a configuration. | owner: authoritative (conductor packet, 2026-10-31) |
| D3 | Is the warning merely informational? | No. An interactive `docs init` offers a yes/no to fix it; confirming proceeds with the repair. | The observed `architect` `STATUS: BLOCKED` is fixed by wiring, so the warning must offer the remedy, not only name it. | owner: authoritative (conductor packet, 2026-10-31) |
| D4 | Is the repair Squad's business? | No. It is done under `docs init` and the dedicated repair command. Squad's renderer, receipt, and install path are untouched; D-no-settings (archived 2026-09-16 factory-renderer plan) stands for `squad install` and `squad update`. | Verified: nothing in `SquadDoctorCommand`'s or the renderers' write paths is in scope, and no `products/kyber-squad/**` file is in scope. | owner: authoritative (conductor packet, 2026-10-31) |
| D5 | How does the prompt behave when stdin is not a terminal, in CI, or under a non-interactive invocation? | Terminal-gated: prompt only when stdin is an interactive terminal; redirected, piped, or TTY-less stdin declines — no prompt, no write, printed manual repair command. `docs init` gains no consent flag in this slice; explicit consent belongs to the dedicated repair command. | Resolves the tension between D3 and `docs init`'s verified non-interactive design (no prompt, no consent flag today). The command can never block on input it cannot receive, and CI can never receive a surprise write. Rejected alternative: a `--yes` flag — silent widening of a write surface on a scaffolding command. Satisfiable inside `docs init` alone (4.8). Cost of wrong: a CLI flag-surface choice, cheap to reverse before release. | autonomous: low-confidence, no authoritative source |
| D6 | Where does "which servers does this project need" live? | A new section in the host configuration `.kyber-weave/kyber-weave.yml`, scaffolded by `docs init`, independent of Squad's roster. | The assessment names the host configuration the natural home and `docs init` already scaffolds it. There is no truth source outside Squad's roster today. Consequence stated plainly: the file is on this repository's `review.policy.always-human` list, so every declaration change escalates for human review by path alone (1.6); the alternative shape was considered and rejected as D17. | autonomous: recommendation accepted (assessment, conductor packet) |
| D7 | How is "which harnesses are in play" determined? | Per-harness markers: in play when the repository-scope configuration file or the marker directory exists; otherwise one `info` line naming the marker, never a warning. ZCode keys on `.zcode/`. | Verified precedent: `ReportZCodeMcpConfiguration` reports non-users as `info`, keyed on the same `.zcode/` marker `SquadTargetResolver` detects. No general detection exists — Claude Code has no marker but `.mcp.json` itself — so "cannot detect" is a first-class report outcome (2.4). | autonomous: recommendation accepted |
| D8 | What may the check read? | Repository-scope files only; user-scope configuration is never read by check or remedy. | User-scope answers could not be trusted for this corpus anyway (the runbook's `--expect-root` / provenance `root=` concern: a user-scope server may be pinned to another checkout), and ADR 0026's boundary keeps Kyber tools out of the user's shared state. Cost of wrong: low — a detection completeness limit, documented. | autonomous: low-confidence, no authoritative source |
| D9 | What bounds govern the repair's writes? | All of the assessment's bounds, adopted: repository scope by default; per-target opt-in (no target selected, no write); textual insertion that never touches existing entries; a receipt with pre/post digests and a drift-refusing removal; refusal on malformed or foreign files; dry-run; and never a reformat or reorder of bytes it did not write. | The worst realistic outcomes the assessment named — a parse-and-rewrite merge corrupting a committed `.mcp.json` for every teammate, TOML re-serialization reordering a user's whole config — are excluded by construction. Precedents: `kyber capture enable` (receipt, drift refusal, dry-run, byte-for-byte restore) and its insertion-only `ensure-if-absent` seam. TOML handled textually, so no new dependency. | autonomous: recommendation accepted (assessment bounds, conductor packet) |
| D10 | Do wiring findings change `docs init`'s exit code? | No. A successful scaffold exits 0 with warnings, like the existing degraded paths; a declined, refused, or impossible repair is a warning. The repair command's exit codes are a design-phase decision under the CLI surface's exit-code contract. | Verified: the APM-missing and retired-skill paths never fail the command today; an adoption step that newly failed host builds would be a breaking change no one asked for. | autonomous: recommendation accepted |
| D11 | How is an absent third-party binary treated? | Degraded path: the check reports the gap with the manual install pointer, and the repair refuses to write that server's entry anywhere. Never an installation, never a broken entry. First-party binary absence is reported as a broken Kyber-Weave install. | `src/KyberWeave.Cli/AGENTS.md`: Kyber-Weave never installs external software as a side effect; expected dependencies are reported with a manual command. A written entry that cannot launch converts a diagnosable absence into a harness-side startup failure that blames the configuration file. | autonomous: recommendation accepted |
| D12 | Declared scale at authoring time? | `extends-component`, impact components DocGraph and Distribution. | The feature extends the existing `docs init` adoption surface (DocGraph: the check, the runbook, the onboarding) and the CLI command surface and install story (Distribution: the repair command, `cli-command-surface.md`, `install.md`). No new component; Squad is explicitly out (R7). | autonomous: recommendation accepted |
| D13 | Does this work fall under ADR 0026's scope, or a separate decision? | A separate decision: a new ADR records a scoped exception for the docs-branch repair — repository-scope writes under the R5 bounds — with ADR 0026 left intact for Kyber Utilities and cross-referenced. The ADR is required before the remedy ships (R8). | ADR 0026 is scoped to Kyber Utilities but states its rule "for any harness and any command" and draws no repository-scope versus user-scope distinction; a reader could take either side. Recording nothing would let the boundary be inferred, which is how silent scope creep reads later. Decided as D16 under the requirements-phase approval (2026-10-31). | decided as D16; autonomous: recommendation accepted |
| D14 | Does the check extend `squad doctor` toward Factory? | No. Ruled out as D10 by the in-flight factory-mcp-server-visibility plan, and the repair is out of Squad by D4. Decided as D18 under the requirements-phase approval (2026-10-31): no. | A second MCP diagnostic surface would split the truth source; `docs init` is the adoption-moment check the owner chose (D2). | autonomous: recommendation accepted |
| D15 | Are these requirements approved? | Yes: requirements phase approved; proceed to design. | Conductor gate `AUTONOMOUS_APPROVAL: requirements-phase-approval` (2026-10-31). Gate evidence, from the conductor's run against this checkout (2026-10-31): `docs_status` reported `provenance: root=/Users/dave/git/personal/factory/kyber-weave rev=8943b6a dirty=yes documents=141`, a root identical to `git rev-parse --show-toplevel` for this checkout (branch `feat/squad-response-format`, HEAD `8943b6a`), so the provenance root matches; `docs validate .` (no `--merge-ready`, this specification active) — `result: pass`, 0 errors, 0 warnings; `docs drift .` — `result: pass`, 0 errors, 0 warnings. G1 is closed by this evidence. | autonomous: recommendation accepted |
| D16 | OQ1: May the remedy write repository-scope harness MCP configuration, given the standing ownership line (ADR 0026's "for any harness and any command"; D-no-settings)? | Yes. The remedy writes repository-scope files only, under the R5 bounds: per-target opt-in, insertion-only, receipt with drift-refusing removal, refusal on a malformed or foreign file, dry-run preview, never a reformat. The boundary is recorded in a new ADR before the remedy ships (R8, H6); ADR 0026 stands untouched for Kyber Utilities, and the standing "Squad never writes harness settings" decision (D-no-settings) is untouched because Squad is not in this path. | The owner's direction that this work is done under `docs init` and explicitly not as part of `squad` presumes the remedy can write the configuration it is asked to fix. The R5 bounds exclude by construction both worst-case outcomes the assessment named (a parse-and-rewrite merge corrupting a committed `.mcp.json`; TOML re-serialization reordering a user's whole config). The snippet-only fallback recorded as option (b) is not needed and remains the degradation path if the ADR is ever revisited. | autonomous: recommendation accepted (conductor, 2026-10-31) |
| D17 | OQ2: How is the required-server declaration expressed, given the host configuration file is on the always-human reserved path? | A new section in `.kyber-weave/kyber-weave.yml`, accepting and documenting the escalation consequence. | Any change to that file escalates for human review by path alone under `review.policy.always-human` (verified in this repository's configuration). This is a known and accepted consequence of the chosen design: the declaration's schema and its scaffolded default are part of what the owner reviews. The host configuration remains the single source the rest of the product already reads; a second configuration surface was considered and rejected. | autonomous: recommendation accepted (conductor, 2026-10-31) |
| D18 | OQ3: Does the check extend `squad doctor` toward Factory (or any harness)? | No. `squad doctor` is untouched by this specification. | The in-flight factory-mcp-server-visibility plan ruled the extension out as its D10, and the owner's decision that this work belongs to `docs init` and not to Squad excludes it on its own terms. Two MCP diagnostic surfaces would split the truth source. | autonomous: recommendation accepted (conductor, 2026-10-31) |
| D19 | Mid-execution conflict (task 4 RED): for Claude Code and opencode, whose only repository-scope artefact is the configuration file itself, "the target file does not exist" (5.4) and "the harness is not in play" (2.2, design §5's Defaults bullet) name the same condition — does a repair invoked with that target explicitly named create the file, or skip the harness as not in play? | Create — selected means created. An explicitly named target is not gated on the in-play heuristic: the selection is the consent (5.1, 5.3), and 5.4 governs whether the file is created or inserted. Text changed by this row: 5.4 gained one clarifying sentence making that precedence explicit; design §5's Defaults bullet was corrected to agree, and its no-target preview now names each writable harness no marker can detect; tasks 4 and 7 had their wording corrected to match — creation for a named out-of-play target, skip reserved for nothing-to-write; the decision-lineage enumerations now cite D19. | In-play exists so the unattended check stays quiet about harnesses a repository does not use (2.4, D7); it was never a veto over an explicit operator instruction, and the design's own catalog row states that for Claude Code "absence means unknown, not 'not in play'" — the skip reading converts that unknown into a refusal. The skip reading also makes the remedy unavailable in exactly the founding scenario, nothing wired yet: the check reports only `info` for a file-only harness with no file (2.4), no fixable gap is ever offered, and the repair would refuse the one case with nothing to insert into, pushing operators back to hand-editing — the risk the R5 bounds exist to remove. Creation is already a designed outcome, not a new write class: the receipt carries a `created` mode and `mcp-unwire` deletes `created` files (design §5). One uniform rule for every harness; the write stays bounded by per-target opt-in (5.3), catalog writability, the absent-binary refusal (6.2), dry-run (5.10), the receipt (5.8), and unwire (5.9). | autonomous: recommendation accepted (conductor-directed correction, 2026-10-31) |

## Design harvest

The durable design items these requirements call for (H1–H7) are to be tabulated
authoritatively in design (the design phase's harvest table is the single home); the rows
below map each item to the requirement it answers and to its intended target, so this
file's closeout stands alone.

| ID | Durable design item | Answers | Target component | Target doc type | Target document |
|---|---|---|---|---|---|
| H1 | The per-harness MCP configuration shape catalog: file, server key, entry shape, substitution semantics, in-play marker — for all eight harnesses, including ZCode, which has no row in the wiring table today | R2, R5 | DocGraph | runbook | [`docs/docgraph/mcp-runbook.md`](../../../docgraph/mcp-runbook.md) |
| H2 | The required-server declaration schema in the host configuration, and the always-human consequence of editing it | R1 | DocGraph | reference | [`docs/configuration.md`](../../../configuration.md), [`docs/docgraph/onboarding.md`](../../../docgraph/onboarding.md) |
| H3 | The `docs init` check step: report lines, new rule ids, exit behaviour, opt-out | R2, R3 | DocGraph | onboarding | [`docs/docgraph/onboarding.md`](../../../docgraph/onboarding.md), [`docs/cli-command-surface.md`](../../../cli-command-surface.md) |
| H4 | The prompt and non-interactive contract: terminal-gated, decline-on-redirect, no consent flag on `docs init` | R4 | DocGraph | reference | [`docs/cli-command-surface.md`](../../../cli-command-surface.md) |
| H5 | The repair command: target selection, insertion-only write policy, receipt and removal, refusals, dry-run, exit codes | R5, R6 | DocGraph | runbook | [`docs/docgraph/mcp-runbook.md`](../../../docgraph/mcp-runbook.md), [`docs/cli-command-surface.md`](../../../cli-command-surface.md) |
| H6 | The ownership-boundary ADR — the record of the D16 repository-scope write exception versus ADR 0026 | R8 | DocGraph | adr | new ADR under [`docs/adr/`](../../../adr/README.md) |
| H7 | The docs-init / Squad separation statement: who may write harness MCP configuration, and that Squad still does not | R7 | KyberSquad | architecture | [`docs/kyber-squad/architecture.md`](../../../kyber-squad/architecture.md) (cross-reference only; no boundary change) |

## Closeout mapping

Verified at closeout (2026-11-01) against the shipped code and tests, not against this
document. Every row of this file's Decisions table (D1–D19) and every harvest row (H1–H7) is
mapped below to a live governed document. **ADR disposition: [ADR 0051](../../../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md)**
records the D16 boundary, ahead of the write path (R8.3, design DD20, task 5) — no ADR waiver
is claimed for it.

| ID | Canonical destination |
|---|---|
| D1, D2, D10 | [CLI command surface](../../../cli-command-surface.md) — the two separate verbs, the check among `docs init`'s steps, and the exit-code rows |
| D3, D5, D19 | [MCP runbook: the prompt and no terminal](../../../docgraph/mcp-runbook.md#the-prompt-and-what-happens-without-a-terminal), [MCP runbook: `docs mcp-wire`](../../../docgraph/mcp-runbook.md#docs-mcp-wire) |
| D4, D14, D18 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) — the docs-init / Squad split, and `squad`'s diagnostic surface left alone |
| D6, D17 | [Configuration: MCP servers](../../../configuration.md#mcp-servers) — the declaration's home and the always-human consequence of editing it |
| D7, D8, D11 | [MCP runbook: the check](../../../docgraph/mcp-runbook.md#the-check) — the in-play markers, repository scope only, and the absent-binary classes |
| D9 | [MCP runbook: the receipt](../../../docgraph/mcp-runbook.md#the-receipt), [MCP runbook: what the repair writes](../../../docgraph/mcp-runbook.md#what-the-repair-writes-and-what-it-will-not) — the write bounds, refusal, and dry run |
| D12 | [Component catalog](../../../catalog.md) — the two components this specification extends, DocGraph and Distribution |
| D13, D16 | [ADR 0051](../../../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md), [ADR index](../../../adr/README.md) |
| D15 | Documentation waiver — the requirements-phase approval is a historical record, not a surviving architectural, operational, or behavioural invariant; it is preserved permanently in this document's Decisions table. |
| H1 | [MCP runbook](../../../docgraph/mcp-runbook.md) — the per-harness shape catalog, the ZCode row, and the writable/snippet-only table |
| H2 | [Configuration: MCP servers](../../../configuration.md#mcp-servers), [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring) |
| H3 | [Install](../../../install.md) — what an adopting repository's `docs init` now checks and offers; [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring), [CLI command surface](../../../cli-command-surface.md), [Rule reference](../../../ci-pipelines/rule-reference.md) |
| H4 | [MCP runbook: the prompt and no terminal](../../../docgraph/mcp-runbook.md#the-prompt-and-what-happens-without-a-terminal), [CLI command surface](../../../cli-command-surface.md) |
| H5 | [MCP runbook: `docs mcp-wire`](../../../docgraph/mcp-runbook.md#docs-mcp-wire), [MCP runbook: `docs mcp-unwire`](../../../docgraph/mcp-runbook.md#docs-mcp-unwire), [CLI command surface](../../../cli-command-surface.md) |
| H6 | [ADR 0051](../../../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md), [ADR index](../../../adr/README.md) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) |

**Requirement verification.** R1–R9 are met by delivered code with a named test each; R10.1
and R10.2 are met with one honest limit — live acceptance, a real harness session reaching
the server after the repair, stays the owner's check and is `[NOT LIVE-VERIFIED]` (10.2, G3).
R10.3's test-first *ordering* cannot be verified from the archived bundle: the RED runs live
in the worker sessions, not in gate output.

## GAPS

- **G1 (closed 2026-10-31):** `docs validate` and `docs drift` could not be run by the
  authoring session — its harness has neither the kyber-weave docs MCP tools nor a shell,
  the same structural blindness this specification addresses. **Closed by the conductor's
  run against this checkout (2026-10-31):** `docs_status` reported
  `provenance: root=/Users/dave/git/personal/factory/kyber-weave rev=8943b6a dirty=yes documents=141`,
  a root identical to `git rev-parse --show-toplevel` for this checkout (branch
  `feat/squad-response-format`, HEAD `8943b6a`), so the provenance root matches;
  `docs validate .` (no `--merge-ready`, this specification active) — `result: pass`,
  0 errors, 0 warnings; `docs drift .` — `result: pass`, 0 errors, 0 warnings. The results
  stand for this checkout and are the gate evidence recorded in D15.
- **G2:** The ZCode repository-scope shape (`zcode.json` / `.zcode/config.json`, key
  `mcp.servers`) is assessment-supplied. Verified here: the `.zcode/` marker and the
  user-tier `~/.zcode/cli/config.json` (`SquadGlobalRoots`). Design must pin every
  harness's exact shape and key against a source before the remedy emits bytes (H1); the
  runbook's seven documented rows are the source for the other seven harnesses.
- **G3:** Live acceptance — a real harness session reaching a declared server after the
  repair — cannot be exercised from this session. It stays the owner's live check, and
  delivery notes remain `[NOT LIVE-VERIFIED]` until it is performed, consistent with prior
  renderer plans.
- **G4:** Exact spellings are deliberately unset at this phase: the repair command's name,
  the opt-out flag's spelling, the new `KW-*` rule-id values, the declaration section's
  key names, and the repair command's exit-code table are design-phase decisions (H2–H5).
  The requirements name behaviours so design can name spellings without reopening them.

## OPEN_QUESTIONS

None. OQ1, OQ2 and OQ3 were decided on 2026-10-31 under the conductor's requirements-phase
approval and are recorded in the permanent Decisions table above as D16, D17 and D18, each
with provenance `autonomous: recommendation accepted`. Each decision row carries the
question, the answer, and the supporting information that decided it.
