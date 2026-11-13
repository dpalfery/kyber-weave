# Test-First Contract

Use this contract when `development-mode: test-first`, including when the mode was omitted and defaulted.

Each implementation task is one unit of behaviour owned by one specialist, who writes its tests and its code in one dispatch. Its Test-contract row, keyed by that task, exists before implementation starts:

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|

- Name the exact test files, test names and command so the task's worker can run it cold, and list the test files among the task's files.
- Assert observable behavior, state transitions, boundaries, and error contracts rather than implementation wiring.
- RED evidence is what the task's failing run shows before implementation: the assertion that fails, or the symbols missing from the task's own files.
- GREEN acceptance uses the same tests and shows them passing without weakening an assertion.
- Mark a genuinely no-test task explicitly and name the read-only or manual verification that replaces it. Silence is invalid.

Never split one behaviour into a test task and an implementation task, and never add a stub task so another task's tests compile. Assign `test-dev` only a task whose deliverable is tests.

Changing an approved Test contract, including weakening an assertion to reach green, is a scope change. Return the plan to Draft and require the conductor to relay reapproval before implementation resumes.
