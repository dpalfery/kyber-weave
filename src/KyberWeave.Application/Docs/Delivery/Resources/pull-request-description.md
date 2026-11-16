<!--
The one pull request description format. The create-pull-request skill fills it by hand, and the
deliver tool on the kyber-weave docs server fills it from what the run recorded; neither keeps a
second copy. Everything inside this comment is guidance and is never sent.

Write for a human reviewer who knows engineering but not this run: plain words, short, why more
than how. No internal rule ids, tool names, agent names, decision or ledger ids, or scratch
paths unless the reviewer needs one to act. Do not paste raw commit lists or file lists; the
provider shows both.

Each {{token}} is replaced by its section's text. A section whose token has no value is left out,
heading and all. When the host repository has its own pull request template, fill the host's
sections instead: each host section takes the section below whose subject it asks for (summary,
type, linked work, changes, testing, documentation, notes); a host section none of these answers
keeps its checklist for the reviewer; and the sections below the host template does not ask for
(Not verified, Decisions made for you to check, Pending human review, Follow-up work filed, Notes)
follow it.

- summary: the problem and the outcome, in one to three sentences.
- type: bug fix, feature, breaking change, refactor, documentation, and so on.
- linked-work: the issue or work item, as the skill's Linked work section decides; omit when none.
- changes: a short list of what changed, summarized, not the commits.
- validation: evidence as short readable lines, such as "Checks per task: 4 tasks, all passed
  (build, test, format)" and "Review: approved, no open findings". Never write "tests pass"
  without naming what ran.
- not-verified: every check that did not run or could not be confirmed, one line each with the
  reason; "Nothing: every check listed above ran." when there is none.
- decisions: decisions made without a person (autonomous mode) that rest on no authoritative
  source, one line each, so the reviewer can overturn them; omit when there are none.
- pending-review: review findings still open, one line each; omit when there are none.
- follow-ups: the todos filed for work found outside this change's scope, one line each with its
  path; omit when none was filed.
- documentation-impact: the documentation-impact outcome verbatim, plus what documentation
  changed or why none was needed.
- notes: anything else reviewers should know.
-->
## Summary

{{summary}}

## Type of change

{{type}}

## Linked work

{{linked-work}}

## Changes

{{changes}}

## Validation

{{validation}}

## Not verified

{{not-verified}}

## Decisions made for you to check

{{decisions}}

## Pending human review

{{pending-review}}

## Follow-up work filed

{{follow-ups}}

## Documentation impact

{{documentation-impact}}

## Notes

{{notes}}
