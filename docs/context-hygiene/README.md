---
id: context-hygiene-index
title: Context Hygiene — Governed Agent Instruction Surfaces
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-15
---

# Context Hygiene — Governed Agent Instruction Surfaces

> **Keep agent context clean, secure, and deterministic across coding harnesses.**

AI coding agents are only as capable as the instructions and skills loaded into their context window. When agent prompts, instruction files, and tool definitions grow unchecked, context rot sets in: token budgets inflate, agents hallucinate across overlapping skills, and unvetted instruction surfaces introduce critical security vectors.

**Context Hygiene** is Kyber-Weave's governance engine for the artifacts that shape an agent's reasoning surface: Agent Skills (`SKILL.md`), harness prompt files (`AGENTS.md`, `CLAUDE.md`, `.cursorrules`, etc.), and instruction templates.

---

## Why Context Hygiene?

In modern agentic development workflows, engineering teams quickly encounter three critical breakdown points:

### 1. Instruction Drift and Multi-Harness Divergence
When developers work across multiple harnesses (Cursor, Claude Code, GitHub Copilot, Cline, Antigravity), prompt definitions and persona instructions diverge rapidly. A rule updated in one environment is forgotten in another, leading to inconsistent code quality and conflicting architectural decisions across team members.

### 2. Ambiguous Routing and Skill Collisions
As teams accumulate skills, description boundaries blur. When an agent cannot distinguish whether to invoke `csharp-dev`, `dal-dev`, or `code-review` for a database refactor, it either fails to trigger the appropriate tool or loads multiple unneeded instruction blocks into context—wasting expensive token headroom and introducing erratic behavior.

### 3. Unchecked Security Surface (Prompt Injection & Execution Hazards)
Skills contain instructions, regexes, scripts, and MCP references. An unmonitored skill can inadvertently instruct agents to execute destructive shell commands, exfiltrate sensitive repository data, or bypass architectural boundaries.

---

## Core Capabilities

| Capability | How It Solves the Problem | Command |
|---|---|---|
| **Spec Conformance Gating** | Validates `SKILL.md` frontmatter, file link references, allowed tools, and required instruction sections against deterministic schemas. | `kyber-weave skill validate` |
| **Routing Readiness Scoring** | Evaluates skill descriptions against lexical and semantic triggers to prevent skill collisions and guarantee high-confidence agent routing. | `kyber-weave skill route` |
| **Multi-Harness Parity** | Lints agent instruction files across 6+ IDE harnesses to guarantee behavioral consistency and identify stale instructions. | `kyber-weave agent validate` |
| **Instruction-Surface Scanning** | Scans skills and agent definitions for prompt injection patterns, unsafe shell commands, hardcoded secrets, and destructive workflows. | `kyber-weave skill scan` · `agent scan` |

---

## Jump In

Explore the in-depth documentation and governance specifications for Context Hygiene:

* **[Skill Governance](skills.md)** — Schema specification, frontmatter standards, lexical/semantic routing validation, and simulation.
* **[Agent Harness Governance](agents.md)** — Cross-harness parity checks, instruction synchronizers, and drift detection across 6 supported harnesses.
* **[Instruction-Surface Security Scanning](security-scanning.md)** — Detection rules for prompt injection, sensitive data leakage, and untrusted tool invocation.
* **[Enterprise ALM Governance Playbook](../alm-governance-playbook.md)** — Operating skill and prompt governance at scale in enterprise CI/CD pipelines.
