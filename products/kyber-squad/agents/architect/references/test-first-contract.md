# Test-First Contract

Use this contract when `development-mode: test-first`, including when the mode was omitted and defaulted.

Every implementation task has a Test-contract row created before implementation starts:

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|

- Name the exact test surface and command so `test-dev` can execute the task cold.
- Assert observable behavior, state transitions, boundaries, and error contracts rather than implementation wiring.
- RED evidence must show the contract test existed and failed for the intended missing behavior before implementation.
- GREEN acceptance uses the same contract test and shows it passing without weakening the assertion.
- Mark a genuinely no-test task explicitly and name the read-only or manual verification that replaces it. Silence is invalid.

The task graph sequences RED before GREEN for each scope and preserves GREEN through REFACTOR and review. Disjoint RED and GREEN work may run concurrently when dependencies and file scopes permit it.

Changing an approved Test contract, including weakening an assertion to reach green, is a scope change. Return the plan to Draft and require the conductor to relay reapproval before implementation resumes.
