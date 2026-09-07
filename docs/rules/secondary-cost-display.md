---
id: rules/secondary-cost-display
title: Secondary cost display and token-first hierarchy standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-09-06
---

# Secondary cost display and token-first hierarchy standard

This rule mandates that dollar spend and financial cost figures are strictly presented as secondary,
derived metrics alongside primary engineering dimensions (tokens, latencies, cache stability, and error rates).

---

## Mandate

Financial cost and dollar spend must never serve as the top-level navigation item, the default
sort order, or the primary landing metric of any diagnostic workflow, scorecard, or context inspector.

Cost figures must appear only as secondary, derived indicators adjacent to underlying engineering
metrics (fresh input tokens, cache read tokens, execution latency, and turn count).

---

## Motivation

AI developer tooling that prioritizes financial cost above all else encourages pathological optimization:
1. **Focus on Trailing Artifacts**: Dollar spend is a trailing pricing artifact that fluctuates with
   vendor pricing revisions, volume discounts, model promotions, and enterprise contracts. Optimizing
   primarily for cost leads to brittle, short-term hacks.
2. **Obscuring Root Engineering Bottlenecks**: The genuine technical constraints of LLM-based agents
   are context window exhaustion, cache invalidation churn, slow tool latency, and prompt drift. A
   system organized around dollar spend diverts attention from structural prompt defects.
3. **Misleading Equivalences**: Cheap, poorly performing models can appear superior to high-capability
   models in cost-first rankings, even when the cheap models incur 10× more retry turns, produce broken
   code, and degrade developer productivity.

Treating cost as a derived secondary metric keeps engineering focus squarely on context hygiene and
cache predictability while providing transparent financial accounting.

---

## Requirements

### R1: Spend as a Derived Value
Cost must always be computed from verified token counts, call frequencies, and published pricing
rate tables. Cost is never a canonical input or primary grouping key.

### R2: Non-Primary Navigation and Sorting
- In diagnostic navigation hierarchies, the landing views must organize by context pressure, cache
  invalidation frequency, and finding severity, not total spend.
- In lists and tables of sessions, runs, or tools, the default sort order must reflect engineering
  impact (token volume, recoverable waste, latency, or error count). Cost may be provided as an
  optional user-selected secondary sort.

### R3: Pricing Basis Transparency
Every displayed cost figure must explicitly state its calculation basis:
- Published rate table version.
- Custom enterprise override.
- Harness-reported expenditure.
Where rates are unavailable or models are flat-rate/free, the UI must display `unpriced` or `flat-rate`,
never `$0.00`.

### R4: Mechanical Boundary Enforcement
Cost types and financial data structures must remain behind adapter boundaries and never leak into
core diagnostic domain entities (`Run`, `AgentExecution`, `Finding`, or scorecard vectors).

---

## Exceptions

Dedicated billing views, monthly budget monitoring dashboards, and procurement export tools whose
explicit purpose is financial accounting are permitted to use dollar spend as their primary metric.

---

## Verification & Conformance

- Boundary tests (`dash/kyber/tools/boundary.test.ts`) assert that cost-shaped types do not appear
  in canonical diagnostic entities.
- UI component tests (`dash/src/components/kyber/Scorecard.test.tsx`) assert that financial figures
  never occupy primary card headers or default table sorting properties.
