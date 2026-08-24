---
id: docs-index
title: Kyber-Weave documentation
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-16
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
- [Coding standards](standards/README.md) — how code is written here, one folder per technology

Agents and skills resolve this corpus by name rather than by path: the **Config Reg** block in
the repository root [`AGENTS.md`](../AGENTS.md) publishes `<component-catalog>`,
`<csharp-coding-standard>` and the rest, and `docs init` regenerates it from
[configuration](configuration.md). A portable instruction file that names a property stays
correct in a repository that arranges its documentation differently.

### The 4 Core Pillars

- [Context Hygiene](context-hygiene/README.md) — Why and how to govern agent prompts and skills across coding harnesses
- [DocGraph](docgraph/README.md) — Why and how to turn markdown docs into a queryable in-memory graph joined to live code
- [Kyber-Squad](kyber-squad/README.md) — Why and how to deploy canonical agent squads across 10 IDE harnesses with rollback
- [KyberDash](dash/README.md) — Why and how to observe and tune agent context with local .NET Aspire OTEL telemetry

---

## Specs, plans, and todos

Not all documentation describes what exists today. Kyber-Weave organizes forward-looking work
across three categories, each with its own folder and its own place in the governed
vocabulary (`doc-type: spec` / `plan` / `todo`), so that emerging designs, execution
sequences, and deferred findings stay structured and discoverable without polluting canonical
documentation.

- **[`docs/specs/`](specs/README.md) — spec.** Net-new work: no existing architecture to
  build on. Upfront, spec-driven-development style (the Kiro spec mode / GitHub Spec Kit
  lineage) — requirements, design, and system boundaries get defined before implementation
  starts. *This is the same `specs/` workflow the Kyber-Squad product's `product-owner` agent
  prescribes for every project it's installed into, applied here to Kyber-Weave's own
  repository — under `docs/`, since this repository overrides the docs-root from the Squad's
  product default.*
- **[`docs/plans/`](plans/README.md) — plan.** Work that extends or fits within *existing*
  architecture — even substantial work, new files, new patterns — as long as there's a
  precedent it's building on. Lightweight, typically a single file, typically
  architect-authored, sequencing concrete tasks.
- **[`docs/todo/`](todo/README.md) — todo.** A reminder of work not done now: a finding, a
  deferred fix, a suggestion declined rather than acted on. Usually the seed for a future spec
  or plan — small enough work can just get picked up and fixed directly instead. Whichever
  successor it becomes is the implementor's call at pickup time, not something decided when
  the todo is written.

**Choosing spec vs. plan is a human judgment call, not a mechanical rule** — the ontology
doesn't try to gate it. The test that matters: is there already architecture here to extend,
or is this genuinely starting from nothing? A large new subsystem built on an existing
component's established patterns (a new interface implementing an already-designed seam, for
example) is still a plan; a spec is reserved for when that architectural precedent doesn't
exist yet.

**All three share one lifecycle, and the same closeout mechanism.** Each stays in its active
folder while current, then archives to the matching `docs/archive/` folder once done — the
pattern `docs/plans/README.md`'s inventory already shows. Closeout is always a `docs-dev`
task: verify the work against evidence, migrate the affected content into canonical
documentation, then archive. That's identical for a spec and a plan — a spec's closeout is
"exactly like a plan's." What differs is only what the work actually produced: a spec usually
creates new or substantially-rewritten architecture/onboarding/requirements pages, since it
started from nothing; a plan usually updates pages that already existed, since it built on
architecture already documented. The mechanism is the same either way.

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

Unified multi-harness deployment and lifecycle control plane for 23 canonical agents and
26 skills across 9 coding harnesses with transactional rollback.
Start at the [Kyber-Squad Overview](kyber-squad/README.md) for value proposition and adoption rationale.

| Page | Covers |
|---|---|
| [Overview & Why Kyber-Squad](kyber-squad/README.md) | Value proposition, solving multi-harness fragmentation, capability lattices |
| [Adoption & usage guide](kyber-squad/onboarding.md) | `squad install`, `update`, `uninstall`, `status`, `doctor`, `pack`, scopes, target resolution |
| [Architecture](kyber-squad/architecture.md) | AgentIR, permission lattice, role-skill lowering, state store, mutex lease, transaction engine |
| [Requirements & degradation](kyber-squad/requirements.md) | KS-001–KS-008 specifications, structured degradation taxonomy, capability matrix |
| [Renderer coverage — what's left](todo/kyber-squad-renderer-coverage.md) | Which of the 9 harnesses install today, and the per-target context for implementing the rest |

## Feature 4 — Review council

Parallel code review: a council of specialist lenses over the diff, the host's deterministic
gate suite, and a rule-based verdict engine that decides from both.
Start at the [Review council overview](code-review/README.md) for the idea and the two commands.

| Page | Covers |
|---|---|
| [Overview & Why the council](code-review/README.md) | Lens fan-out, gates as evidence, what makes the verdict trustworthy |
| [Architecture](code-review/architecture.md) | Three layers, two lens seats, the evidence schema, verdict rules, permissions, configuration |

## Feature 5 — KyberDash (Upcoming)

Local interactive web dashboard consuming OpenTelemetry data from the .NET Aspire dashboard to observe, analyze, and tune agentic context windows.
Start at the [KyberDash Overview](dash/README.md) for value proposition and development roadmap.

| Page | Covers | Status |
|---|---|---|
| [Overview & Why KyberDash](dash/README.md) | Value proposition, OTEL telemetry ingestion, context window heatmap, tuning loop | Draft |

---

## Supporting Infrastructure

The cross-cutting infrastructure, diagnostic pipeline, and distribution mechanics supporting the core features:

- [Coding standards](standards/README.md) — Per-technology standards, resolved through the Config Reg
- [Rules](rules/README.md) — Repository-wide rules, independent of any one technology
- [Architecture decision records](adr/README.md) — What was decided, and what was rejected
- [Reference](reference/README.md) — Material with no other home
- [CI Pipelines Architecture](ci-pipelines/architecture.md) — Rule ids, severities, output formats, exit codes
- [Rule Reference](ci-pipelines/rule-reference.md) — Every `KW-*` id in one table
- [Workflow Runbook](ci-pipelines/workflows-runbook.md) — Copy-ready GitHub Actions gates
- [Installing Kyber-Weave](install.md) — Binary distribution and installation steps
- [Configuration](configuration.md) — Customizing the ontology and tool settings
- [Distribution and release flow](distribution.md) — Maintainer-facing release process

