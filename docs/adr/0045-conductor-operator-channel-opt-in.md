---
id: adr/0045-conductor-operator-channel-opt-in
title: Conductor Operator Channel as an Opt-In Render-Time Overlay, and Non-Blocking Autonomous Delivery
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-27
component: KyberSquad
code-refs:
  - SquadOperatorChannel
  - CopilotMcpServerProbe
  - CopilotRenderer
decided-by: []
supersedes: []
---

# ADR 0045: Conductor Operator Channel as an Opt-In Render-Time Overlay, and Non-Blocking Autonomous Delivery

## Status

Accepted, 2026-10-27. Records two decisions. Decision 1 gives the Copilot CLI conductor an
opt-in channel to the operator, added at install time only when the user turns it on, through an
MCP server whose name the user chooses. Decision 2 changes autonomous delivery so that review
findings are reported in the pull request rather than holding the change back.

## Context

Autonomous runs stop when the conductor reaches a hard block: a question only the operator can
answer, such as which of two options to take. The conductor cannot reach the operator from inside
a run, so the run waits or ends. A separate Model Context Protocol (MCP) server can carry a message
to the operator and return a reply. An MCP server is a local program that exposes tools to an agent.
The conductor needs that channel only when it runs unattended, and only on a machine where the
operator has set one up.

Three facts constrain the design:

- The conductor has no shell and no MCP access by default. Its tool list is fixed at render time,
  so any change to what it can call must be written into its agent file when Squad installs or
  updates.
- Squad renders one canonical source to many harnesses. Only Copilot CLI at global scope can reach
  an operator server the user declares in `$COPILOT_HOME/mcp-config.json`.
- Kyber-weave knows nothing about the operator's server. It can only add an MCP server to the
  conductor by name, so the name belongs to the operator's configuration, not to the product.
- Autonomous delivery previously held a change back whenever the council returned `NEEDS_HUMAN`
  or an exhausted `REQUEST_CHANGES` loop, and whenever a circuit-breaker stayed open. That made the
  run wait for a human who might not read the draft pull request for hours.

## Decision

### 1. Operator channel as an opt-in render-time overlay with an operator-chosen server name

The conductor gets the operator channel through a render-time overlay, not through its base
definition and not through a separate profile. Two environment variables, both read by
`SquadOperatorChannel` only, control it. They are read when `squad install`, `squad update`, or
`squad doctor` renders; `squad status` compares disk with the receipt and never renders.

- `KYBER_SQUAD_OPERATOR_CHANNEL` must be exactly `true`. Any other value, including unset, renders
  the default output.
- `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER` names the MCP server, and it is not fixed in the product. The
  name must match `^[A-Za-z0-9._-]{1,64}$` (1 to 64 ASCII letters, digits, '.', '_' or '-').

The overlay applies only when all of these hold:

- The target is Copilot CLI and the scope is global, rendered into `$COPILOT_HOME/agents`.
- `$COPILOT_HOME/mcp-config.json` declares the named server under `mcpServers`.
  `CopilotMcpServerProbe` answers this. It resolves a symlink to its final target. It refuses a
  zero-length target before opening it, because POSIX special files such as FIFOs, devices, and sockets
  report zero bytes. It requires a regular file, caps the read at 1 MiB, and skips a leading UTF-8 byte
  order mark. Any I/O, authorisation, or JSON error returns `false`, so the server counts as absent.

When the overlay applies, `CopilotRenderer` appends exactly `<name>/*` to the conductor's `tools`
list. It then appends the overlay text (`agents/conductor/overlays/operator-channel.md`) after the
conductor's authored body, with the validated name substituted for the placeholder
`{{operator-channel-server}}`. Otherwise the output is byte-identical to the output without the
feature. Parity tests pin that claim across 13 targets and 2 scopes.

When the variable is `true` but the overlay cannot apply, the result is recorded, never silent. The
outcome depends on the target and scope:

- Copilot CLI global scope only, with the gate on:
  - server name unset or empty: warning `operator-channel-server-name-missing`, overlay off.
  - name outside the allowed characters or longer than 64: warning
    `operator-channel-server-name-invalid`, overlay off. The raw value is never written into a rendered
    file.
  - valid name not declared under `mcpServers`: warning `operator-channel-server-absent`, overlay off.
- Any other target or scope, with the gate on: only degradation `operator-channel-not-rendered` on the
  conductor. No name warning is emitted there, whatever the name variable holds.

The warnings reach the install and update output.

Autonomous mode is the only place the overlay's behaviour applies. It defines three events:

- A hard-block question: the conductor sends it, keeps the returned thread id, and waits. Each receive
  call blocks on the server for up to five minutes and returns the reply, `no reply yet`, or `expired`.
  The conductor makes at most six receive calls, about 30 minutes. The server also expires the thread
  30 minutes after the send, which is the backstop if the count is wrong.
- The final report: sent, with no wait.
- A blocked or failed run status: sent, with no wait.

A reply counts only on the conductor's own thread. It must name one offered option id, or, when the
question offered `RECOMMENDED: none`, be a clear free-text answer. An unclear reply gets one clarifying
send within the remaining wait. Still unclear means no answer, and the existing autonomous path applies.
An accepted reply is recorded with provenance `operator decision via operator channel <responder> <timestamp>`.

