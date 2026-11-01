---
id: adr/0019-pi-native-subagents-and-primary-lowering
title: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-31
---

# ADR 0019: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering

## Status

Accepted, 2026-09-14. Records the Pi rendering classification delivered with the Pi harness
target. Does not change how other native or fallback targets lower agents.

## Context

Pi core ships no sub-agents and no per-agent primary selection. `.pi/SYSTEM.md` and
`--system-prompt` replace the prompt for the whole session. The original coverage todo
therefore treated Pi as a primitive-less target and would have lowered every canonical agent
to a skill.

The owner runs Pi with `@tintinweb/pi-subagents`, which defines a Claude Code-like custom-agent
file format under `.pi/agents/` and a skill tree under `.pi/skills/`. That extension is what
makes a native agent projection possible. It is not first-party Pi.

The extension's default `maxSubagentDepth` is 2 (main session 0, subagent 1, nested child 2).
A conductor rendered as a subagent would run at depth 1, which puts its specialists at depth 2
and removes the nested delegation that `architect` and `code-reviewer` need. `/agents` lists
subagent types only; nothing promotes a custom agent into the main session.

## Decision

1. **Pi is a native target.** `PiRenderer` emits subagent-invocation agents as
   `.pi/agents/<name>.md` in the `@tintinweb/pi-subagents` custom-agent format, and canonical
   skills as `.pi/skills/<name>/SKILL.md`. No Pi output may carry a `role-` prefix. Agents and
   skills occupy separate namespaces, so the seven agent/skill name intersections are not
   collisions on Pi.
2. **Primary-invocation agents lower, they do not become subagents.** The fallback profile's
   `no-primary-agent` value decides the outcome, read from the loaded source. The current
   corpus has one such agent, `conductor`, with value `skill`: it renders as
   `.pi/skills/conductor/SKILL.md` at depth 0, recording `role-skill-fallback`. Value `omit`
   emits nothing and records `omitted`.
3. **A lowered identity that is already a canonical skill fails closed.** Native classification
   forbids a `role-` escape hatch. `PiRenderer` throws `SquadRenderValidationException` rather
   than emitting a duplicate.
4. **The extension is a prerequisite, not a deployed artifact.** Squad does not install
   `@tintinweb/pi-subagents`. Without 0.19.0 or later (peer: Pi ≥ 0.84.0), the emitted agent
   files have no effect. `squad doctor` does not detect the extension.

## Alternatives Considered

- **Lower every agent to a skill (original todo).** Rejected. The owner has the extension
  configured, and native agent files give `/agents`, `@handle` mentions, and
  `allowed_subagents` that a skill-only tree cannot.
- **Render the conductor as a subagent, matching Claude/Cursor/Copilot/Codex.** Rejected. Pi
  has no primary-agent primitive, so the file would spawn at depth 1 and nested specialist
  delegation would hit the default depth cap. `/agents` cannot replace `main` with that file.
  Reconfirmed when the live-Pi observations reopened the plan (U7).
- **Wait for first-party Pi sub-agents.** Rejected. The owner named the third-party format as
  the rendering target now; a later first-party primitive would be a superseding ADR.

## Consequences

- Every future `invocation: primary` agent on Pi inherits the same lowering. Changing that
  rule means rewriting `PiRenderer`, its contract tests, and the onboarding depth guidance.
- Operators invoke the Squad conductor with `/skill:conductor`, not from `/agents`. Project
  trust is required before `.pi/skills/` loads; project `.pi/agents/` load without it.
- Format drift in `@tintinweb/pi-subagents` is now a Squad maintenance obligation. A
  first-party Pi agent primitive does not automatically replace this renderer.
- Project `.pi/agents/<name>.md` silently overrides a same-named global agent. That is
  intended project authority, and it narrows any pre-existing global files that share a
  canonical identity.

## Amendment 2026-10-24 (revised): `network.read` via canonical per-target overlay — Option A REVERSED

Owner decision Option A 2026-10-23 (`network.read → mcp` renderer lowering, eight-tool
vocabulary) is **REVERSED 2026-10-24 on verified live evidence and is not implemented**
(plan `2026-10-23-pi-renderer-network-read-mcp`, D1). Bare `mcp` in `tools:` is refused by
the Pi loader under the unknown-name rule — plain `tools:` names must be built-ins, and
subagents inherit the documented `extensions: ["-builtin:mcp"]` global opt-out — so the
revision-1 mapping emitted invalid frontmatter. The original 2026-10-24 amendment text
recording that lowering is superseded by this revision; no reader should re-derive the
reverted mechanism from it.

