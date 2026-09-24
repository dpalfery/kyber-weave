# GitHub Actions (CI/CD) Code Review Best Practices

## Workflow Security
- **Pin Actions Versions:** Ensure third-party actions are referenced with a specific version (commit SHA or tag, e.g., `@v4`) instead of `@latest` or `@master` to prevent supply-chain attacks.
- **Secrets Handling:** Confirm secrets are passed securely via GitHub's secrets store and NEVER echoed or logged in plaintext in any `run:` commands.
- **Minimize Permissions:** Check that workflows explicitly restrict `GITHUB_TOKEN` permissions to only those needed using the `permissions:` key.
- **Untrusted Input Mitigation:** Ensure user-supplied input (e.g., `github.event.issue.body`) is not directly passed into shell commands without sanitization to avoid injection.

## Workflow Efficiency and Organization
- **Modular Workflows & Reuse:** Avoid monolithic workflow files. Split workflows by concern and use reusable workflows to prevent duplication.
- **Job Parallelization & Concurrency:** Look for opportunities to run independent jobs (lint, test, build) in parallel. Check for explicit concurrency controls to prevent parallel runs of the same PR from interfering.
- **Caching:** Verify usage of caching (build artifacts, package caches) to improve workflow speed, especially for heavy dependency installation.

## CI Integration & Quality Gates
- **Triggers:** Confirm CI triggers are correctly set (e.g., `on: pull_request`) to run tests and linters for PRs.
- **Quality Gates:** Ensure code coverage or quality gates are enforced and fail the build if thresholds aren't met.
- **Skipped Steps:** Flag any skipped steps or disabled tests that might reduce quality coverage.

## Automation Hooks
- **Safe AI Integration:** If using AI-based code review bots, ensure the model's API key is stored in secrets and not exposed (e.g., dropping sudo privileges).
- **Structured Output:** Check that automated reviews produce structured output (JSON) focusing on correctness, performance, security, and maintainability.
