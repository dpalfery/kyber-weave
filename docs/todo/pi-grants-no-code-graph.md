---
id: todo/pi-grants-no-code-graph
title: A Pi subagent has no code index tool
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-07
---

# A Pi subagent has no code index tool

The 2026-11-07 code-graph grant reached Command Code, OpenCode and Kilo through the shared
`GrantsOrchestratorExcludedMcpServers` predicate, and Claude, ZCode and Devin already carried it.
**Pi still renders only `kw-docs`**, so a Pi subagent answers every code question with grep — the
same degradation this grant was introduced to fix on the other three targets.

The reason it was left out is not an oversight and not a judgement about Pi. Pi's grant vehicle is
a per-server adapter overlay: `PiRenderer` emits `extensions: [pi-mcp-adapter]`, the proxy entry
`ext:pi-mcp-adapter/mcp`, then one per-tool entry per roster tool. The adapter's namespace mapping
was verified for exactly one server, the hyphenated first-party key `kw-docs`, which maps to
`mcp__kw_docs__`. `codegraph` has no hyphen, so its mapping is identity — **inferred, not probed**.
Emitting a second server's overlay on an inferred namespace is what the renderer refuses to do on
record ([ADR 0050](../adr/0050-universal-kyber-weave-mcp-grant.md) decision 7), so the grant waits.

Open when someone picks this up: probe the adapter's naming code over a second server and record
the result, decide whether the overlay gains a server-parameterised derivation or a second explicit
block, and settle what a Pi subagent does when the adapter is absent, since the per-tool names
resolve against nothing without it.

This todo is complete when a Pi subagent's rendered file grants the code graph in a spelling that
was probed rather than inferred, a contract test pins it, and the Pi row of the capability matrix
says what the grant is instead of why it is missing.
