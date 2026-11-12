---
id: adr/0069-the-review-council-loops-until-approve-and-computes-its-verdict-from-seat-output
title: The review council loops until APPROVE and computes its verdict from seat output
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-11
component: ReviewCouncil
decided-by: []
supersedes: []
code-refs:
  - VerdictEngine
  - SeatOutputParser
  - CitedLineCheck
---

# ADR 0069: The review council loops until APPROVE and computes its verdict from seat output

## Status

Accepted, 2026-11-11.

## Context

The review council ([ADR 0002](0002-three-layer-review-council-verdict-engine.md)) was written to
re-review after fixes, but by 2026-11-11 its prose had drifted to one pass. An assessment of pull
requests 582 to 591 measured what that drift cost:

- Each council ran once. The fixes for its own findings shipped unreviewed: the conductor's
  remediation pass ended in a task gate, and `bug-crusher` reviewed once.
- `kyber-weave review verdict` failed 7 of 8 calls on hand-built findings JSON (`KW-REVIEW-023`).
  Reviewers then wrote verdicts by hand, and at least six majors were downgraded to minor on the way.
- Every seated run waited about 15 minutes for `inspectcode` to time out, and the timeout counted as
  a blocking failure. A loop that runs until `APPROVE` could never end against that. (A separate
  change has since stopped `inspectcode` timing out and lets it run alongside the other gates; the
  deadline below still bounds any gate that runs long.)
- Seats were exempt from reporting discovery, so 3 CodeGraph calls against 322 raw reads went
  unseen. One run left out 14 lenses, security included, with no reasons.

## Decision

The council re-reviews after every fix round until it returns `APPROVE`. Every critical and major
finding, and every failed blocking gate, goes to the worker that owns its scope; every cycle after
the first is a verifier cycle over the fix delta and the prior findings. Minor findings never start
a cycle. The loop has a fixed hard stop: 5 review cycles, or 3 cycles in a row in which the count of
surviving critical and major findings does not fall. At the stop, the findings still open are
listed in the pull request as pending human review, and delivery continues. The loop runs on the
open pull request, and required CI alone decides ready.

The verdict is computed by `kyber-weave review verdict --seats <cycle>/seats`, from the seats'
own output saved unchanged, one file per lens. The reviewer can keep a finding, or drop it with a
reason (`--drop <id>=<reason>`); it cannot edit a finding or change its severity. Every catalogue
lens needs a seat file or a recorded reason for not seating it.

## Alternatives considered

- **One bounded remediation pass, then park.** This was the rule in force. It shipped every fix
  unreviewed, and parking a finding after one pass hid whether the fix worked. Rejected for the
  loop with a hard stop, which ends on evidence (no progress) rather than on a count of one.
- **A hand-built findings JSON as the verdict input.** This was the documented path. It is where
  `KW-REVIEW-023` came from, and it is the step where severities were rewritten. Rejected for
  parsing what the seats already write. A command converting seat YAML to JSON was also rejected,
  because it keeps an editable file between seat and verdict.
- **Holding ready until the loop ends.** This would contradict the accepted delivery rule that
  delivery never waits for the council and that required CI alone decides ready. Bot review of
  mid-loop code is a smaller cost than reopening that contract.
- **Switching a verifier cycle to a full council when a fix adds a file, a dependency or a public
  contract.** Rejected during review. Such a delta widens the verifier cycle (more lenses
  re-seated) instead; `full` is a mode a person selects.

## Consequences

- **A run deadline, and timed-out gates are NOT RUN.** The council runs its gates with
  `review gates --deadline`, which bounds the run at `review.council-deadline-seconds` (default
  600). Under `--deadline`, a gate stopped by its own timeout or by the deadline is NOT RUN
  (`KW-REVIEW-039`) and does not fail the run. Without `--deadline` nothing changes: there is no
  run deadline, and a timed-out blocking gate fails `review gates` (`KW-REVIEW-022`) as before, so
  CI, task gates and `--gate` re-checks are unchanged. `review verdict` always reads a timed-out or
  deadline-cut gate as NOT RUN, never blocking. The opt-in flag is a correction made in review: an
  exit code of 0 on a timed-out blocking gate everywhere would have let CI pass with tests not run.
