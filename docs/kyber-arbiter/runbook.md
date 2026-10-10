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
`.cursor/hooks.json`). Phase 3 adds Kilo (the `.kilo/plugin/kyber-arbiter.ts` shim),
Antigravity (the owned `kyber-arbiter` group in `.agents/hooks.json`), Factory (the owned
block in `.factory/hooks.json`, dropped while the user's hooks live in
`.factory/settings.json` — see fail-closed behaviour below), and Devin (the owned block
in `.devin/hooks.v1.json`). Warp and ZCode render no hooks: they are fallback-only — see
the MCP fallback below. The hook binary is invoked as:

```bash
kyber-weave-arbiter hook --harness <claude|copilot-vscode|copilot-cli|opencode|pi|codex|cursor|kilo|antigravity|factory|devin> [--caller <agent>]
```

Every target except `claude` and `copilot-vscode` is project-wide: it gates only dispatches
carrying the `KYBER-ARBITER: true` marker and carries no `--caller`.
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
| Warp | No trust gate — there are no hooks to trust. Setup instead: Squad writes no MCP configuration for Warp, so register `kyber-weave-arbiter serve --repo-root <root>` as an MCP server in Warp's own MCP configuration. Until it is registered only the advisory marker fallback enforces there. |
| ZCode | No trust gate — there are no hooks to trust. Setup instead: Squad writes no MCP configuration for ZCode, so register `kyber-weave-arbiter serve --repo-root <root>` as an MCP server in ZCode's own MCP configuration. Until it is registered only the advisory marker fallback enforces there. |
| Cursor, Copilot CLI, OpenCode | No documented trust gate. |

`squad install` and `squad update` print the applicable step for the rendered targets.

## Fail-closed behaviour

Any internal hook error blocks the dispatch with an explicit reason carrying
`KW-ARB-HOOK-001` — never a silent pass. On conductor triggers the block carries an
escalation envelope for architect; the conductor dispatches architect with it and never
retries the blocked dispatch unchanged. Timeouts are bounded by `provider.timeout-ms` plus
the harness budget; a hook killed mid-evaluation still leaves its ledger event, which
`audit` reports as a decision-less entry.

Pre-dispatch denies block on every target. Post-dispatch limits differ by harness: some
targets deliver outcomes only as context, and Antigravity and Devin deliver none. The
[harness notes](architecture.md#harness-facts) state each limit, and the
[degradation taxonomy](../kyber-squad/requirements.md#degradation-taxonomy) lists the records.
Antigravity cannot observe returns, so `READY-001`'s completion check and `MODE-001`'s RED
check answer `returns-unobservable` there, which allows and is logged; the in-flight overlap
check still runs.
Devin's dispatch target and prompt are read from `tool_input.profile` and
`tool_input.prompt`. The vendor documents only that the tool "takes a profile", so those
argument names rest on the undocumented fact F12. A `run_subagent` dispatch missing either
is logged as unmarked, and `audit` reports it as `KW-ARB-AUDIT-002`, including when the
target is absent: a Squad dispatch whose profile the harness did not send is still flagged.
If Devin renames the arguments, every dispatch goes unmarked until the adapter is updated.
`arbiter doctor` prints one informational line for each of copilot-cli, codex and cursor.

## The MCP fallback (Warp and ZCode)

Warp documents no hooks, and project-level hooks are not executed in ZCode's current
version, so the marker fallback is advisory there and the MCP fallback is the enforcement
surface. Squad writes no MCP configuration for either, so register the server yourself in
the harness's own MCP configuration:

```bash
kyber-weave-arbiter serve --repo-root <root>
```

`serve` exposes three tools. `arbiter_evaluate(trigger, facts)` evaluates one event
exactly as the hook would — same facts, rules, ledger append and decision record — and
returns the same allow, envelope or review note; the caller asserts the routing facts and
the log records `caller-source: asserted`, so this path is advisory.
`arbiter_rules(trigger?)` lists the rule catalogue, and `arbiter_status()` reports the
root, rule-set hash, rule count and provider state, never the key — both read-only. Every
response leads with a provenance line naming the root it answered from; the root is
resolved from `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the working directory. The
conductor calls `arbiter_evaluate` before each dispatch and after each return, and
code-reviewer calls it once before the lens fan-out and once before the refutation
fan-out. On Squad-rendered fallback targets the tools are granted through the
`decision.query` capability, allowed only for the `orchestrator` and `reviewer` profiles —
the conductor and code-reviewer.

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
2. **The contradicted documented cell** — which cell of the
   [harness table](architecture.md#harness-facts) the behaviour contradicts.
3. **The affected trigger** — recorded as `arbiter-not-enforced` for that trigger on that
   harness until the defect is fixed or the fallback covers it.

## Related

- [Architecture](architecture.md) — engine, rules, configuration, harness facts
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — installing hooks, trust, scopes
- [Rule reference](../ci-pipelines/rule-reference.md) — every diagnostic id
