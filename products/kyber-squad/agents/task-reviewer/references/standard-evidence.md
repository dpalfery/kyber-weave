# Standard Evidence

Use this reference only for `development-mode: standard`.

The packet must contain the approved verification contract and fresh evidence from the current tree:

- the named automated tests exist and their runner completed with the reported result;
- required build, analyzer, package, deployment, or read-only integration checks are present when the contract names them;
- evidence covers the task's acceptance criteria and exact scope;
- rework before pass 2 or pass 3 includes a new run rather than reusing stale output.

Standard mode does not require historical RED evidence. It still requires appropriate automated tests, current verification, this three-pass task audit, and the end-of-run `code-reviewer` council.

Missing or mismatched verification evidence is a fixable FAIL with `ESCALATION: none`. If the approved verification contract itself must change, require the conductor to reopen that contract's approval gate rather than accepting an improvised substitute.
