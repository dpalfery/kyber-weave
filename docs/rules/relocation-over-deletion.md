---
id: rules/relocation-over-deletion
title: Relocation over context deletion standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-09-06
---

# Relocation over context deletion standard

This rule mandates that all context optimization recommendations, automated diagnostic advice,
and prompt engineering tooling prioritize relocating, reordering, and progressively disclosing
context over deleting or pruning it.

---

## Mandate

Automated diagnostic tools, agent evaluators, and LLM review systems must recommend moving
context (stabilizing cache breakpoints, progressive disclosure, dynamic on-demand tool loading,
or subagent segregation) in preference to deleting context.

Pruning or removing prompt text, instructions, or tool definitions must never be recommended
based solely on absence of invocation during observed runs. Deletion recommendations are permitted
only when byte-identical redundancy or verified duplicate compaction waste is deterministically proven.

---

## Motivation

AI coding agents depend heavily on latent instructions, safety constraints, negative boundaries,
and error-recovery guidelines. A prompt instruction or tool schema that was never invoked during
a specific trace is not necessarily wasteful:
- It may provide guardrails that prevented erroneous actions (negative constraints).
- It may represent latent capabilities required for alternative branches, edge cases, or runtime errors.
- It may serve as conditioning context that establishes task framing and persona alignment.

Categorizing uninvoked context as "waste" and recommending its deletion creates severe operational risks:
agents lose resilience against edge cases, fail at error recovery, or violate safety protocols.

Conversely, **context relocation** is non-destructive and reversible:
1. **Cache Breakpoint Stabilization**: Moving frequently modified context (e.g. conversational history)
   after static instructions allows model providers to cache the static prefix, dramatically reducing
   token costs and latency without deleting any instructions.
2. **Progressive Disclosure & On-Demand Tools**: Deferring detailed tool schemas or skill documentation
   behind searchable tool directories or dynamic lookups keeps initial turns lean while keeping the
   full capability available when requested.
3. **Subagent Segregation**: Delegating specialized tasks to child agents with focused prompts preserves
   root agent context hygiene without eliminating capabilities.

---

## Requirements

### R1: Preference for Reversible Architectural Changes
Every diagnostic recommendation engine must propose non-destructive structural improvements
before any context reduction:
- Cache-friendly prefix reordering.
- Progressive disclosure and dynamic schema registration.
- Subagent delegation and task isolation.
- Context compaction and summarization at turn boundaries.

### R2: Empirical Classification Without Value Judgements
Context blocks must be categorized strictly by empirical observability of use:
`evidence of use: strong / weak / none / unobserved`.
Diagnostic tools must never apply normative or value-laden labels such as "avoidable",
"wasteful", or "useless" based solely on absence of activation.

### R3: Mandatory Outcome-Risk Disclosure
Whenever a recommendation advises trimming, pruning, or condensing any context item, it must
include an explicit, non-collapsible **Outcome-Risk Caveat** detailing how the change could
negatively affect execution fidelity, safety checks, or downstream subagent performance.

### R4: Deterministic Linting of Advice
Automated recommendation generators, diagnostic rules, and LLM review prompts must pass
deterministic test gates ensuring they do not emit ungrounded context deletion suggestions.
Prompts and templates instructing models to "find prompt waste to delete" fail repository review.

---

## Exceptions

Deletion or removal recommendations are valid only under the following strictly verified conditions:
1. **Byte-Identical Duplicate Schemas**: An identical MCP tool or function schema is registered
   multiple times within the same turn phase.
2. **Post-Compaction Residue**: Detailed turn history that was already summarized in an earlier
   compaction block is redundantly re-injected into subsequent turns.
3. **Quarantined Non-Model Noise**: Telemetry records that represent HTTP health checks or non-model
   spans incorrectly attributed to context windows.

---

## Verification & Conformance

- Diagnostic engines in `dash/kyber/analysis/findings.ts` and `review.ts` are verified via unit tests
  (`findings.test.ts`, `review.test.ts`) to ensure all generated recommendations adhere to relocation
  principles.
- CI static analysis and linting guard against prompt advice that suggests deleting uninvoked capabilities.
