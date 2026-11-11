---
name: dp-code-reviewer
description: 'Use when a change has already been reviewed and the author has pushed fixes — re-checks only the prior findings rather than re-running the whole council, then recomputes the verdict. Also use to select a review mode: shadow to calibrate without gating, or full to force a fresh council scan. Do NOT use for the first review of a change; invoke code-review for that.'
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
| `verifier` | Re-checks **only the prior findings** | After a fix push |
| `full` | Forces a complete council re-scan | The change moved on materially, or the prior review is stale |

### shadow

Run the review, produce and record the verdict, and let the change proceed regardless.

This exists because a review system's first weeks are its least trustworthy, and finding that
out by blocking real work is expensive in exactly the currency the system needs most —
willingness to keep it on. Run shadow until the false-positive rate is known. Then
switch. Do not skip it on a repository the reviewer has never seen.

### verifier

The default after a fix push, and the reason this skill exists.

A verifier cycle reviews the fix delta `git diff <prior head>...<head>`, re-seats only the
lenses that own an outstanding finding or whose predicate the delta newly satisfies, and
passes `--prior <prior cycle folder>`.

1. Read the prior cycle folder your packet names
   (`.agent_scratchpad/<task-id>/review/cycle-<n-1>/`): its `findings.json` holds the
   findings to re-check and its `head` file holds the prior head the delta starts from.
2. For each surviving finding, re-check **only that finding**: is the specific defect, at
   that file and line, still present? Re-read the code; do not trust the fix description.
3. Re-run the gates. Gates are cheap relative to the council and their result is the
   evidence, so they run every time — never carry a gate result forward from an earlier run.
4. Re-seat **only** the lenses that own a finding still outstanding, plus any lens whose
   applicability predicate the fix delta newly satisfies. Every other lens is carried from
   the prior cycle by `--prior`; a lens the prior cycle did not cover either is recorded with
   `--not-seated <lens>=<reason>`.
5. Recompute the verdict with the `code-review` skill's verdict command, writing this
   cycle's folder and passing `--prior` with the prior cycle folder.

**When verifier mode is not enough.** Escalate to `full` — or re-run every applicable
lens over the changed scope — when the fix changes behaviour outside the original
finding. That includes a new file, a new dependency, or a changed public contract, and
it also includes changed logic in a file the prior review already covered: a targeted
re-check of the named finding will not look at the new risk next to it. A strictly
isolated fix — the defect at that file and line, and nothing else — stays in
`verifier`.

### full

Everything, from scratch, ignoring prior state. Use when escalating from verifier, when the
prior review predates a change to the lens catalogue or the policy, or when the verdict is
being disputed.

## The re-review loop

The council re-reviews after every fix round until it returns APPROVE. Only critical and
major findings, and failed blocking gates, start a fix round; minor findings never loop.

1. **Review** — `code-review` in the current mode.
2. **Verdict**:
   - `APPROVE` → the change is ready. Report it and stop.
   - `REQUEST_CHANGES` → continue.
3. **Route** the critical and major findings and the failed blocking gates back to the agent that wrote the code — not to a general worker.
   Send the findings verbatim: the excerpt, the evidence, the failure scenario, and the
   suggestion. A paraphrased finding loses the specificity that makes it actionable.
4. **Wait** for the fix. Do not fix anything here; this skill orchestrates, it does not edit.
5. **Re-review** in `verifier` mode. Return to step 2.

### Termination

- `APPROVE` — done.
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
- **Never carry a stale gate result forward.** Gates are re-run every cycle.
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
