---
id: adr/0079-the-reviewer-starts-its-own-seats-where-sub-agents-can-dispatch
title: The reviewer starts its own seats where sub-agents can dispatch
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-14
component: ReviewCouncil
decided-by: []
supersedes:
  - adr/0077-the-dispatching-session-seats-the-council-from-the-gate-runs-evidence
code-refs:
  - SquadTargetCatalog
  - SquadSubagentDispatch
  - KiloRenderer
  - TriggerClassifier
  - CouncilSeating
  - ReviewSeatsCommand
  - WorkerGuard
  - ReviewDecisionModel
  - ReviewVerdictDecisions
  - ReviewLensApplicability
  - NotSeatedRecord
  - ReviewVerdictCommand
  - ReviewPolicy
  - SeverityOverrides
---

# ADR 0079: The reviewer starts its own seats where sub-agents can dispatch

## Status

Accepted, 2026-11-13. Decided by the owner. Replaces decision 1 of
[ADR 0077](0077-the-dispatching-session-seats-the-council-from-the-gate-runs-evidence.md); its
decisions 2 to 4 (the evidence the gate run writes, no seat for what a gate proves, and the Arbiter
classifying a seat the conductor starts) stand. Amended 2026-11-13: decisions 6 to 9 make the seats
start while the gates run, after a replay showed the fanned-out review taking 16.7 minutes.
Amended 2026-11-14: decision 10 moves the review's decision-model questions from the Arbiter hooks
into the review commands. Amended 2026-11-14 again, by the owner after a live run: decisions 11 to
13 make every fix round narrow and mechanical, fix the majors an approval leaves, and grade wrong
placement as major. Amended 2026-11-14 a third time, by the owner (quality first, speed second):
decision 12 now loops until the review is clean and sets a fix level (default `minor`), decision 11's cap counts
every finding to fix, and decision 14 defines a nitpick.

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

A replay of the review step on Copilot CLI (a 22-file change, reviewer and seats on Gemini 3.8 Flash)
found two real major findings the single-agent review had missed, but took 16.7 minutes against 6.9.
The reviewer spent 4.3 minutes before the first seat: about two on the gates and two writing fifteen
packets by hand, reading lens files and calling `--help`. The seat batch lasted as long as its
slowest seat (252 seconds, which read the whole diff in chunks and re-ran the documentation gates
through MCP tools); one seat that returned nothing was re-sent alone for 3.9 minutes more; and the
refutation ran after everything else. Three seats spent 27 to 87 seconds deciding a lens did not
apply, which the changed-file list already showed.

## Decision

1. **One review dispatch where sub-agents can dispatch.** The conductor (or `bug-crusher`)
   dispatches `code-reviewer` once. The reviewer prepares the seats with `review seats`, starts them
   while the council gates run (decision 6), then adjudicates and computes the verdict with the
   verdict engine. A reviewer that holds a tool to start agents never applies a lens itself. Only one with
   no such tool applies the lenses itself, and its report says so.
2. **The `review-council` skill where they cannot.** The session a human started loads it in place
   of the one dispatch. The skill dispatches `code-reviewer` in hand-back mode (first body line
   `COUNCIL-SEATS: hand-back`). The reviewer runs `review seats --hand-back` and returns
   `STATUS: COUNCIL_SEATS_NEEDED` with its dispatch blocks. The main session starts the first seats
   in one batch together with a second reviewer dispatch that runs the gates, then the after-gates
   seats, and re-invokes the reviewer with one outcome line per seat. Each seat writes its output to the `Seat file:` its block
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

6. **Seats start while the gates run.** `kyber-weave review seats` syncs the tree's index, opens the
   cycle folder, writes the diff and the change half of `evidence.md` (each changed file with its
   diff line range), writes every packet with the lens charter inside, and prints each seat's
   dispatch block, in seconds. The reviewer sends the gate command and every seat that needs only
   the diff in one parallel batch, or starts the gates in the background where a shell call would
   hold up the batch. The gate run rewrites `evidence.md` with every result and the coverage, writes
   `duplicates.json`, and prints the gate-proven skips of ADR 0077. `test-adequacy`,
   `static-analysis-triage` and `duplicate-implementation` read a gate's output, so they start
   when the gates finish. Nothing else runs before the first batch: `review seats` replaces
   `doctor --ensure` and `review cycle`, and the reviewer has the exact command lines.
7. **No seat for a lens a catalogue file pattern rules out.** Where a lens's Applicability names the
   kinds of file it needs (`dependency-supply-chain`, `infra-workflow`, `test-infrastructure`, and
   `correctness` and `test-adequacy` on a documentation-only change), `review seats` checks the
   changed-file list against wide patterns and records the lens with `--not-seated` when nothing
   matches. Every other applicability question stays with the seat. The council is never scaled to
   the size or risk of the change.
