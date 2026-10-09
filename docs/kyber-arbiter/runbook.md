---
id: kyber-arbiter/runbook
title: Kyber Arbiter runbook
doc-type: runbook
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-10-09
status: current
decided-by:
  - adr/0028-kyber-arbiter-three-step-decision-gates
  - adr/0029-squad-owned-blocks-in-shared-hook-files
---

# Kyber Arbiter runbook

Operating the Arbiter: wiring hooks, granting trust, staying fail-closed, and diagnosing a
host. For what it is and why, see the [architecture](architecture.md).

## Hooks

Hooks render through `squad install` / `squad update` when the project's `arbiter.enabled`
is true. Phase 1 wires Claude (per-agent frontmatter hooks), Copilot in VS Code (per-agent
`.agent.md` hooks), Copilot CLI (`.github/hooks/kyber-arbiter.json`), and OpenCode (the
plugin shim). Phase 2 adds Pi (the `.pi/extensions/kyber-arbiter.ts` shim), Codex (the
owned block in the shared `.codex/hooks.json`), and Cursor (owned entries in the shared
`.cursor/hooks.json`). The hook binary is invoked as:

```bash
kyber-weave-arbiter hook --harness <claude|copilot-vscode|copilot-cli|opencode|pi|codex|cursor> [--caller <agent>]
```

Shared-file hooks (`pi`, `codex`, `cursor`) gate project-wide and carry no `--caller`.
It reads the harness event on stdin and writes only the harness's decision document on
stdout — logging goes to stderr. The exit code is 0.

## Trust steps

A hook that never runs enforces nothing, and the audit cannot see dispatches no hook
observed — so grant trust at install time:

| Harness | Trust gate |
|---|---|
| Claude | Accept the workspace trust dialog for the project. `claude -p` sessions never count: headless runs execute no project sub-agent frontmatter hooks and stay unenforced. |
| Copilot in VS Code | Open the project as a trusted workspace and keep `chat.useHooks` enabled, or the hooks stay off. |
| Codex | Trust the project's `.codex/` layer, then review and trust the new hook through `/hooks`. Every `squad update` that changes the hook needs that review again, or the changed hook is skipped. |
| Pi | Trust the project so that `.pi/extensions/` loads. Until trust is granted the extension does not load and dispatches stay ungated. |
| Cursor, Copilot CLI, OpenCode | No documented trust gate. |

`squad install` and `squad update` print the applicable step for the rendered targets.

## Fail-closed behaviour

Any internal hook error blocks the dispatch with an explicit reason carrying
`KW-ARB-HOOK-001` — never a silent pass. On conductor triggers the block carries an
escalation envelope for architect; the conductor dispatches architect with it and never
retries the blocked dispatch unchanged. Timeouts are bounded by `provider.timeout-ms` plus
the harness budget; a hook killed mid-evaluation still leaves its ledger event, which
`audit` reports as a decision-less entry.

On the Copilot CLI target (`copilot-cli`) post-dispatch outcomes are advisory only:
`Deny`, `PostBlock` and `PostAnnotation` all render as `additionalContext`, which the
harness surfaces as context rather than enforcement. Pre-dispatch denies still block.
On the Codex target (`codex`) post-dispatch outcomes are likewise advisory only: every
post-dispatch outcome renders as `hookSpecificOutput.additionalContext`, because a
`decision: block` there would replace the sub-agent's result.
On the Cursor target (`cursor`) the post-dispatch path writes only `additional_context`,
so a post-dispatch finding is advisory there too; the pre-dispatch `permission: deny`
still blocks.
`arbiter doctor` prints one informational line for each of these three targets.

## Setup, doctor, audit

Three commands cover the provider lifecycle; the key is never shown and never stored in a
file:

```bash
kyber-weave arbiter setup    # choose provider (none, TypeSafe cloud, local Ollama); stores the key in the OS credential store
kyber-weave arbiter status   # provider, model, endpoint origin, whether a key resolves — never the value
kyber-weave arbiter doctor   # warns on switched-off model rules, untuned models, missing keys, un-ignored log dirs, missing binaries; notes the Copilot CLI, Codex and Cursor post-dispatch advisory limitations
```

Read-only inspection:

```bash
kyber-weave arbiter validate [path]  # arbiter: section, user override, and shipped rules
kyber-weave arbiter rules [--trigger <t>]
kyber-weave arbiter plan <file>      # what the parser understood, with KW-ARB-PLAN-001 / KW-ARB-PARSE-00x
kyber-weave arbiter eval --trigger <t> --event <file> [--provider none]  # offline dry run; writes nothing
kyber-weave arbiter audit [--plan <file>] [--session <id>] [--since <ISO-8601>]
```

`audit` reports decision-less events, unmarked dispatches to Squad agents, unpaired
pre/post events, and implementation packets whose body names a planning path
(`KW-ARB-AUDIT-001`…`-004`), and lists attestations for information. code-reviewer runs
`audit --plan <PLAN_FILE>` at the end of a run and cites the result.

## Recording a harness defect

Support is claimed from vendor documentation, so a misbehaving hook is a defect against the
vendor's stated behaviour — record each one with three facts:

1. **Harness and version** — the harness token and the exact harness version observed.
2. **The contradicted documented cell** — which cell of the per-harness hook tables in the
   [architecture](architecture.md#phase-1-harness-facts) (Phase 1) or
   [architecture](architecture.md#phase-2-harness-facts) (Phase 2) the behaviour contradicts.
3. **The affected trigger** — recorded as `arbiter-not-enforced` for that trigger on that
   harness until the defect is fixed or the fallback covers it.

## Related

- [Architecture](architecture.md) — engine, rules, configuration, harness facts
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — installing hooks, trust, scopes
- [Rule reference](../ci-pipelines/rule-reference.md) — every diagnostic id
