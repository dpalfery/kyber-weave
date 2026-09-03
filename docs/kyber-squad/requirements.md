---
id: squad/requirements
title: Kyber-Squad requirements and degradation contract
doc-type: requirements
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-30
status: current
---

# Kyber-Squad requirements and degradation contract

This document defines the formal requirement specifications (**KS-001** through **KS-008**) and the structured degradation contract governing Kyber-Squad.

---

## Requirements Specifications

| ID | Requirement Specification |
|---|---|
| **KS-001** | **Canonical Source Governance**: Maintain exactly 21 canonical agent instruction bodies and 24 canonical skill identities under `products/kyber-squad/`. The skill tree retains 64 supplemental resources, for 88 files total, and agents own 10 progressive-disclosure references; every owner's local references form a validated resource closure, all retained until the skill-resource content-preserving migration is accepted. Generated role-skill projections and target-rendered `.github` trees do not alter the canonical product inventory. |
| **KS-002** | **Deterministic Resolution & Permission Lattice**: Resolve canonical identity, invocation mode, model profiles, capabilities, permissions, delegation hierarchies, fallbacks, aliases, and instruction body digests deterministically. Permission translation adheres to the lattice `deny < ask < allow`. Unsupported `ask` permissions narrow to `deny`, and unenforceable `ask` or `deny` constraints cause representation omission rather than permission broadening. A Copilot-only internal capability profile may validate exact target tool membership but must not replace or widen the shared capability profile or metadata. |
| **KS-003** | **Deterministic Target Resolution**: Resolve deployment targets from explicit CLI flags, saved repository configuration, existing receipts (for update/uninstall), or strong filesystem markers. The `all` keyword expands strictly to the approved 9-target roster (`codex`, `cursor`, `claude`, `copilot`, `opencode`, `kilo`, `antigravity`, `warp`, `factory`). |
| **KS-004** | **Transactional Lifecycle & State Governance**: Execute install, update, and uninstall operations via an isolated render plan with preflight validation, exact-match adoption (`--adopt`), managed-edit preservation, exclusive cross-process mutex leasing (`kyber-weave-squad-<root-key>`), leaf-level no-overwrite claim/publish execution, compare-and-restore rollback, and lock/receipt state applied last. |
| **KS-005** | **Version Lockstep**: Enforce exact version equality across the CLI, Squad release asset, and MCP server. Verify all release assets against published SHA-256 checksums without installing external dependencies as side effects. |
| **KS-006** | **Dual Distribution Packaging**: Provide `squad pack` to build an APM distribution zip containing all agents with their owned resources, all skills with their resources, and MCP configurations, plus an adjunct Agent Plugins v1 artifact exposing the complete recursive portable skill tree and MCP surfaces only — never agents or agent-owned resources. Every rendered role embeds its canonical instruction digest. |
| **KS-007** | **Release Pipeline Publishing**: Publish versioned `kyber-squad-X.Y.Z.zip` and `kyber-squad-plugin-X.Y.Z.zip` artifacts in GitHub Releases, validated against the pinned APM release. |
| **KS-008** | **Documentation & Plan Closeout**: Maintain canonical architecture, onboarding, requirements, configuration, and distribution documentation, keeping the governed corpus at zero validation findings. |

---

## Degradation Contract

Harnesses differ in their native capabilities (e.g. support for primary agents, subagent spawning, interactive confirmation prompts, and tool filtering). When a target harness cannot natively execute a canonical capability, Kyber-Squad degrades safely according to explicit rules.

The product currently has seven agent/skill intersections, all distinct-body collisions, and no
shared identities. Fallback targets preserve each of those skills and emit the matching agent as
`role-<name>`. `conductor` has an unoccupied skill identity and therefore lowers to a same-name
role skill.

### Degradation Taxonomy

Every non-native translation emits a structured degradation record in `squad.receipt.json` using one of these codes:

