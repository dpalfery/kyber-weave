# Lens: test-adequacy

## Applicability

Applies when the diff changes executable logic. Runs **after** the test and coverage gate, so
the coverage report is available as an input.

Skip for documentation-only changes. If the change has logic but the coverage gate did not
run or produced nothing, do not skip — report the absence, because "we do not know whether
this is tested" is the finding.

## What this lens owns

Whether the tests that exist would actually catch this change breaking. Not how many there
are, and not what percentage of lines they touch.

## What to look for

**The test that proves the fix.** For a bug fix, there must be a test that fails without the
change and passes with it. Read the test and ask whether it would genuinely have failed
against the old code. A test written after the fix, asserting the new behaviour without ever
having been red, proves that the code does what it does — which is not the same claim.

**Behaviour, not implementation.** Tests asserting on internal calls, private state, call
counts, or the exact shape of an intermediate value break when the code is refactored and
pass when the behaviour regresses. They are a maintenance cost carrying negative safety
value. Tests asserting on observable outcomes are the opposite.

**Coverage that is padding.** Tests over trivial accessors, generated code, or
constructor-assigns-field exist to move a percentage. Where the diff adds tests like these
alongside a coverage threshold, name it plainly: the threshold is being satisfied rather
than met.

**Uncovered branches that matter.** Read the coverage report against the diff, then use
judgement. An uncovered error path in payment handling is a finding; an uncovered
`ToString` override is not. Report the branch, what reaching it requires, and what happens if
it is wrong — not the percentage.

**Assertions that cannot fail.** A test with no assertion. An assertion against a value the
test itself computed the same way as the code. A comparison of something to itself. An
exception assertion so broad that any failure satisfies it, including the wrong one.

**Edge cases the change introduces.** New boundary, new branch, new failure mode — is each
exercised? This is where uncovered-branch analysis and correctness analysis meet, and it is
the most valuable thing this lens produces.

**Coverage floor.** Report the measured figures against the floor the host declares under
`review.coverage`. State the gap; do not compute a verdict from it.

## What this lens must not report

- A raw coverage percentage as a finding in itself. The gate reports the number; you report
  what is unprotected and why it matters.
- Test naming, structure, or framework preferences.
- Requests for tests on code the change did not touch.
- Correctness defects in the production code — the correctness lens owns those. If a test is
  missing *because* the code is wrong, report the missing test and leave the defect alone.
