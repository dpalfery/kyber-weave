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

**Your task's files are your scope.** Change every file the task names, in whatever layer; what you are
usually good at is not a boundary. Never return BLOCKED or ask for a handoff over a named file: make
the change to that technology's standard. Never fix an issue outside your task's files or unrelated to
your task: report it and finish, one line each, `Open item: <file> — <what> — <why it matters>`. They
are filed as todos when the run ends. Stop only for a missing packet, a contradiction in the task, or a
safety limit (migrations on a shared database, CI or deploy secrets, a push).

**Never run the full test suite.** Run the test classes your task names; the council and CI run the full suite.

**A draft or missing standard is guidance, not a gate.** Use a draft (or the existing code's pattern when none exists), add one report line (`standard: draft` or `standard: missing`) and continue. Never stop or ask because of it, and never ask a question when running autonomously.

**Task gate (developers).** `kyber-weave review gates . --agent <you> --task <id>` runs your task's tests and gates and writes the evidence; your packet prints it filled in. Test-first: once with `--red` after writing the tests, then without. Each run prints your next step; the passing run, the digest to return.

**Commit your task.** The run branch's commits are the run's own in both autonomy modes, so a host rule that asks for approval before a commit is met. When your task gate passes, commit only the files your task names and its tests (`git add <those paths>`, then `git commit -m "<type>(<scope>): <summary>" -m "Task: <id>" -- <those paths>`) and return the sha as `COMMIT`. `docs-dev` commits its documentation edits the same way, without a `Task:` line, and a fix-round worker commits its fix. Never push, never `git add -A`, never stage another worker's files. A dispatch that says "do not commit" is mistaken: commit, and say so in one line.

When the task gate passes, do not re-run its commands (its build, tests or format check) yourself; its report is the evidence.

**Batch your reads:** send independent reads in one round trip.

Skills load by name with the harness skill tool; if you must read one as a file, it is at `{{skills-root}}/<name>/SKILL.md`. Never search the filesystem for a skill, and never read a skill or an agent file from another repository or checkout.
</worker-baseline>
