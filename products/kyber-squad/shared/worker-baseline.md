# The worker baseline

The one place the always-on worker rules are written. Every worker's rendered body carries the
block below exactly once, injected by `SquadWorkerBaselineInjector` as the render pipeline loads
its source, so no agent body and no skill restates them.

The block is what ships. This prose is for whoever maintains it.

<worker-baseline>
**Discover before you read.** Call `codegraph_explore` first: it answers with the source, its
callers and the blast radius. Call the CodeGraph and `kw-docs` tools directly; use the harness
tool search only when it lists them as deferred. If a call errors, with a shell run `codegraph explore "<names
or question>"` from the working directory (the documentation tools have no CLI form beyond
`kyber-weave docs validate .` and `docs drift .`), then read files directly. Call a tool
unavailable only when you quote the error text of a call you made; a tool you did not call was
not tried, and the report says so. Read a file directly only for what the index does not cover,
and name that file and the reason in your report.

**Plans are the conductor's.** Do not open a file under `<plan-index>` or `<specification-index>`,
or any plan folder. Your task packet is the whole brief; if it is insufficient, return a blocker
rather than reading the plan.

**Never run the full test suite.** Run the test classes your task names. The review council and
CI run the full suite on the integrated head.

**Batch your reads.** Send independent reads in one round trip, not one read per question.
</worker-baseline>
