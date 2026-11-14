---
id: adr/0050-universal-kyber-weave-mcp-grant
title: The kw-docs MCP Server Reaches Every Entitled Agent, the Conductor Included
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-07
component: KyberSquad
decided-by: []
supersedes: []
---

# ADR 0050: The kw-docs MCP Server Reaches Every Entitled Agent, the Conductor Included

## Status

Accepted, 2026-10-31. Records the owner's directive that widened the `kw-docs` MCP grant to
every agent whose capability profile resolves and allows `filesystem.read`, and the narrowing of
the pure-orchestrator carve-out to the `codegraph` and `context7` servers. Amends
[ADR 0034](0034-factory-droid-entry-point-skill.md) and
[ADR 0019](0019-pi-native-subagents-and-primary-lowering.md) in place; it does not alter the
capability lattice, the renderers' native/fallback classification, or any renderer's non-MCP
permission translation.

Amended 2026-11-05, in place: every spelling below is stated against the server key and tool names
[ADR 0058](0058-mcp-servers-and-tools-are-named-by-purpose.md) puts in force — the server is
`kw-docs` and the standards tool is `docs_get_coding_standard_for_technology`. **The probes
recorded in decision 7 ran against the old spelling and were not re-run**; what they established is
name-independent, because it is a property of each harness's own naming rule and not of the string
it was fed. The Kilo sanitizer keeps a hyphen and drops an underscore whatever the server is
called; the Pi adapter maps any hyphen in a server key to an underscore before any other character
is mapped; the Antigravity entry takes its name from the declaration. Each rule therefore applies
unchanged to the `kw-` key, and only the literal string each grant spells changes. A reader who
finds an old spelling quoted anywhere in the corpus is reading a name that no longer resolves.

## Context

