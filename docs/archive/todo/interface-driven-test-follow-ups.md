---
id: archive/todo/interface-driven-test-follow-ups
title: Finish moving the misplaced integration tests and clean up the shared test library
doc-type: todo
component: CI Pipelines
status: archived
owner: dpalfery
last-reviewed: 2026-11-18
documentation-impact: extends-component
impact-components: [CI Pipelines]
---

# Finish moving the misplaced integration tests

Delivered on 2026-11-18. Fifteen logic classes left
[`KyberWeave.Integration.Tests`](../../../tests/KyberWeave.Integration.Tests/AGENTS.md) for the unit
project of the code they test, every real-resource case stayed behind with a name saying what it
proves, and the shared test library's cleanup list is closed. Each class moved on its own run so a
half-moved class could not land; the counts below are the xunit cases, measured with a class filter
before and after.

## The classes

| Class | Before | Where it lives now |
|---|---|---|
| `ArbiterInstallCommandTests` | 20 | 18 `Cli.Tests`, 2 `Integration.Tests` |
| `ArbiterUninstallCommandTests` | 17 | 15 `Cli.Tests`, 1 `Application.Tests`, 1 `Integration.Tests` |
| `DocsInitMcpCheckTests` | 19 | 19 `Cli.Tests` |
| `ReviewVerdictInputTests` | 116 | 116 `Cli.Tests` |
| `ReviewVerdictSeatsCommandTests` | 80 | 79 `Cli.Tests`, 1 `Integration.Tests`, plus 1 unit twin |
| `SquadHomeStateStoreTests` | 14 | 14 `Application.Tests/Squad/Deployment` |
| `SquadGlobalRootTests` | 111 | 79 + 29 + 1 `Application.Tests`, 1 `Content.Tests`, 1 `Integration.Tests` |
| `SquadDeploymentStateLayoutTests` | 29 | 25 + 1 `Application.Tests`, 1 `Cli.Tests`, 3 `Integration.Tests`, plus 3 unit twins |
| `SquadDeploymentStatePathSafetyTests` | 201 | 191 + 2 `Application.Tests`, 2 `RepoPolicy.Tests`, 8 `Integration.Tests`, plus 8 unit twins |
| `SquadDeploymentStateInstallTests` | 16 | 15 `Application.Tests`, 1 `Cli.Tests` |
| `SquadDeploymentStateUpdateTests` | 18 | 17 `Application.Tests`, 1 `Cli.Tests` |
| `SquadDeploymentStateRestoreTests` | 20 | 18 `Application.Tests`, 2 `RepoPolicy.Tests` |
| `SquadDeploymentStateRecoveryTests` | 52 | 51 `Application.Tests`, 1 `RepoPolicy.Tests` |
| `SquadDeploymentStateTransactionTests` | 75 | 73 `Application.Tests`, 1 `RepoPolicy.Tests`, 1 `Integration.Tests` |
| `SquadCliCommandTests` | 61 | 60 `Cli.Tests`, 1 `Integration.Tests` |
| `SquadGlobalHomeCliTests` | 47 | 47 `Cli.Tests` |

