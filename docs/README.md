---
id: docs-index
title: Kyber-Weave documentation
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-15
---

# Kyber-Weave documentation

This corpus is governed by the tool it documents. Every page carries conformant
frontmatter, every `code-refs` entry resolves against a live code index, and
`kyber-weave docs validate .` and `docs drift .` both run clean — see
[the ontology](documentation-ontology.md) for what that means and
[governance](docgraph/governance.md) for how it is enforced.

An agent should reach this corpus through [`docs_explore`](docgraph/mcp-runbook.md) rather
than by reading these files.

## Start here

- [Installing Kyber-Weave](install.md) — one command
- [The documentation ontology](documentation-ontology.md) — the opinion everything rests on
- [Component and owner catalog](catalog.md) — the authoritative vocabulary
- [Configuration](configuration.md) — adapting the defaults to your repository

### The 4 Core Pillars

- [Context Hygiene](context-hygiene/README.md) — Why and how to govern agent prompts and skills across coding harnesses
- [DocGraph](docgraph/README.md) — Why and how to turn markdown docs into a queryable in-memory graph joined to live code
- [Kyber-Squad](kyber-squad/README.md) — Why and how to deploy canonical agent squads across 10 IDE harnesses with rollback
- [KyberDash](dash/README.md) — Why and how to observe and tune agent context with local .NET Aspire OTEL telemetry

---

## Feature 1 — Context Hygiene

Governance for the artifacts that shape an agent's context: Agent Skills and harness agent definitions.
Start at the [Context Hygiene Overview](context-hygiene/README.md) for value proposition and adoption rationale.

| Page | Covers |
|---|---|
| [Overview & Why Context Hygiene](context-hygiene/README.md) | Value proposition, failure modes of unmanaged instruction surfaces, core capabilities |
| [Skill governance](context-hygiene/skills.md) | Spec conformance, routing readiness, simulation |
| [Agent harness governance](context-hygiene/agents.md) | Parity and drift across six harnesses |
| [Instruction-surface scanning](context-hygiene/security-scanning.md) | The shared security engine |
| [ALM governance playbook](alm-governance-playbook.md) | Operating skills at enterprise scale |

## Feature 2 — DocGraph

The opinionated documentation structure, its conformance gates, graph-first claim analysis,
and the in-memory retrieval graph served to agents over MCP.
Start at the [DocGraph Overview](docgraph/README.md) for value proposition and adoption rationale.

| Page | Covers |
|---|---|
| [Overview & Why DocGraph](docgraph/README.md) | Value proposition, code-joined graph model, sub-millisecond MCP search |
| [Adoption](docgraph/onboarding.md) | `docs init`, the authoring skill, retrofitting an existing tree |
| [Architecture](docgraph/architecture.md) | The pipeline, the two-clock reload, the code-graph join |
| [Retrieval and ranking](docgraph/retrieval.md) | Scoring, authority weighting, budgeted excerpts |
| [Analysis and review](docgraph/analysis.md) | Graph-first duplicate/conflict/terminology detection, agent verdicts, managed glossary |
| [Governance gates](docgraph/governance.md) | `docs validate`, `docs drift`, `docs catalog` |
| [MCP server runbook](docgraph/mcp-runbook.md) | Serving the graph to an agent |

## Feature 3 — Kyber-Squad

Unified multi-harness deployment and lifecycle control plane for 20 canonical agents and
25 skills across 10 coding harnesses with transactional rollback.
Start at the [Kyber-Squad Overview](kyber-squad/README.md) for value proposition and adoption rationale.

| Page | Covers |
|---|---|
| [Overview & Why Kyber-Squad](kyber-squad/README.md) | Value proposition, solving multi-harness fragmentation, capability lattices |
| [Adoption & usage guide](kyber-squad/onboarding.md) | `squad install`, `update`, `uninstall`, `status`, `doctor`, `pack`, scopes, target resolution |
| [Architecture](kyber-squad/architecture.md) | AgentIR, permission lattice, role-skill lowering, state store, mutex lease, transaction engine |
| [Requirements & degradation](kyber-squad/requirements.md) | KS-001–KS-008 specifications, structured degradation taxonomy, capability matrix |

## Feature 4 — KyberDash (Upcoming)

Local interactive web dashboard consuming OpenTelemetry data from the .NET Aspire dashboard to observe, analyze, and tune agentic context windows.
Start at the [KyberDash Overview](dash/README.md) for value proposition and development roadmap.

| Page | Covers | Status |
|---|---|---|
| [Overview & Why KyberDash](dash/README.md) | Value proposition, OTEL telemetry ingestion, context window heatmap, tuning loop | Draft |

---

## Supporting Infrastructure

The cross-cutting infrastructure, diagnostic pipeline, and distribution mechanics supporting the core features:

- [CI Pipelines Architecture](ci-pipelines/architecture.md) — Rule ids, severities, output formats, exit codes
- [Rule Reference](ci-pipelines/rule-reference.md) — Every `KW-*` id in one table
- [Workflow Runbook](ci-pipelines/workflows-runbook.md) — Copy-ready GitHub Actions gates
- [Installing Kyber-Weave](install.md) — Binary distribution and installation steps
- [Configuration](configuration.md) — Customizing the ontology and tool settings
- [Distribution and release flow](distribution.md) — Maintainer-facing release process

