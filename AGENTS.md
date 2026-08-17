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

Touching the self-updater, `install.sh`, or the Squad release path also means running the
local release loop:

```bash
./scripts/update-loop.sh
```

It publishes the working tree as a stand-in Release, serves it from loopback, and drives a
real self-update and a real `squad install` against published single-file binaries. Nothing
reaches github.com. See [Verifying a release locally](#verifying-a-release-locally).

## Non-negotiables
**Kyber-Weave for documentation:** Before grepping or reading files under `docs/` to answer a question, use the Kyber-Weave MCP tool `mcp__kyber-weave__docs_explore`. It ranks on declared frontmatter identity, returns the one relevant `##` section rather than a whole runbook, and carries that document's resolved joins to the code graph. Before renaming, moving, or changing the contract of a code symbol, use `mcp__kyber-weave__docs_for_symbol` to find the documentation that must change with it: a `code-refs` entry is a formal claim of ownership, which grep cannot distinguish from a passing prose mention. There is no CLI equivalent of either tool — if the MCP server is unavailable, fall back to the [documentation index](docs/README.md) and state that the tool was unavailable. The corpus excludes `docs/archive/`, which is historical and is never retrieved as current guidance.

<!-- CODEGRAPH_START -->
## CodeGraph

In repositories indexed by CodeGraph (a `.codegraph/` directory exists at the repo root), reach for it BEFORE grep/find or reading files when you need to understand or locate code:

- **MCP tool** (when available): `codegraph_explore` answers most code questions in one call — the relevant symbols' verbatim source plus the call paths between them, including dynamic-dispatch hops grep can't follow. Name a file or symbol in the query to read its current line-numbered source. If it's listed but deferred, load it by name via tool search.
- **Shell** (always works): `codegraph explore "<symbol names or question>"` prints the same output.

If there is no `.codegraph/` directory, skip CodeGraph entirely — indexing is the user's decision.
<!-- CODEGRAPH_END -->

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

## Verifying a release locally

A self-updater is always executed by the **old** binary. A fix to the update path therefore
cannot be proven by the release that contains it — only by updating away from a build that
predates it. Tag-and-wait cycles get this wrong silently: the release ships, the same failure
reappears, and the fix looks broken when it was simply never the code that ran.

The loop closes that gap without touching github.com:

```bash
./scripts/update-loop.sh                  # publish, serve, self-update, squad install
./scripts/update-loop.sh --keep           # leave the sandbox in place to inspect
./scripts/update-loop.sh --from <git-ref> # update away from an older build
```

Three pieces, usable separately:

| Script | Does |
|---|---|
| [`scripts/release-local.sh`](scripts/release-local.sh) | Publishes one RID with release.yml's exact flags into `.local-release/v<version>/`, plus Squad archives and `SHA256SUMS.txt`. |
| [`scripts/local-release-server.py`](scripts/local-release-server.py) | Serves that tree as the GitHub Releases endpoints the CLI reads. Loopback only. |
| [`scripts/update-loop.sh`](scripts/update-loop.sh) | Drives the two together and asserts the outcome. |

The redirect is `KYBER_WEAVE_RELEASE_ORIGIN`, resolved by
[`ReleaseOrigin`](src/KyberWeave.Cli/Update/ReleaseOrigin.cs). It accepts **loopback
authorities only**, and permits plain HTTP only for a loopback URL under an active override —
a redirect off the local server still has to be HTTPS. Those restrictions are the point of
the type; `ReleaseOriginTests` pins them, and widening them needs a reason you can state.

Run against a **published single-file binary**, never `dotnet run`. The failure this exists
to catch — a running image replacing itself and then failing to load an assembly it had not
yet touched — does not exist in any other shape.

## Where to go next

| Working on | Read |
|---|---|
| Writing C# anywhere in this repository | [`docs/standards/csharp/README.md`](docs/standards/csharp/README.md), the path declared as **<csharp-coding-standard>** below |
| The engine — parsing, validation, search, export | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| Tests | The path declared as **<test-coding-standard>** below, then [`tests/KyberWeave.Tests/AGENTS.md`](tests/KyberWeave.Tests/AGENTS.md) for fixtures |
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

`AGENTS.md` and `CLAUDE.md` here are **hand-authored**, with one exception: the Config Reg
block below is generated. `docs init` rewrites everything between its two markers from
[`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml) on every run, and touches
nothing outside them. Edit the configuration, not the block. `docs validate` reports a stale
one as `KW-CONFIG-REG-002`.

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