- **Seats report discovery.** A seat closes with `Discovery: codegraph N, docs N, direct reads N`
  above its attestation line, and an `Index fallback:` line only when it read around the index. On a
  findings or `NO FINDINGS` seat, a missing line, an unexplained run of direct reads, or a
  fallback value outside the three modes draws `KW-REVIEW-044`; a `SKIPPED` seat is exempt. It is a warning, never a verdict input: the cost is now visible, not
  reduced.
- **Unseated lenses need a reason.** The verdict refuses to compute while a catalogue lens has
  neither a seat file nor a `--not-seated <lens>=<reason>` (`KW-REVIEW-041`); a verifier cycle
  carries earlier coverage through `--prior`. A seat file must be named for one lens.
- **The seat parser reads what seats write.** It tries strict YAML first, then the flat schema
  seats really produce. A file that declares `NO FINDINGS:` or `SKIPPED:` and also holds findings
  is refused (`KW-REVIEW-040`). A cited line outside its file is moved to the line holding its
  excerpt, or dropped (`KW-REVIEW-043`).
- **Each cycle keeps a record.** `.agent_scratchpad/<task-id>/review/cycle-<n>/` holds the seat
  files, `gates.json`, `findings.json` and `head` until the run ends, because the next verifier
  cycle reads it.
- **Cost, measured on the run that shipped this.** Cycle 1, a full council, seated 13 lenses plus
  one batched refutation for about 1.3M tokens in 17 minutes. Cycle 2, a verifier cycle, seated 10
  for about 1.0M tokens in 12 minutes. Cycle 3 seated 6 for about 0.44M tokens in 11 minutes and
  returned `APPROVE`. Majors went 9, then 1, then 0, and no cycle used a hand-written verdict.
- **An approved council still missed a class of defect, and four mechanisms answer it.** After
  the council approved this change, the first bot review found nine real defects in the same code.
  Five were crashes or wrong accepts on malformed input: an overflowing parsed count, a null
  character in a cited path, a cited line of 0, and a null element in a findings list and in a gates
  list. Two were contradictory seat files accepted silently, and two were prose statements that
  disagreed with a sibling document. In seven of the nine a rule covered the defect and the owning
  seat had read the code, but traced only the inputs it thought of; one fix was confirmed FIXED on
  the single input its finding named; one file was never opened. The answers are: a required
  input-site table in the correctness seat, whose absence on a seat that reported findings or
  `NO FINDINGS` draws `KW-REVIEW-047` (a `SKIPPED` seat is exempt); a verifier rule
  that a finding is FIXED only when the fix refuses its whole class; two documentation checks (a
  rule's subject is checked at the call site, and every statement of a rule id or command is
  compared side by side); and hand-offs, so test-adequacy routes an unproven malformed input to
  correctness and the reviewer gives seats no hunt list of its own.
- **What becomes harder.** The reviewer must write seat files to disk, and a reviewer could still
  alter them on the way; capturing seat output in the Arbiter's hook would close that. Each fix
  round costs another council cycle, bounded by the hard stop.
- **Amended 2026-11-11: the developer side carries the same rules.** A developer whose change adds
  or alters a site where an outside value is parsed, converted, or passed to a file or collection
  API fills the input-site table the correctness lens defines, at the developer exit gate. A
  developer fix for a reported defect covers the defect's whole class, by the rule the verifier
  uses to call a finding FIXED. Every fix for a review finding verified as real ships with a test
  that fails without the fix, bot review rounds included; a finding classified as a nitpick needs
  none. The rules live in the `delivery-execution` skill's developer exit gate and the review-fix
  skills; this record states the reviewer side and the amendment only.