Every MCP-granting renderer applied one entitlement rule: grant MCP to an agent whose capability
profile allows `filesystem.read`, except the agent whose capability profile id is `orchestrator`.
That exclusion was justified on the reasoning that a role which only routes work has no use for
documentation or code-graph lookups, and it was recorded that way in the renderer remarks and in
[Kyber-Squad architecture §8](../kyber-squad/architecture.md#8-rendering).

One of the `kw-docs` server's tools is `docs_get_coding_standard_for_technology`, which hands an
agent the coding standards it must follow for the stack it is editing. The orchestrator writes no
code, but it dispatches, reviews digests, and answers owner questions about what a specialist was
required to do — and the standard is the artifact that settles those questions. The exclusion
therefore withheld the one MCP surface the orchestrator's own job depends on, on reasoning that
holds for a code-graph lookup and not for a standards lookup.

The change lands on top of the Factory per-droid whitelist delivered in
[ADR 0034](0034-factory-droid-entry-point-skill.md)'s 2026-10-31 amendment, which derived a droid's
`mcpServers` value from the servers the squad itself declares.

**Amended 2026-11-07, in place: the code graph is granted too, from the same predicate.** Decision 2
reserved `codegraph` and `context7` for non-orchestrator agents but left each renderer to decide
whether to emit that grant, and the measured conductor run showed the result — Command Code, OpenCode
and Kilo granted only `kw-docs`, so their subagents had no index tool at all and every code question
degraded to grep. The carve-out is unchanged and is now **applied**: `codegraph` is granted to every
agent `GrantsOrchestratorExcludedMcpServers` entitles, `context7` is not widened, and the names come
from the toolchain roster rather than from a list inside a renderer. Decision 2 states the boundary
and the per-target table below states each vehicle; the run rule that made the gap expensive is
[ADR 0061](0061-code-editing-runs-stop-on-unusable-tools.md).

## Decision

1. **The owner's directive governs: `kw-docs` is granted to every agent whose capability
   profile resolves and allows `filesystem.read`, `conductor` included.** Provenance: the owner
   stated on 2026-10-31 that every agent, the orchestrator among them, must reach the
   `kw-docs` MCP server because `docs_get_coding_standard_for_technology` is critical to quality
   for every agent. The directive names the `kw-docs` server only. On the shipped squad, whose
   every profile allows `filesystem.read`, the rule yields all twenty-four agents — each on the
   file its target renders. **Two targets have no file to render it on for the orchestrator**: on
   Devin and
   Pi the conductor is a lowered skill, so the entitlement there is structurally inexpressible
   rather than withheld (decision 5 states the per-target outcomes, and the parity guard pins the
   two absences).

2. **The pure-orchestrator carve-out is reversed for `kw-docs` and survives for `codegraph` and
   `context7` — and the surviving half is now actually granted.** `kw-docs` reaches the orchestrator.
   `codegraph` is granted to every agent whose capability profile is not the pure orchestrator and
   that `filesystem.read` entitles, through `GrantsOrchestratorExcludedMcpServers` — the same
   predicate `kw-docs` rides, its narrowed half — so the orchestrator keeps the carve-out and every
   specialist gains an index tool. `context7` is not widened: the measured failure was the code
   graph, and third-party documentation lookup is a separate decision for a separate reason. The
   `filesystem.read` anchor is kept rather than dropped, so a capability profile that cannot be
   resolved still withholds every grant: the rule stays fail-closed.

3. **The orchestrator's widened reach is an accepted, recorded widening.** The `conductor`
   profile denies `filesystem.search` and `process.execute`, and this change grants it eight
   read-only documentation tools. That is honestly a widening: the tool surface reaches text the
   profile's own `search` decision withholds. It is recorded here as accepted rather than
   described as equivalent, because the alternative — a caller-specific capability entry
   for "documentation query" — would widen the ontology to describe a grant that is
   already expressible as what it is. Every tool on the `kw-docs` server is declared
   `ReadOnly` and `OpenWorld = false`, so the widening adds no write, no execution, and no
   network reach; `process.execute: deny` and every other profile decision are untouched.

4. **The entitlement predicate lives in one place.** The three renderer-local copies of the rule
   (Claude, ZCode, Devin) and the Factory copy consolidate into one shared predicate on
   `CapabilityDegradations`, which already owns the per-agent capability-degradation prose every
   renderer emits: `GrantsUniversalMcpServers` for the widened rule and
   `GrantsOrchestratorExcludedMcpServers` for the narrowed one, beside the shared
   `PureOrchestratorProfile` constant. A cross-renderer parity guard
   (`SquadUniversalMcpGrantContractTests`) pins the per-renderer suites to that one predicate,
   computing the granted set from the loaded source rather than from any renderer, so a future
   edit cannot change one target's grant without changing the others. Sharing the predicate is
   what makes the narrowed carve-out a single decision rather than four that happen to agree.
   Since the 2026-11-07 amendment the same type owns the derivations the code-graph grant is
   built from — `CodeGraphMcpServer`, `CodeGraphMcpToolNames` and `CodeGraphMcpServerNames`, all
   read off the toolchain roster — and `SquadCodeGraphGrantContractTests` pins that set, so the
   widened half cannot drift apart the way the four copies would have.

5. **Per-target outcomes.** The rule is expressed in whatever form each harness actually accepts,
   and the form is the contract rather than a rendering detail:

   | Target | Grant form | Outcome for `conductor` |
   |---|---|---|
   | Factory | Per-droid server-level `mcpServers` whitelist derived from the squad's `mcp.json` (today exactly `kw-docs`); the key is never omitted | Whitelisted as `[kw-docs]` |
   | Claude | Tool-level wildcards in the agent's `tools` allow-list | Receives `mcp__kw-docs__*` only, and not `mcp__codegraph__*` or `mcp__context7__*` |
   | ZCode | Enumerated `mcp__<server>__<tool>` names from `toolchain.yml`'s `required-mcp-tools`; no wildcard is expanded | Receives the eight `kw-docs` names, not the `codegraph`/`context7` names |
   | Devin | The same enumerated `mcp__<server>__<tool>` names in `allowed-tools`, on the subagent path only | **Structurally inexpressible, not narrow.** A Devin primary agent lowers to a skill, and a Devin skill carries no rendered `allowed-tools` — the renderer emits that key on the subagent path alone, because on a skill it pre-approves rather than restricts — so there is no governed surface on which the orchestrator could be granted anything. Its reach on this target is only what the session it runs in already has |
   | OpenCode | The server-scoped permission keys `kw-docs_*`, and `codegraph_*` for an agent outside the pure orchestrator | Granted: `kw-docs_*`. No `codegraph_*` key, and no `context7` key |
   | Copilot | The agent's own `copilot-tools` frontmatter entry `kw-docs/*`; no renderer injects it | Gains that one entry in canonical frontmatter |
   | Command Code | Enumerated `mcp__kw-docs__<tool>` names in the agent's `tools`, plus the `mcp__codegraph__explore` name from the same roster for an agent outside the pure orchestrator | Receives the eight `kw-docs` names, and no code-graph or `context7` name |
   | Pi | Renderer-derived overlay for every entitled **subagent**, replacing the hand-authored `research-agent`-only overlay for this server: `extensions: [pi-mcp-adapter]`, the proxy entry `ext:pi-mcp-adapter/mcp`, then one `ext:pi-mcp-adapter/mcp__kw_docs__<tool>` entry per roster tool | Granted in every subagent file. A primary agent lowers to a skill, which carries no `tools` or `extensions`, so the grant is structurally inexpressible on the lowered form and the degradation record says so — the same class of exception as Devin |
   | Kilo | The agent frontmatter `permission` map entries `kw-docs_*: allow`, and `codegraph_*: allow` for an agent outside the pure orchestrator; an unentitled agent emits no `permission` key at all | Granted: `permission: {kw-docs_*: allow}` and no code-graph key |
   | Antigravity | The agent frontmatter `mcpServers` array entry `{name, command, args}`, read from the squad's own `mcp.json`; no entry when the squad declares no such server | Granted: `mcpServers: [{name: kw-docs, command: kyber-weave-mcp, args: [--repo-root, .]}]` |
   | Cursor, Codex, Warp | No per-agent key: MCP reach is structurally inherited from the session | Unchanged, and already reaching `kw-docs` once the operator wires it |

   **Two targets have no conductor grant surface at all, and that is a structural fact rather than
   a narrower grant.** On **Devin** and **Pi** the conductor is a lowered skill, and a skill on
   either harness carries no grant vehicle: Devin's skills take no rendered `allowed-tools`, and
   Pi's take no `tools` or `extensions`. So the orchestrator's reach on those two is whatever the
   session it runs in already has, and nothing rendered claims otherwise. The absence is asserted
   rather than described: `SquadUniversalMcpGrantContractTests` pins
   `targetsWithoutConductorSurface` to exactly `["devin", "pi"]` and cross-checks that list against
   the primary-to-skill lowering table, so a third target that quietly stopped rendering the
   orchestrator would fail the guard instead of slipping through an intersection. Each renderer's
   remarks state the same thing in prose. A reader diagnosing why `conductor` cannot call
   `docs_get_coding_standard_for_technology` on one of those two should start here, not from a
   withheld grant.

   **The ZCode consequence is stated rather than implied.** `validateSubagentMcpRequirements`
   makes every name in `required-mcp-tools` a hard connected-server requirement for every ZCode
   agent, so a ZCode install that declares no `kw-docs` server is a `squad doctor` failure
   for every agent including `conductor`. The grant is hard on ZCode in a way it is not on the
   other targets: there, wiring the server is a prerequisite of a passing install, not an
   optional upgrade.

   **The Command Code withholding sentence becomes conditional.** Command Code reaches MCP tools
   only when they are named in `tools`, so the record that says parent MCP was not inherited is
   emitted only when the derived grant is empty — the same shape the Factory whitelist record
   already uses. A granted agent records no withholding claim.

   **Why Claude grants a wildcard and the others enumerate.** Claude's allow-list accepts
   `mcp__<server>__*`, so the grant is one entry and every tool the server exposes is reachable
   without a renderer edit. ZCode, Devin and Command Code expand no wildcard, so they name the
   eight tools literally; that is not a preference for enumeration but the only form those
   harnesses accept, and it is why the roster they enumerate is the toolchain's
   `required-mcp-tools` rather than a list of their own. The asymmetry has a consequence worth
   stating: **a tool added to the `kw-docs` server later reaches every Claude agent with no
   renderer change and no failing test**, because the wildcard grants whatever the server
   exposes. Today every tool on that server is declared `ReadOnly`, so the exposure is
   read-only, and the server's tool set is fixed by the roster the toolchain guard pins. A
   mutating tool would need a new capability decision before the wildcard could carry it to
   Claude, and nothing enforces that today: the guard pins the declared tool names and every
   server tool carries `ReadOnly = true`, so a mutating tool would arrive as a visible roster
   change plus a lost attribute, and the decision is a review obligation rather than a gate. The
   three enumerating targets would need no such decision to be widened, and that asymmetry is
   the argument for enumerating everywhere if the server's tool set ever becomes mutable rather
   than by decision. Amended 2026-11-13: [ADR 0078](0078-the-conductor-finalizes-and-archives-plans-through-two-docs-tools.md)
   is that decision. It adds two tools that are not `ReadOnly` and write only a plan's lifecycle
   files, refusing any other path, and keeps them on the universal grant.

6. **`conductor` receives the Copilot entry through canonical frontmatter.** Copilot's per-agent
   tool grant comes from the agent's own `copilot-tools` list, which is canonical source rather
   than renderer output, so the grant is one entry in `conductor.md` and no renderer change. This
   is an expected always-human review finding: the visible diff is a frontmatter edit on the
   primary agent, not a renderer rule.

7. **All three probed harnesses landed a per-agent grant, and the probe is the contract.** Kilo
   and Antigravity document a per-agent MCP surface, and Pi's overlay spelling had never been
   observed. None of the three was emitted from vendor documentation: each was probed against a
   pinned build, and each probe returned a grant rather than the exclusion this decision
   originally reserved as the fallback.

   - **Kilo — wired, pinned to `@kilocode/cli` 7.8.18** (`Kilo-Org/kilocode` tag `v7.8.18`). The
     agent-file loader parses every frontmatter key into the agent config and validates the result
     against the agent schema; `permission` is a first-class key whose values are `ask`, `allow`,
     `deny`, or null, with arbitrary keys carried through a catch-all map. MCP tools are gated by
     that same map under the namespaced key `{server}_{tool}`, and the server name is sanitized
     only for characters outside `[a-zA-Z0-9_-]` — so the hyphen survives, the server wildcard is
     `kw-docs_*`, and an underscore spelling would parse cleanly and match nothing. That was
     established by executing Kilo's own matcher, not by reading the docs: the
     `kw-docs`-prefixed form matches `kw-docs_*`; the same name with an underscore in place of the
     hyphen does not. Corroborated by
     kilo.ai/docs/automate/mcp/using-in-kilo-code and kilo.ai/docs/customize/custom-subagents
     (both read 2026-10-31).
   - **Antigravity — wired, pinned to the installed `agy` 1.4.12**, the same live-validation
     discipline [ADR 0022](0022-antigravity-native-agents.md) established for every Antigravity
     capability. The entry is an `mcpServers` array of `{name, command, args}` objects. That shape
     was called successfully; the single-key map spelling (`- <key>: {command, args}`)
     listed and ran but was never actually invoked, so it is not emitted. `command` and `args` are
     read from the squad's own `mcp.json` rather than hardcoded, and a missing or malformed
     declaration yields no entry — the fail-closed answer. `--repo-root .` is workspace-relative
     deliberately: a probe with an absolute path returned the global server's root, so a
     same-named global definition can take precedence over the per-agent entry.
   - **Pi — wired, with a corrected spelling.** The derived overlay's per-tool entries are
     `ext:pi-mcp-adapter/mcp__kw_docs__<tool>`, not `mcp__kw-docs__<tool>`. The server
     namespace maps the hyphen to an underscore before any other character is mapped, so
     `kw-docs` is reached as `kw_docs`. This was established by executing the published
     `pi-mcp-adapter` naming code over the roster and corroborated by Pi's own MCP documentation
     ("every character other than letters, digits, and `_` replaced by `_`"); the corpus's existing
     `mcp__context7__resolve_library_id` could not have revealed it, because that server name has
     no hyphen.

   **The Pi spelling is stated here because the two spellings are easy to confuse.** The wrong one
   — the hyphenated form that mirrors the server key — resolves against no registered Pi tool: it
   exists only on the adapter's *direct*-tool surface (the `{server}_{tool}` form the Kilo row
   describes), which Squad never declares onto. A reader who finds the hyphenated spelling
   attributed to Pi is reading a stale copy, and the hyphenated form belongs only to targets whose
   naming rule keeps it — Claude, ZCode, Devin, and Command Code, where the server name is passed
   through verbatim.

   **Version floor: `pi-mcp-adapter` 6.0.0, current release 7.0.0.** The deferred/tool-search
   surface this naming belongs to arrived in 6.0.0, whose changelog first records tools becoming a
   Pi deferred tool named `mcp__<server>__<tool>`. 7.0.0 is the supported target and is the version
   the naming code was executed against. An operator still on 5.x has no deferred surface at all
   and would name these tools `kw-docs_<tool>`; there the `mcp` proxy entry
   keeps working and only the enumerable per-tool names are lost. That 5.x cannot be the target is
   also visible in the corpus itself: the checked-in `mcp__context7__resolve_library_id` entry
   resolves on no version before 6.0.0 either.

   **An exclusion remains the rule when a probe cannot establish a shape.** That part of this
   decision is unchanged and is why the probes were run: a field emitted on a guess is an
   unenforceable instruction, which every renderer here refuses on record. What changed is the
   answer, not the standard.

