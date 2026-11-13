# The worker baseline

The one place the always-on worker rules are written. Every worker's rendered body carries the
block below exactly once, injected by `SquadWorkerBaselineInjector` as the render pipeline loads
its source, so no agent body and no skill restates them.

The block is what ships. This prose is for whoever maintains it.

<worker-baseline>
**Discover before you read.** Call `codegraph_explore` first, and the `kw-docs` tools after it,
directly; use the harness tool search only when it lists them as deferred. If a call errors, run
`codegraph explore "<names or question>"` in the shell, then read files directly, naming each file
and why. Call a tool unavailable only with the quoted error of a call you made; otherwise report it
as not tried.

When you work in a linked worktree, pass projectPath=<absolute path of your worktree> to every kw-docs tool and to codegraph_explore; the answer's provenance root= must equal your worktree.

**Plans are the conductor's.** Do not open a file under `<plan-index>` or `<specification-index>`,
or any plan folder. Your task packet is the whole brief; if it is insufficient, return a blocker
rather than reading the plan.

**Never run the full test suite.** Run the test classes your task names. The review council and
CI run the full suite on the integrated head.

**A draft or missing standard is guidance, not a gate.** Follow the existing code's pattern, note
`standard: draft` (or `standard: missing`) in your report, and continue. Never stop or ask because
of it, and never ask a question when running autonomously.

**Batch your reads.** Send independent reads in one round trip, not one read per question.
</worker-baseline>