A reply answers that one question only. It never authorises merge, publish, delete, or spend, never
approves a gate, and never widens scope. The hard stops in the autonomy contract stay in force.

The canonical `copilot-tools` key and `CopilotToolCatalog` are unchanged. The overlay entry is added
after catalog normalisation, so canonical source cannot name it. No sandbox detection is attempted; the
environment variable is the only gate.

### 2. Non-blocking autonomous delivery

These delivery rules replace the earlier push rule. They amend the autonomy contract and the
autonomous-gates inventory, and they reverse owner decision Q5 of the archived
[issue 386 plan](../archive/plans/2026-10-24-issue-386-reduce-autonomous-gates.md).

- The draft pull request is pushed before the review council runs, so the council reviews a
  pull request that already exists.
- The draft is marked ready once required CI passes. `github-devops`, using the `create-pull-request`
  skill, waits for CI for at most 60 minutes. If a required check fails or CI is not finished in that
  time, the pull request stays draft and the report says "draft: CI failed" or "draft: CI pending".
- Review runs after the pull request is ready. The council verdict goes in the pull request
  description; it does not gate readiness.
- In autonomous mode, `NEEDS_HUMAN` from the council, the protected-path escalation (`KW-REVIEW-008`),
  the size escalation (`KW-REVIEW-009`), and unresolved circuit-breakers are reported findings. They
  are listed in the pull request description and the final report as pending human review. None of
  them stops delivery.
- `KW-REVIEW-008` still returns `NEEDS_HUMAN` on its protected paths. The engine's verdict is
  unchanged; only what autonomous delivery does with it changes.
- In supervised mode, `NEEDS_HUMAN` stays terminal.
- The self-review step keeps the name `pre-push` as the name of its gate. It is a gate that runs
  after the draft push, and a draft push never waits for it.

## Alternatives considered

- **A fixed server name, `operator-channel`, hard-coded in the product.** Rejected. Kyber-weave knows
  nothing about the operator's server, so the product should not decide its name. The name belongs to
  the operator's configuration, passed through `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER`.
- **Static allowlist entry in the conductor's base definition.** Rejected. Where the server is
  absent the tool name is dead, and every existing install changes, including installs that will
  never use the channel. The default output must stay byte-identical.
- **A separate profile or a second conductor identity.** Rejected. It changes the roster on every
  target and forks the conductor's instructions into two copies that must be kept in step.
- **Reading `KYBER_SQUAD_OPERATOR_CHANNEL` at agent run time.** Rejected. The conductor has no shell
  and cannot read the environment. The gate therefore has to be resolved at install time.
- **Keeping `NEEDS_HUMAN` and open circuit-breakers as parking holds on delivery.** Rejected for
  autonomous mode. The run then waits for a human who may not read the draft pull request, and the
  draft is already the human review. Supervised mode keeps its terminal stop.

## Consequences

Trust model:

- The environment variable check is advisory at run time. The real enforcement is that the named
  server exists only in the sandbox the operator controls. A user who declares the server on a machine
  that should not reach an operator has granted that reach.
- The server name is untrusted text until validated. The name is checked against the allowed characters
  and length before use, and the raw value is never written into a rendered file. Only the validated
  name reaches the tool entry and the overlay text.
- VS Code also reads `~/.copilot/agents`. There the `<name>/*` entry can be inert, because an
  unrecognised tool name is ignored. The overlay text still renders, so the conductor may describe a
  channel it cannot call. The overlay tells the conductor to say so in its final report and carry on
  without the channel, so the run degrades rather than fails.
- A config passed through `--additional-mcp-config` is invisible to the installer. The probe reads only
  `$COPILOT_HOME/mcp-config.json`, so the feature stays off for that setup.
- The probe follows a symlinked `mcp-config.json` to its target. A zero-length target is refused before it
  is opened, and a target that is not a regular file is refused, so a named pipe or device is not read.
  The file is capped at 1 MiB.
- That refusal is an approximation by design. .NET has no public API that reports a file's type without
  opening it, so the probe relies on the zero-length test to avoid blocking on a special file. A special
  file that reports a non-zero length would not be caught by that test, and a read on it could block a
  render. Treat this as a known limit, not a guarantee.
- The per-call MCP timeout is undocumented. The five-minute per-call wait is a risk to verify in the
  sandbox before relying on it.
- The server must return only authorised replies. Squad cannot check that; the server's author must.
- Project scope is excluded. Project agent files are also read by VS Code and the GitHub cloud agent,
  where the server is usually absent, so an entry there would be dead.
- This is an explicit exception to the non-broadening guarantee in the Kyber-Squad requirements. It is
  the only render that grants the conductor an MCP capability, and it is off by default.
- If the MCP client's per-call timeout is below five minutes, the per-call wait drops and the number of
  receive calls rises, so the total stays about 30 minutes.

Delivery:

- Autonomous runs can now open a pull request that carries open findings and is marked ready. The
  pull request description and final report are the only place those findings are recorded, so the
  reviewer must read them.
- A failing required CI check still keeps the pull request draft. Blocking gates still return
  `REQUEST_CHANGES`.
- Anything that reads `autonomy.md` or the autonomous-gates inventory must use the non-blocking wording.
  Earlier wording is removed rather than kept beside it.

Maintenance:

- Any change to the overlay text, the tool entry, the name rules, or the probe is a render change. It
  needs parity tests across all targets and scopes.