The operative fix lives one layer up, in the canonical Squad agent file: two optional flat
`kyber-squad.agent/v1` keys, `pi-extensions` (array of Pi extension ids) and `pi-tools`
(array of additional Pi tool names, `ext:<extension>/<tool>` form only — bare names are
rejected so the overlay cannot smuggle a built-in past the capability lattice). The Pi
renderer lowers the overlay onto the verified wiring: `extensions: [pi-mcp-adapter]` (YAML
sequence, the exact live-probed form) plus the declared `ext:` tool names appended to the
capability-derived `tools` CSV. Basis: 2026-10-24 live probes (run twice) — a subagent
carrying that wiring started the context7 server, resolved a library ID, and returned
sourced docs, while the scope gate refused the call without the nested
`mcp__<server>__<tool>` allowlisting. Scope is `research-agent` only, declaring exactly the
verified entries (`ext:pi-mcp-adapter/mcp`,
`ext:pi-mcp-adapter/mcp__context7__resolve_library_id`,
`ext:pi-mcp-adapter/mcp__context7__query_docs`); the extension prerequisite is the
`pi-mcp-adapter` npm package in the deployment harness.
This supersession leaves Q1-A intact: the renderer vocabulary returns to the seven-tool
`read, grep, find, ls, edit, write, bash`; `network.read` still has no expressible built-in
Pi tool and still records `permission-not-expressible` (true of the renderer alone — the
overlay is the additive per-agent exception that supplies web access); `network.publish`
still has no built-in tool and still records `permission-not-expressible`; `extensions:
false` is still emitted unconditionally for every agent without an overlay; `tools` is still
always emitted; and `process.execute: deny` stays withheld for read-only roles. Caveat: the
loader behaviour for a missing `pi-mcp-adapter` extension on a deployment machine is
unverified and is not assumed.

## Amendment 2026-10-31: the `kyber-weave` overlay is renderer-derived

The overlay mechanism above is unchanged. What changes is who writes it for one server.
`context7` stays exactly as the 2026-10-24 amendment records it: a hand-authored per-agent
declaration in the canonical agent file, because a server whose use is a specific agent's research
work is a decision that belongs to that agent's author ([ADR 0049](0049-universal-kyber-weave-mcp-grant.md)
grants `kyber-weave` to every entitled agent and leaves this one agent-scoped). `kyber-weave` no
longer needs that declaration: the renderer derives its overlay for every agent the shared
entitlement predicate grants, so `research-agent` stops being the only Pi agent that reaches the
documentation server and no canonical body has to carry an entry that the rule already supplies.

The spelling the earlier draft left open is settled: the derived entries are
`ext:pi-mcp-adapter/mcp__kyber_weave__<tool>`. `pi-mcp-adapter` names a deferred tool
`mcp__<namespace>__<tool>`, mapping the hyphen in the server name to an underscore before any
other character is mapped, so `kyber-weave` is reached as `kyber_weave`; the
`mcp__kyber-weave__<tool>` form this amendment's first draft recorded names no registered tool and
is wrong. The 2026-10-24 entries could not have revealed the mapping, because `context7` carries no
hyphen. The naming was established by executing the adapter's published naming code over the
squad's roster and corroborated by a live MCP `tools/list`, where the adapter's own `doctor`
reported `kyber-weave: ok, 8 tools`. It arrived in `pi-mcp-adapter` **6.0.0** — the release whose
changelog first records tools becoming a deferred tool named `mcp__<server>__<tool>` — and
**7.0.0** is the supported target. An operator on 5.x has no deferred surface at all and would name
these tools `kyber-weave_<tool>`, so there the `mcp` proxy entry carries the server and only the
enumerable per-tool names are lost.

What remains an owner probe is the **in-session listing**: that a live subagent session shows these
names and answers a call is not something a renderer test establishes. The spelling itself is no
longer inferred, and the 2026-10-24 caution still governs the rest of the overlay — a `tools:`
entry the Pi loader refuses is a `tools-error`, which is why every derived entry keeps the `ext:`
form and no MCP tool name is ever written bare.

A primary agent lowered to `.pi/skills/<name>/SKILL.md` carries no `tools` or `extensions` key, so
the derived grant is structurally inexpressible on that form. Its degradation record names
`kyber-weave` and says the entry point reaches the server only through the session it runs in —
the same class of exception as Devin's lowered primary agent.

The renderer's own vocabulary is untouched: `tools` is still always emitted, `extensions: false`
is still emitted unconditionally for an agent without an overlay, and the bare-name rejection that
keeps the overlay from smuggling a built-in past the lattice still applies to whatever the renderer
derives.

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md)
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md)
- [ADR 0049: The kyber-weave MCP Server Reaches Every Entitled Agent, the Conductor Included](0049-universal-kyber-weave-mcp-grant.md)
- [Plan: Add Pi as a Kyber-Squad harness target](../archive/plans/2026-09-14-pi-harness-target.md) (archived)