8. **Each seat has a budget.** A `review-seat` dispatch has `arbiter.seat-budget` (3 minutes or 30
   tool calls, 2 minutes for one call), enforced by the worker guard as `KW-ARB-BUDGET-001`. Past it
   every read is denied with the instruction to return what the seat has, closing with
   `Budget reached, not examined: …`; the seat-file write is still allowed. The verdict command
   reports that line as a coverage gap (`KW-REVIEW-054`). Seats read only the diff lines their lens
   needs and do not re-run the documentation gate tools.
9. **Follow-up work goes out together and early.** A seat that returns nothing is sent once more in
   the next batch, never alone; after two empty replies it is recorded as not seated with "no
   result", except `correctness` and `test-adequacy`, without which the verdict is not computed.
   `review verdict --candidates` lists refutation candidates from the seats saved so far, and each
   candidate is refuted in its own spawn, all in one parallel batch with the after-gates seats and
   retries. The verdict command then runs once, as `kyber-weave review verdict .` with no other argument: it
   finds the newest cycle folder and reads the lenses `review seats` and the gate run recorded as not
   seated in its `not-seated.json`, so the reviewer never retypes a reason. Each review command ends
   with the `next:` command line to copy.

10. **The review commands ask the decision model, when one is configured.** The Arbiter's review
    questions hung off dispatch hooks that saw a seat-file path or no finding at all: in a replay the
    nitpick question was asked 13 times and answered "unsure" 13 times, the lens question was asked for
    1 of 16 seats, and the claim question was never asked while a refuter took 2.3 minutes. The
    commands hold the diff, the changed files and the seat files, so they ask instead, through the
    Arbiter's client, rules, floors and decision log. `review seats` asks `KW-ARB-LENS-001` for each
    seated judgement lens and leaves a confidently inapplicable one unseated. `correctness`,
    `test-adequacy` and the gate-reading lenses are never asked, and the question is only whether the
    lens's subject is in the diff, never the size or risk of the change. `review verdict` merges
    confident duplicates (`KW-ARB-DUP-001`), drops confident minor nitpicks (`KW-ARB-NITPICK-001`; never
    major or critical), skips the refuter for a candidate whose cited code confidently supports its claim
    (`KW-ARB-CLAIM-001`; a confident "contradicts" still goes to a refuter), and notes a severity that
    looks out of line with its impact (`KW-ARB-SEVERITY-001`, advisory), and matches a finding to a host
    severity override (`KW-ARB-OVERRIDE-001`, decision 15). Every change is printed with its
    confidence. The model is optional: provider `none`, no key, `--no-decision-model`, a fault or an
    unsure answer leaves the review exactly as it was, the first fault stops further calls, and nothing
    depends on the Arbiter's hooks or `arbiter.enabled`. The hook side of the four rules answers from
    step 0, so no question is asked twice.

11. **A fix round is checked narrowly, never by the whole council.** In a live Copilot CLI run the
    conductor asked, in its own words, to "re-run the full council over the whole accumulated change"
    after a one-finding fix, and the reviewer complied: 11.1 minutes against the first review's 7.4.
    `review seats` printed every seat and only hinted to send fewer. Now, in any cycle after the
    first, it seats only the lenses that raised the findings the fix round took, plus `correctness`
    and `test-adequacy` when the fix changed anything but Markdown, on the fix-only diff; the gate run
    re-runs only the gates whose inputs the fix touched, and its `after-gates:` line names only seated
    lenses. Only a fix delta of more than 300 changed lines (the quick-review threshold) seats every
    lens the delta's files call for; the first review is not scaled to change size. The conductor
    sends fixed verifier text, and the reviewer answers a widening request with the verifier cycle and
    says so. The loop caps are 5 cycles, or 3 in a row with no drop in the count of findings to fix (the `fix:` lines, minors included under the default); `review verdict` computes them from the cycle folders.
12. **The verdict names the fix round; the loop runs until the review is clean.** `review verdict`
    prints `fix:` lines (failed blocking gates, and every critical, major and minor finding),
    `follow-up:` lines (findings the policy leaves out, and findings the reviewer dropped as
    `pre-existing debt`) and a closing `fix round:` line (`KW-REVIEW-056`), and the conductor
    dispatches from them, one dispatch per owning worker carrying all of its lines. APPROVE means not
    blocked, not nothing to do: any `fix:` line gets a fix round and then a narrow verifier cycle, and
    this repeats until a cycle prints none (`review clean: proceed to archive`) or a cap stops it,
    when what is left is printed as `open:`, listed in the pull request under pending human review and
    filed with the follow-up work. The owner's rule: quality is priority one and speed priority two;
    three rounds of review and fixes that leave the independent AI reviewer on the pull request
    nothing to find are a success, and two rounds that leave it several findings are a failure.
    One setting, `review.policy.fix-level`, decides what is fixed; each level includes the ones before it:
    `critical` (failed blocking gates and critical findings), `major` (also majors), `minor` (also
    minors; the default) and `nitpick` (also nitpicks, for a team that wants everything fixed). It
    decides what is fixed, never what blocks: strictness, `major-count-blocks` and `min-confidence`
    decide that. Findings below the level are listed in the pull request and filed as todos; a nitpick
    below it appears only in the review report. The key it replaced, `minor-findings`, never shipped;
    a config that still carries it reads `follow-up` as `major` and `fix` as `minor`. Before this
    amendment an approval got exactly one fix round and the owner had not yet chosen.
