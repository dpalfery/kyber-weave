# KyberWeave.Core

The engine. Everything the CLI and the MCP server do is implemented here; those two are
thin hosts over this assembly.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.

## Core does not construct its own collaborators

This is the constraint most easily broken by accident. Core defines **ports** and takes
them as constructor arguments or factories. **Composition roots** — `DocsCommandComposition`
in the CLI, `Program.cs` in the MCP server — decide which implementation is used.

`ICodeGraphResolver` is the worked example: Core consumes the interface, the CLI and MCP
host construct `CodeGraphResolverAdapter`, and tests inject `FakeCodeGraphResolver` so the
whole drift and join surface can be driven without `sqlite3` on the machine.

`DocumentIndexHost` takes `Func<ICodeGraphResolver>` and `Func<DocumentSet>` for the same
reason. If you find yourself newing up a loader or a resolver inside Core, the dependency
belongs in the caller.

## Layout

| Directory | Holds |
|---|---|
| `Docs/` | Parsing, search, validation, export, scaffolding — the DocGraph engine |
| `Skills/`, `Agents/` | Governance per artifact class |
| `Security/` | `InstructionSurfaceScanner`, shared by skill and agent scanning |
| `Diagnostics/` | `Diagnostic`, `DiagnosticReport`, `Severity` |
| `CodeGraph/` | Read-only port over the CodeGraph index |
| `Configuration/` | `kyber-weave.yml` loading and merging |
| `Text/`, `Parsing/` | Vectorizer, frontmatter reader |

## Rule ids live next to the code that raises them

Declared as `public const string` on the validator or linter that emits them, so the id
and the condition cannot drift apart. They are permanent — see the repository rules.

A `Diagnostic` carries `Code`, `Severity`, `Message`, `Subject`, `Path`, and `Hint`. Fill
the hint whenever the fix is knowable; `DocSpecValidator.Nearest` computes an edit-distance
suggestion and is offered only when the distance is plausibly a typo.

## Configuration merges by replacement

`OntologyConfigLoader.Merge` replaces lists rather than appending, and an **empty list is a
real value that clears the default** — `Clone` falls back only on null. That is how a host
drops the inherited `DevOps/*` exclusions. Preserve this when adding config keys, and add
the key to `OntologyYamlSection` with a hyphenated name.

## Dependencies

Markdig and YamlDotNet only. Adding a third needs a stated reason — see the sqlite3
decision recorded in `CodeGraph/CodeGraphResolverAdapter.cs`.

`InternalsVisibleTo` exposes internals to `KyberWeave.Tests`, so helpers like link
normalization and vocabulary parsing are unit-tested directly rather than only through the
public command surface. Marking something `internal` does not put it beyond test reach.

## Documented behaviour

Several algorithms here are described in the governed corpus, and the description is
expected to stay true:

- Retrieval scoring, authority weighting, and budgeting — [`docs/docgraph/retrieval.md`](../../docs/docgraph/retrieval.md)
- The corpus/joins two-clock reload — [`docs/docgraph/architecture.md`](../../docs/docgraph/architecture.md)
- Every rule id — [`docs/ci-pipelines/rule-reference.md`](../../docs/ci-pipelines/rule-reference.md)

Changing a weight, a threshold, or a severity means updating those documents in the same
change. `docs_for_symbol` finds which ones claim the symbol you are editing.
