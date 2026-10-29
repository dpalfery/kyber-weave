# Working in this repository

Kyber-Weave governs the artifacts that shape agent behaviour. **What** it does and **why**
lives in [`docs/`](docs/README.md) — this file is only operational rules for working in the
code without breaking the repository.

## Commands

### The inner loop

While iterating, run `scripts/fast-loop.sh` rather than the full gate suite. It inspects
modified files and routes to targeted checks for .NET, KyberDash, the tray crate, and
governed docs. Use `--scoped` for declared gates limited to changed files, or `--full` for
all gates unscoped (`review gates . --all`). It is **not** evidence and must never be cited
as such ([ADR 0005](docs/adr/0005-task-level-fast-review.md)). Run `--full` before pushing.

### Before opening a PR

Run the same declared gate suite a review cites — scoped by default, full before push:

```bash
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Council gates skip gates whose `include` does not match changed files (`KW-REVIEW-026`);
see [`docs/code-review/architecture.md`](docs/code-review/architecture.md). Format, build,
test, and skill argv are the `review.gates` entries in
[`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml). There is no separate
restore gate: `--no-restore` is omitted on format and build, so those gates restore
themselves. InspectCode runs in the council (`dotnet tool restore` once per clone) and
exits zero whether or not it finds anything; the result is `artifacts/inspectcode.xml` —
see the `resharper-clt` skill. Release/self-updater/kyberdash
build paths: `./scripts/update-loop.sh` ([`docs/distribution.md`](docs/distribution.md#verifying-a-release-locally)).
KyberDash and tray gates: [`dash/AGENTS.md`](dash/AGENTS.md), [`dash/tray/AGENTS.md`](dash/tray/AGENTS.md).

## Exploration order

Do not start with grep, find, or arbitrary browsing when a semantic index covers the question.
MCP tool names may be namespace-qualified by the active harness.

<!-- CODEGRAPH_START -->
**Code:** When `.codegraph/` exists, use `codegraph_explore` (or `codegraph explore "…"`)
before text search; name a file or symbol when you need line-numbered source.
<!-- CODEGRAPH_END -->

If `.codegraph/` is missing, this repository authorizes `codegraph init .` at the root
(repository-local only; installing CodeGraph is the user's decision). Before init, record
`git status --short` and both staged (`git diff --cached`) and unstaged (`git diff`)
tracked diffs; afterwards undo only tracked edits init introduced. Blocked or unusable
init → report under GAPS and fall back;
`docs drift` without an index → report skipped, not passed.

Before `docs/` greps, use Kyber-Weave MCP `docs_explore`; before contract changes,
`docs_for_symbol`; for taxonomy, `docs_glossary` and `docs_analysis_candidates`. No CLI
equivalent — fall back to [`docs/README.md`](docs/README.md). Never use `docs/archive/` as
current guidance. Confirm each response's `provenance:` `root=` matches
`git rev-parse --show-toplevel` (`docs_status`); a mismatch means the wrong repository.

## Non-negotiables

- **Persisted plans belong in docs.** Under `<docs-root>/plans` / `<plan-index>`; no hidden
  tool folders. Committed, not untracked — same for archived plans under
  `<docs-root>/archive/plans`.
- **`TreatWarningsAsErrors` is on, with `AnalysisMode=all`.** Do not add to `NoWarn` in
  `Directory.Build.props` without a reason you can state.
- **Rule ids are permanent (`KW-*`).** Add new ids; never reuse or renumber.
- **Editing anything under `docs/` means re-running `docs validate` and `docs drift`.** The
  corpus stays at zero findings.
- **Do not widen the ontology to make a failure disappear.** Fix the document.
- **Todos:** capture authorized deferred work under `<todo-index>`. A review finding does not
  authorize a repository change by itself.

## KyberDash

The tray and web dashboard are **display layers** only — no isolated feature logic,
schedulers, or jobs; shared engine, one datastore, one `/api/kyber/*` API. See
[`docs/rules/kyberdash-display-layer.md`](docs/rules/kyberdash-display-layer.md) and
[`dash/AGENTS.md`](dash/AGENTS.md). KyberDash is first-party ([ADR 0020](docs/adr/0020-kyberdash-one-time-fork.md)); upstream references stay only in `dash/LICENSE` and `dash/THIRD_PARTY_NOTICES.md`.

## Dependencies

Core dependency policy (Markdig, YamlDotNet, sqlite3 CLI for CodeGraph): see
[`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md).

## Where to go next

| Working on | Read |
|---|---|
| Writing C# anywhere in this repository | [`docs/standards/csharp/README.md`](docs/standards/csharp/README.md), the path declared as **<csharp-coding-standard>** below |
| The engine — parsing, validation, search, export | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| Installing, updating, or configuring Kyber-Weave or KyberDash | [`docs/cli-command-surface.md`](docs/cli-command-surface.md) — every verb and flag, the exit-code contract, and the canonical flows |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| KyberDash gates and the display-layer rule | [`dash/AGENTS.md`](dash/AGENTS.md) |
| KyberDash architecture and runbook | [`docs/dash/architecture.md`](docs/dash/architecture.md), [`docs/dash/runbook.md`](docs/dash/runbook.md) |
| Tests | The path declared as **<test-coding-standard>** below, then [`tests/KyberWeave.Tests/AGENTS.md`](tests/KyberWeave.Tests/AGENTS.md) for fixtures |
| Authoring documentation | [`docs/documentation-ontology.md`](docs/documentation-ontology.md), and the `kyber-docs-authoring` skill in [`.apm/skills/`](.apm/skills/kyber-docs-authoring/SKILL.md) |

## House style

Match the surrounding code: comments explain why, not what; diagnostics carry a hint when a
nearest-match suggestion is computable.

## Protecting hand-authored instruction files

`AGENTS.md` and `CLAUDE.md` here are **hand-authored**, with one exception: the Config Reg
block below is generated. `docs init` rewrites everything between its two markers from
[`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml) on every run, and touches
nothing outside them. Edit the configuration, not the block. `docs validate` reports a stale
one as `KW-CONFIG-REG-002`.

[`apm.yml`](apm.yml) deliberately pins `targets: [agent-skills]`. **Do not remove that pin or
add `.apm/instructions/`**: an unscoped compile can overwrite the hand-authored instruction
files. The configuration documents the rationale and why the pin does not constrain consumers.

<!-- KYBER_WEAVE_CONFIG_REG_START -->
## Repository Configuration & Paths Registry (Config Reg)

Agents and skills look up the following properties to find the documentation and
references that belong to this repository.

- **<docs-root>**: `docs`
- **<documentation-index>**: `docs/README.md`
- **<documentation-ontology>**: `docs/documentation-ontology.md`
- **<component-catalog>**: `docs/catalog.md`
- **<standards-root>**: `docs/standards`
- **<csharp-coding-standard>**: `docs/standards/csharp/README.md`
- **<test-coding-standard>**: `docs/standards/test/README.md`
- **<typescript-coding-standard>**: `docs/standards/typescript/README.md`
- **<rust-coding-standard>**: `docs/standards/rust/README.md`
- **<tauri-coding-standard>**: `docs/standards/tauri/README.md`
- **<go-coding-standard>**: `docs/standards/go/README.md`
- **<plan-index>**: `docs/plans/README.md`
- **<specification-index>**: `docs/specs/README.md`
- **<todo-index>**: `docs/todo/README.md`
- **<adr-index>**: `docs/adr/README.md`
- **<rules-index>**: `docs/rules/README.md`
- **<reference-index>**: `docs/reference/README.md`
- **<review-guidance>**: `REVIEW.md`
- **<autonomy>**: `autonomous`

Skills and other portable instruction files SHALL reference these paths by the
property name above — "the path declared as **<component-catalog>**
in the repository root `AGENTS.md`" — rather than embedding a relative link that
traverses out of the skill's own directory. A skill written that way stays correct
when this repository moves something; only this registry has to change.

Generated by `kyber-weave docs init` from `.kyber-weave/kyber-weave.yml`. Edit that
file rather than this block: everything between the markers is rewritten on the next
run.
<!-- KYBER_WEAVE_CONFIG_REG_END -->
