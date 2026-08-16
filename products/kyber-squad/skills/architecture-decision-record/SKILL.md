---
name: architecture-decision-record
description: Capture an architectural decision as an ADR once it has been reached — a choice that constrains future work, had viable alternatives that were rejected, and would be expensive to revisit. Use when a design discussion has converged on such a decision, when the user asks for an ADR, or when a decision needs superseding. Do NOT use for reversible implementation choices, library picks with no lasting constraint, or to minute a discussion that has not concluded.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Architecture Decision Record

An ADR records **why** the system is shaped the way it is, for a reader who was not in the room and cannot ask.

## ALWAYS apply the three tests before writing

Write an ADR only when a decision has been **reached** and meets all three:

1. **It constrains future work.** Later changes must live with it.
2. **Viable alternatives were rejected**, and the rejection is not self-evident.
3. **It would be expensive to revisit** — reversing it means reworking code, data, or infrastructure.

A decision that fails any test is a normal implementation choice. It belongs in the code and the component's documentation, not here.

**NEVER open an ADR because a discussion started.** Agents and users weigh trade-offs constantly — which library, how to shape a service, whether to add a cache. Minuting all of that buries the handful of records that matter, and this repository's retrieval already ranks documents by standing. The trigger is *convergence*, not debate.

Worked distinctions:

| Situation | ADR? | Why |
|---|---|---|
| "Should this service use a queue or direct calls?" — still being argued | **No** | No decision yet. Wait for convergence. |
| Decided: schema deploys as one idempotent SQL file, no migration framework | **Yes** | Constrains every future schema change; alternatives existed; reversing means retrofitting migrations. |
| Chose `System.Text.Json` over `Newtonsoft.Json` in one service | **No** | Reversible in an afternoon, constrains nothing else. |
| Decided: the browser never holds an access token; the BFF owns the session | **Yes** | A security boundary the whole front end is built around. |
| Named a class `OrderProcessor` rather than `OrderService` | **No** | Not a decision, a preference. |
| Decided: agent roles stay synchronised across all six harnesses | **Yes** | Every future agent change inherits the obligation. |

## NEVER write one without approval

The ADR is a governed document and creating documentation requires user approval. Present the decision, the alternatives, and the consequences **in the conversation first**, and ask. Write the file only after an explicit yes.

If the user declines, do not write it and do not raise it again in the same session.

## Placement and identity

Active ADRs live in the path declared as **Architecture Decision Records** in the repository root `AGENTS.md` Config Registry; superseded ones move to the archive path recorded there. Read the repository documentation standard for the lifecycle, and the documentation ontology for the frontmatter schema, before writing.

- Filename: `ADR-{YYYY-MM-DD}-{kebab-slug}.md`
- Frontmatter `id`: `adr/{YYYY-MM-DD}-{kebab-slug}` — the date is the decision date, and it never changes afterwards.
- `doc-type: adr`, `status: current`.
- `supersedes`: the `id` of any ADR this one replaces. Otherwise `[]`.
- `code-refs` / `api-endpoints`: symbols and routes the decision governs, when the decision is about specific code. These are validated against the code graph, so every entry must resolve.

## Structure

```markdown
---
id: adr/{date}-{slug}
title: "ADR-{date}: {Short Decision Title}"
doc-type: adr
status: current
owner: {team from the catalog}
last-reviewed: {date}
code-refs: []
api-endpoints: []
decided-by: []
supersedes: []
---
# ADR-{date}: {Short Decision Title}

**Status:** Accepted
**Date:** {date}

## Context

The forces in play: the constraint, the problem, what was true at the time. Enough that a
reader who was not present can judge whether the reasoning still holds. State facts, not
narrative — no meeting history, no attributions.

## Decision

What was decided, in the active voice and one paragraph. "Schema is deployed via a single
idempotent SQL file applied by sqlcmd." Not "we discussed and felt that…".

## Alternatives considered

Each viable option that was rejected, and why. An ADR with no rejected alternatives is
either not a decision or is hiding the interesting part.

## Consequences

What this commits the system to — including the costs. An ADR that lists only benefits is
advocacy, not a record. State what becomes harder, what is now manual, what a future
change will have to work around.
```

## Superseding

**NEVER edit the decision out of an accepted ADR.** A reversal is itself a decision, and the record of what was believed before is what makes the reversal legible.

To supersede: write a new ADR naming the old one's `id` in `supersedes`, set the old one's `status` to `superseded`, add a line under its title pointing to the replacement, and move it to the archive path. Documents that referenced the old decision through `decided-by` are updated to point at the new one.

## Linking the decision to what it governs

A decision no one can find does not constrain anything. Add the ADR's `id` to the `decided-by` frontmatter of every document the decision governs — that key is what carries the decision through the documentation graph to the components it binds.

## Verify before finishing

Run the repository's documentation validation and drift checks. `code-refs` and `api-endpoints` resolve against the code graph, and a `decided-by` or `supersedes` pointing at a non-existent id fails validation.
