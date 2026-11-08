---
id: todo/sql-database-architect-is-not-a-catalogue-developer-slug
title: sql-database-architect is not a catalogue developer slug
doc-type: todo
status: draft
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-08
---

# sql-database-architect is not a catalogue developer slug

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
