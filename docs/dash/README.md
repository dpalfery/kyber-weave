---
id: dash-index
title: KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-08-15
---

# KyberDash — Interactive Telemetry & Context Tuning for Agentic Workflows

> **Observe, analyze, and tune agentic context windows with local OpenTelemetry visualization powered by .NET Aspire.**

Modern AI coding agents operate through complex, multi-turn execution loops: invoking local tools, delegating tasks across specialized subagents, and ingesting repository context. Without dedicated observability, agent workflows remain opaque black boxes: developers cannot see where token budgets are wasted, which tool calls introduce latency bottlenecks, or when context windows approach saturation thresholds.

**KyberDash** is a dedicated local web dashboard that ingests OpenTelemetry (OTEL) traces, metrics, and logs streamed from the .NET Aspire dashboard, translating raw telemetry into actionable context tuning insights for agentic workflows.

---

## Why KyberDash?

Optimizing AI agent performance and context quality requires moving from intuition to empirical telemetry. Teams face three key observability hurdles:

### 1. The Token "Black Box" & Context Window Bloat
Agents frequently consume hundreds of thousands of tokens per session without clear visibility into what proportion is spent on system prompts, skill instructions, tool parameters, or conversation history. KyberDash provides granular, turn-by-turn token breakdowns to identify bloat immediately.

### 2. Unseen Latency Bottlenecks & Multi-Agent Handoff Failures
In complex workflows involving orchestrators, subagents, and MCP servers, a slow tool execution or a circular subagent delegation loop can stall development. KyberDash visualizes execution timelines and distributed call trees to pinpoint bottlenecks and failed handoffs.

### 3. Tuning Instructions Without Empirical Data
Refining agent instructions and skill definitions has historically been guesswork. With KyberDash, developers can measure the direct impact of prompt changes on token efficiency, tool call accuracy, and execution latency across iterative runs.

---

## Key Capabilities (Planned)

| Capability | How It Solves the Problem |
|---|---|
| **.NET Aspire OTEL Ingestion** | Connects seamlessly to .NET Aspire's local OpenTelemetry collector to ingest traces, metrics, and structured logs. |
| **Interactive Context Heatmap** | Visualizes context window headroom, token accumulation rates, and context pressure across conversation turns. |
| **Tool & Subagent Trace Visualizer** | Displays deep hierarchical call graphs of MCP tool executions, subagent invocations, and return payloads. |
| **Context Tuning Feedback Loop** | Provides empirical metrics to benchmark prompt reductions and skill routing optimizations against historical sessions. |

---

## Documentation Roadmap

KyberDash is an upcoming feature currently under active architectural design. As development progresses, the following technical documentation pages will be published in this directory:

* **Architecture (`architecture.md`)** — Deep dive into the telemetry ingest pipeline, local web architecture, and Aspire OTEL integration.
* **Onboarding & Setup (`onboarding.md`)** — Quickstart guide to configuring .NET Aspire and launching the local KyberDash UI.
* **Context Tuning Playbook (`tuning-playbook.md`)** — Practical guidelines for diagnosing context bloat and optimizing agent workflows using telemetry data.
