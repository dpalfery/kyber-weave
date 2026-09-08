---
id: context-hygiene/skills
title: Skill governance
doc-type: architecture
status: current
component: ContextHygiene
source-root: src/KyberWeave.Core/Skills
owner: dpalfery
last-reviewed: 2026-08-30
code-refs:
  - SkillLoader
  - RoutingLinter
  - DescriptionScorer
  - LexicalRoutingStrategy
  - SkillReviewExchange
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
| `skill review export` | Export candidate descriptions for semantic review | — |
| `skill review import` | Import and validate review verdicts | fails on **error** (`KW-SKILL-REVIEW-001`) |
| `skill catalog` | Inventory with version / owner / score | — |
| `skill pack` | Bundle into a Copilot Studio–compatible `.zip` | — |
| `skill new` | Scaffold a spec-correct skill | — |

Agent candidates use `{Harness}:{RoleName}` as `id` so the same role in two harnesses
stays distinct. Import requires exactly one verdict for every exported candidate.

## Spec conformance — `KW-SKILL-SPEC-001`…`-012`

Structural checks against the open format: required frontmatter, name and description
presence and shape, body limits, and resource references that must resolve. Most are
errors; a few are warnings or informational, because the spec leaves some choices to the
author.

## Routing readiness — `KW-SKILL-LINT-001`…`-011`

The distinctive half. A spec-valid skill that never fires is worthless, and the spec has
nothing to say about that.

### Trigger framing vs. action summaries

In multi-agent systems and LLM tool selection, descriptions are **activation triggers**,
not passive documentation summaries. When an orchestrator or LLM selects which skill to
invoke, it evaluates caller intent against the trigger condition declared in the description.

Descriptions that summarize internal task actions (for example, `"Generates SQL queries,
validates schema syntax, and connects to Postgres database"`) describe *what the skill does
internally* rather than *when the orchestrator should activate it*. This leads to ambiguous
routing and collisions.

Effective skill descriptions lead with **explicit trigger framing** and define **negative
boundaries**:
- **Trigger phrasing**: Open with `"Use when..."`, `"Use for..."`, `"Invoke when..."`,
  `"Apply when..."`, or `"Trigger when..."` (e.g. `"Use when the user requests database
  query inspection, syntax validation, or PostgreSQL query tuning."`).
- **Negative boundary**: State explicitly when *not* to use the skill (e.g. `"Do NOT use
  for database schema migrations or user permission management."`).

### DescriptionScorer rubric

`DescriptionScorer` operationalizes this guidance into an auditable 0–100 score across
five dimensions:

```text
password-reset — routing score 100/100
  Dimension          Score   Detail
  Trigger clause     35/35   States when to use the skill.
  Negative boundary  20/20   States when NOT to use the skill — prevents over-firing.
  Specific opening   15/15   Opens with a concrete trigger or action verb.
  Trigger keywords   15/15   25 distinct content terms.
  Length budget      15/15   289 chars — within a healthy routing budget.
```

- **Trigger clause (35 pts)**: Highest-weighted dimension. Requires explicit trigger
  conditions (`"Use when..."`, `"Use for..."`, `"Invoke when..."`). Descriptions leading
  solely with action verbs without a trigger clause score 0.
- **Negative boundary (20 pts)**: Explicit exclusion boundaries (`"Do NOT use for..."`,
  `"Avoid using for..."`, `"Excludes..."`) that prevent over-firing.
- **Specific opening (15 pts)**: Concrete opening rather than vague filler (`"helps with..."`,
  `"assists with..."`).
- **Trigger keywords (15 pts)**: Specificity and lexical richness across distinct domain terms.
- **Length budget (15 pts)**: Conciseness within a healthy 40–500 character routing budget.

### Diagnostic rules

- `KW-SKILL-LINT-001` (Warning): Description routing score falls below threshold (`MinDescriptionScore: 70`).
- `KW-SKILL-LINT-007` (Warning): Description is an action summary rather than a trigger specification.
- `KW-SKILL-LINT-008` (Warning): Description contains excessive filler phrases (`"This skill is designed to..."`, `"allows the user to..."`).
- `KW-SKILL-LINT-010` (Error): **Name collision** — two skills cannot share a name and both be selectable.
- `KW-SKILL-LINT-011` (Warning): Description overlap between two skills.
- `KW-SKILL-REVIEW-001` (Error): Review verdict payload is malformed or invalid during `skill review import`.

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

## Deployment control plane with Kyber-Squad

While ContextHygiene validates individual skill specifications, scores routing readiness,
and scans instruction surfaces, **[Kyber-Squad](../kyber-squad/architecture.md)** acts as the
unified multi-harness deployment control plane. Kyber-Squad maintains 24 canonical skills
(alongside 21 canonical agent roles and lowering rules) under `products/kyber-squad/` and
manages their transactional deployment, drift tracking, and lifecycle. Its catalog declares nine
harness targets. Five renderers are implemented and registered (`copilot`, `cursor`, `claude`,
`codex`, and `antigravity`); `opencode`, `kilo`, `warp`, and `factory` fail renderer-coverage
preflight until their renderers are implemented.

Every raw `SKILL.md` except the explicitly evolved `product-owner` and `bug-crusher` matches the
Hotshot golden bytes. Canonical storage and recursive Squad packages preserve 64 supplemental
resources, for 88 skill-tree files, and renderers project them beside the rendered principal, so
deployed skill references resolve. The tracked root `.github/` self-deployment predates resource
delivery; surplus packaged content remains until the
[content-preserving migration todo](../todo/migrate-skill-resources-into-standards.md) is accepted.

The agent and skill namespaces intersect at seven names, all distinct-body collisions. No product
identity is shared. Fallback rendering keeps each colliding skill at `<name>` and lowers its agent
to `role-<name>`; agents with unoccupied skill identities, including `conductor`, lower to
same-name role skills.

## Related

- [Agent harness governance](agents.md) — the other half of ContextHygiene
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — multi-harness deployment control plane
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — deploying canonical skills and agent squads
- [Instruction-surface scanning](security-scanning.md) — the shared security engine
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-*` id
