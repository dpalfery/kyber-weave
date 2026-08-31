# Test-First Evidence

Use this reference only for `development-mode: test-first`.

The packet must contain the approved Test-contract row and matching RED/GREEN evidence:

- the named test exists in the declared test project or runner;
- RED was observed before implementation and failed for the intended missing behavior, not a syntax, setup, or unrelated failure;
- GREEN is a current run of the same contract after implementation;
- the assertion still expresses the approved observable behavior and was not weakened to pass;
- rework before pass 2 or pass 3 includes fresh GREEN evidence and, when the contract itself changed under approved re-planning, fresh matching RED evidence.

Missing, mismatched, or invented RED/GREEN evidence is a fixable FAIL with `ESCALATION: none`. Name exactly which contract field or run is absent. Do not create the evidence yourself and do not accept a passing test that was added only after implementation as proof of test-first ordering.

If the approved behavior or Test contract must change, the task is no longer ordinary rework. FAIL and require the conductor to return the artifact to its planning approval gate.
