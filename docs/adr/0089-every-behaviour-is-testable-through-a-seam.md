---
id: adr/0089-every-behaviour-is-testable-through-a-seam
title: Every behaviour is testable through a seam, and Domain holds data only
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-18
component: CI Pipelines
decided-by: []
supersedes: []
code-refs:
  - IFileSystem
  - IWritableFileSystem
  - IPathResolver
  - IProcessRunner
  - IProcessEnvironment
  - IIdGenerator
  - IConsole
  - ILease
  - IEmbeddedResources
  - ICorpusSource
  - InMemoryFileSystem
  - PhysicalFileSystem
---

# ADR 0089: Every behaviour is testable through a seam, and Domain holds data only

## Status

Accepted, 2026-11-18. Decided by the owner. It completes decision D5 of the layered-refactor
completion specification (draft, 2026-11-16), which asked for one unit test project per product
project and use cases proved over fakes. The interface-driven unit tests specification delivered
it; this record keeps the decisions that constrain future work.

## Context

On 2026-11-17 the full product test run took 251 seconds against a two-minute rule, and an
inventory of every class found why: the product reached outside resources directly instead of
through interfaces.

- About 2,800 call sites in `src/` touched the file system, processes, the environment, the clock,
  random ids, the console or locks directly, and about 1,200 of them sat in classes that also held
  logic.
- 32 static classes in `KyberWeave.Data` held logic and could receive no interface; 17 static
  service locators in `KyberWeave.Core` and `KyberWeave.Core.Docs` built adapters with `new`.
- `KyberWeave.Domain` held 210 static classes of logic (about 38,000 lines) among its 440 files.
- The interfaces that existed were thin and bypassed: `IProcessRunner` had one constructor
  consumer while a static `ProcessRunner.Run` was called 23 times, and a static configuration
  loader was called 23 times while `IKyberWeaveConfigRepository` existed.
- Of 615 test classes, 117 (15 %) were true unit tests. 372 needed a real resource.

The owner's instruction (2026-11-17): every portion of the application is interface-driven so all
code can be tested by a true unit test and no integration test is required, with one exception —
sealed framework types that offer no interface.

## Decision

1. **This is not new scope.** It completes D5 of the layered-refactor specification; the owner
   confirmed it was part of the original modernization goal.

2. **One file-system seam, with role views.** `IFileSystem` (read) and `IWritableFileSystem`
   (write) are one family, extended to cover every member the product uses. The seven narrow file
   interfaces (`IRepositoryFileStore`, `IDoctorFileSystem`, `ISquadFileAccess`,
   `ISquadLinkFileSystem`, `IFileStatReader`, `IExclusiveFileWriter`, `IContainedFileReader`) stay
   as role views, so a caller can ask for the write it needs and nothing more.

3. **The in-memory file system is ours.** `InMemoryFileSystem` is built against our seam, not
   taken from a package: packages do not model links, exclusive create or case-preserving names
   faithfully. It reproduces atomic rename with overwrite, exclusive create, case-insensitive but
   case-preserving names where the platform has them, links and their resolution, deletion without
   following links, enumeration, length, last-write time, and fault injection at the Nth operation.

4. **A fake is kept honest by a contract suite.** Every seam has one abstract suite in
   `tests/KyberWeave.Testing`, run against the fake in a unit project and against the adapter in
   `KyberWeave.Integration.Tests`. A behaviour the fake cannot reproduce is a failing contract test,
   not a silent difference. A fake previously kept the suite green while real installs failed
   (`FakeSquadReleaseSource`), which is the failure this rule prevents.

5. **There is no new clock interface.** `TimeProvider` already offers the wall clock, monotonic
   timestamps and elapsed time, timers and delay, and the framework ships `FakeTimeProvider`.

6. **Static entry points are removed, not kept as shims.** A static class that holds logic becomes
   an instance class behind an interface, and its static entry point goes away once its callers
   move. Pure functions over their arguments and constants stay static. The layered refactor left
   25 shim types; shims keep the old path reachable.

7. **`KyberWeave.Domain` holds data only.** Its logic moved to `KyberWeave.Application`. Domain
   needs no seam, and the seams stay in `Contracts`, which already references `Domain`; Domain
   referencing `Contracts` would be a cycle. What counts as data: records, DTOs, property bags,
   value objects, enums, options, constants, and a read-only property computed from the type's own
   fields.

8. **Integration tests run only in CI.** They are the safety net, in their own required job.
   Developer agents, task gates and the review council's gates run the unit and content projects and
   never the integration project. The review council's integration-test gate was removed.

9. **The seven crash-safety weaknesses became contract tests.** An independent review of the parked
   journal-batching branch listed seven weaknesses in the squad deployment code; each became a
   crash-safety contract test and was fixed where it was moved onto the seams.

