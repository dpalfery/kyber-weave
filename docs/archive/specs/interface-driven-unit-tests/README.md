---
id: archive/specs/interface-driven-unit-tests/index
title: Interface-driven unit tests specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-18
documentation-impact: extends-component
impact-components: [CI Pipelines, KyberSquad, KyberArbiter, DocGraph, ReviewCouncil]
---

# Interface-driven unit tests specification

**Status:** Archived, 2026-11-18. Approved by dpalfery, 2026-11-17 (requirements, design and
tasks).

Completes decision D5 of the layered-refactor completion specification: every product behaviour is
testable by an in-memory unit test written against interfaces, outside resources sit behind a small
set of seams implemented by thin adapters, each fake is proven against its adapter by one shared
contract suite, and integration tests become a safety net run only in CI. The one allowed exception
is a sealed framework type with no interface.

## Outcome

Delivered in six phases. The full unit run (every unit project plus the content, policy and
architecture projects) takes **15.8 seconds** against the 120-second limit, from 251 seconds when the
work started. The integration project is CI's own required job and does not count toward the budget.
The old catch-all `tests/KyberWeave.Tests` is gone, each product project has its unit test project,
the content tests read through the corpus seam, and the deployment, update and recovery paths run
their crash-safety contract suite on the in-memory and real file systems.

Where the delivery diverged from the design, the implementation is the truth and the decision record
says so:

- `KyberWeave.Domain` holds data only, declares no interface, and the 17 interfaces and four
  service-handle types it still held moved to `Contracts` (D7, D12).
- Content tests are a third kind in `KyberWeave.Content.Tests`; `PromptContent.Tests` was folded in
  and `RepoPolicy.Tests` stayed as it is (D10).
- The empty `Core`, `Core.Docs` and `Core.Squad` projects were deleted.
- The squad transaction journal gained a `committed` phase, and install, update and uninstall recover
  an unfinished transaction before planning.
- Self-update hardening was accepted although it changes behaviour (requirement 10.1).
- Fifteen integration classes were still misplaced logic tests; the owner is converting them one per
  run and they are tracked in
  [`docs/todo/interface-driven-test-follow-ups.md`](../../../todo/interface-driven-test-follow-ups.md).

Canonical documentation now carries the durable content: [the seams register](../../../reference/seams.md),
[the test standard](../../../standards/test/test.standards.md), [`AGENTS.md`](../../../../AGENTS.md),
[the review guidance](../../../../REVIEW.md), [the Kyber-Squad architecture](../../../kyber-squad/architecture.md)
and [ADR 0089](../../../adr/0089-every-behaviour-is-testable-through-a-seam.md). The remaining product
and test residue is in `docs/todo/`.

## Decisions

The permanent decision table is in [design](design.md#decisions). Each decision is recorded in one
owning canonical document, linked below.

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| D1 | Is this new scope? | No: it completes D5 of the layered-refactor completion specification. | Accepted |
| D2 | One file-system interface or many? | One family (`IFileSystem`, `IWritableFileSystem`), the narrow interfaces as role views. | Accepted |
| D3 | Build the in-memory file system or take a package? | Build it against our seam. | Accepted |
| D4 | How is a fake kept honest? | One contract suite per seam, run on the fake and the adapter. | Accepted |
| D5 | A monotonic clock interface? | No: `TimeProvider` already has the members, and its fake ships with the framework. | Accepted |
| D6 | Keep static entry points as shims? | No: remove each once its callers move. | Accepted |
| D7 | What does `Domain` hold? | Data only; its logic moves to `Application`, and the seams stay in `Contracts`. | Accepted |
| D8 | Where do integration tests run? | CI only, as their own required job; developer agents and gates run the unit and content projects. | Accepted |
| D9 | The seven crash-safety weaknesses | Each becomes a contract test and is fixed in phase 3. | Accepted |
| D10 | Is a shipped-content test a unit test? | Neither unit nor integration: content tests in `KyberWeave.Content.Tests` through the corpus seam. | Accepted |
| D11 | Enforce "only adapters touch resources" with an architecture test? | No: written guidance and review. | Accepted |
| D12 | Where do service interfaces live? | In `Contracts`, always; a member naming `Domain` logic is narrowed to name data. | Accepted |

## Design harvest

The items that outlived the specification and where each now lives.

| ID | Design item | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| H1 | The seams table and the adapter register | CI Pipelines | reference | yes |
| H2 | The test project layout and what is an integration test | CI Pipelines | coding-standard | yes |
| H3 | The in-memory file system and the contract suites | CI Pipelines | coding-standard | yes |
| H4 | Crash-safety contract tests for squad deployment | KyberSquad | architecture | yes |
| H5 | Decisions D1–D12 and the owner decisions made during delivery | CI Pipelines | adr | yes |
| H6 | Verify: the seams, the review findings and the documented rules | CI Pipelines, ReviewCouncil | reference, architecture | yes |
| H7 | Look up: the Arbiter calibration harness is an opt-in integration test | KyberArbiter | architecture | yes |

## Closeout mapping

Every decision and harvest item maps to the living document that now carries it.

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| D1 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| D2 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| D3 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| D4 | [Test standard](../../../standards/test/test.standards.md#the-seams-and-their-fakes) | extends-component | archived |
| D5 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| D6 | [ADR 0089](../../../adr/0089-every-behaviour-is-testable-through-a-seam.md) | extends-component | archived |
| D7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) | extends-component | archived |
| D8 | [Test standard](../../../standards/test/test.standards.md#three-kinds-of-test) | extends-component | archived |
| D9 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#crash-safety-contract-tests) | extends-component | archived |
| D10 | [Documentation ontology](../../../documentation-ontology.md) | extends-component | archived |
| D11 | [Review council architecture](../../../code-review/architecture.md#review-guidance) | extends-component | archived |
| D12 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| H1 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| H2 | [Test standard](../../../standards/test/test.standards.md) | extends-component | archived |
| H3 | [Test standard](../../../standards/test/test.standards.md#the-seams-and-their-fakes) | extends-component | archived |
| H4 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md#crash-safety-contract-tests) | extends-component | archived |
| H5 | [ADR 0089](../../../adr/0089-every-behaviour-is-testable-through-a-seam.md) | extends-component | archived |
| H6 | [Review council architecture](../../../code-review/architecture.md#review-guidance) | extends-component | archived |
| H7 | [Kyber Arbiter architecture](../../../kyber-arbiter/architecture.md#provider-and-models) | extends-component | archived |
| D3 | [Test standard](../../../standards/test/test.standards.md#the-seams-and-their-fakes) | extends-component | archived |
| H6 | [Seams register](../../../reference/seams.md) | extends-component | archived |


| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 10 requirements in EARS form |
| [Design](design.md) | Evidence, architecture, the seams, the in-memory file system, contract tests, what stays an integration test, phases, decisions D1 to D12, no open questions |
| [Tasks](tasks.md) | 36 tasks in five phases plus closeout, test-first |
| [Inventory](inventory/README.md) | Class-by-class evidence for every project and test project |
