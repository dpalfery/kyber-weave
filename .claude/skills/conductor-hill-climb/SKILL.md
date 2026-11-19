---
name: conductor-hill-climb
description: Hill-climbing dev-test loop for the Kyber-Squad conductor. Repeatedly runs the conductor headless in GitHub Copilot CLI on the hotshot-logistics-demo feature prompt, analyses each finished, crashed or hung run, fixes the root causes in kyber-weave, redeploys, resets the demo and runs again until the conductor loop is clean, efficient and fast. Use whenever the owner asks to troubleshoot, tune, harden, benchmark or "hill climb" the conductor or graph loop end to end, to run the demo prompt through the conductor repeatedly, or to resume or report on a hill-climb that is already under way, even if the skill is not named.
---

# Conductor hill climb

The loop: **deploy → run → collect and reset → analyse → fix → repeat.** Each run is
the conductor delivering the demo feature (four job-status cards, database to UI) in
`hotshot-logistics-demo`, unattended, through Copilot CLI. Each round fixes what the
previous run exposed, so the next run measures the fix.

The aim is the best conductor run, judged on three axes in priority order:

1. **Quality**: the feature works end to end, the build and tests on the run branch
   are green, the review council approves, and no defect needs a human.
2. **Efficiency**: AI units and tokens spent, wasted or repeated dispatches,
   failed tool calls, rework cycles.
3. **Speed**: wall-clock minutes from start to the final report.

Never trade a quality defect for speed.

## Decisions already made by the owner

These are settled. Don't ask about them again.

| Topic | Decision |
|---|---|
| Run cap | 10 runs (`HC_MAX_RUNS`). |
| Stop rule | Stop after **2 runs in a row with no new defect and no gain** over the best run, or at the cap. "No gain" means less than about 10% better on minutes or AI units: one run per change is noisy, so small moves are not gains. |
| Push and PR | Blocked for every run. `start-run.sh` sets a per-process push URL and denies the push tools. The run ends with `NOT DELIVERED`, which is expected and not a defect. |
| Fixes | Go on one kyber-weave branch, one commit per fix. One pull request at the end of the climb. |
| Deploy | Each run uses the current kyber-weave tree, deployed at user level by `deploy-kyber.sh` before the run. Run 1 deploys HEAD as the baseline. |
| Who edits | Claude edits agent and skill prompts itself. Code fixes go to opencode workers under the owner's standing worker rules. Fix a defect as soon as it shows up, even mid-run; the fix ships with the next deploy. |
| Reset | Remove only the worktrees and branches the run created. Leave earlier worktrees alone and mention that they exist. |

## Layout

All settings are in `scripts/config.sh` and can be overridden from the environment:
demo path, prompt, conductor model, timeout and run cap. State lives outside both
repositories, in `$HC_HOME`, which defaults to `hill-climb/` beside the kyber-weave
checkout. This keeps it out of both git trees and alive across context summaries.

```
$HC_HOME/
  ledger.md                 # one row per run plus a list of defects; the source of truth
  release/                  # local releases built by deploy-kyber.sh
  runs/run-NN/
    prompt.md meta.json exit.json metrics.json
    session.jsonl           # Copilot event stream (--output-format json)
    session.md              # readable transcript (--share)
    stderr.log copilot-logs/
    evidence/               # run branches as a bundle, per-branch log and stat, uncommitted diffs
```

**Read `ledger.md` first** whenever you start or resume. Context gets summarised during
a climb that runs for hours, and the ledger is how you know which run you're on, which
defects are open, and what the best score is.

## Setup (once per climb)

1. In kyber-weave, fetch origin and branch from the remote default branch:
   `git fetch origin && git switch -c fix/conductor-hill-climb-<harness> origin/main`,
   where `<harness>` is the harness doing the fixing (for example `claude`).
2. Create `$HC_HOME/ledger.md` from the template below if it doesn't exist.
3. `scripts/preflight.sh` checks four things:
   - the demo checkout is clean on its default branch and fast-forwarded;
   - the conductor is deployed for Copilot;
   - the conductor model answers;
   - other worktrees in the demo are reported.
4. `scripts/deploy-kyber.sh` builds a local release from the tree and installs it with
   `kyber-weave update` and `squad update --global`. A build takes several minutes, so
   run it in the background. It prints the deployed version, which goes into the
   ledger.

## One round

### 1. Run

Start `scripts/start-run.sh "<what changed since the last run>"` in the background. It
prints the run directory, then blocks until Copilot exits or the timeout
(`HC_RUN_TIMEOUT_MIN`, default 120) kills it.

Watch the run live. Don't launch it and go idle. Use a Monitor on
`<run>/session.jsonl`. Event types worth following:

- `assistant.message`: tool requests, including sub-agent dispatches;
- `tool.execution_complete`: check `success`;
- `session.error`;
- `session.usage_checkpoint`: spend so far.

Report each phase to the owner in one line: intake, plan, finalize, each task wave,
council, closeout. If no event arrives for about 10 minutes, flag the stall and say what
the run was last doing.

When a defect is plain mid-run, such as a hook denial, a tool error loop, or a wrong
dispatch, record it in the ledger and start the fix at once. Let the run continue
unless it can no longer produce useful evidence. Only then stop it, and score it as
`crashed` with the reason.

### 2. Collect, verify, reset

1. Run `scripts/collect-and-reset.sh`. It saves evidence, writes `metrics.json`, then
   removes the run's worktrees and branches and fast-forwards the demo.
