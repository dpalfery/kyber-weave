---
id: todo/docs-init-emit-full-suite-gate-key
title: Make docs init write the full-suite key on whole-suite test gates
doc-type: todo
component: DocGraph
status: archived
owner: dpalfery
last-reviewed: 2026-11-08
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Make docs init write the full-suite key on whole-suite test gates

> [!NOTE]
> **Closed 2026-11-08.** `GateCatalogue` now renders `full-suite: true` on every gate whose
> `role` is `test` and on no other gate, so a host scaffolded by `kyber-weave docs init` is guarded
> from its first review. The rule is derived from the role rather than from a list of ids, so a
> family that later ships a separate integration or end-to-end suite carries the refusal without a
> row being added anywhere. The generated command is deliberately **not** wrapped in
> `scripts/proc-guard.sh`: the guard is this repository's own file and does not ship to a host,
> while the key is the half that travels. `GateCatalogueRenderTests.WholeSuiteCatalogueGatesRenderFullSuiteAndATargetedGateDoesNot`
> pins both directions in one comparison — a scaffold that marked every gate full-suite and one
> that marked none both fail — and the rendered fixtures under
> `tests/KyberWeave.Tests/fixtures/gate-catalogue/*/expected-review.yml` carry the same key.
> `docs validate` reports no `KW-CONFIG-002` for it, because `full-suite` is a declared council-gate
> key. Recorded in [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md)
> decision 5.

This repository's `.kyber-weave/kyber-weave.yml` marks its whole-suite gates `full-suite: true`, so
the council can run them and a developer agent cannot. A host that runs `kyber-weave docs init`
gets its review gates from the shipped catalogue, not from this file. When this todo was written,
its generated whole-suite test gates carried no `full-suite` key, so they ran without the refusal
from [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md).

Checked 2026-11-06: the gate renderer
(`src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`) and the block writer that puts the gates
into the host's config (`src/KyberWeave.Core.Docs/Scaffolding/ReviewBlockWriter.cs`) emitted no
`full-suite`. No file under `src/`, `templates/` or `products/` mentions `proc-guard`, which is what
settled the second question.

## Acceptance criteria

- [x] A repository scaffolded by `kyber-weave docs init` has `full-suite: true` on each whole-suite
      test gate it writes, and a targeted test gate does not carry it — `IsFullSuite` reads the
      gate's `role`, and `GateCatalogueRenderTests` pins both directions.
- [x] The scaffold tests pin both — `GateCatalogueRenderTests`, `DocsInitReviewGatesTests` and the
      five `fixtures/gate-catalogue/*/expected-review.yml` files.
- [x] `kyber-weave docs validate` reports no `KW-CONFIG-002` warning for the key on a current CLI —
      verified: the run over this tree reports no such finding.

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| FW1 | Does the catalogue write `full-suite` on a whole-suite gate? | Yes, on every gate whose `role` is `test`, and on no other. The role is the catalogue's own definition of a whole-suite run, so a family's second whole-suite gate is covered without another row, and a single check keeps running for a developer agent. | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 5 |
| FW2 | Is the generated test command wrapped in `scripts/proc-guard.sh run --council`? | No. The key is what makes `review gates` refuse the gate. `proc-guard.sh` is this repository's own script and does not ship to a host, so wrapping would put a path in a host's configuration that the host does not have. | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 5; [review council architecture](../../code-review/architecture.md#the-shipped-gate-catalogue) |
| FW3 | What happens to a host already scaffolded without the key? | Nothing automatic: re-running `docs init` never edits an existing `review:` block. The host adds the key by hand, or copies the rendered block from `kyber-weave review catalogue`. | [Review council architecture](../../code-review/architecture.md#the-shipped-gate-catalogue) |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH1 | ReviewCouncil | adr | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) | A whole-suite gate carries `full-suite: true`, the shipped catalogue writes it from the gate's role, and the generated command is not wrapped in the process guard. |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| FW1, FW2, DH1 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) decision 5 and Consequences | ReviewCouncil | 2026-11-08 | archived |
| FW3 | [Review council architecture: the shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | ReviewCouncil | 2026-11-08 | archived |