What stays in the integration project is one of the three kinds
[the test standard](../../standards/test/test.standards.md#three-kinds-of-test) allows: a real
symlink, real case-sensitive or case-insensitive volume behaviour, real permissions, an operating
system file share, a real deployment transaction and receipt, a real install-then-uninstall flow, a
real release extraction, or a composition smoke test. Each real-resource case keeps an in-memory twin
where the fake can express the behaviour, except where an adapter contract suite already proves it;
those cases cite the suite instead.

One product seam was added: `ICorpusSource.LinkTarget`, so a portable content test can again assert
that a linked file cannot escape its portable root. One product class was made parallel-safe:
`DocsInitCommand`'s two prompt overrides moved from process-wide statics to per-instance properties.

## The shared test library

All eleven cleanups are closed:

- `InMemoryFileSystem` exposes one root name, `Root`; there is no `RootPath` alias.
- The update tests already ran in memory through `UpdateWorld`; no test starts a real loopback child.
- `Data.Tests` and `Application.Tests` now install the shared test setup from their own assembly
  initializer instead of relying on another project's.
- `TestSquadCorpus` and `ResourceBearingSquadFixture` already defaulted to the in-memory tree.
- `ICorpusSource` gained the read-only link query, and the portable-root escape assertion is back.
- The two test classes that read a root from `Path.GetTempPath()` already take it from the fake root.
- The canonical-corpus root check lives in `Content.Tests`; the duplicate in the Warp renderer class
  was removed and its assertions folded into the content case.
- The four CodeGraph "never initialised" doctor cases have in-memory twins in
  `Application.Tests/Doctor`; the integration copies stay for the real container wiring and cite the
  twin.
- The file-system contract suite proves a directory link that escapes the root, on the fake and on
  the real adapter.
- The prose catalog check is named a content test and the paid model grading an opt-in integration
  harness in [the test standard](../../standards/test/test.standards.md).
- The content tests read the squad source through the corpus seam, with the real-disk packer left
  only where packaging is the subject.

Residue deliberately left, none of it blocking:

- `ResourceBearingSquadFixture` keeps an unused disk branch; removing it would edit ten call sites
  for no behavioural gain.
- `SquadPathPolicy` still calls `Path.GetFullPath` twice, tracked in
  [seam conversion leftovers](seam-conversion-leftovers.md).
- `InMemoryCorpusSource` models file links only; a seeded directory link is not expressible.
- `SquadTestCommands.InMemoryLifecycle` takes a state rather than a store, so two cases build a
  second state over the same tree; a store-taking overload would read better.
- The whole test suite still runs only in CI and the review council's gate; a developer runs the
  destination project, as the repository instructions say.

## Decisions

| ID | Question | Answer / decision | Supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What decides whether a case moves or stays? | A case moves to the unit project of the code it tests when it needs no outside resource; it stays in `KyberWeave.Integration.Tests` only as an adapter contract run, an end-to-end smoke test of a flow that also has unit tests, or a real third-party program test. A kept case gets an in-memory twin unless an adapter contract suite already proves the behaviour, in which case the case cites the suite. | This is requirement 7.1 and the test standard's three kinds of test. Every class's report records which cases stayed and why. | `autonomous: specification requirement 7.1` |
| D2 | How are helpers shared across the converted classes? | One fake per interface in `tests/KyberWeave.Testing`; a new fake is a new file there, and an existing fake gains a member only when it is missing. Document fixtures and scenario builders live once, in the testing library or a single shared base. | Duplicated helpers were the closeout's main cost: three review-fixture files and two deployment-state bases were reconciled during the move. | `autonomous: specification requirement 5.5` |
| D3 | How is the lost portable-root escape assertion restored? | Add `ICorpusSource.LinkTarget` to the corpus seam, implemented by the checkout adapter and the in-memory fake, rather than keeping the check only in the integration project. | The seam is test infrastructure; a read-only link query is the smallest member that makes the rule checkable without the disk. | `autonomous: todo item 5` |
| D4 | How do the docs-init prompt overrides stop serialising their test class? | Move `DocsInitCommand`'s `InteractiveCheckOverride` and `ConfirmFixOverride` from process-wide statics to per-instance `internal init` properties; behaviour is unchanged when unset. | The old class had to disable parallelisation and reset the statics on dispose, which was a test-visible product defect. | `autonomous: todo class DocsInitMcpCheckTests` |
| D5 | What happens to this todo when the work is done? | Archive it with the delivered counts and the residue list, and mark its row archived in the todo index. A completed reminder that stays open is read as work not done. | Precedent: the archived Factory prompt todo. | `autonomous: todo lifecycle` |

## Design harvest

| ID | Target component | Target doc type | Durable item | Destination |
|---|---|---|---|---|
| H1 | CI Pipelines | reference | The corpus seam answers a file's final link target, and the file-system contract suite proves a directory link that escapes the root and a directory-link move that moves the link rather than its target | [seams.md](../../reference/seams.md) |
| H2 | CI Pipelines | coding-standard | The prose catalog check is a content test; the paid model grading is an opt-in integration harness no gate runs | [test.standards.md](../../standards/test/test.standards.md) |

## Closeout mapping

| ID | Destination |
|---|---|
| D1, D2, D3, D4 | [seams.md](../../reference/seams.md) — the three kinds of test, one fake per interface, the corpus link query, and the per-instance prompt seam |
| D5 | [Todos](../../todo/README.md) — the archived row that records this delivery |
| H1 | [seams.md](../../reference/seams.md) — the corpus link query and the directory-link move contract case |
| H2 | [test.standards.md](../../standards/test/test.standards.md) — the catalog check and the opt-in grading harness |

## Related

- [Seams, adapters and the exceptions they keep](../../reference/seams.md) — the register these tests
  sit behind, now including the corpus link query and the directory-link move contract case
- [Test coding standard](../../standards/test/test.standards.md#three-kinds-of-test) — which project a
  test belongs in
- [ADR 0089](../../adr/0089-every-behaviour-is-testable-through-a-seam.md) — the decision this work
  completes
