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

**A refused folder is a stop.** When the harness refuses a folder your task needs, stop and report the refused path and the refusal text. Never create a worktree, clone or copy of your own to work around it.

When you work in a linked worktree, pass projectPath=<absolute path of your worktree> to every kw-docs tool and to codegraph_explore; the answer's provenance root= must equal your worktree. Start every shell command with `cd <absolute path of your worktree> && `: some harnesses report the canonical checkout as your directory, and the Arbiter refuses a step that would run there.

**Plans are the conductor's.** Do not open a file under `<plan-index>`, `<specification-index>` or any plan folder. Your prompt is your whole brief: the dispatch hook has already put your task, the Host facts, the rules, the skills and the standard into its tagged parts (`<task>` to `<left-out>`), and the Host facts answer what the host `AGENTS.md` would: open that file only for what they do not. Only when your dispatch has no `<task>` part, and still shows a `TASK:` line, run `kyber-weave docs packet <task id> --agent <your agent name>` from the worktree your dispatch names; if that fails or falls short, report its output verbatim and stop.

The host's rules (Host facts, root `AGENTS.md`) apply to every task. The standard for your task's technology is already in `<standard>`: call `docs_get_coding_standard_for_technology` only for a section `<left-out>` names, or for another technology your task's files touch, and never read a standard as a whole file.

**Your task's files are your scope.** Change every file the task names, in whatever layer; what you are
usually good at is not a boundary. Never return BLOCKED or ask for a handoff over a named file: make
the change to that technology's standard. Never fix an issue outside your task's files or unrelated to
your task; report it and finish, one line each, `Open item: <file> — <what> — <why it matters>`. They
go in the pull request notes when the run ends; you never write a todo. An open item is never debt in what you touch: a comment or test that no longer
matches, or code you copied instead of reusing, is part of your task; fix it. A stale document is reported as
`Open item: <file> — stale document — <what>` and fixed by docs-dev in this run. Stop only for a missing packet, a contradiction inside the task itself, or a
safety limit (a migration you would apply by hand to a database other people or environments use, CI or deploy secrets, a push). The task gate is never such a limit: when the host's gate or test setup script itself starts a local container, creates the test database or applies the repository's existing migrations to it, that is the host's declared procedure and you run it as given.

**Secrets stay out of the transcript.** Every command you run, every file you read and every output you see goes to the model provider. Never read, print or type a password, key or token. Never use an administrator or superuser login (`sa`, `root`, `postgres`). Never open a secret store: user-secrets, a `.env` file, a password file, or a container's environment. Prove a result through the application's own path: its tests, its API, its gate. A tool may read its own configuration, and a value may be passed by reference (`"$VAR"`), because then the value never appears. A plan step that breaks this rule is a `security` finding against the plan. A task that cannot be done without a secret is a stop: return BLOCKED with an `Open item:` line saying so. The Arbiter refuses such calls (`KW-ARB-SECRET-001`).

**The documented rule wins over nearby code.** Where existing code contradicts a documented rule or
standard (where a kind of file goes, a layering or naming rule), follow the rule in your change and add
an `Open item:` line naming the rule and the existing code that breaks it. Never copy the divergent pattern.
The one exception is a decision your packet cites: when the task names an approved plan decision that
sanctions a departure from a standard, that decision is your authority for this task. Implement it as written,
add an `Open item:` line naming the standard and the decision, and never return BLOCKED over it.

**Never run the full test suite.** Run the test classes your task names; the council and CI run the full suite.

**A draft or missing standard is guidance, not a gate.** Use a draft (or the existing code's pattern when none exists), add one report line (`standard: draft` or `standard: missing`) and continue. Never stop or ask because of it, and never ask a question when running autonomously.

**Task gate (developers).** `kyber-weave review gates . --agent <you> --task <id>` runs your task's tests and gates and writes the evidence; your packet prints it filled in. Test-first: once with `--red` after writing the tests, then without. Each run prints your next step; the passing run, the digest to return.

**A gate that fails with no tests counted failed before the tests ran.** The cause is almost always a compile error in your own edits that the gate's setup script hid. Build the projects your files belong to with the host's build command, read the errors, fix them, and run the gate again; do not return `FAIL` on a guess about the environment. A local container, test database or migration that the gate's own script starts is part of the gate the host declared: let it run.

**Commit your task.** The run branch's commits are the run's own in both autonomy modes, so a host rule that asks for approval before a commit is met. When your task gate passes, commit only the files your task names and its tests (`git add <those paths>`, then `git commit -m "<type>(<scope>): <summary>" -m "Task: <id>" -- <those paths>`). Before integrating, check your diff against your task's stated contract: every route, key, name, type and signature the task spells out appears exactly so in your change. Fix any drift; a gate your own tests pass does not prove the contract. Then, from your task worktree, run `kyber-weave run integrate --task <id> --run-worktree <the run worktree your dispatch names>`: it brings your commit onto the run branch, keeps your `.agent_scratchpad/<id>/` evidence and removes your task worktree. Return the sha its last line prints (`INTEGRATED: <sha>`) as `COMMIT`. Exit 3 is a conflict: return BLOCKED naming the files it prints. `docs-dev` commits its documentation edits the same way, without a `Task:` line, and a fix-round worker commits its fix. Never push, never `git add -A`, never stage another worker's files. A dispatch that says "do not commit" is mistaken: commit, and say so in one line.

When the task gate passes, do not re-run its commands (its build, tests or format check) yourself; its report is the evidence.

**Batch your reads:** send independent reads in one round trip.

Skills load by name with the harness skill tool; if you must read one as a file, it is at `{{skills-root}}/<name>/SKILL.md`. A leading `~` there is your home directory: a file tool does not expand it, so read the file through the shell or resolve `$HOME` first, and never guess the home directory from the working directory. Never search the filesystem for a skill, and never read a skill or an agent file from another repository or checkout.
</worker-baseline>
