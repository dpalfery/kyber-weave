# Plan Authoring

The plan file is durable state. A live agent instance is disposable.

## Start or resume

1. Resolve the plans directory and **<plan-index>** through Config Reg, then read the index.
2. Use a prompt-supplied `PLAN_FILE` only when its canonical path remains beneath the plans directory. If none is supplied, create a date-prefixed Draft plan and its index row before broad discovery. Change an existing file with an in-place edit, never a whole-file rewrite, whenever the harness gives you an edit tool. On harnesses where file editing requires an existing destination file (such as Devin), initialize the file via shell (for example, `touch <file>`) before editing. A note or excuse for why the process was not followed is not a resolution. The process exists to ensure quality; going around it signals bad quality. Do not put such a note in **Active Plans**. A pull request that does will not be approved.
3. Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree.
4. Reconcile conductor-supplied answers against the decision ledger before more discovery.
5. Save after every discovery batch and before every status handoff.

Open only active Draft, Ready, In progress, or Blocked plans. Draft supports planning only. Review required, Completed, Superseded, and archived artifacts are not implementation authority.

## Required plan contract

Every plan contains:

- governed frontmatter including `development-mode: test-first | standard`, `documentation-impact: new-component | extends-component | no-design-change`, and `impact-components` (catalog component names; required and non-empty for `new-component` and `extends-component`);
- problem and goal;
- a permanent Decisions section recording all material choices, alternatives, questions, and approvals;
- the declared scale (`documentation-impact` plus `impact-components`) and a `Design harvest` table keyed by Target component and Target doc type (one row per durable design, contract, interface, behaviour, or operations section; a `no-design-change` scale carries `Documentation waiver: <reason>` instead of rows);
- investigation findings;
- the mode-specific Test contract or verification contract;
- dispatchable tasks with objective, acceptance criteria, dependencies, required skills, and its `session` group, naming every file it touches by path plus symbol, the full test class names its runner must match, and exact commands (the rules are below);
- a dependency graph;
- risks, out-of-scope boundaries, verification gates, review, and `docs-dev` closeout.

