---
name: dp-code-reviewer
description: 'Use when a change has already been reviewed and the author has pushed fixes — re-checks only the prior findings rather than re-running the whole council, then recomputes the verdict. Also use to select a review mode: shadow to calibrate without gating, or full to force a fresh council scan. Do NOT use for the first review of a change; invoke code-review for that.'
license: MIT
---

# Review modes and the re-review loop

The `code-review` skill runs one review. This one governs what happens **around** it: which
mode a review runs in, and what happens after findings come back and get fixed.

The reason this is a separate concern is cost. A full council over a diff is thirteen lens
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
willingness to keep it on. Run shadow until the false-positive rate is known and the
`always-human` paths have been checked against what the repository actually holds. Then
switch. Do not skip it on a repository the reviewer has never seen.

### verifier

The default after a fix push, and the reason this skill exists.

1. Take the previous review's accepted findings.
2. For each, re-check **only that finding**: is the specific defect, at that file and line,
   still present? Re-read the code; do not trust the fix description.
3. Re-run the gates. Gates are cheap relative to the council and their result is the
   evidence, so they run every time — never carry a gate result forward from an earlier run.
4. Re-run **only** the lenses that own a finding still outstanding, plus any lens whose
   applicability predicate the new diff newly satisfies.
5. Recompute the verdict from the surviving findings and the fresh gate results.

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

1. **Review** — `code-review` in the current mode.
2. **Verdict**:
   - `APPROVE` → the change is ready. Report it and stop.
   - `NEEDS_HUMAN` → **stop immediately.** Do not iterate, do not attempt to fix into an
     approval. Report which rule escalated it and hand it to a person.
   - `REQUEST_CHANGES` → continue.
3. **Route** the findings back to the agent that wrote the code — not to a general worker.
   Send the findings verbatim: the excerpt, the evidence, the failure scenario, and the
   suggestion. A paraphrased finding loses the specificity that makes it actionable.
4. **Wait** for the fix. Do not fix anything here; this skill orchestrates, it does not edit.
5. **Re-review** in `verifier` mode. Return to step 2.

### Termination

- `APPROVE` — done.
- `NEEDS_HUMAN` — escalated, and the loop ends there.
- **Three cycles without the accepted-finding count falling** — stop and escalate. A loop
  that is not converging is not going to, and the usual cause is a finding the implementing
  agent does not understand or does not accept. That needs a person, not a fourth attempt.
- **Five cycles total** — stop and escalate regardless of progress.

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
Verdict:   {APPROVE|REQUEST_CHANGES|NEEDS_HUMAN}   Risk: {LOW|MEDIUM|HIGH}
Findings:  {surviving} surviving ({fixed} fixed, {new} new since cycle {n-1})
Gates:     {passed}/{total}   {failing gate ids, if any}
Next:      {COMMIT_READY|FIXING|RE_REVIEWING|ESCALATED}
```

The `fixed` and `new` counts are the ones worth reading. A cycle where surviving findings
did not fall is the signal to escalate, and it is invisible if only the total is reported.