8. **Cursor, Codex, and Warp are excluded with citations, because the per-agent surface does not
   exist.** Cursor subagents inherit every tool from the parent, including MCP, with no `tools`
   allow-list key (`cursor.com/docs/subagents.md`, read 2026-10-31). Codex custom agents inherit
   the parent's MCP servers and a custom agent file cannot override those settings
   (`developers.openai.com/codex/subagents`, read 2026-10-31). Warp supports MCP, but Squad's Warp
   surface is role skills, which carry no tool scoping (`docs.warp.dev/agents/capabilities/mcp`,
   read 2026-10-31). The directive's goal is already met on the first two the moment the operator
   wires the server, and on Warp every skill runs in a session whose MCP the operator configures.
   Emitting a per-agent key on any of the three would be a claim the harness does not honour.

## Alternatives Considered

- **Leave the carve-out and answer the orchestrator's standards questions from its own
  instructions.** Rejected by the owner. A standard copied into an agent body drifts from the
  standard; the tool is the single copy that cannot.
- **Grant every MCP server, `codegraph` and `context7` included, to the orchestrator.** Rejected.
  The directive's reason is the standards tool, and the carve-out's original reasoning still
  holds for symbol lookup and third-party documentation.
- **Add a capability-lattice entry for documentation query so the grant becomes lattice-derived.**
  Rejected. MCP reach is renderer-derived on every target today, and the lattice declares seven
  capabilities, none of them MCP. Adding one to describe a read-only tool surface would widen the
  ontology for a grant the existing predicate already expresses.
