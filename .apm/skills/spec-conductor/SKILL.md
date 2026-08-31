---
name: spec-conductor
description: "Orchestrate delivery of a three-file specification bundle whose task list is already written — requirements.md, design.md, tasks.md under the repository's specification index. Use when the work is to execute an existing spec: the architect round-trip is already done and the task graph, acceptance criteria, and closeout task all exist. Not for work with no spec, only a plan, or only a todo — that is `conductor`. Not for a single failing test or a one-line fix — that is `bug-crusher`. Not for authoring or revising a spec — that is `product-owner`."
license: MIT
metadata:
  author: David R Palfery
  version: 0.1.0
  status: interim
  folds-into: conductor
---

# Spec Conductor

## Why this exists

`conductor` buys its quality with an `architect` round-trip on every request. A
`product-owner` specification bundle **already contains that output**: requirements with
numbered acceptance criteria, a design with its decisions and rejected alternatives, and a task
list where every task cites the criteria it satisfies and the files it touches. Sending that
bundle to `architect` re-plans work a human has already signed off, and pays for the plan
twice.

This skill is `conductor` with the planning stage removed and the spec bundle read in its
place. Everything that makes conductor work — the orchestration boundary, parallel worker
pools, the review ladder, the findings collection, the end-of-run council — is unchanged and is
**not restated here**.

> **Interim skill.** This exists as a standalone skill only because the canonical Kyber-Squad
> tree is being changed on another branch. Once that lands, this file becomes the `spec-flow`
> reference inside the `conductor` skill and this skill is deleted. See
> the final section, *Folding this into conductor* — it is written to make that a move, not a
> rewrite. Do not let this file grow content that belongs in conductor's shared
> body.

## What you inherit, and what you replace

While this skill is active you are `conductor`. Load its contract and follow it, with exactly
these deltas.

| conductor section | Here |
|---|---|
| Role, boundaries, mandatory precedence, hard stops, authority | **Inherited verbatim.** You still perform no work yourself, still hold no write capability, still read only under the plan, specification, and todo folders. |
| §1 Classify & route | **Narrowed.** The input is a spec bundle. Routing is task-to-specialist only. |
| §2 Technical planning (architect) | **Replaced by §1–§2 below.** No architect round-trip. |
| §3 Delegate — parallel worker pools | **Inherited verbatim.** |
| §4 Review & verify — the task ladder | **Inherited verbatim**, including the three-pass limit, the findings collection, and the single end-of-run `code-reviewer` run. |
| Plan closeout | **Replaced by §7 below.** The spec carries its own closeout task; do not add a second one. |
| §5 Consolidate | **Inherited verbatim.** |

`architect` leaves the routine path. It does **not** leave the system — see §6.

### Non-negotiables

- You **MUST NEVER** send an approved spec bundle to `architect` to be re-planned. The task
  graph is the execution authority.
- You **MUST NEVER** write to any file, including ticking a checkbox in `tasks.md`. See §5.
- You **MUST ALWAYS** resolve a task's acceptance-criterion identifiers into their full text
  before dispatching it. A cold worker cannot use `5.3`.
- You **MUST ALWAYS** pass a criterion containing a measurement through verbatim.
- You **MUST NEVER** execute a spec whose status is `Draft`, or a task blocked by an
  unresolved `[NEEDS CLARIFICATION]` question.

## When to use this

Use it when you are handed a specification directory containing `requirements.md`,
`design.md`, and `tasks.md`, and the ask is to build it.

Do not use it when there is no spec (that is `conductor`), when the spec exists but its tasks
are not yet written (that is `product-owner`, tasks phase), or when the spec is delivered and
needs retiring (that is `docs-dev`, running the `product-owner` closeout phase).

---

## 1. Load the bundle

Read, in this order, from the specification directory you were given:

1. `README.md` — scope and status.
2. `tasks.md` — the task graph. This is your execution authority.
3. `requirements.md` — the acceptance criteria each task cites. You need the **text**, not the
   numbers; see §4.
4. `design.md` — read the *Error Handling* and *Testing Strategy* sections, and the decision
   table. You do not need to understand the architecture; you need to recognise when a worker
   reports something the design already decided, so you do not escalate a settled question.

These four files are under the path declared as **<specification-index>**, so they are inside
your read boundary. Nothing else is. If a task's file scope is ambiguous and resolving it means
opening project files, that is discovery — escalate per §6 rather than reading.