2. Check quality from the evidence, not from the run's own claims:
   - Restore the bundle into a throwaway worktree under `$HC_HOME/verify`:
     `git worktree add <dir> <run-branch>` after `git fetch <bundle> '*:*'`.
   - Build it and run the demo's tests there.
   - Check that the feature really flows from the database to the UI, as the prompt
     asks.
   - Remove the throwaway worktree afterwards.

### 3. Analyse

Sources, cheapest first:

1. `metrics.json`: outcome, minutes, AI units, tool failures by tool, session errors,
   and the tail of the final report.
2. `session.md`: the narrative. Find where time and spend went, and every retry,
   denial, circuit-breaker trip or `BLOCKED`.
3. `stderr.log` and `copilot-logs/` for crashes and hook output.
4. KyberDash, if the engine answers, for detected context and tool problems and run
   outcomes. Its tools time out when the engine is down; then carry on without it.
5. kyber-weave source, through CodeGraph and kw-docs, to find the cause of each defect.

Look for these:

- **Quality**: the feature is incomplete or broken, mock data is in production
  components, tests are missing or red, the council requested changes, or closeout
  was skipped.
- **Crash or hang**: an unavailable model, a hook denial, an MCP `TOOL_FAILURE`,
  timeouts, or a run waiting on a question.
- **Efficiency**: tasks dispatched twice, a worker re-reading what its packet already
  holds, rework cycles, oversized packets, the wrong difficulty tier, failed tool
  calls retried blindly.
- **Speed**: independent tasks run one after another, long waits, slow gates, a full
  test suite where a targeted one was enough.

Every defect gets exactly one outcome in the ledger:

- `fixed <commit>` on the climb branch;
- `issue <link>`, for work too large for the climb;
- `raised`, sent to the owner with a recommendation. Use this for demo-repo defects
  and owner decisions.

"Declined" is not an outcome. To call a defect not real, write one line of checked
evidence.

### 4. Fix

- **Fix the root cause.** When the same failure could come from several places, find
  every site, write a short design note in the ledger, then make one shared fix.
- **Prompt and skill edits** go under `products/kyber-squad/...`. Edit the source
  there, never the deployed copies under `~/.copilot`.
- **Code fixes** go to workers in small pieces of about 20 minutes each. Verify each
  result and keep a branch ledger.
- **Tests**: run only the touched tests (`scripts/fast-loop.sh --touched`), never the
  full suite. CI covers the rest.
- Commit each fix on its own with a message that names the run that found it.

### 5. Score and decide

Add a row to the scorecard. Then:

- If the run cap is reached, or the stop rule holds, finish.
- If only an owner decision blocks progress, stop and ask it as one self-contained
  question with your recommendation.
- Otherwise deploy and start the next round.

## Finish

1. Report to the owner in a few lines:
   - best run versus run 1 on quality, minutes and AI units;
   - defects fixed;
   - defects raised or ticketed, with links;
   - anything still open.
2. Open one pull request from the climb branch with the `create-pull-request` skill.
   Its description carries the scorecard and the defect list.
3. Leave the hill-climb build installed and say so.
   `kyber-weave update <released version>` returns to a real release.

## Ledger template

```markdown
# Conductor hill climb: <date started>

Climb branch: fix/conductor-hill-climb-<harness>   Best run: run-NN

## Scorecard
| Run | Kyber version | Outcome | Quality (build/tests/council/feature) | Minutes | AI units | Tool failures | New defects | Note |
|---|---|---|---|---|---|---|---|---|

## Defects
| ID | Found in | Axis | What happened (one line) | Root cause | Outcome |
|---|---|---|---|---|---|

## Design notes
```

## Known traps (verified 2026-11-19)

- **Model.** `conductor.agent.md` pins no model. Copilot's user-level default
  (`~/.copilot/settings.json` → `gpt-5.6-terra`) is not available, so a run without
  `--model` crashes at once. The scripts pass `HC_CONDUCTOR_MODEL`. Don't change the
  user-level setting without the owner's explicit yes.
- **Billing.** Since June 2026 Copilot bills by AI credits, worked out from tokens.
  The JSONL `result` event still has a `premiumRequests` field, but the spend figure is
  `session.usage_checkpoint` → `totalNanoAiu`. `aiUnits` in the metrics is that value
  divided by 10⁹. `HC_MAX_AI_CREDITS` sets an optional per-run cap.
- **Deploying from the tree.** `dotnet run … squad update` asks GitHub for a release
  numbered after the checkout and fails. Only the local release in
  `deploy-kyber.sh` deploys the working tree.
- **Autopilot.** The default `--max-autopilot-continues` of 5 can end a long run
  early. The scripts use 20. A run that ends with work left over may have hit this
  limit; it is a harness setting, not a conductor defect.
- **Environment crashes.** A run that dies on a network fault (DNS or TLS errors reaching the
  Copilot API, the same minute an opencode worker fails to reach its provider) is not a conductor
  defect. Score it `crashed (environment)`, mine what it reached, and do not count it toward the
  stop rule's two quiet runs.
- **Spend by role.** `metrics.json` → `byAgent` sums AI units per agent role (the conductor is
  `conductor`). Use it to see which role a fix should target.
- **Autonomy.** The prompt starts with `autonomy: autonomous`, so the conductor
  approves its own gates and asks nothing. A question to the user in the transcript
  is a defect.
