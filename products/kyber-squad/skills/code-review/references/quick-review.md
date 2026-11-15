# Quick review

A change of at most 300 changed lines (added plus removed, from `git diff --numstat <base>...HEAD`) gets the quick review before its pull request is opened; a larger change gets the full pre-push council.

The quick review is one pass by the session that made the change. It starts no seats and no
spawns, runs no gates beyond what the root `AGENTS.md` requires before a pull request, and does
not run the verdict engine. Walk the whole diff against this checklist:

1. **Intent.** Does the diff do what its description says, and nothing it does not say?
2. **Correctness of the changed lines.** Boundaries, absent values, error paths, and state.
3. **Siblings.** When the diff changes a rule's wording in one file, list every other file that carries the same rule text, check each one, and report each sibling the diff left stale.
4. **Contract-assertion strength.** A test or contract check that would pass with the fix reverted is a finding (`REVIEW.md` R14).
5. **Defect classes.** The defect-class checklist of the [pre-push gate](pre-push.md), step 3.

Fix every major-or-worse finding before the pull request is opened. The pull request
description carries one line: `Quick review: <n> findings, <m> fixed, open: <ids or none>`. In this skill, a finding verified as real is one that survived the council's adjudication; a quick review has no council, so the session's own check against the cited code stands in for it. The test rule in step 2 of the [pre-push gate](pre-push.md) applies to a quick review's fixes too.