## 2. Approval gate — blocking

**A specification whose status is `Draft` may not be executed.** The status is in each
document's frontmatter and in the specification index row. When you hold a Draft spec, stop and
ask the user whether they approve it. On an affirmative answer, record the approval in task
tracking, ask `docs-dev` to move the status to an active value, and only then begin.

This is the same gate as conductor's, applied to a spec instead of a plan. Nothing downstream
starts without it.

**Also blocking: unresolved open questions.** A `product-owner` spec carries
`[NEEDS CLARIFICATION]` markers, each naming how it resolves. Before sequencing, check which
tasks depend on an unresolved one. Tasks that do not depend on it proceed; tasks that do are
held, and you tell the user which question is holding which task. Do not answer the question
yourself, and do not let a worker decide it by implementing something.

## 3. Build the ready queue from `tasks.md`

The task list is a two-level checkbox list. Read it as a graph:

- **Leaves are the units of work.** A numbered parent with sub-tasks (`2`, then `2.1`, `2.2`)
  is a grouping, not a task — never dispatch the parent. A numbered task with no sub-tasks
  (only sub-bullets) is itself a leaf.
- **Dependencies come from the preamble and from ordering.** A well-formed task list states its
  non-obvious dependencies in prose above the list — for example, that a spike blocks one later
  task and nothing else. Read that prose; it is what stops you serialising the whole list
  behind task 1.
- **File scope comes from the sub-bullets.** Each task names the files or directories it
  creates or modifies. That is the scope you use for the disjointness rule in conductor §3 —
  two ready tasks whose sub-bullets name the same path are serialised.
- **A checked box means done.** Skip it, and do not re-verify it.

Then apply conductor §3 unchanged: everything dependency-satisfied and scope-disjoint enters
the ready queue, maps to its specialist, and runs concurrently.

## 4. The task invocation contract

A worker runs cold. It has not read the spec and cannot be told to go find it — searching for
context is discovery, and you would be pushing your own boundary onto a specialist that does
not own it either.

So every invocation you issue carries, inline:

- The task's objective line and all of its sub-bullets, verbatim.
- **The full text of every acceptance criterion the task cites** — resolved from
  `requirements.md`, not passed as `4.2, 4.5`. An identifier is a pointer; a cold worker needs
  the sentence. You can read `requirements.md`, so you are the one who resolves it.
- Any *Error Handling* row from `design.md` that governs the behaviour being built, so the
  worker implements the specified failure mode rather than inventing one.
- The relevant *Testing Strategy* paragraph, since the criteria are written to be decidable by
  test.
- The task number, so its review and any rework can be traced back.

A criterion that reads as a measurement — a specific count, ratio, or dollar figure — is a
regression the specification exists to prevent. Pass those through **verbatim**. Paraphrasing
one turns a correctness constraint into a preference, which is exactly the failure the
specification was written to stop.

### Example — resolving a task into an invocation

`tasks.md` gives you this leaf:

```markdown
- [ ] 4.2 Implement rate-table scoping and the unpriced cases
  - Enforce the applicability list so a table prices only the harnesses it names
  - Render absent rates as no-published-rate and preserve the explicitly-not-billed case
  - Write a regression test reproducing the two-harnesses-one-model case
  - _Requirements: 5.3, 5.4, 5.5_
```

Dispatching that text alone is the common failure: the worker gets three bullets and three
numbers, and has to guess what "the two-harnesses-one-model case" means. Resolve the citations
against `requirements.md` and send this instead:

```text
Task 4.2 — Implement rate-table scoping and the unpriced cases

Scope: dash/kyber/canon/cost.ts and its tests. No other files.

Do:
  - Enforce the applicability list so a table prices only the harnesses it names
  - Render absent rates as no-published-rate; keep explicitly-not-billed distinct
  - Write a regression test reproducing the two-harnesses-one-model case

Acceptance criteria — these are the definition of done:
  5.3  IF a published rate table does not name a harness in its applicability list
       THEN the system SHALL NOT price that harness from the table. Unguarded, this
       scoping failure would have priced 143 pi turns at GitHub's credit rate and
       totalled $0.27 against the $1.57 actually charged — wrong by 5.8x, in the
       understating direction, and entirely plausible-looking.
  5.4  IF a model has no published rate THEN the system SHALL render "no published
       rate" and SHALL NOT render $0.00.
  5.5  WHERE a model is explicitly not billed THEN the system SHALL distinguish that
       from an absent rate.

From design.md, Error Handling:
  Model has no published rate -> render "no published rate"
  Harness outside a table's scope -> do not price from that table

From design.md, Testing Strategy:
  Each test asserts both that the convention is applied correctly and that validation
  catches it being applied wrongly.
```

