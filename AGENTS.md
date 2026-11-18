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
Three mechanical refusals back that rule (best-effort; limits in the test standard):
`kyber-weave review gates` refuses a gate marked `full-suite: true` with `KW-REVIEW-033` unless the caller is the council
(`--council`) or a person at a terminal; `scripts/fast-loop.sh --full`, `--scoped` and `--regenerate` exit 89 from a
non-interactive shell; and the shipped gate catalogue writes `full-suite: true` on every whole-suite gate.

Inside a Squad run, your task packet names the test command and your agent rules say when to
run it. Otherwise, run `scripts/fast-loop.sh --touched` while iterating: it inspects modified files and
routes to targeted checks for .NET, KyberDash, the tray crate, and governed docs. For .NET it runs only the
product test classes that name a type from a changed C# file, the layer test projects at the same time, and checks
format and style on the changed files only (about 45 seconds warm, against several minutes for the whole suite);
when nothing matches it falls back to the fast tier. `--help` lists
its modes; `--full` and `--scoped` reach the `test` gate, so they are not for dev agents and exit 89 from a non-terminal shell. It is
**not** evidence and must never be cited as such ([`REVIEW.md`](REVIEW.md)).

A full run, wherever it happens — the council's `test` gate or CI — is timed and its number reported: see
[the two-minute budget](#the-net-unit-suite-completes-in-120-seconds-or-less).

Run the tests that cover your change, plus build/lint of the touched projects, and check each
run reports a non-zero test count. The commands for each area:

- .NET: [`docs/standards/test/test.standards.md`](docs/standards/test/test.standards.md#completion)
- Repository policy (scripts, workflows, gate configuration, docs prose, source scans):
  [`tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`](tests/KyberWeave.RepoPolicy.Tests/AGENTS.md)
- Shipped content: agents, skills, standards, documents and workflows, read through the corpus seam:
  [`tests/KyberWeave.Content.Tests/AGENTS.md`](tests/KyberWeave.Content.Tests/AGENTS.md)
- KyberDash: [`dash/AGENTS.md`](dash/AGENTS.md#gates)
- Tray UI and tray crate: [`dash/tray/AGENTS.md`](dash/tray/AGENTS.md#gates)

**Every test, gate and e2e command runs directly, with no process wrapper.** `.NET` commands run via
`dotnet`, so Roslyn/MSBuild build servers stay warm; KyberDash (`dash/`) and tray (`dash/tray/`)
commands run via `npm --prefix dash …`, `npx --prefix dash playwright test …` and
`cargo … --manifest-path dash/tray/src-tauri/Cargo.toml …`. KyberDash no longer spawns Node
children for jobs or the OTLP receiver, so there is no watchdog to wrap them with: the safety now
comes from bounded, allow-listed process sites ([ADR 0047](docs/adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md)).
`fast-loop.sh` runs the same commands directly. Playwright runs only with
`--config dash/playwright.isolated.config.ts`, and never start the dashboard to run a test. See
[Running tests safely](docs/dash/runbook.md#running-tests-safely).

### Before opening a PR

A change of at most 300 changed lines (added plus removed, from `git diff --numstat <base>...HEAD`)
gets the quick review before its pull request is opened; a larger change gets the full pre-push
council. Both are in the `code-review` skill.

Run the build and the targeted tests above, the `task-gate` skill if you are a dev agent, and the
docs gates:

```bash
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

#### The .NET unit suite completes in 120 seconds or less — a hard limit

The full run — `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`, the `test` gate's command over
`tests/KyberWeave.Tests` — must complete within 120 seconds of wall-clock time. That is a hard limit, not a target.
Time every local full run: the runner's closing summary `duration:` line is the number, and whoever ran it, person
or agent, states it in their report. Any local full run over 120 seconds is escalated at once, whatever else the
machine was doing — no second run to confirm. The work is not done: escalate with the measured time, the commit,
the slowest test classes and what else was running on the machine, if known. Then loop: per-class timings, fix or
split the slowest classes, re-measure, until it is at or under 120 seconds. Never raise the budget, skip or disable
tests, or report the task complete while it is over; a change that makes the suite slower than the budget is not mergeable.

What keeps the run there is written down, in [the test standard's speed rules](docs/standards/test/test.standards.md#the-suite-stays-inside-its-time-budget);
read them before you write a test. Do not add a guard script or a timing check for them: the rules are guidance an
author reads, and the classes themselves stay performant.

**Unit tests live in one project per product project**: `tests/KyberWeave.<Project>.Tests` for Application, Hooks, Kernel,
Mcp, Arbiter, Cli and Data (see [the test standard](docs/standards/test/test.standards.md#stack)); classes not yet moved
are still in `tests/KyberWeave.Tests`.

**Integration tests live in `tests/KyberWeave.Integration.Tests`.** A test that performs a real Squad
deployment, starts a real program, or needs a real file system beyond its own small scratch folder
belongs there. That project is the safety net: CI runs it on every pull request as its own required job.
A developer agent, a task gate and the review council's gates run unit and content projects only, never this one. The rule for which project a test belongs in, and the in-memory unit
test that stands in for new behaviour, are in
[the test standard](docs/standards/test/test.standards.md#which-project-a-test-belongs-in).

`review gates .` is the review council's command, not a dev agent's: its `test` gate has no filter,
so any .NET change runs the whole product suite, scoped or not. CI runs the full suite on the PR.
Without `--council`, from a shell with no terminal, its full-suite gates (`test`, `ts-test`, `cargo-test`)
are not run: each is reported as a refused blocking failure (`KW-REVIEW-033`, exit `1`) that means "not run
here", not a code finding. Only the `code-reviewer` council and CI pass `--council`. A scoped run still
runs the other gates; with `--all` a refused full-suite gate ends the run at once, with no gate started.

Run every test and e2e command you start yourself directly: `.NET` via `dotnet`, KyberDash and tray
via `npm --prefix dash …`, `npx --prefix dash playwright test …` and `cargo … --manifest-path
dash/tray/src-tauri/Cargo.toml …`. In [`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml)
every gate — the `dotnet test` gates (`test`, `test-policy`) and the dash
`ts-test` gate — declares its command directly. Council gates skip gates whose `include` does not match changed files (`KW-REVIEW-026`);
see [`docs/code-review/architecture.md`](docs/code-review/architecture.md). Format, build,
test, and skill argv are the `review.gates` entries in
[`.kyber-weave/kyber-weave.yml`](.kyber-weave/kyber-weave.yml). There is no separate
restore gate: `--no-restore` is omitted on format and build, so those gates restore
themselves. Release/self-updater/kyberdash
build paths: `./scripts/update-loop.sh` ([`docs/distribution.md`](docs/distribution.md#verifying-a-release-locally)).
KyberDash and tray gates: [`dash/AGENTS.md`](dash/AGENTS.md), [`dash/tray/AGENTS.md`](dash/tray/AGENTS.md).

## Exploration order

Do not start with grep, find, or arbitrary browsing when a semantic index covers the question.
MCP tool names may be namespace-qualified by the active harness.

<!-- CODEGRAPH_START -->
**Code:** When `.codegraph/` exists, use `codegraph_explore` (or `codegraph explore "…"`)
before text search; name a file or symbol when you need line-numbered source.
<!-- CODEGRAPH_END -->

In a worktree, or when the tool is not listed, run `codegraph explore "…"` from that
worktree's shell; read files directly only if it fails. A worktree indexes ITSELF with
`kyber-weave doctor --ensure .` and never shares the main checkout's index.

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
current guidance. The server's configuration key is `kw-docs`. Every answer leads with a
`provenance:` line carrying `root=`, `branch=`, `repo=`, `rev=`, `dirty=` and `documents=N`
(`docs_get_source_repository_and_revision`). Compare `repo=`, the repository's common git
directory, not `root=`: a `root=` that is not your own working tree's root is another worktree
of this repository, and only a different `repo=` means the wrong repository. `branch=` names the
branch the answer came from.

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
[`src/KyberWeave.Application/AGENTS.md`](src/KyberWeave.Application/AGENTS.md).

## Where to go next

| Working on | Read |
|---|---|
| Writing C# anywhere in this repository | [`docs/standards/csharp/csharp.standards.md`](docs/standards/csharp/csharp.standards.md), the path declared as **<csharp-coding-standard>** below |
| The engine — `KyberWeave.Application` and the layers under it | [`src/KyberWeave.Application/AGENTS.md`](src/KyberWeave.Application/AGENTS.md) |
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
- **<csharp-coding-standard>**: `docs/standards/csharp/csharp.standards.md`
- **<test-coding-standard>**: `docs/standards/test/test.standards.md`
- **<typescript-coding-standard>**: `docs/standards/typescript/typescript.standards.md`
- **<rust-coding-standard>**: `docs/standards/rust/rust.standards.md`
- **<tauri-coding-standard>**: `docs/standards/tauri/tauri.standards.md`
- **<go-coding-standard>**: `docs/standards/go/go.standards.md`
- **<github-actions-coding-standard>**: `docs/standards/github-actions/github-actions.standards.md`
- **<plan-index>**: `docs/plans/README.md`
- **<specification-index>**: `docs/specs/README.md`
- **<todo-index>**: `docs/todo/README.md`
- **<adr-index>**: `docs/adr/README.md`
- **<rules-index>**: `docs/rules/README.md`
- **<reference-index>**: `docs/reference/README.md`
- **<review-guidance>**: `REVIEW.md`
- **<onboarding>**: `done`
- **<autonomy>**: `autonomous`

Skills and other portable instruction files SHALL reference these paths by the
property name above — "the path declared as **<component-catalog>**
in the repository root `AGENTS.md`" — rather than embedding a relative link that
traverses out of the skill's own directory. A skill written that way stays correct
when this repository moves something; only this registry has to change.

Generated by `kyber-weave docs init` from `.kyber-weave/kyber-weave.yml`. Edit that
file rather than this block: everything between the markers is rewritten on the next
run. The configuration is the authority when the two disagree: follow it, and have
`docs init` regenerate this block, rather than stopping a run over the difference.
<!-- KYBER_WEAVE_CONFIG_REG_END -->
