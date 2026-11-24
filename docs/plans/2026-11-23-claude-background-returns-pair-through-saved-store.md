---
id: plans/2026-11-23-claude-background-returns-pair-through-saved-store
title: Claude background returns pair with their dispatch through the saved store
doc-type: plan
status: current
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-11-23
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberArbiter
---

# Claude background returns pair with their dispatch through the saved store

**Status: Ready, 2026-11-24.**

## Request

> Fix the checker first

Context carried with the dispatch: the "checker" is the Arbiter's dispatch-readiness check, which blocked two tasks of another run because it never recorded the returns of finished background workers on Claude Code.

## Problem and goal

On Claude Code a background worker's return (`PreToolUse` on `SubagentHandback`) is written to the ledger with the hand-back's own `tool_use_id`, no pair digest and no headers, so it never pairs with its dispatch's pre event. The launch-to-agent link that should join them (`ClaudeHookAdapter.LaunchLinks`) is a static dictionary inside a hook process that exits after every event, so the hand-back process never sees it. The `SubagentStop` fallback (`RecoverLostReturn`) closes a dispatch only when exactly one open pre matches the agent type, so with three open `dev-easy` dispatches it closes none. The unpaired pres stay in flight for the whole `UnreturnedDispatchHorizon`, and the readiness check blocks every later task whose files overlap them.

Goal: one shared fix at every site that loses the link. Claude follows background dispatches through the same saved store Copilot CLI already uses (`IBackgroundDispatchStore` through `IBackgroundDispatchEngine`), every hand-back pairs with its own dispatch's pre, and the stop-event recovery closes the exact dispatch of the agent that stopped when the harness names the agent id, on Claude and Copilot alike. The in-memory statics `LaunchLinks` and `HandbackReturns` are removed.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Fix the checker (the readiness check blocked tasks because background returns on Claude were never recorded against their dispatch) | T1 | `ClaudeBackgroundReturnTests`: three same-type background dispatches each hand back through a fresh adapter per event and each post row carries its own dispatch's `pre-id`; `InFlightPaths` is empty afterwards |
| "first" (this fix comes before resuming the other run) | Scope: one task, no dependency on other work | Plan holds only this fix |

## Scale

