# Test-First Contract

Use this contract when `development-mode: test-first`, including when the mode was omitted and defaulted.

Each implementation task is one unit of behaviour owned by one specialist, who writes its tests and its code in one dispatch. Its Test-contract row, keyed by that task, exists before implementation starts:

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|

- Name the exact test files, test names and command so the task's worker can run it cold, and list the test files among the task's files.
- Assert observable behavior, state transitions, boundaries, and error contracts rather than implementation wiring.
- RED evidence is what the task's failing run shows before implementation: the assertion that fails, or the symbols missing from the task's own files.
- GREEN acceptance uses the same tests and shows them passing without weakening an assertion.
- Write the cases the end-of-run review will ask for, so no fix round is needed for them. For each behaviour the task adds, the row's observable behavior names: the normal case; each filter or option both set and unset (a query with an optional filter has an unfiltered test and a filtered one); the empty result; each input the code refuses, with the exact error; each input the code normalises or defaults (blank or whitespace treated as absent, trimming, a default value), with the value that results; and for a new or changed endpoint, one caller the policy allows and one it denies. A case you leave out is named in the row with the reason.
- Mark a genuinely no-test task explicitly and name the read-only or manual verification that replaces it. Silence is invalid.

Never split one behaviour into a test task and an implementation task, and never add a stub task so another task's tests compile. A task whose capabilities are only test authoring is a task whose deliverable is tests alone.

Changing an approved Test contract, including weakening an assertion to reach green, is a scope change. Return the plan to Draft and require the conductor to relay reapproval before implementation resumes.