Name required skills, not owning agents. The conductor maps skills to the live specialist inventory. Declare a dependency only when a task consumes another task's concrete output or their file scopes cannot safely overlap. Component labels and table order do not create scheduling barriers. Maximize parallel execution and err on the side of risk: prefer optimistic file scopes and add a dependency only for a real consumed output or a certain file overlap. Size each task for efficient execution by a single worker; do not split work further just to add parallelism. Four small file edits belong to one worker; changes of substantial size across independent projects belong to separate workers. DAL, React, C#, and DevOps tasks can run at the same time. When C# changes span multiple projects, split them across `csharp-dev` workers only if each project's change is large enough and decoupled enough to justify it. Respect `review.max-concurrent-workers` (default 4, host-configurable in `.kyber-weave/kyber-weave.yml`); the harness runs ready tasks up to that cap. (Q4 reversal, issue #386: this supersedes the 2026-10-14 "no max concurrency" directive.) Each developer task ends with the worker's own `task-gate` run, and a failing gate goes straight back to the same worker; no agent reviews a single task.

A plan never carries the review command. The end-of-run council derives it from the `code-review` skill — measured against the change with `--base`, and admitted to the full-suite gates with `--council` — so a command copied into a plan, an index row or a task packet goes stale the moment the gate list or one of its flags moves, and the reviewer then runs something nobody validated. Say who reviews and which skill governs it; leave the argv to the reviewer.

## Task granularity

A task row is also the source of one worker's prompt, and that worker reads nothing else. So the
plan carries what the prompt needs:

- **A path plus a symbol** for every file a task names: the file's repository-relative path together
  with the type, method or setting inside it. A line number is a hint at most, because it goes stale
  the moment an earlier task edits the same file, and a worker that trusts it edits the wrong place.
- **Full test class names**, fully qualified, because a runner filter that matches no class runs
  zero tests and exits success.
- **The exact commands** the task runs, complete enough to paste into a shell.

## Session groups

Every task declares a `session` group, and the group is the unit of worker continuity: tasks in one
group run in one worker session, so the worker that made a change makes the follow-on fix to it
rather than paying again to rebuild context it already had.

The boundary is exact:

- A session group may continue follow-on fixes and later same-role tasks on the same change.
- It may merge RED tasks when a later one does not need an earlier GREEN.
- It may never merge a RED task into its GREEN. Those stay two workers, because the implementation
  specialist's passing run only proves the contract while someone else wrote it.

## Task file

At finalization the architect saves the task file beside the plan, `<plan>.tasks.json` in the
directory named by **<plan-index>**, returns its path, and the file is committed with the plan at
once.

It carries a schema version, the plan's path, the execution digest, and one entry per task: id,
role, dependencies, file scope, `session` group and the full prompt. The execution digest covers the
parsed tasks, file scopes, dependencies, skills, test rows and mode, so flipping a task's status or
editing a decisions row leaves it valid while editing a task makes it stale.

Two slots stay unfilled — `{worktree}` and `{prior_results}` — because no plan can know the worktree
the run picks or what an earlier worker reported. Everything else in a prompt is final when the file
is written.

## Decisions

The `## Decisions` section is permanent state, preserved intact across all lifecycle transitions (Draft -> Ready -> execution -> closeout -> archive). It must NEVER be deleted upon finalization or archival.

Record all decisions in a structured table:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |

## Documentation gates after plan writes

After every plan save, obtain the two checks. In the canonical checkout call `docs_validate_governed_document_schema` and `docs_detect_drift_between_documents_and_code` on the kw-docs server and require the provenance `rev=` and `branch=` on each response to equal your `RUN_BASE` and `RUN_BRANCH`: `repo=` alone proves nothing because every worktree shares it, and the server is bound to the canonical checkout. Inside a worktree the server cannot see your plan, so run `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` there yourself and report `DOCS_VALIDATE: pass (cli, <path>)`. Leave `mergeReady` false while the plan is in the active plans folder. If you hold a shell and the MCP result is missing or does not match (canonical checkout included), run the two CLI commands in the checkout that holds your plan. If you hold no shell and the MCP result does not match, accept a conductor-supplied `DOCS_CHECKS: <results>`; with none, return `STATUS: BLOCKED` and name the gap. A shell is not permission to implement.

Persist each question with a stable id (for example, D1, D2 or Q1, Q2), the question or architectural choice, the selected answer or decision, mitigating and supporting context (alternatives considered, trade-offs, constraints), and status/approval provenance.

While in Draft, unapproved questions have status `OPEN` or `RECOMMENDED`. Return every currently independent question, up to four, in one handoff:

```text
STATUS: NEEDS_DECISION
PLAN_FILE: <saved path>
QUESTION: [Qn] <decision>
OPTIONS: <a> / <b> / ...
RECOMMENDED: <option> — <reason>
```

Under `autonomy: autonomous`, decide per the autonomous-decisions reference and return only hard-block escalations. An autonomous hard-block escalation uses this NEEDS_DECISION shape PLUS `ESCALATION: hard_block`, `SOURCES_SEARCHED`, `COST_OF_WRONG` (and `RECOMMENDED`); see [autonomy-decisions.md](autonomy-decisions.md) for the exact block.

Do not place an unapproved recommendation in the approved answer column until approved. When answered or approved, update the row in place: record the chosen answer, document any mitigating considerations or constraints, and record the explicit approval provenance (for example, `Approved by Hal <date>` or `User decision <date>`).

If no interactive questions were needed during planning, the plan must still carry the permanent `## Decisions` table documenting the baseline choices, assumptions, and constraints that govern the implementation, and `PLAN_READY` includes `NO_QUESTIONS` with the concrete reason the request was already decision-complete. `NO_QUESTIONS` does not bypass authoring the permanent `## Decisions` table.

## Ready and finalization

Return `PLAN_READY` only when the saved Draft has no open decisions (all rows in `## Decisions` have status `ANSWERED` or approved), contains the selected mode and matching approved contract, declares its scale (`documentation-impact` with `impact-components`) and its `Design harvest` table, has a schedulable dependency graph, and both documentation checks pass:

```text
STATUS: PLAN_READY
PLAN_FILE: <saved path>
OPEN_DECISIONS: none
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

The conductor presents **approve and execute**, stating the declared scale and components so the owner confirms scale before execution. Proceed at high confidence only when the scale and harvest targets are unambiguous. On a `FINALIZE` invocation carrying explicit approval, record that approval in the decision rows, preserve the permanent `## Decisions` section intact (do NOT delete it), set the plan and index to Ready, save the task file beside the plan and return its path, rerun both documentation checks, and return:

```text
STATUS: PLAN_FINALIZED
PLAN_FILE: <saved path>
TASK_FILE: <saved tasks.json path>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

A failed write or check returns `STATUS: PLAN_WRITE_ERROR` with the intended path and exact error. Never report a later lifecycle from unsaved state.

## Planning inside a worktree

The docs server is bound to the canonical checkout, so it cannot see a plan written into another worktree: its answers would validate the wrong corpus and report a pass for a file it never read. When your packet names `WORKTREE: <absolute path>`, write the plan under the directory named by **<plan-index>** inside that worktree and run `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` there in your own shell; report `DOCS_VALIDATE: pass (cli, <path>)`. Only when your harness gives you no shell, return `STATUS: BLOCKED` naming `server root differs: run the docs checks in the worktree`, so the conductor runs them and re-invokes you with `DOCS_CHECKS: <results>`. With no `WORKTREE` in the packet and a `repo=` that differs, the repository is not yours to plan: return `STATUS: BLOCKED`.
