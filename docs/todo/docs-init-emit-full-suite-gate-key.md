---
id: todo/docs-init-emit-full-suite-gate-key
title: Make docs init write the full-suite key on whole-suite test gates
doc-type: todo
component: DocGraph
status: draft
owner: dpalfery
last-reviewed: 2026-11-06
---

# Make docs init write the full-suite key on whole-suite test gates

This repository's `.kyber-weave/kyber-weave.yml` marks its whole-suite gates `full-suite: true`, so
the council can run them and a developer agent cannot. A host that runs `kyber-weave docs init`
gets its review gates from the shipped catalogue, not from this file. Its generated whole-suite
test gates carry no `full-suite` key, so they run without the refusal from
[ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md).

Checked 2026-11-06: the gate renderer
(`src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`) and the block writer that puts the gates
into the host's config (`src/KyberWeave.Core.Docs/Scaffolding/ReviewBlockWriter.cs`) do not emit
`full-suite`. No file under `src/`, `templates/` or `products/` mentions `proc-guard`, so the
generated test commands are not wrapped in `scripts/proc-guard.sh` either. The key is documented in
`docs/configuration.md` (the `review:` section), where it is described as a gate option.

**Work.** Have the catalogue emit `full-suite: true` on each whole-suite test gate it writes. Decide
whether the generated test command should also be wrapped in `scripts/proc-guard.sh run --council`,
and record the decision. Hosts already scaffolded are not changed by this todo; their owners
add the key by hand or re-run the scaffold.

**Acceptance.** A repository scaffolded by `kyber-weave docs init` has `full-suite: true` on each
whole-suite test gate it writes, and a targeted test gate does not carry it. The scaffold tests pin
both. `kyber-weave docs validate` reports no `KW-CONFIG-002` warning for the key on a current CLI.
