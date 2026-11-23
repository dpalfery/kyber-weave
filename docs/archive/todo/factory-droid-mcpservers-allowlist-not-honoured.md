---
id: archive/todo/factory-droid-mcpservers-allowlist-not-honoured
title: A Factory droid's mcpServers allowlist is not honoured at runtime, so a named server reaches no tool
doc-type: todo
component: KyberSquad
status: archived
owner: dpalfery
last-reviewed: 2026-11-22
documentation-impact: extends-component
impact-components: [KyberSquad]
---

# A Factory droid's mcpServers allowlist is not honoured at runtime, so a named server reaches no tool

Delivered on 2026-11-22: `FactoryRenderer` keeps emitting the per-droid `mcpServers`
whitelist unchanged and additionally appends each granted server's roster tool ids as
`{server}___{tool}` entries to the droid's `tools:` list, after the semantic tool IDs —
that list is the form the harness honours at runtime. The entitlement rule deciding which
droids are granted at all is unchanged, and a denied droid still renders `mcpServers: []`
with no `___` entries under `tools:`.

The Factory renderer writes a per-droid `mcpServers` whitelist, and this repository's pack renders
droids that list `mcpServers: [codegraph, kw-docs]`. At runtime that list grants nothing: the droid
receives no MCP tools at all, whatever names it holds, while a droid that omits the key entirely
inherits the parent session's servers and their tools. A rendered grant therefore reads as granted
and behaves as withheld, which is the one outcome the whitelist exists to distinguish.

The difference is the key's presence, not its contents, so no value of the list produces a working
grant and no test over the rendered file can catch it: the file is correct and the harness ignores
it.

The working workaround is to list the exact registered MCP tool ids in the droid's `tools:` array
instead of naming the server under `mcpServers`. That names each tool rather than a server, so it
must be revisited whenever a server adds or renames a tool.

This affects the droids this repository's pack renders, so the renderer's contract and what the
harness actually honours have to be reconciled.

**Work.** Decide whether the renderer keeps emitting `mcpServers` and the gap is documented as a
harness limit, or whether it emits the tool ids under `tools:`. Either way, record which form the
harness honours, and keep the fail-closed outcome for a droid whose profile denies `filesystem.read`.

**Acceptance.** A rendered droid on the affected harness reaches the servers it is granted, verified
live rather than from the rendered file; or the renderer states, where an operator reads it, that
the key grants nothing on that harness and names the form that does. The rule deciding which droids
are granted at all is unchanged.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| FMCP-1 | Which form does the renderer emit so a granted droid reaches MCP tools at runtime? | Both. The renderer keeps emitting the `mcpServers` whitelist unchanged (documents intent, fail-closed `[]`) and appends one `{server}___{tool}` entry per roster tool of each granted server to `tools:`, after the semantic tool IDs, in granted-server then roster order. A granted server the roster does not name contributes no entry. | The harness honours only the `tools:` entries; the whitelist alone reaches no tool, verified at runtime. Tool names come from the toolchain roster, so a renamed tool moves the entry without a renderer edit. The entitlement rule (universal vs orchestrator-excluded, ADR 0050) is unchanged. | Implemented 2026-11-22 in `FactoryRenderer`; pinned by `FactoryRendererContractTests`. |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| FMCP-1 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (MCP grants per target: the Factory grant is the `tools:` entries, with `mcpServers` retained as declared intent); [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md#factory) (the Factory section names the runtime grant and the droid-visible servers). |
| H1 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (MCP grants per target); [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md#factory) (the Factory section); [kyber-squad/requirements.md](../../kyber-squad/requirements.md#target-capability-and-degradation-matrix) (the Factory Droids row); [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#factory-notes) (the Factory notes and grant table). |
| H2 | [kyber-squad/requirements.md](../../kyber-squad/requirements.md#target-capability-and-degradation-matrix) (the Factory Droids row states the per-class grant the new contract cases pin). |

## Design harvest

| ID | Durable item | Target component | Target doc type / location |
|---|---|---|---|
| H1 | A Factory droid's runtime MCP grant is the `{server}___{tool}` entries under `tools:`; `mcpServers` documents intent and is still always emitted, never omitted | KyberSquad | ARCHITECTURE `docs/kyber-squad/architecture.md` (MCP grants per target); REFERENCE `docs/docgraph/mcp-runbook.md` (Factory section); REFERENCE `docs/kyber-squad/requirements.md` (Factory Droids row); REFERENCE `docs/kyber-squad/onboarding.md` (Factory notes) |
| H2 | The grant contract is pinned per entitlement class: specialist, orchestrator, denied profile, empty declaration | KyberSquad | TEST `tests/KyberWeave.Application.Tests/FactoryRendererContractTests.cs` |
