# Working in this repository

Kyber-Weave governs the artifacts that shape agent behaviour. **What** it does and **why**
lives in [`docs/`](docs/README.md) — this file is only operational rules for working in the
code without breaking the repository.

## Writing style: applies to everything you write

This covers every message to the user, every doc, commit message, PR description and code
comment, and every prompt or handoff to another agent.

Assume the reader is switching between several tasks and remembers nothing about this one.
Write for a strong developer who is reading it for the first time.

- **Bottom line first.** Open with what happened, what you found, or what you need.
- **No assumed context.** Don't rely on earlier messages, other files or past sessions.
  Give the minimum background in one line.
- **Plain words.** Explain every name, ID, acronym or piece of jargon the first time it
  appears. Never use internal labels like plan IDs or task numbers as if the reader knows them.
- **Short.** Cut anything the reader doesn't need in order to act or decide.
- **Messages to the user** must also follow [How to respond to the user](products/kyber-squad/response-guidance.default.md).

## Commands

### Code comments

**Write few.** Code that follows the coding standards needs no comments; add one only where
the code departs significantly from those standards. Public APIs are the exception: they
always get XML doc comments (`///`). The writing style above still applies to every comment
you do write.

### The inner loop

**No dev agent runs the full test suite**, in or out of a Kyber-Squad run, and a cross-cutting change is no exception:
run a targeted set instead. The council's `test` gate and CI run the full suite. To reproduce a CI failure, run the classes or files CI reported.
A dev agent's full run is refused by the guard (best-effort; limits in the test standard): `scripts/proc-guard.sh run` exits 89 on an unfiltered
`dotnet test`, `npm --prefix dash run test` or `cargo test`, and names the targeted alternative. The
council and CI pass `--council`; a person at a terminal is not refused.

Inside a Squad run, your task packet names the test command and your agent rules say when to
run it. Otherwise, run `scripts/fast-loop.sh` while iterating: it inspects modified files and
routes to targeted checks for .NET, KyberDash, the tray crate, and governed docs. `--help` lists
its modes; `--full` and `--scoped` reach the `test` gate, so they are not for dev agents and exit 89 from a non-terminal shell. It is
**not** evidence and must never be cited as such ([`REVIEW.md`](REVIEW.md)).

Run the tests that cover your change, plus build/lint of the touched projects, and check each
run reports a non-zero test count. The commands for each area:

- .NET: [`docs/standards/test/README.md`](docs/standards/test/README.md#commands)
- Repository policy (scripts, workflows, gate configuration, docs prose, source scans):
  [`tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`](tests/KyberWeave.RepoPolicy.Tests/AGENTS.md)
- KyberDash: [`dash/AGENTS.md`](dash/AGENTS.md#gates)
- Tray UI and tray crate: [`dash/tray/AGENTS.md`](dash/tray/AGENTS.md#gates)

**Every local test, gate and e2e command runs through the process watchdog.** From the
repository root, wrap it as `scripts/proc-guard.sh run -- <command>`. The watchdog kills the
command's process tree at an absolute cap (exit 86) and fails a run that leaves children
behind (exit 87). The caps change only by flag (`--max-node`, `--max-tree`), never by environment
variable. `fast-loop.sh` already wraps its test and gate calls. The `test` gate takes `--slot dotnet-test`, so
overlapping full runs on one machine queue (exit 88 if the wait runs out) instead of piling onto its
cores; partial runs, builds and format checks never take a slot. Playwright runs only with
`--config dash/playwright.isolated.config.ts`, and never start the dashboard to run a test. See
[Running tests safely](docs/dash/runbook.md#running-tests-safely) and
[ADR 0047](docs/adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md).

### Before opening a PR

Run the build and the targeted tests above, the `task-gate` skill if you are a dev agent, and the
docs gates:

```bash
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

`review gates .` is the review council's command, not a dev agent's: its `test` gate has no filter,
so any .NET change runs the whole product suite, scoped or not. CI runs the full suite on the PR.
Without `--council`, from a shell with no terminal, its full-suite gates (`test`, `ts-test`, `cargo-test`)
are not run: each is reported as a refused blocking failure (`KW-REVIEW-033`, exit `1`) that means "not run
here", not a code finding. Only the `code-reviewer` council and CI pass `--council`. A scoped run still
runs the other gates; with `--all` a refused full-suite gate ends the run at once, with no gate started.

Run any test or build command you start yourself through `scripts/proc-guard.sh run --`. The
`dotnet test` gates (`test`, `test-policy`) and the dash `ts-test` gate in [`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml)
already run through it. Council gates skip gates whose `include` does not match changed files (`KW-REVIEW-026`);
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

If `.codegraph/` is missing or stale and a docs drift / codegraph tool needs it, this
repository authorizes `codegraph init .` at the root immediately
(repository-local only; installing CodeGraph is the user's decision). Do not ask for user
approval before that provisioning step. Before init, record `git status --short` and both
staged (`git diff --cached`) and unstaged (`git diff`) tracked diffs; afterwards undo only
tracked edits init introduced. Blocked or unusable init → report under GAPS and fall back;
`docs drift` without an index → report skipped, not passed.

Before `docs/` greps, use Kyber-Weave MCP `docs_answer_question_from_repo_documentation`;
before contract changes, `docs_find_documents_citing_code_symbol`; for taxonomy,
`docs_look_up_glossary_term_definition` and `docs_find_duplicates_conflicts_ambiguous_terms`.
No CLI equivalent — fall back to [`docs/README.md`](docs/README.md). Never use `docs/archive/` as
current guidance. The server's configuration key is `kw-docs`. Confirm each response's
`provenance:` `root=` matches `git rev-parse --show-toplevel`
(`docs_get_source_repository_and_revision`); a mismatch means the wrong repository.

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
| The engine — `KyberWeave.Core`, `KyberWeave.Core.Docs` and `KyberWeave.Core.Squad` | [`src/KyberWeave.Core/AGENTS.md`](src/KyberWeave.Core/AGENTS.md) |
| CLI commands and output | [`src/KyberWeave.Cli/AGENTS.md`](src/KyberWeave.Cli/AGENTS.md) |
| Installing, updating, or configuring Kyber-Weave or KyberDash | [`docs/cli-command-surface.md`](docs/cli-command-surface.md) — every verb and flag, the exit-code contract, and the canonical flows |
| The MCP server | [`src/KyberWeave.Mcp/AGENTS.md`](src/KyberWeave.Mcp/AGENTS.md) |
| KyberDash gates (`dash/web` and `dash/tray` are display layers) | [`docs/rules/kyberdash-display-layer.md`](docs/rules/kyberdash-display-layer.md), [`dash/AGENTS.md`](dash/AGENTS.md) |
| KyberDash architecture and runbook (`dash/web` and `dash/tray` are display layers) | [`docs/rules/kyberdash-display-layer.md`](docs/rules/kyberdash-display-layer.md), [`dash/AGENTS.md`](dash/AGENTS.md), [`docs/dash/architecture.md`](docs/dash/architecture.md), [`docs/dash/runbook.md`](docs/dash/runbook.md) |
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
- **<github-actions-coding-standard>**: `docs/standards/github-actions/README.md`
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