13. **Wrong placement is major.** A class or file placed against the repository's documented
    structure (in C#, the wrong project, layer or folder) is graded `major` by the model-placement
    lens, and `minor` no longer includes a missed naming or style convention (that is a nitpick, never fixed; decision 14). In the run, a new DTO in the
    Domain layer was graded minor although the documented rule puts shared DTOs in a contracts
    project; wrong placement compounds, because every later contributor copies it. How existing code
    that breaks such a rule is handled is [ADR 0083](0083-a-documented-rule-outranks-the-code-around-it-and-out-of-scope-work-becomes-todos.md).

14. **A nitpick is not fixed unless the fix level says so.** A nitpick is a purely stylistic point with no effect on how the
    application operates: formatting, wording preference, naming style. Below fix level `nitpick`, lenses do not raise
    nitpicks (`REVIEW.md` *What not to flag*); the decision model's nitpick question uses the same
    words and drops a confident one from the counted set, and `review verdict` drops any finding a lens
    still graded `nitpick`. Either way the verdict report lists it as dropped, and nowhere else carries it: an
    external reviewer that raises one is tuned, not appeased. A naming issue that causes a defect or
    breaks a documented public contract is graded by its impact.

15. **The host grades severity last.** The owner asked whether a repository can set its own view of
    what is critical, major, minor or nitpick; prose in `REVIEW.md` only hoped a seat applied it, and a
    shipped lens file is overwritten on update. Severity now decides what the fix round takes
    (decision 12), so the host gets a structured `Severity overrides` table in its review guidance
    (the `<review-guidance>` file, not a second one): finding class, severity, optional lens and
    technology scope, reason and a citable id. Precedence is host override, then the shipped lens
    guidance, then the general definitions; a row moves a grade up or down, `nitpick` and `critical`
    included. `review seats` gives each seat only its rows, a seat cites the row it applied
    (`override:`), and `review verdict` applies the row's severity before anything else reads it
    (`KW-REVIEW-057`), so the verdict rules, the fix-level routing, the PR sections and the loop's
    counts all see the host's grade, and `findings.json` records which row applied. With a decision
    model, an uncited finding is matched to its rows (`KW-ARB-OVERRIDE-001`); without one, only cited
    rows apply. A grade the host set is not put to the nitpick filter or the severity note. `docs
    validate` checks the rows (`KW-REVIEW-058` to `-061`, and the advisory `-062`). No "never report"
    value: suppression already does that. This repository's first row is decision 13's placement rule,
    stated as the owner's own.

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
- **A budget can cut a seat short.** A seat stopped at 3 minutes reports what it did not examine
  rather than everything it would have found. The line makes the gap visible; it does not close it.
- **The file patterns can be wrong in one direction.** They are wide so that a lens is seated when in
  doubt; an infrastructure change in a folder no pattern names would leave `infra-workflow` unseated.
- **The budget is enforced only where the worker guard runs** (Copilot CLI and Claude Code with the
  Arbiter on). Elsewhere it is the seat's own instruction.
- **The decision model judges text, not code paths.** A confident "supports" spares a refuter on the
  cited lines and the lines the evidence cites; a claim about something no line shows (a folder that
  holds no file) stays with the refuter only because the model is then unsure. The nitpick and
  duplicate steps are enforced by default; `shadow: true` on either rule returns it to logging only.
- **An override is matched by wording.** A seat or the decision model decides whether a finding
  belongs to a row's class; a vague class matches too much or too little, and the "lowers a shipped
  critical" check (`KW-REVIEW-062`) is a word-overlap advisory, not proof.
- **Where nesting is off by default, the operator must turn it on.** OpenCode and Kilo stay on the
  skill path until the operator sets `subagent_depth`, and the render cannot see that setting. VS Code
  Copilot without its setting falls back to the reviewer applying the lenses itself.

## Consequences

- On Claude, Copilot, Cursor and Pi a review costs one reviewer dispatch. The conductor sees only
  the verdict.
- `review-lens` and `review-triage` stay in the conductor's and `bug-crusher`'s rosters, for the
  `review-council` skill alone.
- A harness moves to nesting by changing one row in `GetSubagentDispatch`, once it is verified.