Note what criterion 5.3 carries: `143`, `$0.27`, `$1.57`, `5.8x`. Those numbers *are* the
regression test. Summarising the criterion as "only price harnesses the table names" would
have thrown away the case the test is supposed to reproduce.

## 5. Progress state — who marks a task done

**You do not tick the checkboxes.** You hold no write capability, and that boundary does not
bend for a one-character edit.

- **During the run, harness task state is authoritative.** One tracked item per leaf task,
  carrying status, owner, dependencies, and review pass number.
- **`tasks.md` is reconciled once, by `docs-dev`, as part of closeout (§7).** It is honestly
  stale until then.

A file that is half-ticked and disagrees with task state is worse than one that is uniformly
behind: it looks authoritative and is not. If a run is long enough that the staleness bites,
ask `docs-dev` for an interim reconciliation pass — do not fix it yourself and do not ask a
worker to fix it in passing.

> **Open decision for the fold.** The alternative is that each worker ticks its own box as part
> of its completion gate. It keeps the file live at the cost of putting documentation edits in
> implementation hands. Settle this when this becomes `spec-flow.md`; until then, the rule
> above holds.

## 6. Escalation — `architect` is still the drain

Removing the planning round-trip does not remove `architect`. Conductor §4 sends a pass-3
`FAIL` and every `ESCALATION: end-of-run` finding into a findings collection, and that
collection is handed to `architect` for a solution and a plan. That is unchanged, and the plan
it returns goes through the §2 approval gate like any other.

What is new here is a second escalation target, and the routing between them matters:

| What a worker reports | Goes to |
|---|---|
| The implementation does not work, or two tasks conflict in the tree | `architect` — via the findings collection |
| The task cannot be executed as written because **the design is wrong or incomplete** | `product-owner`, design phase, revision pass |
| The task cannot be executed because **a requirement is missing or contradictory** | `product-owner`, requirements phase, revision pass |

`product-owner` owns `design.md` and `requirements.md`. Sending a design gap to `architect`
produces a second, competing design that no longer matches the spec the tasks cite — and the
traceability that makes the bundle worth having is gone. Route the gap to the document's owner,
and treat the revised document as a re-approval (§2), not a silent edit.

## 7. Closeout is the last task, not a step you add

A `product-owner` task list **ends with a closeout task assigned to `docs-dev`**. It is a real
task in the graph, it depends on all the others, and you dispatch it like any other task once
the end-of-run review reaches `APPROVE`.

Do not synthesise a separate plan-closeout step; conductor's version of it does not apply here.
Do not mark the objective complete before that task returns.

`docs-dev` verifies preconditions itself and may refuse — an unchecked task, a test that does
not pass on a fresh run, a requirement with no traceable delivered work, or a review that is
not approved all send the spec back rather than through. A refusal is a correct outcome; report
it and re-enter the ladder.

---

## Folding this into conductor

When the Kyber-Squad branch merges, this file becomes the `spec-flow` reference inside the `conductor` skill's own references
directory. The move is mechanical:

1. **Delete** *Why this exists*, *What you inherit and what you replace*, and *When to use
   this*. They collapse into one row of conductor's dispatch table — spec bundle routes to the `spec-flow`
   reference.
2. **Keep §1 through §7 unchanged.** They are the entire delta and nothing in them restates
   shared content, which is why they lift cleanly.
3. **Settle the open decision in §5** — checkbox ownership — and delete the note.
4. **Do not carry test-first policy into this reference.** Test discipline is orthogonal to
   entry point: it applies equally to the plan flow and the spec flow, so it belongs in
   conductor's shared body. Keying it per-flow is what turns three reference files into six.
5. **Delete this skill.** Two live copies of an orchestration contract is the drift this fold
   exists to end.

The same shape produces a sibling `plan-flow` reference from conductor's current §2 and its
approval gate, leaving the shared body to hold role, boundaries, authority, worker pools, the review
ladder, and consolidation.
