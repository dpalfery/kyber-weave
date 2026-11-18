# KyberWeave.Core

**This directory holds no source files.** The project stays, but do not delete it and do not add
code here.

Every class that used to live here moved when the engine was split into layers:

| It was | It now lives in |
|---|---|
| logic — a parser, renderer, validator, resolver, planner or service | [`../KyberWeave.Application`](../KyberWeave.Application) |
| data — a record, value object, enum, options bag or constants | [`../KyberWeave.Domain`](../KyberWeave.Domain) |
| an interface | [`../KyberWeave.Contracts`](../KyberWeave.Contracts) |
| an adapter over a resource — file system, process, git, HTTP | [`../KyberWeave.Data`](../KyberWeave.Data) |

A static locator that fronted one of those was deleted rather than moved: its callers take the
`Contracts` interface and the container registers the `Application` implementation.

## Why the empty project is still here

`LayerReferenceTests` pins what `KyberWeave.Core` may reference — only `KyberWeave.Contracts`,
`KyberWeave.Domain` and `KyberWeave.Kernel` — and `CoreAssemblySplitTests` pins the hosts' and the
feature assemblies' reference sets against it. Removing the project would delete the layer those
tests describe.

That reference set is also why new code does not go here: `KyberWeave.Core` cannot reach
`KyberWeave.Application` or `KyberWeave.Data`, so logic written here could not use the engine.

## The engine does not construct its own collaborators

This constraint used to be stated in this file for `KyberWeave.Core`; it now applies to
`KyberWeave.Application`. A class defines or takes **ports** — a `Contracts` interface, or a seam
such as `IFileSystem`, `IProcessRunner` or `TimeProvider` — as constructor arguments.
**Composition roots** decide which implementation is used: the CLI's `*CommandComposition` types,
`Program.cs` in the MCP server, and `ServiceCollectionExtensions` in `Application` itself.

`ICodeGraphResolver` is the worked example: `Application` consumes the interface, the hosts
construct `CodeGraphResolverAdapter`, and tests inject `FakeCodeGraphResolver` so the whole drift
and join surface runs without `sqlite3` on the machine. If you find yourself newing up a loader or
an adapter inside `Application`, the dependency belongs in the caller.

## Where the engine's other rules went

- **Child processes** go through `ProcessRunner` in
  [`../KyberWeave.Kernel/Processes`](../KyberWeave.Kernel/Processes): never drain one redirected
  stream before the other, and never use the concatenated `Arguments` string. `ProcessRunnerTests`
  reintroduces the deadlock against a child that fills both streams.
- **Rule ids** (`KW-*`) are declared as `public const string` on the validator or linter that emits
  them, so the id and the condition cannot drift apart. They are permanent: add new ones, never
  reuse or renumber.
- **Documented behaviour.** Retrieval scoring and budgeting are described in
  [`docs/docgraph/retrieval.md`](../../docs/docgraph/retrieval.md), the two-clock corpus reload in
  [`docs/docgraph/architecture.md`](../../docs/docgraph/architecture.md), and every rule id in
  [`docs/ci-pipelines/rule-reference.md`](../../docs/ci-pipelines/rule-reference.md). Changing a
  weight, a threshold or a severity updates those documents in the same change;
  `docs_find_documents_citing_code_symbol` finds the ones that claim the symbol you are editing.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.
