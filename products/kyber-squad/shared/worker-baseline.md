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
as not tried. Name specific symbols or files in each `codegraph_explore` query rather than broad topics.
If the result says "Output too large … Saved to: <path>", `view` that file once and use it as your
answer: do not re-run the query and do not fall back to grep.

When you work in a linked worktree, pass projectPath=<absolute path of your worktree> to every kw-docs tool and to codegraph_explore; the answer's provenance root= must equal your worktree.

**Plans are the conductor's.** Do not open a file under `<plan-index>`, `<specification-index>` or any plan folder. Your first action is `kyber-weave docs packet <task id> --agent <your agent name>`, run from the worktree your dispatch names (a dispatch with no task id has no packet); its output is your whole brief, and its Host facts section answers what the host `AGENTS.md` would: open that file only for what it does not. If it fails or falls short, report that output verbatim and stop.

**Never run the full test suite.** Run the test classes your task names. The review council and
CI run the full suite on the integrated head.

**A draft or missing standard is guidance, not a gate.** Follow the existing code's pattern, note
`standard: draft` (or `standard: missing`) in your report, and continue. Never stop or ask because
of it, and never ask a question when running autonomously.

**Task gate (developers).** Test-first, after writing the tests: `kyber-weave review gates . --agent <you> --paths <file> --task <id> --red --out .agent_scratchpad/<id>/task-gate-red.json`. When done: the same without `--red`, to `task-gate.json`. Each run prints your next step; the passing run prints the digest to return.

**Batch your reads.** Send independent reads in one round trip, not one read per question.

Skills load by name with the harness skill tool; if you must read one as a file, it is at `{{skills-root}}/<name>/SKILL.md`. Never search the filesystem for a skill, and never read a skill or an agent file from another repository or checkout.
</worker-baseline>
