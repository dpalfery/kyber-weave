# Standard Verification Contract

Use this contract only when the conductor supplies an explicit user opt-out and the plan records `development-mode: standard`.

Every implementation task has a verification-contract row:

| Task | Automated test project or file | Runner command | Acceptance behavior | Additional current evidence |
|---|---|---|---|---|

Standard mode removes the requirement to demonstrate historical RED. It does not remove automated tests, current verification, task audit, or final council review.

- Name the exact automated test surface and current command that proves the acceptance criteria.
- Add build, analyzer, packaging, deployment, or read-only integration evidence when the task requires it.
- Mark a genuinely no-test task explicitly and name the validation that replaces an automated test.
- Require fresh evidence after implementation and again after review-driven rework.

Changing an approved verification contract returns the plan to Draft and requires the conductor to relay reapproval before implementation resumes.
