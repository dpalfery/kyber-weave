---
id: adr/0079-the-reviewer-starts-its-own-seats-where-sub-agents-can-dispatch
title: The reviewer starts its own seats where sub-agents can dispatch
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: ReviewCouncil
decided-by: []
supersedes:
  - adr/0077-the-dispatching-session-seats-the-council-from-the-gate-runs-evidence
code-refs:
  - SquadTargetCatalog
  - SquadSubagentDispatch
  - KiloRenderer
  - TriggerClassifier
---

# ADR 0079: The reviewer starts its own seats where sub-agents can dispatch

## Status

Accepted, 2026-11-13. Decided by the owner. Replaces decision 1 of
[ADR 0077](0077-the-dispatching-session-seats-the-council-from-the-gate-runs-evidence.md); its
decisions 2 to 4 (the evidence the gate run writes, no seat for what a gate proves, and the Arbiter
classifying a seat the conductor starts) stand.

## Context

ADR 0077 made every dispatched `code-reviewer` hand its lens seats back to the conductor, because it
read about 60 Copilot CLI sessions in which no sub-agent called `task` and concluded that a
sub-agent cannot start sub-agents. That was wrong. Checked on 2026-11-13:

- **Copilot CLI 1.0.110:** a top-level session dispatched the installed `code-reviewer`, which
  dispatched `review-lens` and received its reply. The sub-agent holds the `task` tool.
- **Pi 1.4.0 with `@tintinweb/pi-subagents`:** main, middle and leaf worked when the middle agent's
  frontmatter lists `allowed_subagents`. Depth is capped at 2, counting the main session as 0.
- **Factory Droid 1.6.0:** a droid declaring `tools: ["Read","Task"]` received Read and Skill but
  no `Task`. A droid cannot nest. The conductor runs there as a skill in the main session (ADR 0034,
  ADR 0036), and that session can dispatch.
- **Claude Code (documentation):** a sub-agent that lists `Agent` can spawn sub-agents, up to three
  layers below the main conversation by default.
- **Cursor (documentation):** the main agent and its direct sub-agents can launch sub-agents; one
  more layer, no further.
- **OpenCode 1.19.5 and Kilo 7.8.27 (source):** the task tool refuses a nested task while the
  session depth reaches `subagent_depth`, default 1, so nesting is off unless the operator raises
  it. A sub-agent's session also denies `task` unless the agent's own permission rules name it.
- **VS Code Copilot:** nesting needs the user setting
  `chat.subagents.allowInvocationsFromSubagents` (from the documentation; not tried).
- **Codex:** the current sub-agent documentation names no depth setting; unverified.

So the handback was a round trip that most harnesses never needed. Each review cost at least two
reviewer dispatches, and every lens finding passed through the conductor's context twice.

## Decision

1. **One review dispatch where sub-agents can dispatch.** The conductor (or `bug-crusher`)
   dispatches `code-reviewer` once. The reviewer runs the council gates, which write the evidence,
   then starts every `review-lens` and `review-triage` seat itself in one parallel batch, each given
   its packet and the evidence path, then adjudicates and computes the verdict with the verdict
   engine. A reviewer that holds a tool to start agents never applies a lens itself. Only one with
   no such tool applies the lenses itself, and its report says so.
2. **The `review-council` skill where they cannot.** The session a human started loads it in place
   of the one dispatch. The skill dispatches `code-reviewer` in hand-back mode (first body line
   `COUNCIL-SEATS: hand-back`). The reviewer runs the gates and returns `STATUS: COUNCIL_SEATS_NEEDED`
   with one `SEAT:` block per seat. The main session starts every seat in one batch and re-invokes the
   reviewer with one outcome line per seat. Each seat writes its output to the `Seat file:` its block
   names and replies with that path, so findings never enter the main session. The reviewer and the
   verdict command read the seat files. The conductor's own instruction is one sentence: dispatch
   `code-reviewer`; where sub-agents cannot dispatch, load `review-council`.
3. **The render says which path a harness is on.** `SquadTargetCatalog.GetSubagentDispatch` holds the
   fact per harness, and the render answers the `{{subagents-dispatch}}` placeholder with `yes` or
   `no`. A harness is marked nesting only where it was verified and needs no setting Squad does not
   write:

   | Harness | Path | What the render does for nesting |
   |---|---|---|
   | Claude | nesting | Lists `Agent(<roster>)` in the reviewer's `tools` (already rendered) |
   | Copilot | nesting | Lists `agent` (already rendered); VS Code's setting is documented, not written |
   | Cursor | nesting | Nothing to render |
   | Pi | nesting | Emits `allowed_subagents` from the reviewer's roster (already rendered) |
   | OpenCode | `review-council` | Emits the reviewer's `task` map (already rendered); `subagent_depth` is the operator's |
   | Kilo | `review-council` | Now emits the reviewer's `task` map; `subagent_depth` is the operator's |
   | Factory, Devin | `review-council` | Nothing: nesting is impossible or not enabled |
   | Codex, Antigravity, Warp, ZCode, Command Code | `review-council` | Nothing: unverified, so the safe path |

4. **Seats may write their own seat file.** `review-lens` and `review-triage` move from `read-only`
   to a new `review-seat` profile: read-only plus `filesystem.write`. They still run no shell and
   never change the tree. The Arbiter still classes them as read-only targets.
5. **The reviewer reads the evidence.** It does not run tests, builds, deploy scripts, Playwright or
   any other end-to-end suite the gates already ran.

The Arbiter classification ADR 0077 added stays: on the `review-council` path the conductor or
`bug-crusher` starts the seats, and on Factory, whose hook names no caller, the `LENS:` or `REFUTE:`
header infers `code-reviewer`.

## What is given up

- **The write grant is wider than the need.** The capability lattice has no path scoping, so the
  rule that a seat writes only its seat file is an instruction. Every harness carries the grant,
  although only the `review-council` path uses it.
- **The return-side Arbiter rules see less on the skill path.** The quote and pre-existing checks
  (`KW-ARB-QUOTE-001`, `KW-ARB-PREEX-001`) and the nitpick filter (in shadow) read a lens's reply. A
  seat that replies with a path gives them nothing to annotate. The reviewer's own quote check and
  the verdict command's cited-line check (`KW-REVIEW-043`) still run over the seat files. On the
  nesting path seats still reply to the reviewer, and nothing changes.
- **Where nesting is off by default, the operator must turn it on.** OpenCode and Kilo stay on the
  skill path until the operator sets `subagent_depth`, and the render cannot see that setting. VS Code
  Copilot without its setting falls back to the reviewer applying the lenses itself.

## Consequences

- On Claude, Copilot, Cursor and Pi a review costs one reviewer dispatch. The conductor sees only
  the verdict.
- `review-lens` and `review-triage` stay in the conductor's and `bug-crusher`'s rosters, for the
  `review-council` skill alone.
- A harness moves to nesting by changing one row in `GetSubagentDispatch`, once it is verified.
