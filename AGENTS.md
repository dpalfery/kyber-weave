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
**`PlansGoInDocs`**
Do not store plan files in .folders. Any plan developed for this project will be stored in the `<docs-root>`\plans folder so that it is not lost and stays with the project.

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

**Capture deferred work as todos.** When an agent or contributor identifies work not done now
(such as a finding, a deferred fix, or a declined suggestion), add a todo under
[`docs/todo/`](docs/todo/README.md) rather than letting it evaporate. See
[`docs/todo/README.md`](docs/todo/README.md) for mechanics and [`docs/README.md`](docs/README.md)
for the conceptual distinction between specs, plans, and todos.

## Where to go next

| Working on | Read |
|---|---|
| The engine — parsing, validation, search, export | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| Tests | [`tests/KyberWeave.Tests/AGENTS.md`](tests/KyberWeave.Tests/AGENTS.md) |
| Authoring documentation | [`docs/documentation-ontology.md`](docs/documentation-ontology.md), and the `kyber-weave-docs` skill in [`.apm/skills/`](.apm/skills/kyber-weave-docs/SKILL.md) |

## Exploration: CodeGraph & Kyber-Weave outrank Grep and Search

Do NOT start by grepping, finding, or reading arbitrary files across the repository. Treat CodeGraph and Kyber-Weave as **eager first-line tools**:

1. **For Code Exploration & Understanding (CodeGraph):**
   - Use **`codegraph_explore`** (MCP `call_mcp_tool` or CLI `codegraph explore "<query>"`) **BEFORE** using `grep_search`, `find`, or browsing raw source files.
   - Returns verbatim symbol definitions, callers, callees, dynamic dispatch links, and blast-radius summaries in a single call.
   - Name symbols, file names, or natural-language architectural questions in your query.

2. **For Documentation, Concepts & Governance (Kyber-Weave):**
   - Use **`docs_explore`** (MCP `call_mcp_tool`) **instead of grepping `docs/`**.
   - Ranking uses declared frontmatter identity, joins live code symbols via CodeGraph, and excludes superseded archive documents.
   - Use **`docs_for_symbol`** before renaming or modifying symbols to locate documentation that must change with it.
   - Use **`docs_glossary`** and **`docs_analysis_candidates`** for taxonomy and review queries.

3. **Fallback only:**
   - Fall back to `grep_search`, `list_dir`, or direct file reading only when querying unindexed text assets or after CodeGraph/DocGraph have pointed to specific non-code files.

Product questions — what DocGraph is, how retrieval ranks, what a rule means — are answered in the docs corpus via `docs_explore`. Start at [`docs/README.md`](docs/README.md).

## House style

Match the surrounding code. Two habits are consistent throughout and worth keeping:

**Comments explain why, not what.** The codebase carries `<remarks>` blocks giving the
reasoning behind a decision — why sqlite3 and not the package, why the corpus is rebuilt
rather than persisted, why plans are demoted in ranking. Preserve that when editing, and
add it when a choice would otherwise look arbitrary.

**Diagnostics carry a hint.** A finding that cannot be acted on is noise. Where a
nearest-match suggestion is computable, offer it.

## A note on these files

`AGENTS.md` and `CLAUDE.md` here are **hand-authored and stay that way.**

`apm compile` generates files at exactly these paths. Left unconfigured, APM auto-detects
targets from the harness folders present and resolves to `all`, which writes `AGENTS.md`,
`CLAUDE.md`, `GEMINI.md`, `.github/copilot-instructions.md` and a file per harness — over
the top of these.

[`apm.yml`](apm.yml) pins `targets: [agent-skills]` to prevent that. Compile then scopes to
`.agents/skills/` and produces no output, so these files are safe. **Do not remove that
pin**, and do not add `.apm/instructions/`. This repository uses APM for one thing:
distributing the `kyber-weave-docs` skill via `apm install`.

Consumers are unaffected — `--target` outranks `apm.yml` in APM's resolution chain, and
`docs init` always passes it explicitly.
