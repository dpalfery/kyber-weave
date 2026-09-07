---
id: dash-index
title: KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-05
---

# KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows

> **Observe, analyze, and tune agentic context windows with local OpenTelemetry visualization powered by .NET Aspire and multi-surface UI delivery.**

Modern AI coding agents operate through complex, multi-turn execution loops: invoking local tools, delegating tasks across specialized subagents, and ingesting repository context. Without dedicated observability, agent workflows remain opaque black boxes: developers cannot see where token budgets are wasted, which tool calls introduce latency bottlenecks, or when context windows approach saturation thresholds.

**KyberDash** delivers observability across four local execution surfaces (Terminal TUI, Web Dashboard, Electron Desktop, and Windows Menubar/Tauri). Its canonical `canon.db` store accepts OTLP traces and logs plus supported local session sources. Session APIs read that store only; the retired Python pipeline's `sessions.db` is not a production path.

---

## Why KyberDash?

Optimizing AI agent performance and context quality requires moving from intuition to empirical telemetry. Teams face key observability hurdles:

### 1. The Token "Black Box" & Context Window Bloat
Agents frequently consume hundreds of thousands of tokens per session without clear visibility into what proportion is spent on system prompts, skill instructions, tool parameters, or conversation history. KyberDash provides granular, turn-by-turn token breakdowns to identify bloat immediately.

### 2. Unseen Latency Bottlenecks & Multi-Agent Handoff Failures
In complex workflows involving orchestrators, subagents, and MCP servers, a slow tool execution or a circular subagent delegation loop can stall development. KyberDash visualizes execution timelines and distributed call trees to pinpoint bottlenecks and failed handoffs across derived runs and parent/child executions.

### 3. Tuning Instructions Without Empirical Data
Refining agent instructions and skill definitions has historically been guesswork. With KyberDash, developers can measure the direct impact of prompt changes on token efficiency, tool call accuracy, and execution latency across iterative runs, backed by calibrated predictions and phase-aligned comparisons.

### 4. Flexible UI Delivery Across 4 Local Surfaces
Observability belongs where the developer already works:
- **Terminal TUI (`dash/src/dashboard.tsx`)**: Instant terminal dashboard with keyboard navigation for command-line workflows.
- **Web Dashboard (`dash/dash/`)**: Standalone browser application for progressive-disclosure diagnostics, run comparison, and context inspection.
- **Electron Desktop (`dash/app/`)**: Dedicated desktop window with persistent views and local IPC.
- **Windows Menubar / Tray (`dash/windows/`)**: Lightweight background tray companion for continuous spend monitoring.

---

## Key Capabilities

| Capability | How It Solves the Problem |
|---|---|
| **6-Level Diagnostic Spine** | Progressive-disclosure navigation across All Harnesses (Attention) → Harness → Run → AgentExecution → Turn → ContextItem. Evaluated across 6 independent dimension vectors without composite scoring. |
| **Context Inspector & Copy-Out** | Reads unclipped plain-text context blocks subdivided by part, with whole-turn and per-block clipboard export. Governed by a default 14-day rolling retention window and CLI purge command (`kyber purge-content`). |
| **Telemetry-Grounded Finding Engine** | Detects structural context defects (duplicate schemas, prefix instability, compaction bloat) with ≥2 linked evidence rows, outcome-risk caveats, and waste ranking (`waste × risk × confidence`). Enforces relocation over deletion. |
| **Phase-Aligned Run Comparison** | Compares runs of a task family aligned by logical phase rather than turn index. Enforces statistical sufficiency ($n \ge 5$ completed pairs without regression) and tracks prediction calibration curves. |
| **Opt-In LLM Review Seam** | Provides on-demand prompt analysis from local (Ollama/vLLM) or cloud models. Requires explicit per-invocation user action, enforces relocation constraints, and isolates model text from canonical finding tables. |
| **ASAD Context Dashboard** | The `Context` page renders the Agent Session Analysis Dashboard from canonical session payloads: overview, per-turn spend, context composition, tool/schema cost, execution timeline, cost/token accounting, caveats, and a content-inspector drawer. |
| **Canonical-Store Projection** | `~/.kyberdash/canon.db` stores rebuildable derived session, run, execution, and finding payloads over canonical records. Rebuildable via `kyber build`. |
| **Multi-Signal Enrichment** | OTLP logs enrich their correlated span-shaped record rather than creating a second record. When a turn has both OTLP and file evidence, OTLP counters win and file content is used only if OTLP has no parts. |
| **Tool & Subagent Trace Visualizer** | Displays deep hierarchical call graphs of MCP tool executions, subagent invocations, and return payloads. |

