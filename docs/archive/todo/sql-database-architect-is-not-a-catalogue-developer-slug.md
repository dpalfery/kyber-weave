---
id: todo/sql-database-architect-is-not-a-catalogue-developer-slug
title: sql-database-architect is not a catalogue developer slug
doc-type: todo
status: superseded
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-17
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# sql-database-architect is not a catalogue developer slug

> [!NOTE]
> **Superseded 2026-11-17.** The removal deleted `sql-database-architect`, so the vocabulary question
> this todo raised no longer has a subject: no catalogue entry can name it, and no host holds an
> agent-keyed entry for it except the ones `kyber-weave review migrate-task-gates` rewrites. The
> record below is kept for provenance. The remaining question — pinning the catalogue's developer-slug
> vocabulary — is answered by `GateCatalogueRenderTests`, which fails on an agent key outside the
> published set.

`GateCatalogue.AgentOrder` lists thirteen developer slugs — `csharp-dev`, `dal-dev`, `maui-dev`,
`pulumi-dev`, `github-devops`, `typescript-dev`, `react-dev`, `rust-dev`, `tauri-dev`, `go-dev`,
`python-dev`, `test-dev`, `docs-dev` — and `ParseGate` rejects any catalogue task gate naming an agent
outside it. `sql-database-architect` is a real agent with a real `review.task-gates` entry in this
repository, and it is not on that list.

Found while wiring the suppressions gate: naming `sql-database-architect` among a catalogue gate's
`agents` fails every render with "is not a known developer agent slug". The workaround shipped is to
leave it off the catalogue entry and keep it in this repository's own config, which is why this
repository runs the gate for that agent and a host scaffolding from the catalogue does not.

## What closing this needs

- Decide whether the vocabulary is genuinely closed (a fixed set of developer roles a catalogue may
  target) or was simply never updated when `sql-database-architect` was added. The answer changes
  whether the fix is a list entry or a documented boundary.
- If it should be listed, add it to `AgentOrder` with its technology, then add it to the catalogue
  gates that apply to it — the suppressions gate at minimum — and regenerate the csharp golden
  fixture.
- Pin the vocabulary itself: a test that every agent named in this repository's `review.task-gates`
  is either a catalogue developer slug or recorded as intentionally catalogue-exempt. Without that,
  the next agent added quietly gets no catalogue gate and nothing says so.

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| SQ1 | Is the catalogue's developer-slug vocabulary closed, or was it simply never updated? | Closed. The removal deleted `sql-database-architect`, so no catalogue entry can name it, and the question of adding it to `AgentOrder` is retired with the agent. | [The worker roster and developer tiers](../../kyber-squad/architecture.md#12-the-worker-roster-and-developer-tiers) |
| SQ2 | What happens to a host's agent-keyed entry for a removed agent? | It keeps resolving through the shipped technology map for two minor releases, and `kyber-weave review migrate-task-gates` rewrites it as `review.technology-gates`. | [Task gates](../../code-review/architecture.md#task-gates) |
| SQ3 | Is the vocabulary pinned? | Yes: `GateCatalogueRenderTests` fails on an agent key outside the published developer-slug set. | [Task gates](../../code-review/architecture.md#task-gates) |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH1 | ReviewCouncil | architecture | [Task gates](../../code-review/architecture.md#task-gates) | A catalogue task gate may name only a published developer slug, and an agent-keyed host entry resolves through the shipped map until the migration rewrites it. |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| SQ1 | [The worker roster and developer tiers](../../kyber-squad/architecture.md#12-the-worker-roster-and-developer-tiers) | ReviewCouncil | 2026-11-17 | archived |
| SQ2, SQ3, DH1 | [Task gates](../../code-review/architecture.md#task-gates) | ReviewCouncil | 2026-11-17 | archived |
