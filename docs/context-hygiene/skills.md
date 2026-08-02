---
id: context-hygiene/skills
title: Skill governance
doc-type: architecture
status: current
component: ContextHygiene
source-root: src/KyberWeave.Core/Skills
owner: dpalfery
last-reviewed: 2026-08-01
code-refs:
  - SkillLoader
  - RoutingLinter
  - DescriptionScorer
  - LexicalRoutingStrategy
---

# Skill governance

A skill is a `SKILL.md` file that an orchestrator may load into an agent's context. That
makes it a supply-chain artifact: it is code that executes as instructions. Skill
governance answers to the **Agent Skills open format spec**, and adds the checks the spec
cannot make — whether a skill will actually be selected, and whether it is safe to load.

> Skill validate / lint / scan / route / catalog / pack / new originated as
> [SkillForge](https://github.com/bonaniibm/SkillForge), MIT-licensed. Absorbed here and
> maintained in this repository; there is no upstream sync. See
> [NOTICE](../../NOTICE).

## Commands

| Command | What it answers | Gate |
|---|---|---|
| `skill validate` | Is this spec-conformant? | fails on **error** |
| `skill lint` | Will it route, and does it collide with another skill? | fails on **error** |
| `skill scan` | Is it safe to load into context? | fails on **critical** (configurable) |
| `skill route` | Which skill fires for this prompt? | fails below `--min-accuracy` |
| `skill catalog` | Inventory with version / owner / score | — |
| `skill pack` | Bundle into a Copilot Studio–compatible `.zip` | — |
| `skill new` | Scaffold a spec-correct skill | — |

## Spec conformance — `KW-SKILL-SPEC-001`…`-012`

Structural checks against the open format: required frontmatter, name and description
presence and shape, body limits, and resource references that must resolve. Most are
errors; a few are warnings or informational, because the spec leaves some choices to the
author.

## Routing readiness — `KW-SKILL-LINT-001`…`-011`

The distinctive half. A spec-valid skill that never fires is worthless, and the spec has
nothing to say about that.

`DescriptionScorer` produces an auditable 0–100 routing score across five dimensions:

```
password-reset — routing score 100/100
  Dimension          Score   Detail
  Trigger clause     25/25   States when to use the skill.
  Negative boundary  20/20   States when NOT to use the skill — prevents over-firing.
  Specific opening   15/15   Opens with a concrete action verb.
  Trigger keywords   20/20   25 distinct content terms.
  Length budget      20/20   289 chars — within a healthy routing budget.
```

The score is a breakdown rather than a verdict, so an author can see which dimension cost
them and fix that one. `KW-SKILL-LINT-010` — a name collision — is the only error in this
tier; two skills cannot share a name and both be selectable.

## Routing simulation — `skill route`

Turns "which skill fires?" from a guess into a test:

```
✔  I'm locked out of my laptop and forgot my password   →  password-reset
✔  Checkout is throwing 500 errors for a lot of users    →  incident-triage
✔  What's the weather in Kolkata tomorrow?               →  (no fire)
Routing accuracy: 100% (7/7), threshold 85%.
```

`LexicalRoutingStrategy` is deterministic and offline — cosine similarity over stemmed,
stop-word-filtered term vectors — so it runs in CI with no API key and gives the same
answer twice. `IRoutingStrategy` is the seam for embedding or LLM-judge strategies later.

**It approximates, it does not replicate.** A real orchestrator sees other skills, tool
definitions, and conversation history. Treat `skill route` as a regression test against
your own eval set, not a guarantee.

## Trust surface — `skill scan`

See [security scanning](security-scanning.md); `skill scan` and `agent scan` are the same
engine over different artifacts.

## Kyber-Weave ships a skill of its own

`kyber-weave-docs`, at `.apm/skills/kyber-weave-docs/`, is the authoring skill
[`docs init`](../docgraph/onboarding.md) deploys. It is governed by the gates on this
page like any other — `skill validate` and `skill scan` clean, routing score 100/100 —
which is the only honest way to ship a skill from a skill-governance tool.

It is distributed as an [APM](https://microsoft.github.io/apm) package, so the harness
layout for each runtime is APM's problem rather than a second copy of that mapping here.

## Related

- [Agent harness governance](agents.md) — the other half of ContextHygiene
- [Instruction-surface scanning](security-scanning.md) — the shared security engine
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-*` id