---

## Documentation Roadmap

The following technical documentation pages are published in this directory:

* **Operational Runbook ([`runbook.md`](runbook.md))** — Local development, execution runners, CLI maintenance commands (`kyber build`, `purge-content`), and test suites across all 4 surfaces.
* **Architecture ([`architecture.md`](architecture.md))** — Canonical-store ingest, 6-level diagnostic spine, pure signals engine, finding contracts, and REST API contract.
* **Telemetry Inventory ([`telemetry-inventory.md`](telemetry-inventory.md))** — Verified per-harness collection and measurability outcomes, including owner-controlled runtime gates.
* **ADR 0006: Soft Fork & Merge Zone ([`../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md`](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md))** — Core architectural decision for vendored subtree and embedded OTLP receiver.
* **ADR 0007: Agent Session Analysis Integration ([`../adr/0007-kyberdash-agent-session-analysis-integration.md`](../adr/0007-kyberdash-agent-session-analysis-integration.md))** — Single coherent session view in Context, 5-tab topology, and dual-database bridge.
* **ADR 0008: Single Canonical Store ([`../adr/0008-kyberdash-single-canonical-store.md`](../adr/0008-kyberdash-single-canonical-store.md))** — Retires the Python-store fallback.
* **ADR 0009: Multi-Signal Ingestion ([`../adr/0009-multi-signal-ingestion-span-shaped-record.md`](../adr/0009-multi-signal-ingestion-span-shaped-record.md))** — Establishes log enrichment, non-model quarantine, and source precedence.
* **ADR 0011: ASAD-Only Context View ([`../adr/0011-asad-only-context-view-and-payload-contract.md`](../adr/0011-asad-only-context-view-and-payload-contract.md))** — Context renders only the ASAD dashboard from the canonical session payload.
* **ADR 0012: Progressive Disclosure 6-Level Diagnostic Spine ([`../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md`](../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md))** — Six-level hierarchy, first-class runs, and independent dimension vectors.
* **ADR 0013: Finding Contracts & Waste Ranking ([`../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md`](../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md))** — Telemetry-grounded findings with evidence rows, waste ranking, and relocation discipline.
* **ADR 0014: Unclipped Context & Retention Protocol ([`../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md`](../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md))** — Context Inspector, whole-turn copy out, and 14-day rolling retention.
* **ADR 0015: Opt-In LLM Review Seam ([`../adr/0015-opt-in-llm-context-review-seam.md`](../adr/0015-opt-in-llm-context-review-seam.md))** — On-demand LLM review seam with finding table isolation.
* **Relocation Over Context Deletion Standard ([`../rules/relocation-over-deletion.md`](../rules/relocation-over-deletion.md))** — Mandates that diagnostic recommendations prioritize moving and progressive disclosure over context removal.
* **Honest Unobservability Standard ([`../rules/honest-unobservability.md`](../rules/honest-unobservability.md))** — Missing telemetry and coverage gaps are explicit and never coerced to zero.
* **Secondary Cost Display Standard ([`../rules/secondary-cost-display.md`](../rules/secondary-cost-display.md))** — Financial spend is strictly a derived secondary metric behind token and latency health.
* **Ban on Composite Efficiency Scores ([`../rules/composite-efficiency-ban.md`](../rules/composite-efficiency-ban.md))** — Prohibits computing or rendering single-scalar composite efficiency scores across multidimensional telemetry.
