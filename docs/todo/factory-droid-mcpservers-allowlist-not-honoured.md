---
id: todo/factory-droid-mcpservers-allowlist-not-honoured
title: A Factory droid's mcpServers allowlist is not honoured at runtime, so a named server reaches no tool
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-11-07
---

# A Factory droid's mcpServers allowlist is not honoured at runtime, so a named server reaches no tool

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
