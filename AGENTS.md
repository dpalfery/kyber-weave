# Working in this repository

Kyber-Weave governs the artifacts that shape agent behaviour. **What** it does and **why**
lives in [`docs/`](docs/README.md) — this file is only about not breaking things while
working in the code.

## Commands

Before opening a PR, run the local equivalents of the required CI gates, plus the
CodeGraph-backed documentation drift check:

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
```

Touching the self-updater, `install.sh`, or the Squad release path also means running the
local release loop documented in [distribution](docs/distribution.md#verifying-a-release-locally):

```bash
./scripts/update-loop.sh
```

## Exploration order

Do not start with grep, find, or arbitrary file browsing when a semantic index covers the
question. MCP tool names may be namespace-qualified by the active harness.

<!-- CODEGRAPH_START -->
**Code:** When `.codegraph/` exists, use `codegraph_explore` before text search to understand
or locate code:

- The MCP tool returns relevant symbol definitions and call paths. Name a file or symbol when
  current line-numbered source is required.
- The shell fallback is `codegraph explore "<symbol names or question>"`.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->

**Documentation:** Before grepping or reading under `docs/`, use the Kyber-Weave MCP
`docs_explore` tool. Before renaming, moving, or changing a code symbol's contract, use
`docs_for_symbol` to find formal `code-refs` ownership. Use `docs_glossary` and
`docs_analysis_candidates` for taxonomy and review queries. These MCP operations have no CLI
equivalent; if the server is unavailable, start at the [documentation index](docs/README.md)
and state that the fallback was necessary. Never retrieve `docs/archive/` as current guidance.

## Non-negotiables

**Persisted plans belong in docs.** When a durable plan artifact is warranted, store it under
`<docs-root>/plans`, the directory indexed by `<plan-index>`. Do not put plans in hidden tool
folders or create a plan file for an otherwise ephemeral execution plan.

**`TreatWarningsAsErrors` is on, with `AnalysisMode=all`.** A warning fails the build.
Fix the cause; do not add to the `NoWarn` list in `Directory.Build.props` without a reason
you can state.

**Rule ids are permanent.** A `KW-*` id is what suppressions, SARIF baselines, and
code-scanning alerts key on. Renumbering one silently un-suppresses findings in every host
repository. Add new ids; never reuse or renumber.

**Editing anything under `docs/` means re-running `docs validate` and `docs drift`.** This
repository's documentation is a governed corpus that must stay at zero findings — that
claim is made in the README, and it has to remain true.

**Do not widen the ontology to make a failure disappear.** If a document fails, fix the
document.

**New dependencies need justification.** Core takes only Markdig and YamlDotNet. The
CodeGraph index is read through the `sqlite3` CLI rather than `Microsoft.Data.Sqlite`
because that package's native dependency carries an unresolved advisory — a deliberate
trade, documented where it is made.

**Capture authorized deferred work as todos.** During implementation, preserve accepted
follow-up work under [`docs/todo/`](docs/todo/README.md). A review finding or declined
suggestion does not by itself authorize a repository change: report it, and create a todo only
when the user accepts or explicitly asks to retain it. See the todo index for mechanics and the
[documentation index](docs/README.md) for the distinction between specs, plans, and todos.

## Where to go next

| Working on | Read |
|---|---|
| Writing C# anywhere in this repository | [`docs/standards/csharp/README.md`](docs/standards/csharp/README.md), the path declared as **<csharp-coding-standard>** below |
| The engine — parsing, validation, search, export | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| Tests | The path declared as **<test-coding-standard>** below, then [`tests/KyberWeave.Tests/AGENTS.md`](tests/KyberWeave.Tests/AGENTS.md) for fixtures |
| Authoring documentation | [`docs/documentation-ontology.md`](docs/documentation-ontology.md), and the `kyber-weave-docs` skill in [`.apm/skills/`](.apm/skills/kyber-weave-docs/SKILL.md) |

## House style

Match the surrounding code. Two habits are consistent throughout and worth keeping:

**Comments explain why, not what.** The codebase carries `<remarks>` blocks giving the
reasoning behind a decision — why sqlite3 and not the package, why the corpus is rebuilt
rather than persisted, why plans are demoted in ranking. Preserve that when editing, and
add it when a choice would otherwise look arbitrary.

**Diagnostics carry a hint.** A finding that cannot be acted on is noise. Where a
nearest-match suggestion is computable, offer it.

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
- **<plan-index>**: `docs/plans/README.md`
- **<specification-index>**: `docs/specs/README.md`
- **<todo-index>**: `docs/todo/README.md`
- **<adr-index>**: `docs/adr/README.md`
- **<rules-index>**: `docs/rules/README.md`
- **<reference-index>**: `docs/reference/README.md`

Skills and other portable instruction files SHALL reference these paths by the
property name above — "the path declared as **<component-catalog>**
in the repository root `AGENTS.md`" — rather than embedding a relative link that
traverses out of the skill's own directory. A skill written that way stays correct
when this repository moves something; only this registry has to change.

Generated by `kyber-weave docs init` from `.kyber-weave/kyber-weave.yml`. Edit that
file rather than this block: everything between the markers is rewritten on the next
run.
<!-- KYBER_WEAVE_CONFIG_REG_END -->
