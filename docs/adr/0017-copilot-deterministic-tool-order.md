---
id: adr/0017-copilot-deterministic-tool-order
title: Exact Copilot Tool Membership with One Cross-Agent Emission Order
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-12
---

# ADR 0017: Exact Copilot Tool Membership with One Cross-Agent Emission Order

## Status

Accepted, 2026-09-12. Records the Copilot rendering contract for exact per-agent tool membership with one cross-agent emission order. Does not change the semantic capability lattice used by other harnesses.

## Context

GitHub Copilot custom-agent `tools` is a closed, platform-specific allow-list. Mapping it from the shared capability vocabulary is lossy: granular edit tools (`edit/createFile`, `edit/rename`, …) and per-agent membership cannot be recovered from `filesystem.write` alone.

The canonical source pins exact membership per agent. Independently, a single global emission order maximizes shared serialized prefixes across the 24-agent roster. Per-agent ordering would churn YAML even when membership is unchanged.

## Decision

1. **Membership is lossless and Copilot-specific.** Each canonical agent declares `copilot-tools`. `CopilotRenderer` emits that set, not a capability-to-tool reconstruction. Unknown identifiers, duplicates, or an order the catalog cannot project are validation failures.
2. **Order is global and has no per-agent exceptions.** `CopilotToolCatalog.OrderedTools` is the only sequence. `Normalize` filters that sequence against the agent's membership. Frequency-descending usage counts motivated the sequence; they are not a runtime cache-hit guarantee.
3. **Capability remains an upper bound, not a membership source.** A Copilot-specific capability profile may further withhold tools that the lattice cannot express as `ask`; it must not add tools the agent did not declare and must not widen the shared cross-harness profile.
4. **Serialization stays a YAML flow sequence** with MCP wildcards single-quoted. That shape is part of the Copilot contract, not a formatter preference.

The approved order is:

```text
vscode
tool_search
read
todo
codegraph/*
kyber-weave/*
context7/*
search
execute
web
edit
agent
edit/createDirectory
edit/createFile
edit/editFiles
edit/rename
vscodeGeneral/rename
```

## Amendment 2026-10-12: `tool_search`

`tool_search` is Copilot's built-in tool-discovery tool (upstream `ToolName.ToolSearch`,
grouped under the `vscode` tool set): it surfaces definitions for tools the agent's
allow-list already grants, so an agent with an explicit `tools` list cannot discover
tools without naming it. It slots directly after `vscode` and binds to no capability
(`null`, like `vscode` and `todo`): discovery over the already-allowed set confers no new
grant, so no capability profile changes. Per the consequences above, the slot, the binding,
and the canonical membership update landed together, and every canonical
agent declares it.

Architect membership note (same date): the architect's `copilot-tools` moved from the
granular `edit/*` ids plus `web` to broad `edit` without `web`, so it now reads
`vscode, tool_search, read, todo`, the MCP wildcards, `search, execute, edit, agent`.
The catalog keeps the granular ids (history still names them); the architect was their
only canonical member. The rejected alternative below still describes the adoption-time
reasoning and is left intact.

## Alternatives Considered

- **Capability-to-tool map as the allow-list.** Rejected. It cannot preserve architect granular-edit tools or canonical per-agent sets.
- **Per-agent source order.** Rejected. Membership-identical agents would still differ in bytes and destroy shared prefixes.
- **Unconditional `vscode`/`todo` plus capability-gated extras.** Rejected as a membership rule (it silently adds tools). Ungoverned tools may still require no capability *check*; they must still be declared in `copilot-tools` to appear.

## Consequences

- Adding a Copilot tool identifier requires a catalog order slot, capability binding, and canonical membership updates together.
- Tool *order* is normalized through the catalog; membership and bodies come from the canonical source.
- Other harness renderers continue to ignore `copilot-tools`.

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md)
- [Superseded Copilot sync plan](../archive/plans/2026-08-29-kyber-squad-hotshot-golden-sync.md) (superseded; membership and order harvested here)
