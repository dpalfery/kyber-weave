---
id: todo/interface-driven-test-follow-ups
title: Finish moving the misplaced integration tests and clean up the shared test library
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# Finish moving the misplaced integration tests

## The finding

The interface-driven unit tests specification retired with
[`KyberWeave.Integration.Tests`](../../tests/KyberWeave.Integration.Tests/AGENTS.md) still holding
fifteen classes whose subject is logic, not a real resource. They pass, but they spend the
integration job's time and prove behaviour a unit test over fakes should prove. Each belongs in the
unit project of the code it tests:

| Class | Destination |
|---|---|
| `ArbiterUninstallCommandTests` | `KyberWeave.Cli.Tests` |
| `DocsInitMcpCheckTests` | `KyberWeave.Cli.Tests` |
| `ReviewVerdictInputTests` | `KyberWeave.Cli.Tests` |
| `ReviewVerdictSeatsCommandTests` | `KyberWeave.Cli.Tests` |
| `SquadHomeStateStoreTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadGlobalRootTests` | `KyberWeave.Application.Tests` |
| `SquadDeploymentStateLayoutTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStatePathSafetyTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStateInstallTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStateUpdateTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStateRestoreTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStateRecoveryTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadDeploymentStateTransactionTests` | `KyberWeave.Application.Tests/Squad/Deployment` |
| `SquadCliCommandTests` | `KyberWeave.Cli.Tests` |
| `SquadGlobalHomeCliTests` | `KyberWeave.Cli.Tests` |

`ArbiterInstallCommandTests` is already converted. The owner is doing the rest one class per run on
the `opencode` branch, so this list is the queue, not a single task. Each move keeps the class's
real-resource cases where they belong: a symlink, permission-denied, lease-contention or
real-child case stays in the integration project with a name that says why.

## Smaller cleanups in the same area

- `InMemoryFileSystem` no longer has the `RootPath` alias; keep `Root` and do not reintroduce a
  second name for the field.
- `LoopbackFinishingProcess` moved to `KyberWeave.Testing`; the three update tests it blocked can
  now use a fake process or the in-memory archive extractor instead of the shared helper.
- `Data.Tests` and `Application.Tests` do not call `TestRuntime.Initialize` from an assembly
  initializer. A test that relies on `TestTempRoot` redirection in those projects must not depend on
  another project's initializer.
- `TestSquadCorpus` and `ResourceBearingSquadFixture` still default to a real disk tree. A test that
  needs no real files should build the fixture on `InMemoryFileSystem`.
- The corpus seam has no link query, so `PortableContentTests` lost its symlink-escape assertion.
  Add a read-only link query to `ICorpusSource`, or move that one check to the integration project.
- `SquadWorkerBaselineFileTests` and `SquadWorktreeMcpTests` still derive a root from
  `Path.GetTempPath()`. Derive it from the fake root instead.
- `ToolRoot_ExistsForCanonicalCorpus` (the Antigravity class) checks the shipped corpus, so it belongs
  in `KyberWeave.Content.Tests`.
- Four CodeGraph "never initialised / run init" doctor cases in `McpLiveProbeTests` run only on the
  real disk with a shell-stub server. Add the in-memory doctor wiring that gives them unit coverage.
- The file-system contract suite has no config symlink-escape case (it needs a real link in the
  integration run). Add it so the fake and `PhysicalFileSystem` agree on that behaviour.
- `ProseRuleCatalogTests` and `ProseRuleGradingTests`: keep the catalog check in the content project
  and the paid model grading as an opt-in integration harness, and say which is which in the test
  standard when they move.
- The content tests still load the squad source through `RealSquadServices` on the real file system,
  with the root taken from the seam. Reading through `ICorpusSource` itself would remove the last
  real-disk dependency from the content project.

## Why this was not fixed here

The closeout's job is to retire the specification without losing the work. The owner chose to
convert the fifteen classes separately, one per run, so a half-moved class cannot land. The cleanup
items are small and independent; folding them into the same runs would mix subjects.

## Related

- [Seams, adapters and the exceptions they keep](../reference/seams.md) — the register these tests
  sit behind
- [Test coding standard](../standards/test/test.standards.md#three-kinds-of-test) — which project a
  test belongs in
- [ADR 0089](../adr/0089-every-behaviour-is-testable-through-a-seam.md) — the decision this work
  completes
