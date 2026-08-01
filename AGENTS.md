# Working in this repository

Kyber-Weave governs the artifacts that shape agent behaviour. **What** it does and **why**
lives in [`docs/`](docs/README.md) — this file is only about not breaking things while
working in the code.

## Commands

```bash
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

Before opening a PR, the gates this project applies to itself must pass:

```bash
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
dotnet run --project src/KyberWeave.Cli -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli -- skill scan .apm/skills/kyber-weave-docs
```

## Non-negotiables

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

## Where to go next

| Working on | Read |
|---|---|
| The engine — parsing, validation, search, export | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| Tests | [`tests/KyberWeave.Tests/AGENTS.md`](tests/KyberWeave.Tests/AGENTS.md) |
| Authoring documentation | [`docs/documentation-ontology.md`](docs/documentation-ontology.md), and the `kyber-weave-docs` skill in [`.apm/skills/`](.apm/skills/kyber-weave-docs/SKILL.md) |

## Prefer querying the docs over reading them

If `kyber-weave-mcp` is available, use **`docs_explore`** instead of grepping `docs/`.
Ranking uses declared frontmatter identity, which prose does not carry, and the corpus
excludes the archive — which must never be cited as current guidance. Use
**`docs_for_symbol`** before renaming anything, to find the documentation that must change
with it.

Product questions — what DocGraph is, how retrieval ranks, what a rule means — are
answered there, not here. Start at [`docs/README.md`](docs/README.md).

## House style

Match the surrounding code. Two habits are consistent throughout and worth keeping:

**Comments explain why, not what.** The codebase carries `<remarks>` blocks giving the
reasoning behind a decision — why sqlite3 and not the package, why the corpus is rebuilt
rather than persisted, why plans are demoted in ranking. Preserve that when editing, and
add it when a choice would otherwise look arbitrary.

**Diagnostics carry a hint.** A finding that cannot be acted on is noise. Where a
nearest-match suggestion is computable, offer it.

## A note on these files

`AGENTS.md` and `CLAUDE.md` here are **hand-authored**. `apm compile` generates files at
these exact paths from `.apm/instructions/`, and would overwrite them. This repository
uses APM only to distribute the `kyber-weave-docs` skill via `apm install` — do not run
`apm compile` here without moving this content into `.apm/` first.