| Code | Meaning | Example |
|---|---|---|
| `lowered` | An agent role was projected to a role-skill because the target lacks a native agent primitive or primary agent role. | `architect` lowered to skill `architect` on Antigravity. |
| `safety-narrowed` | An interactive confirmation requirement (`ask`) was narrowed to `deny` because the target cannot prompt the user. | A capability requiring `ask` narrowed to `deny` on non-interactive harnesses. |
| `omitted` | An agent or skill was omitted because a required security or execution constraint cannot be enforced by the target. | A role with unenforceable `deny` constraints omitted to prevent unauthorized execution. |
| `workspace-binding-required` | An MCP server configuration in an Agent Plugins package requires host-specific repository path bindings. | Client loads portable skills but requires manual MCP workspace binding. |

---

## Target Capability and Degradation Matrix

| Target Harness | Target Projection | Renderer Coverage | Subagent Delegation | Lowering Policy | Permission Model |
|---|---|---|---|---|---|
| **Codex** | Native Markdown | Implemented and registered | Supported | Not lowered | Native execution |
| **Cursor** | Native `.cursor/agents` | Implemented and registered | Supported | Not lowered | Native execution |
| **Claude** | Native `.claude/agents` | Implemented and registered | Supported | Not lowered | Native execution |
| **GitHub Copilot** | Native instructions/agents | Implemented and registered | Supported | Not lowered | Native execution |
| **OpenCode** | Native `.opencode/agents` | Unsupported; coverage preflight fails | Unavailable | Not lowered | Not implemented |
| **Kilo** | Native `.kilo/agents` | Unsupported; coverage preflight fails | Unavailable | Not lowered | Not implemented |
| **Antigravity** | Role skills | Implemented and registered | Single-agent context | Lowered (`role-*` on collision) | Safety-narrowed |
| **Warp** | Role skills | Unsupported; coverage preflight fails | Unavailable | Lowered (`role-*` on collision) | Not implemented |
| **Factory Droids** | Native `.factory/` | Unsupported; coverage preflight fails | Unavailable | Not lowered | Not implemented |

The nine rows are the declared target roster. Only `copilot`, `cursor`, `claude`, `codex`, and
`antigravity` have implemented and registered renderers. `opencode`, `kilo`, `warp`, and `factory`
fail renderer-coverage preflight before deployment.

---

## Non-Broadening Guarantee

Kyber-Squad enforces a strict non-broadening guarantee across all translations:

> **No capability permission may be escalated from `deny` or `ask` to `allow` during target rendering or role-skill lowering.**

If a target harness cannot guarantee the containment or authorization boundaries specified in an agent's capability profile, the engine will narrow the permission or omit the component entirely.

## Golden-render and knowledge-retention requirement

Every canonical raw `SKILL.md` except the two explicitly evolved skills (`product-owner`,
`bug-crusher`) matches the Hotshot golden bytes, as does every non-evolved agent body; the three
retired `-v3` identities survive only as folded provenance in their canonical migration reports.
Renderers project each owner's validated resource closure beside its principal output, so a fresh
Copilot render emits 113 files with no dangling local references. The tracked root `.github/`
self-deployment predates resource delivery and is refreshed only by a release. Both recursive
package formats retain all supplemental resources. Surplus content remains until the
[skill-resource migration todo](../todo/migrate-skill-resources-into-standards.md) satisfies its
content-preservation, routing, and deployment acceptance criteria.

`products/kyber-squad/` is canonical and package authority. The repository root
`.github/agents/`, `.github/skills/`, `.kyber-weave/squad.lock.yml`, and
`.kyber-weave/squad.receipt.json` are an intentional stale self-deployment outside this
synchronization. They remain untouched until a human refreshes them after a fresh release candidate.

---

## Related

- [Kyber-Squad architecture](architecture.md) — technical design and transaction engine
- [Kyber-Squad onboarding guide](onboarding.md) — command usage and lifecycle workflows
- [The documentation ontology](../documentation-ontology.md) — documentation standards