- **Keep the predicates per renderer and change each one.** Rejected. The rule had already
  drifted into three copies plus a fourth; a narrowed carve-out expressed four times is a
  carve-out that will differ four ways after the next edit.
- **Emit the Antigravity and Kilo fields from the vendor documentation alone.** Rejected on the
  [ADR 0022](0022-antigravity-native-agents.md) precedent, and the rejection was borne out: both
  vendors document a per-agent surface, and neither documents it at the level a renderer can emit
  without guessing. Kilo's documentation does not settle the hyphen, and Antigravity's naming of
  `mcpServers` does not settle which entry shape is actually called. Probes decided both.
- **Write the Pi entries as `mcp__kw-docs__<tool>`, matching the server key and the
  hyphenated spelling the other targets use.** Rejected. The adapter's namespace maps the hyphen
  to an underscore, so that spelling names no registered tool on the version the overlay targets.
  Keeping one spelling across targets would be consistency in exchange for a grant that resolves
  against nothing.

## Consequences

- `conductor` holds eight read-only documentation tools on eight of the thirteen targets. The
  split is **8 per-agent, 3 session-inherited, 2 structurally inexpressible**: eight targets
  render it the grant directly, Cursor, Codex, and Warp reach it through a session the operator
  wires, and **Devin and Pi** cannot express it at all, because a lowered primary agent is a skill
  there and a skill carries no grant vehicle. The last two are recorded as structural rather than
  narrow, and the parity guard pins them by name. Any future agent whose profile allows
  `filesystem.read` is entitled by the same rule with no per-agent declaration.
