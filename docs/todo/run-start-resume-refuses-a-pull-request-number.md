---
id: todo/run-start-resume-refuses-a-pull-request-number
title: run start --resume refuses a pull request number, though the approved decision accepts one
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-11-07
---

# run start --resume refuses a pull request number, though the approved decision accepts one

The approved decision names `--resume <branch-or-pr>`, so a caller may resume a run from a branch
name or from a pull request. The command implements the branch form only: a bare number is refused
with exit `2`, so a caller who follows the decision reads the refusal as "resume is unavailable"
rather than "this spelling is not implemented".

The work the pull request form needs is not in the parser. Resolving a number to a head needs a
network fetch of `pull/<n>/head`, and the command has no fetch step today, which is why the form is
refused rather than resolved.

**Work.** Either implement the pull request form — fetch `pull/<n>/head`, then resume from the
commit it names — or narrow the decision in place so it reads `--resume <branch>` and the refusal
says which form is accepted. A refusal that names the accepted form is the minimum; leaving a caller
to discover the divergence is not.

**Acceptance.** `run start --resume <branch-or-pr>` behaves as whichever form is now decided: a
number resumes the run at the fetched head, or the refusal names a branch as the only accepted
form and the recorded decision says so. A test pins both the accepted form and the refusal.
