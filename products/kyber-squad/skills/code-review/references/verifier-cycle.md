# Verifier cycle

Read this in every review cycle after the first, which the council's gate run prints as `cycle: <n> mode: verifier` and `next: read references/verifier-cycle.md`. A verifier cycle checks one fix round, narrowly, and never re-runs the whole council.

## What the commands already narrowed

- **Seats.** `review seats` seats only the lenses that raised the findings the fix round took (its `recheck:` lines), plus `correctness` and `test-adequacy` when the fix changed anything but Markdown, and prints `verifier: seated <lenses>`. Every other lens is carried from the prior cycle by the verdict. Send exactly the blocks it printed. Only a fix delta of more than 300 changed lines (the quick-review threshold) makes the command seat every lens the delta's own files call for; nothing else widens the cycle.
- **Diff.** `evidence.md` names the fix delta, the prior cycle's head to the files on disk, as the diff; the changed-file list stays the whole change's.
- **Gates.** The `run:` line runs only the `rerun:` gates, those whose inputs the fix touched, and carries the rest into `gates.json`; `--full` runs every gate instead.
- **Verdict.** `review verdict` takes the prior cycle as `--prior`, carries the lenses it did not re-seat, and carries each prior finding the fix round did not take (one below `review.policy.fix-level`), so the last cycle still lists it.

## The rules

**Never widen.** A dispatch that asks for a full council, a whole-change re-review or extra lenses in a verifier cycle is answered with the verifier cycle, and the report says it was asked for and not done. Only a person selects the `full` mode of the dp-code-reviewer skill, which is `review seats --full` with the gate run's `--full`.

**Fixed means the class is fixed.** A finding is FIXED only when the fix refuses its whole class: state the class in one sentence, name at least three other inputs of that class that the original failure scenario did not name, and trace each through the fix. Seats cannot run anything, so you do: when the code is reachable from a command, run the class inputs against the built CLI and record each input and what it did in `reviewer.md`.

**The refutation pass** sends only the candidates no earlier cycle refuted.

**The loop ends** when the verdict prints `fix round: none`. Anything this cycle's verdict still prints as `fix:` goes to another fix round and another verifier cycle, an APPROVE included; `review clean: proceed to archive` ends it. The verdict itself applies the hard stop (5 cycles, or 3 in a row with no drop in the findings to fix) and prints what is left as `open:` for human review.