- The one shared predicate plus the parity guard means a change to the entitlement rule is one
  edit and one failing test away from being wrong, on every target at once.
- Since the 2026-11-07 amendment, every specialist agent on a target that can express a per-agent
  grant also carries the `codegraph` grant, so a code question reaches an index instead of grep.
  The orchestrator still carries none, and `context7` is still withheld from it everywhere. **Pi is
  the one target whose code-graph grant is not rendered**: its grant vehicle is a per-server
  adapter overlay, whose namespace mapping was verified for the one hyphenated first-party key, so
  extending it to a second server is a separate decision and is tracked as a todo rather than
  guessed.
- ZCode installs now require the `kw-docs` server to be declared, or `squad doctor` fails.
  That is a stricter install prerequisite than before, and it follows from the harness's own
  hard requirement rather than from a Squad choice.
- The Pi overlay for this server is renderer-derived: `research-agent`'s hand-authored
  `pi-extensions`/`pi-tools` declaration remains for `context7`, and the `kw-docs` half no
  longer needs a per-agent edit to stay current.
- No capability, dependency, schema, or `KW-*` id is added. The capability lattice is unchanged:
  `products/kyber-squad/profiles/capabilities.yml` still declares only `filesystem.read`,
  `filesystem.search`, `filesystem.write`, `process.execute`, `network.read`, `network.publish`,
  and `delegate`, and MCP reach stays renderer-derived.
