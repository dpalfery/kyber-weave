---
id: squad/requirements
title: Kyber-Squad requirements and degradation contract
doc-type: requirements
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-15
status: current
---

# Kyber-Squad requirements and degradation contract

This document defines the formal requirement specifications (**KS-001** through **KS-008**) and the structured degradation contract governing Kyber-Squad.

---

## Requirements Specifications

| ID | Requirement Specification |
|---|---|
| **KS-001** | **Canonical Source Governance**: Maintain exactly 22 canonical agent instruction bodies and 25 canonical skill source directories under `products/kyber-squad/`. Generated role-skill projections do not alter the source inventory, and generated APM, plugin, or harness trees are never tracked in source control. |
| **KS-002** | **Deterministic Resolution & Permission Lattice**: Resolve canonical identity, invocation mode, model profiles, capabilities, permissions, delegation hierarchies, fallbacks, aliases, and instruction body digests deterministically. Permission translation adheres to the lattice `deny < ask < allow`. Unsupported `ask` permissions narrow to `deny`, and unenforceable `ask` or `deny` constraints cause representation omission rather than permission broadening. |
| **KS-003** | **Deterministic Target Resolution**: Resolve deployment targets from explicit CLI flags, saved repository configuration, existing receipts (for update/uninstall), or strong filesystem markers. The `all` keyword expands strictly to the approved 10-target roster (`codex`, `cursor`, `claude`, `copilot`, `opencode`, `kilo`, `gemini`, `antigravity`, `warp`, `factory`). |
| **KS-004** | **Transactional Lifecycle & State Governance**: Execute install, update, and uninstall operations via an isolated render plan with preflight validation, exact-match adoption (`--adopt`), managed-edit preservation, exclusive cross-process mutex leasing (`kyber-weave-squad-<root-key>`), leaf-level no-overwrite claim/publish execution, compare-and-restore rollback, and lock/receipt state applied last. |
| **KS-005** | **Version Lockstep**: Enforce exact version equality across the CLI, Squad release asset, and MCP server. Verify all release assets against published SHA-256 checksums without installing external dependencies as side effects. |
| **KS-006** | **Dual Distribution Packaging**: Provide `squad pack` to build an APM distribution zip containing all agents, skills, and MCP configurations, plus an adjunct Agent Plugins v1 artifact exposing portable skills and MCP surfaces only. Every rendered role embeds its canonical instruction digest. |
| **KS-007** | **Release Pipeline Publishing**: Publish versioned `kyber-squad-X.Y.Z.zip` and `kyber-squad-plugin-X.Y.Z.zip` artifacts in GitHub Releases, validated against the pinned APM release. |
| **KS-008** | **Documentation & Plan Closeout**: Maintain canonical architecture, onboarding, requirements, configuration, and distribution documentation, keeping the governed corpus at zero validation findings. |

---

## Degradation Contract

Harnesses differ in their native capabilities (e.g. support for primary agents, subagent spawning, interactive confirmation prompts, and tool filtering). When a target harness cannot natively execute a canonical capability, Kyber-Squad degrades safely according to explicit rules.

### Degradation Taxonomy

Every non-native translation emits a structured degradation record in `squad.receipt.json` using one of these codes:

| Code | Meaning | Example |
|---|---|---|
| `lowered` | An agent role was projected to a role-skill because the target lacks a native agent primitive or primary agent role. | `architect` lowered to skill `architect` on Gemini CLI. |
| `safety-narrowed` | An interactive confirmation requirement (`ask`) was narrowed to `deny` because the target cannot prompt the user. | A capability requiring `ask` narrowed to `deny` on non-interactive harnesses. |
| `omitted` | An agent or skill was omitted because a required security or execution constraint cannot be enforced by the target. | A role with unenforceable `deny` constraints omitted to prevent unauthorized execution. |
| `workspace-binding-required` | An MCP server configuration in an Agent Plugins package requires host-specific repository path bindings. | Client loads portable skills but requires manual MCP workspace binding. |

---

## Target Capability and Degradation Matrix

| Target Harness | Native Agents | Subagent Delegation | Lowering Policy | Permission Model |
|---|---|---|---|---|
| **Codex** | Native Markdown | Supported | Not lowered | Native execution |
| **Cursor** | Native `.cursor/agents` | Supported | Not lowered | Native execution |
| **Claude** | Native `.claude/agents` | Supported | Not lowered | Native execution |
| **GitHub Copilot** | Native instructions/agents | Supported | Not lowered | Native execution |
| **OpenCode** | Native `.opencode/agents` | Supported | Not lowered | Native execution |
| **Kilo** | Native `.kilo/agents` | Supported | Not lowered | Native execution |
| **Gemini CLI** | No native agent primitive | Single-agent context | Lowered to role-skills (`role-*` on collision) | Safety-narrowed |
| **Antigravity** | No native agent primitive | Single-agent context | Lowered to role-skills (`role-*` on collision) | Safety-narrowed |
| **Warp** | Native `.warp/` | Supported | Not lowered | Native execution |
| **Factory Droids** | Native `.factory/` | Supported | Not lowered | Native execution |

---

## Non-Broadening Guarantee

Kyber-Squad enforces a strict non-broadening guarantee across all translations:

> **No capability permission may be escalated from `deny` or `ask` to `allow` during target rendering or role-skill lowering.**

If a target harness cannot guarantee the containment or authorization boundaries specified in an agent's capability profile, the engine will narrow the permission or omit the component entirely.

---

## Related

- [Kyber-Squad architecture](architecture.md) — technical design and transaction engine
- [Kyber-Squad onboarding guide](onboarding.md) — command usage and lifecycle workflows
- [The documentation ontology](../documentation-ontology.md) — documentation standards
