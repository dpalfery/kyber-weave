---
id: kyber-squad/arbiter
title: Kyber Arbiter — three-step decision gates
doc-type: architecture
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-02
---

# Kyber Arbiter — three-step decision gates

Kyber Arbiter is the rule engine that answers the Kyber-Squad's in-flight decisions at the
points a harness can enforce: before a delegation, after it returns, before a review lens or
refutation spawn, and when a gate's applicability is decided. The specification is approved
and archived; implementation is delivered in three phase pull requests (D28, D32, D34). This
page is the canonical harvest of the durable design. The full requirements, design, and task
list remain in the [archived specification](../archive/specs/kyber-arbiter/README.md).

## Status

- **Specification:** approved 2026-10-03, archived 2026-10-10.
- **Implementation:** not yet delivered. Phase 1 carries the engine, evaluator, facts, plan
  parser, ledger, configuration, all 18 rules, the CLI commands, the first four harness
  adapters, distribution, and Squad wiring. Phase 2 adds owned blocks and the Pi, Codex, and
  Cursor targets. Phase 3 adds Kilo, Antigravity, Factory, Devin, Warp, ZCode, `serve`, and
  the MCP fallback.
- **Early archive:** the specification was archived before implementation per Q16 so trunk
  carries the governed artifact. The design below is the lasting record; each phase updates
  it with what it delivers.

## Why it exists

The squad's in-flight decisions are currently made by the model being governed:

- **Delegation scope.** Only the conductor's own instructions say a delegation must stay
  inside the approved plan task. Nothing compares the delegation with that task.
- **Ready queue.** Dependencies and file-scope overlap are held only by the conductor's
  attention.
- **Delegation rosters.** Claude ignores `Agent(roster)` for nested sub-agents, and
  Antigravity cannot enforce a roster at all; both are recorded as
  `permission-not-expressible` degradations.
- **Review fan-out.** Every seat decides its own applicability, and every `major`-or-above
  finding pays for a refutation spawn even when its evidence is sufficient.

The Arbiter moves those decisions to deterministic, harness-invoked checks while keeping the
final judgement out of the gated agent's hands.

## Three-step evaluation

| Step | Answers | Surface |
|---|---|---|
| 0 | Exact facts: paths, task ids, dependencies, rosters, string matches | Plain code in the rule engine |
| 1 | Judgements about meaning | One batched `systemone` call to TypeSafe JEV or a local Ollama model (`nimble` or `tev1`) |
| 2 | Anything step 1 flags or is unsure about | A reasoning agent: `architect` for delegations, the refutation lens for review |

One confident red flag is enough to escalate; signals are not averaged. Step 0 answers first,
and no step-1 answer can overturn a plain-code result. A step-1 answer counts only at or above
its rule's confidence threshold. `undecidable` never allows on conductor and investigate
triggers; on review spawn triggers it maps to `allow`, so the review proceeds.

## Surfaces

- **`kyber-weave-arbiter hook --harness <token> [--caller <agent>]`.** The harness hook
  adapter. It handles dispatch gating and the planning-path Read guard, writes only the
  harness's decision document on stdout, and exits 0.
- **`kyber-weave-arbiter serve`.** The Phase 3 stdio MCP fallback for harnesses whose hooks do
  not fire inside sub-agents. It exposes `arbiter_evaluate`, `arbiter_rules`, and
  `arbiter_status`.
- **`kyber-weave arbiter validate | rules | plan | eval | audit | setup | status | doctor`.**
  The human CLI. `eval` is a dry run and never appends to the ledger.
- **The engine.** `src/KyberWeave.Core/Arbiter/`; the CLI and the Arbiter binary are thin
  hosts over it. A separate binary exists because its stdout is a protocol, so it takes no
  Spectre.Console.

## Decision points and rules

The 18 shipped rules use permanent `KW-ARB-*` ids and cover these triggers:

| Trigger family | Triggers | Effects | `undecidable` |
|---|---|---|---|
| Conductor | `delegate`, `delegate.returned`, `delegate.planner`, `delegate.planner.returned` | `allow`, `escalate` | `escalate` |
| Investigate | `investigate`, `investigate.returned` | `allow`, `escalate` | `escalate` |
| Review spawn | `lens.spawn` | `allow`, `skip` | `allow` |
| Review spawn | `refute.spawn` | `allow`, `verify` | `allow` |
| Review return | `lens.returned` | `allow`, `annotate` | `allow` |
| Gate | `gate.select` | `applies`, `not-applicable` | `applies` |

A rule declares `id`, `trigger`, `question`, `answers`, and `effects`, plus either a step-0
`decide` clause or a step-1 `ask` clause. The step-0 predicate set is closed: `all`, `any`,
`not`, `exists`, `equals`, `in`, `matches`, `subset-of`, `intersects`, `count`. A host may
override only `enabled`, the confidence threshold, and effects on a shipped rule; any other
field raises `KW-ARB-CONFIG-005`. Host rules use the full shape under ids that do not start
with `KW-ARB-`. The complete catalogue is in the
[archived design](../archive/specs/kyber-arbiter/design.md#2-rule-catalogue).

## Configuration

The `arbiter:` section in `.kyber-weave/kyber-weave.yml` is disabled by default and has three
parts:

```yaml
arbiter:
  enabled: false
  provider:
    kind: none                         # none | systemone
    endpoint: https://api.typesafe.ai/v1
    model: jev-1.13.0
    timeout-ms: 3000
  rules:
    - id: KW-ARB-SCOPE-002
      confidence-at-least: 0.85
```

- **Provider `none`** (the default) switches model rules off and reports them; steps 0 and 2
  still run. No repository content leaves the machine until a provider is configured.
- **The key** comes from `TYPESAFE_API_KEY` only for the TypeSafe origin, then from the OS
  credential store for the endpoint origin. It never appears in configuration, argv, a log,
  or an exception.
- **The user override** at `~/.config/kyber-weave/arbiter.yml` may hold `provider:` and
  nothing else.
- **Diagnostics** are permanent: `KW-ARB-CONFIG-001` to `-010`, `KW-ARB-KEY-001`,
  `KW-ARB-LOG-001`, `KW-ARB-BIN-001`, `KW-ARB-GUARD-001`, `KW-ARB-PARSE-001` and `-002`,
  `KW-ARB-AUDIT-001` to `-004`, and `KW-ARB-HOOK-001`.

## Ledger, audit, and escalation

Only `hook` and `serve` append to `artifacts/arbiter/` (`decisions.jsonl` and the in-flight
ledger). The CLI writes neither; `audit` reads them and reports gaps. A hook killed during
evaluation leaves an event with no decision, which `audit` reports as `KW-ARB-AUDIT-001`.

A non-allow outcome on a conductor or investigate trigger becomes the block reason, using the
`STATUS: ARBITER_ESCALATION` envelope with fixed field order. `REPEAT` counts earlier
escalations for the same plan file, plan digest, and task; `NEXT` tells the conductor to
dispatch `architect` once, then stop. Review outcomes use `ARBITER_SKIP`,
`ARBITER_VERIFIED`, and `ARBITER_ANNOTATION` notes instead. The Arbiter never drops a
finding.

## Harness support

| Phase | Harnesses | Shape |
|---|---|---|
| 1 | Claude, Copilot in VS Code, Copilot CLI, OpenCode | Agent frontmatter or the harness's own hook file; no shared JSON |
| 2 | Pi, Codex, Cursor | Owned blocks in shared JSON hook files, receipt v3, block drift |
| 3 | Kilo, Antigravity, Factory, Devin, Warp, ZCode | Shared-file adapters and the MCP fallback |

Hooks fail closed: any internal error produces that harness's block, carrying
`KW-ARB-HOOK-001`. Events with no sub-agent target and reads that name no protected path pass
through untouched. Where the harness has a hook timeout field, the per-hook timeout is
`provider.timeout-ms` plus 2 seconds.

## Delivery

Each phase ships as its own pull request with its own verification and review. The task list
covers all three phases, and one closeout follows Phase 3. This early archive records the
design and the delivery contract; the phase pull requests deliver it and update this page
with the identifiers and behaviour they ship.