- `squad install` still writes no harness settings file. The Factory droid whitelist names a
  server; the operator's `.factory/mcp.json` or `~/.factory/mcp.json` launches it.

## Related

- [ADR 0058: MCP servers and tools are named by purpose](0058-mcp-servers-and-tools-are-named-by-purpose.md) — the keys and tool names every grant above is spelled with
- [ADR 0034: Factory Keeps the Conductor Droid and Adds an Entry-Point Skill](0034-factory-droid-entry-point-skill.md) — amended in place: the whitelist is no longer withheld from the orchestrator
- [ADR 0019: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering](0019-pi-native-subagents-and-primary-lowering.md) — amended in place: the overlay is renderer-derived for a declared server
- [ADR 0022: Native per-agent Antigravity rendering](0022-antigravity-native-agents.md) — the live-validation discipline the Antigravity grant inherits
- [ADR 0021: ZCode command lowering](0021-zcode-command-lowering-and-resource-relocation.md) — enumerated MCP tool names and the doctor's hard requirement
- [Kyber-Squad architecture](../kyber-squad/architecture.md#8-rendering) — the per-target grant forms
- [Kyber-Squad requirements](../kyber-squad/requirements.md#target-capability-and-degradation-matrix) — the capability and degradation matrix
- [MCP runbook](../docgraph/mcp-runbook.md#wiring-it-up) — the per-client wiring recipes