`documentation-impact: extends-component` on `KyberArbiter`: the background-return pairing contract on Claude changes (saved store instead of in-memory link) and the stop-event recovery gains an exact-agent path.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How does Claude keep the launch-to-agent link across hook processes? | `HandlePost` on `status: async_launched` calls `IBackgroundDispatchEngine.TrackBackgroundDispatch(new BackgroundDispatch("claude", session_id, tool_response.agentId, caller, tool_input.subagent_type, tool_input.prompt, tool_use_id))`. `LaunchLinks` and `JoinedToolUseId` are deleted. | Reuses the proven Copilot CLI path (`CopilotHookAdapter.HandleCliCall`), which saves `BackgroundDispatchRecord` to disk keyed by session and agent id. The pre is found by call id (Claude pre rows carry `call-id` = the dispatch `tool_use_id`), so a prompt the hook rewrote still pairs. Alternative (a new Claude-only file) rejected: a second store for the same fact. | ANSWERED — autonomous: recommendation accepted |
| D2 | How does the hand-back tell "no saved record" from "turn already recorded", since `DecideBackgroundReturn` answers PassThrough for both? | Add `BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context)` to `IBackgroundDispatchEngine`. The hand-back calls it first: a record → `DecideBackgroundReturn("claude", session_id, agent_id, message, …)`; no record (or no session id, or an engine that is not `IBackgroundDispatchEngine`) → today's unpaired `DecidePost(payload, caller, caller, "", message, …)`, still allowed, with a log line naming the session id and agent id it looked up. | Keeps the existing "never denied, recorded unpaired" contract for a return the Arbiter did not follow. Changing `DecideBackgroundReturn` to return null was rejected: it changes Copilot's call site and both test fakes for the same effect. | ANSWERED — autonomous: recommendation accepted |
| D3 | `HandbackReturns` (static) guards a later foreground `PostToolUse(Agent)` `completed` from recording the same agent twice; it has the same cross-process defect. Keep, fix or remove? | Replace it with the saved record: a `completed` post whose `tool_response.agentId` has a record from `FindBackgroundDispatch` with a non-null `LastResponseDigest` is not recorded again. `HandbackReturns` is deleted. | Same root cause as D1, so fixed in the same change (owner's root-cause rule). | ANSWERED — autonomous: recommendation accepted |
| D4 | Does Claude also take the pending notes `DecideBackgroundReturn` saves? | No. Claude already delivers the note inside the hand-back message (`RenderHandbackAllow`), so taking it again on the next dispatch would deliver it twice. The `PendingNote` field stays unused on Claude. | Copilot needs pending notes because its stop hook may not deliver the rewrite; Claude's hand-back rewrite is synchronous. | ANSWERED — autonomous: recommendation accepted |
| D5 | The stop-event recovery refuses when more than one open dispatch of the agent type exists. How is that fixed for every harness that names the agent? | `ILostReturnRecovery.RecoverLostReturn` gains a trailing `string? agentId = null`. When it is set and the saved store holds a record for (session, agent id): if the record's pre is still open, close exactly that pre (same recovered row as today, headers copied); if it is already paired, close nothing. The exact path keeps the session match, `At < now`, the unpaired check, the refused-dispatch exclusion and the bounded append. It skips the name match, the multiple-match refusal and the late-stop check, because the record already names the one dispatch and a returned sibling of the same type says nothing about it. No record, or no agent id → today's name-based rule unchanged. Claude `HandleSubagentStop` passes the payload's `agent_id`; Copilot `HandleSubagentStop` passes its `agentId`; Factory passes none. | Claude's hooks reference lists `agent_id` as a common input field "for the subagent … the hook fires in" (code.claude.com/docs/en/hooks), so `SubagentStop` carries the stopped worker's id. Factory has no agent id, so its ambiguity stays; see out of scope. | ANSWERED — autonomous: recommendation accepted |
| D6 | `DecideBackgroundReturn` passes `sessionId: null` to `Decide`, so the background post row has no session. Keep? | Pass `record.Dispatch.SessionId`. The name-based recovery's "late stop" check compares a paired post's session with the pre's; a sessionless post lets that rule close a still-open sibling dispatch of the same type. | The worker confirms in `Decide` that a session id on this path changes only the row's `session` field (no pending-note or notification side effect); if it changes more, stop and report. | ANSWERED — autonomous: recommendation accepted |
| D7 | Which existing tests change their assertions? | `ClaudeHandbackTests`: `SeedLaunchLink` and its calls are removed (those tests use a plain `IHookDecisionEngine`, so they exercise the unpaired fallback and keep every assertion); `Handback_ThenForegroundPostCompleted_IsNotRecordedTwice` moves to `ClaudeBackgroundReturnTests` over the real engine, same assertion. `ClaudeHookAdapterTests`: the async-launched test that asserts a `LaunchLinks` entry is rewritten to assert `TrackBackgroundDispatch` received the `BackgroundDispatch` of D1; `LaunchLinks_AreKeyedByToolUseId` is deleted (the member no longer exists). | Every other existing test keeps its assertions; a worker who would have to change another assertion escalates. | ANSWERED — autonomous: recommendation accepted |
| D9 | A paired hand-back goes through `DecideBackgroundReturn`, which evaluates with the record's caller (the dispatcher), target and dispatch prompt. Today the hand-back is evaluated as caller = target = the returning agent, with an empty prompt. Accept the change? | Yes. A paired hand-back is evaluated exactly like a foreground return of the same dispatch: dispatcher as caller, dispatched agent as target, dispatch prompt for trigger classification. The rendering is unchanged: the hand-back is never denied, and a note or envelope is appended to `message` after one blank line. An unpaired hand-back keeps today's inputs. | This is the intended fix, not a side effect: the return rules and the trigger family are defined for the dispatch, so they now judge the right one. Copilot background returns already evaluate this way. Covered by test case (9). | ANSWERED — autonomous: recommendation accepted |
| D8 | One task or several? | One task: the contract members, the engine, three adapters and their tests are one chain in one technology, each step consuming the one before. | Plan-authoring rule: a same-technology chain is one task. | ANSWERED — autonomous: recommendation accepted |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (owner chose "Fix the checker first" 2026-11-23; plan review: 3 findings, 0 rejected), 2026-11-24 |

NO_QUESTIONS: every choice is settled by the existing Copilot pattern, the Claude hooks reference, or the owner's root-cause rule.

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberArbiter | architecture | Claude hand-back hook (`SubagentHandback`) | A background launch is saved to the background-dispatch store on `async_launched`; the hand-back pairs through that record; no in-memory link; a hand-back with no record is recorded unpaired and allowed; a later `completed` post for a recorded agent is skipped |
| KyberArbiter | architecture | Lost-return recovery on stop events | The exact-agent path: a stop event naming an agent id with a saved record closes that dispatch only; the name-based rule remains for harnesses with no agent id |

## Investigation findings

- Read at `claude/fix/arbiter-background-returns-lose-task`, rev `f122a2c65`, through `codegraph_explore` with `projectPath` set to the worktree.
- `ClaudeHookAdapter.HandleHandback` calls `DecidePost(payload, caller, caller, "", message, …)` and passes the hand-back's own `tool_use_id` as the call id; the join result is kept only in `HandbackReturns`.
- `ArbiterHookDecisionEngine.TrackBackgroundDispatch` / `DecideBackgroundReturn` already implement the saved link; only `CopilotHookAdapter` calls them. `FileBackgroundDispatchStore` keys a record by session and agent id.
- `ArbiterLedgerQueries.InFlightPaths` counts a pre as returned only when some post carries its `PreId`; `ReturnsFor` takes the task from the paired pre's headers, so a paired post needs no headers of its own.
- Live ledger (`artifacts/arbiter/ledger.jsonl` in the canonical checkout): the unpaired hand-back rows carry the main session id, so the hand-back's `session_id` equals the dispatch's.
- The test host `InMemoryArbiterHost` wires the real engine over an in-memory `IBackgroundDispatchStore`; `ArbiterLostReturnRecoveryTests` is the example of driving it.
- Not verified live: that `tool_response.agentId` on `async_launched` equals `agent_id` on the worker's `SubagentHandback`. The fixtures were authored from a task packet, not captured. See Risks.
- Discovery: codegraph 7, docs 1 (coding standard), web 1 (Claude hooks reference), direct reads 7 — `ClaudeHookAdapter.cs` lines 44-193 and 358-532 (codegraph returned gaps there), `BackgroundDispatch.cs`, `IBackgroundDispatchEngine.cs`, `ClaudeHandbackTests.cs` (codegraph listed them but did not return their source), the Claude fixture README and `post-async-launched.json` (fixtures, not indexed), `docs/plans/README.md` (plan index); grep for test usages, namespaces and ledger rows.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Application.Tests/Arbiter/ClaudeBackgroundReturnTests.cs` (new, class `KyberWeave.Tests.Arbiter.ClaudeBackgroundReturnTests`; copy the `InMemoryArbiterHost` setup of `ArbiterLostReturnRecoveryTests`, one `ArbiterTestServices.Command` run per event, state shared only through the host's store and ledger) The T1 runner command in the code block under this table: the classes `KyberWeave.Tests.Arbiter.ClaudeBackgroundReturnTests`, `ClaudeHandbackTests`, `ClaudeHookAdapterTests`, `ArbiterLostReturnRecoveryTests`, `CopilotHookAdapterTests`, `CopilotParallelDispatchTests` and `FactoryHookAdapterTests`, each a `FullyQualifiedName~` clause, joined by a plain pipe (OR) | (1) Three `dev-easy` background dispatches (pre, then `async_launched` with distinct `tool_use_id` and `agentId`), handed back out of dispatch order: each post row's `pre-id` is its own dispatch's pre, its `session` is the dispatch session, and `ArbiterLedgerQueries.InFlightPaths` returns no path of the three. (2) Hand-back for an agent with no saved record: recorded unpaired (no `pre-id`), allowed, empty stdout. (3) The same hand-back message twice: one post row. (4) Hand-back, then a foreground `PostToolUse` `completed` naming the same `agentId`: one post row, empty stdout. (5) `async_launched` whose `tool_use_id` matches no pre: no record saved, log names the call id; a later hand-back is recorded unpaired. (6) Two `dev-easy` dispatches, the second's hand-back never arrives, then `SubagentStop` with the second's `agent_id`: exactly the second's pre gets a recovered post (`recovered: SubagentStop`, headers copied); the first stays open. (7) Hand-back recorded, then `SubagentStop` with the same `agent_id`: no new row, the sibling stays open. (8) Three open `dev-easy` dispatches and a `SubagentStop` with no `agent_id`: none closed (today's rule). (9) A hand-back with a saved record reaches the engine with the dispatch's caller (`conductor`), target and prompt (assert on the post row's `caller`/`target` and its trigger family). A rule outcome that adds a note still renders as allow, with the note after one blank line in `message`. In `ArbiterLostReturnRecoveryTests` add: (a) `RecoverLostReturn` with an `agentId` whose record's pre is open among several same-name dispatches closes that one. (b) An `agentId` whose pre is already paired closes none. (c) An unknown `agentId` falls back to the name rule. (d) A same-type sibling has already returned (paired post after the open pre), and the stop for the still-open agent id still closes its own pre: the late-stop check does not apply to the exact path. In `CopilotHookAdapterTests` assert `HandleSubagentStop` passes the payload `agentId` to recovery. In `ClaudeHookAdapterTests` the async-launched test asserts `TrackBackgroundDispatch` received the dispatch. | Cases (1), (4), (6), (7) and the three new recovery cases fail: posts carry no `pre-id` / `FindBackgroundDispatch` and the `agentId` parameter do not exist | All listed classes pass; no existing assertion changed except the three tests the task names |

T1 runner command (the pipe is a plain OR, with no backslash before it):

```bash
dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeBackgroundReturnTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHandbackTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterLostReturnRecoveryTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.CopilotHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.CopilotParallelDispatchTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.FactoryHookAdapterTests"
```

## Tasks

### T1: Claude follows background dispatches through the saved store, and stop events close the exact agent's dispatch

- **Objective:** Every Claude background hand-back pairs with its own dispatch's pre event across hook processes, and a stop event naming an agent id closes only that agent's dispatch, on Claude and Copilot.
- **Capabilities:** C# application code (hook adapters, decision engine, contracts) with in-memory xUnit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `T1`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Contracts/Arbiter/IBackgroundDispatchEngine.cs`: `IBackgroundDispatchEngine`, add `BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context)` (null when the agent is not followed), with XML docs
  - `src/KyberWeave.Contracts/Arbiter/ILostReturnRecovery.cs`: `ILostReturnRecovery.RecoverLostReturn`, add trailing `string? agentId = null`, with XML docs
  - `src/KyberWeave.Application/Arbiter/Hooks/ArbiterHookDecisionEngine.cs`: implement `FindBackgroundDispatch` over `_background.Find`. `DecideBackgroundReturn` passes `record.Dispatch.SessionId` (not null) as `sessionId` to `Decide`, so the post row carries the session; if `Decide` does more with a session than set the row's `session`, stop and report. `RecoverLostReturn`: when `agentId` is set and `_background.Find(dir, sessionId, agentId)` returns a record, close exactly the record's pre if it is still open, else close nothing. Keep the session match, `At < now`, the unpaired and not-refused checks, the same recovered row (headers copied) and the bounded append. Skip the name match, the multiple-match refusal and the late-stop check. With no record or no `agentId`, today's name rule runs unchanged
  - `src/KyberWeave.Application/Arbiter/Hooks/Adapters/ClaudeHookAdapter.cs`: `HandlePost` `async_launched` branch calls `TrackBackgroundDispatch(new BackgroundDispatch("claude", session_id, tool_response.agentId, caller, tool_input.subagent_type, tool_input.prompt, tool_use_id))` when the engine is `IBackgroundDispatchEngine`. `completed` branch: skip when `FindBackgroundDispatch(session_id, tool_response.agentId)` has a non-null `LastResponseDigest`. `HandleHandback`: a record from `FindBackgroundDispatch(session_id, agent_id)` → `DecideBackgroundReturn("claude", session_id, agent_id, message, …)`, which evaluates with the dispatch's caller, target and prompt, as a foreground return does; otherwise today's unpaired `DecidePost`, logging the session id and agent id looked up; rendering unchanged and never denied; do not call `TakePendingNotes` (the note already rides in the hand-back message). `HandleSubagentStop` passes the payload `agent_id`. Delete `LaunchLinks`, `HandbackReturns`, `JoinedToolUseId`, and fix the doc comments that describe them
  - `src/KyberWeave.Application/Arbiter/Hooks/Adapters/CopilotHookAdapter.cs`: `HandleSubagentStop` passes `agentId` to `RecoverLostReturn`
  - `tests/KyberWeave.Application.Tests/Arbiter/ClaudeBackgroundReturnTests.cs`: new class `KyberWeave.Tests.Arbiter.ClaudeBackgroundReturnTests`, cases (1)-(9)
  - `tests/KyberWeave.Application.Tests/Arbiter/ClaudeHandbackTests.cs`: `KyberWeave.Tests.Arbiter.ClaudeHandbackTests`, remove `SeedLaunchLink` and its calls (other assertions stay); move `Handback_ThenForegroundPostCompleted_IsNotRecordedTwice` into the new class over the real engine, same assertion
  - `tests/KyberWeave.Application.Tests/Arbiter/ClaudeHookAdapterTests.cs`: `KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests`, the async-launched test asserts `TrackBackgroundDispatch` received that `BackgroundDispatch` instead of a `LaunchLinks` entry; delete `LaunchLinks_AreKeyedByToolUseId`
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterLostReturnRecoveryTests.cs`: `KyberWeave.Tests.Arbiter.ArbiterLostReturnRecoveryTests`, the four exact-agent recovery cases (a)-(d)
  - `tests/KyberWeave.Application.Tests/Arbiter/CopilotHookAdapterTests.cs`: `KyberWeave.Tests.Arbiter.CopilotHookAdapterTests`, fake `BackgroundEngine` implements `FindBackgroundDispatch`; assert the stop handler passes `agentId`
  - `tests/KyberWeave.Application.Tests/Arbiter/CopilotParallelDispatchTests.cs`: `KyberWeave.Tests.Arbiter.CopilotParallelDispatchTests`, fake `CapturingEngine` implements `FindBackgroundDispatch`
  - `tests/KyberWeave.Application.Tests/Arbiter/FactoryHookAdapterTests.cs`: `KyberWeave.Tests.Arbiter.FactoryHookAdapterTests`, fake `RecoveryEngine` matches the new `RecoverLostReturn` signature
- **Commands:**
  - `kyber-weave review gates . --agent dev-medium --task T1 --red`
  - `kyber-weave review gates . --agent dev-medium --task T1`
- **Acceptance:**
  - `ClaudeHookAdapter` has no static dictionary; no Claude background state lives in process memory.
  - Every Test contract case for T1 passes; the RED run showed cases (1), (4), (6), (7) and the new recovery cases failing.
  - A hand-back with no saved record is still allowed and recorded unpaired, and logs the session id and agent id it looked up.
  - Existing assertions are unchanged except the three tests named under Files.

## Dependency graph

T1 (no dependencies) → end-of-run review → docs-dev closeout.

## Risks

- `tool_response.agentId` on `async_launched` may differ from the worker's `agent_id` on `SubagentHandback` (unverified live). Mitigation: the no-record path logs both keys and keeps the unpaired fallback plus the stop-event recovery, so a mismatch shows in the hook log and degrades to today's behaviour rather than mispairing.
- A hand-back that arrives before its `async_launched` post was processed finds no record. Mitigation: it is recorded unpaired as today, and the exact-agent stop recovery (D5) then closes that dispatch, because the record exists by the time the stop event fires.
- `SubagentStop` without `agent_id` on an older Claude Code build. Mitigation: the name-based rule is unchanged for that case.

## Out-of-scope boundaries

- PR note: `src/KyberWeave.Application/Arbiter/Hooks/Adapters/CopilotHookAdapter.cs` `TrustedDecisions` — another static "in-memory by design" cache across short-lived hook processes — needs a human to judge whether its repeat-fire correlation also fails across processes.
- PR note: `src/KyberWeave.Application/Arbiter/Hooks/Adapters/FactoryHookAdapter.cs` `RecoverLostReturn` — Factory's stop event names no agent id, so several open same-type dispatches still close none — needs a human to decide whether Factory's transcript can supply an id.
- PR note: `artifacts/arbiter/ledger.jsonl` of the blocked run — the four existing unpaired hand-back rows are not repaired by this change — the owner decides whether to re-run or attest those tasks.

## Verification gates

- The T1 task gate (build, the Test contract classes, format, suppression scan).
- `kyber-weave docs validate .` and `kyber-weave docs drift .` after docs-dev closeout.

## Review

The end-of-run council reviews the whole change under the `code-review` skill; this plan carries no review command.

## docs-dev closeout

docs-dev harvests the two Design harvest rows into `docs/kyber-arbiter/architecture.md` (the Claude hand-back hook section and the lost-return recovery section), updates any statement there that describes the in-memory launch link, fills the Closeout mapping, and archives the plan.