10. **A test that reads the shipped content is a third kind of test.** It is neither unit nor
    integration: it lives in `KyberWeave.Content.Tests`, reads the content through the read-only
    corpus seam (`ICorpusSource`), writes nothing and starts nothing, so it runs locally with the
    unit projects and counts toward the 120 seconds. `PromptContent.Tests` was folded into it.
    `RepoPolicy.Tests` stays as it is — it starts programs, so it is not a content test.

11. **No architecture test enforces "only adapters touch resources".** The rule lives in
    [`AGENTS.md`](../../AGENTS.md#seams-only-an-adapter-touches-a-resource), the test standard and
    the review guidance, and a reviewer raises direct resource access outside an adapter as a
    finding. This is consistent with the standing rule that test rules are guidance, not guards.
    One mechanical check predates the decision and stays: the repository-policy scan that keeps
    ambient I/O out of `KyberWeave.Domain`. It is narrower than this rule, and no second guard is
    added.

12. **A service interface lives in `Contracts`, always.** A `Contracts` member that names logic in
    `Domain` was narrowed to name data only; no interface moved out of `Contracts`. Moving one up is
    impossible while `Data` implements or consumes it.

13. **Domain's interfaces and service-handle types moved to `Contracts` together.** Domain no longer
    declares any interface. The 17 interfaces it still declared moved to `Contracts`, together with
    the four types that held service handles: `SquadDeploymentPlan`, `SquadRenderRequest`,
    `SquadLifecycleResult` and `ArbiterServeContext`.

14. **The empty `KyberWeave.Core`, `Core.Docs` and `Core.Squad` projects were deleted.** The owner
    accepted this, reversing an older "do not delete" note. Their remaining members were static
    locators or shims over services that had already moved; nothing held logic of its own.

15. **The squad transaction journal gained a final `committed` phase.** A newer binary recovers an
    older journal. An older binary that meets the new phase refuses loudly and changes nothing;
    that direction was verified before merge.

16. **Install, update and uninstall recover an unfinished transaction before planning.** Recovery
    deletes only what the journal says the transaction wrote and refuses, changing nothing, on any
    other entry. A dry run warns about a pending journal. A clean run may remove an empty
    `.kyber-weave` folder that existed before it: the folder holds no state and the next run
    recreates it.

17. **Self-update hardening was accepted even though it changes behaviour** (requirement 10.1 asked
    for behaviour unchanged): archive entries that are links, devices or FIFOs are refused, setuid
    bits and file times are not restored, the copy is staged and then renamed, the staging area is
    removed on failure, and the probe file has a fixed name (a concurrent-update collision is
    accepted).

18. **The instance link-sync cross-process lock times out after five minutes.** It used to wait
    forever; the lease seam cannot wait without a bound, and an unbounded wait looks like a hang.

19. **The Semgrep finding on the interactive process start is a false positive.**
    `ProcessRunner.RunInteractive` sets `UseShellExecute = false`, and the one caller that
    concatenates arguments refuses `cmd` metacharacters.

20. **`ArbiterCalibrationTests` live in the integration project as an opt-in harness.** They use
    checkout fixtures, environment variables and paid HTTP when enabled, and skip by default.

## Consequences

- Every product behaviour has an in-memory unit test. The full unit run is about 16 seconds locally
  against the 120-second limit, and the integration project is the safety net CI runs.
- A resource call outside an adapter is caught by review, not by the compiler. The register in
  [`docs/reference/seams.md`](../reference/seams.md) is the list a reviewer checks against, and the
  small group of path and platform calls the conversion left is tracked in
  [`docs/todo/seam-conversion-leftovers.md`](../todo/seam-conversion-leftovers.md).
- A fake that drifts from its adapter fails its contract suite, so a green unit run is evidence
  about the real resource too.
- `Domain` is a data project. Adding a method that decides anything there is a review finding.
- Fifteen integration classes were still misplaced logic tests when the specification was archived;
  they are copied one class per run into their unit projects and tracked in
  [`docs/todo/interface-driven-test-follow-ups.md`](../todo/interface-driven-test-follow-ups.md).

## Related

- [Seams, adapters and the exceptions they keep](../reference/seams.md) — the register this decision is read through
- [Test coding standard](../standards/test/test.standards.md) — the three kinds of test, the fakes and the contract suites
- [Review guidance](../../REVIEW.md) — section 15, the findings this decision creates
- [Kyber-Squad architecture](../kyber-squad/architecture.md#crash-safety-contract-tests) — the deployment crash-safety suites
- [ADR 0059](0059-full-test-runs-are-reserved-for-the-council-and-ci.md) — which runs are full suites, and who may start one
