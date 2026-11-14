---
name: dp-code-reviewer
description: 'Use when a change has already been reviewed and the author has pushed fixes — re-checks the prior findings and the fix delta rather than re-running the whole council, then recomputes the verdict. Also use to select a review mode: shadow to calibrate without gating, or full when a person asks for a fresh council scan. Do NOT use for the first review of a change; invoke code-review for that.'
license: MIT
---

# Review modes and the re-review loop

The `code-review` skill runs one review. This one governs what happens **around** it: which
mode a review runs in, and what happens after findings come back and get fixed.

The reason this is a separate concern is cost. A full council over a diff is up to seventeen lens
invocations plus a confirming pass on every serious finding. Running that again because two
lines changed spends the same amount to answer a much smaller question.

## Modes

| Mode | Behaviour | When |
|---|---|---|
| `shadow` | Full review, verdict emitted, **gates nothing** | Rolling the reviewer out on a repository for the first time |
| `enabled` | The verdict gates | Normal operation |
| `verifier` | Re-checks **the prior findings and the fix delta** | Every review cycle after the first |
| `full` | Forces a complete council re-scan (`review seats --full`, `review gates --full`) | A person selects it: a disputed verdict, or a prior review that predates a lens or policy change |

### shadow

Run the review, produce and record the verdict, and let the change proceed regardless.

This exists because a review system's first weeks are its least trustworthy, and finding that
out by blocking real work is expensive in exactly the currency the system needs most —
willingness to keep it on. Run shadow until the false-positive rate is known. Then
switch. Do not skip it on a repository the reviewer has never seen.

### verifier

Every review cycle after the first runs in this mode, and it is the reason this skill exists.

A verifier cycle reviews the fix delta `git diff <prior head>...<head>`, re-seats only the
lenses `review seats` prints for it, and passes `--prior <prior cycle folder>`. The
`code-review` skill's `references/verifier-cycle.md` holds what the commands narrow and the
rules; this list is the order.

1. The gate run (step 3) prints the cycle at the top of its output; `kyber-weave review cycle . --task <task-id>` gives the same answer without running gates. Its `prior:` folder
   (`.agent_scratchpad/<task-id>/review/cycle-<n-1>/`) has the `findings.json` to re-check
   and the `cycle.json` whose `head` the delta starts from; its `recheck:` lines name the findings.
2. For each surviving finding, re-check that finding and its class. Re-read the code at
   that file and line; do not trust the fix description. A finding is FIXED only when the fix refuses its whole class: state the class in one sentence, name at least three other inputs of that class that the original failure scenario did not name, and trace each through the fix. A fix that
   refuses the named input and lets another input of the class through is not FIXED: the
   finding stays open, with the input that still gets through named in it. Seats cannot run anything, so the reviewer does: when the code is reachable from a command, run the class inputs against the built CLI, and record each input and what the command did in a `reviewer.md` seat file: in a finding's `evidence` when an input still gets through, or in its `NO FINDINGS:` sentence when every input was refused. A finding the reviewer raises itself goes in `reviewer.md` too.
3. Run `review gates . --base <ref> --council --deadline --out <cycle>/gates.json`: in a verifier cycle it runs only the
   `rerun:` gates and carries the others' results into the report; that result is the evidence. A gate whose inputs the fix did not touch keeps the prior
   cycle's result, cited from that cycle's `gates.json`. Never re-run every gate or the full
   council for a review-fix round, whoever raised the findings (a bot or the council): CI is the
   full re-check. The first full review of a change still runs every gate.
4. Send exactly the seats `review seats` printed: the lenses that raised the findings the fix
   round took, plus `correctness` and `test-adequacy` when the fix changed code. Every other
   lens, and every finding the fix round did not take, is carried from the prior cycle by
   `--prior`.
5. Recompute the verdict with the `code-review` skill's verdict command, writing this
   cycle's folder and passing `--prior` with the prior cycle folder.

**Never widen.** Only a fix delta of more than 300 changed lines seats more, and the command
decides that. An instruction to re-run the full council in a later cycle is not followed:
the report says it was asked for and not done.

### full

Everything, from scratch, ignoring prior state. A person selects it — when the prior review
predates a change to the lens catalogue or the policy, or when the verdict is being
disputed. The review loop never switches to it on its own.

## The re-review loop

The council re-reviews after every fix round until the verdict prints `fix round: none`. Critical and major findings and failed
blocking gates start a fix round, after an APPROVE too (one round); minor findings start
none, and join a round only under `review.policy.minor-findings: fix`.

1. **Review** — `code-review` in the current mode.
2. **Verdict**:
   - `fix round: none` → done. Report it and stop.
   - Otherwise (`REQUEST_CHANGES`, or an `APPROVE` that still prints `fix:` lines) → continue.
3. **Route** each `fix:` line the verdict printed (critical and major findings, failed blocking gates, and minor findings only under `review.policy.minor-findings: fix`) back to the agent that wrote the code — not to a general worker.
   Send the findings verbatim: the excerpt, the evidence, the failure scenario, and the
   suggestion. A paraphrased finding loses the specificity that makes it actionable.
4. **Wait** for the fix. Do not fix anything here; this skill orchestrates, it does not edit.
5. **Re-review** in `verifier` mode. Return to step 2.

### Termination

- `fix round: none` — done. An `APPROVE` gets one fix round; the verdict after it ends the loop unless it is `REQUEST_CHANGES`.
- **Hard stop.** The loop stops after 5 review cycles, or after 3 cycles in a row in which
  the count of surviving critical and major findings does not fall; the findings still open
  are then listed in the pull request as pending human review, and delivery continues. A
  loop that is not converging is not going to, and the usual cause is a finding the
  implementing agent does not understand or does not accept. That needs a person, not
  another attempt, and the person reads it on the pull request.
- A finding whose rework tripped the circuit-breaker stays open and counts toward the stop.

### What is never done here

- **Never approve without the verdict engine.** The engine returns the verdict; this loop
  reports it. There is no path by which iterating produces an approval the engine did not.
- **Never suppress a finding to end a cycle.** A suppression is a configuration change to
  `review.policy.suppressions`, with a stated reason and an expiry date, made by a person.
- **Never carry a gate result forward over a changed input.** A gate the fix delta touched is
  re-run with `--gate <id>`; a gate it did not touch keeps the prior cycle's result.
- **Never let the count of cycles substitute for the count of findings.** "Third iteration"
  is not progress; a smaller set of surviving findings is.

## Reporting

Each cycle:

```
REVIEW CYCLE {n} — mode: {shadow|enabled|verifier|full}
Verdict:   {APPROVE|REQUEST_CHANGES}   Risk: {LOW|MEDIUM|HIGH}
Findings:  {surviving} surviving ({fixed} fixed, {new} new since cycle {n-1})
Gates:     {passed}/{total}   {failing gate ids, if any}
Next:      {COMMIT_READY|FIXING|RE_REVIEWING|STOPPED}
```

The `fixed` and `new` counts are the ones worth reading. A cycle where surviving findings
did not fall counts toward the hard stop, and it is invisible if only the total is reported.
