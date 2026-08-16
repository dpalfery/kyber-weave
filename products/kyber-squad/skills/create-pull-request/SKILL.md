---
name: create-pull-request
description: Guides developers and agents through creating a Pull Request in the MotorcycleRAG repository. Covers the full lifecycle, pre-PR validation, branching conventions, template usage, CI checks, review expectations, and post-merge documentation closeout.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Create Pull Request — MotorcycleRAG

Use this skill when preparing or creating a Pull Request in the MotorcycleRAG repository. It describes the project-specific standards, checks, and workflow that PR authors must follow. For the mechanical steps of creating the PR via GitHub or the `gh` CLI, consult the `create-pull-request-github` skill.

---

## 1. Pre-PR checklist

Before opening a PR, verify all of the following:

- [ ] **Branch from the correct base.** PRs target `main` (production) or `develop` (integration). Feature and bugfix branches branch from `develop` unless the change is a hotfix targeting `main`. Confirm the parent branch conclusively — consult the `create-pull-request-github` skill.
- [ ] **All local tests pass.** Run the relevant test suites:
  - .NET: `dotnet test MotorcycleRAG.sln --configuration Release`
  - Python (local processor): `cd 2-Application/local-processing-service && poetry run pytest`
  - Web UI: `cd 1-Presentation/MotorcycleRag.WebUI && npm test`
  - Admin Desktop: `cd 1-Presentation/MotorcycleRAG.AdminDesktop && npm test`
- [ ] **Code builds clean.** `dotnet build MotorcycleRAG.sln --configuration Release` completes with no errors.
- [ ] **Lint and formatting pass.** Markdown files pass `markdownlint-cli2`; C# code follows `.editorconfig` and `dotnet format` conventions; Python follows `ruff` rules in `pyproject.toml`.
- [ ] **Canonical documentation is updated.** If the change affects a component's public interface, configuration, architecture, runtime, operations, or workflow, the corresponding README and detailed documentation have been updated. See the path defined by the **Documentation Standard** property in the root `AGENTS.md` for the required shape.
- [ ] **Catalog entry is current.** If the change introduces, moves, renames, or materially alters a component, update the catalog at the path defined by the **Component Catalog** property in the root `AGENTS.md`.
- [ ] **Scoped instructions are followed.** Every subtree has its own `AGENTS.md` with additional rules. Read the nearest scoped `AGENTS.md` before changing files in that subtree.
- [ ] **Clean Architecture is preserved.** Inner layers never depend on outer layers. Contracts contains interfaces only; `Contracts.Models` contains shared DTOs only. Business invariants belong in Domain; Application services belong in `Services`. See the Clean Architecture rules at the path defined by the **Clean Architecture Rules** property in the root `AGENTS.md`.
- [ ] **No secrets or credentials.** The change contains no tokens, connection strings, passwords, `.env` files, customer data, or unsafe deployment commands.
- [ ] **Dev environment is ready.** If setting up from scratch, consult the `setup-dev-environment` skill which covers all tooling.

## 2. Branch naming conventions

Use descriptive branch names that match the following patterns:

| Branch type | Pattern | Example |
|---|---|---|
| Feature | `feature/<short-description>` | `feature/azure-ai-search-index` |
| Bug fix | `bugfix/<short-description>` | `bugfix/null-ref-on-empty-result` |
| Hotfix | `hotfix/<short-description>` | `hotfix/cors-header-regression` |
| Task/chore | `task/<short-description>` | `task/update-dotnet-10` |
| With issue ID | `<type>/<issue-number>-<description>` | `feature/42-audit-error-service` |

When the branch name includes a numeric GitHub issue ID as the first token after the type prefix, the PR description should include `Closes #<id>` to auto-link the issue.

## 3. PR title and description standards

### Title format

Use a concise, descriptive title in the imperative mood, matching the branch name's intent:

```
[Component] Brief description of the change
```

Examples:
- `[API] Add Azure AI Search indexer endpoint`
- `[Admin Desktop] Fix null reference on empty document list`
- `[Local Processor] Update docling dependency to v2.4`

Capitalize the first word of the description. Do not end with a period unless the title contains multiple sentences (rare).

### Description body

Fill out the Pull Request template (`.github/PULL_REQUEST_TEMPLATE.md`) completely. Every section matters — see §4 below for detailed guidance on each field.

## 4. Using the PR template

The repository PR template at `.github/PULL_REQUEST_TEMPLATE.md` is a checklist-driven template. Here is how to fill each section:

### Summary

State the problem being solved and the outcome. Answer "why" more than "how." Be specific enough that a reviewer unfamiliar with the issue context understands the change's purpose.

**Good:** "The Azure AI Search indexer currently blocks on concurrent indexing requests, causing timeout failures for large documents. This change introduces a background queue with configurable concurrency."
**Avoid:** "Fixed indexing."

### Type of Change

Select exactly one. If more than one applies, pick the most impactful category and note additional categories in the summary.

### Component and boundaries

- **Component identity:** Look up the owning component in the path defined by the **Component Catalog** property in the root `AGENTS.md`. If the change spans components, list all affected.
- **Scoped instructions:** Check for a `AGENTS.md` in each affected subtree and verify compliance.
- **Architecture boundaries:** Verify Clean Architecture layering (see the Clean Architecture rules at the path defined by the **Clean Architecture Rules** property in the root `AGENTS.md`).

### Related Issues

Link to the GitHub issue(s) this PR addresses. Use `Closes #<n>` to auto-close an issue on merge. If the PR is one step of a larger issue, use `Part of #<n>`. If no issue exists, state the origin of the requirement (e.g., "Discovered during manual testing of x86 deployment").

### Validation

List focused verification. Do not say "tests pass" generically — be specific:

- Which test suites were run.
- Whether new tests were added for the change.
- Any manual verification performed and in what environment.
- Coverage implications if applicable.

### Documentation impact

This section drives the docs-dev plan-closeout workflow. Be honest:

- Check "canonical documentation updated" only if you updated it in this PR.
- If no update was needed, explain why (e.g., "Internal refactor with no public interface change").
- Documentation updates are required when: public interface, configuration, architecture, runtime, operations, or workflow changes. See the Documentation Standard at the path defined by the **Documentation Standard** property in the root `AGENTS.md`.

### Security and operations

- Verify no secrets are committed (tokens, connection strings, passwords, `.env`).
- If the change introduces a new configuration key or environment variable, document it and update the relevant onboarding or reference documentation.
- If the change affects deployment or operational procedures, document the impact.

## 5. Required CI checks

The PR Gate workflow (`.github/workflows/pr-gate.yml`) runs automatically on every PR targeting `main` or `develop`. The following jobs must pass or be explicitly waived:

| Phase | Job | Trigger | Notes |
|---|---|---|---|
| Build & unit | `build-test` | Code or infra changes | Runs all unit test suites (Linux) |
| Docs quality | `docs-quality` | Documentation changes | Markdown lint, link check, catalog validation, secret scan |
| Security | `codeql` | Code or infra changes | CodeQL C# analysis with `security-extended` queries |
| Security | `trivy` | Code or infra changes | Trivy OSS dependency and filesystem scan |
| Security | `semgrep` | Code or infra changes | Semgrep Community SAST |
| Security | `iac-scan` | Code or infra changes | Checkov scan of Dockerfiles and workflows |
| Deep validation | `unit-mobile` | Code or infra changes | MAUI unit tests on macOS |
| Deep validation | `integration` | Code or infra changes | Integration tests (non-Azure category) |
| Deep validation | `e2e` | Code or infra changes | End-to-end tests with mock services |
| Skill quality | `skill-gate` | Code or infra changes | Kyber-Weave skill validate + lint + scan |

### Handling check failures

1. **Build-test failure:** Inspect the build log. Common causes: missing dependency restore, broken test, or a build error introduced by the change.
2. **Docs-quality failure:** Run `npx markdownlint-cli2` locally. Fix any broken links (check with `lychee --offline`). Run `bash 7-Deployment/scripts/validate-docs.sh` to verify catalog coverage.
3. **Security failure (CodeQL/Trivy/Semgrep/Checkov):** Review the SARIF output in the GitHub Security tab. Address HIGH or CRITICAL findings. If a finding is a false positive, annotate it in the PR with a brief explanation referencing the relevant SARIF rule.
4. **Integration/E2E failure:** Check the test log for the failing test name. Reproduce locally with `dotnet test --filter "FullyQualifiedName=<test-name>"`.
5. **Skill-gate failure:** Run Kyber-Weave commands locally: `kyber-weave skill validate`, `kyber-weave skill lint`, or `kyber-weave skill scan` on `.agents/skills/`. Fix any validation errors or HIGH+ severity findings.

If a check is consistently failing due to an environment issue (not your change), add a PR comment documenting the failure and tag a maintainer.

## 6. Review process

### Who reviews

- **Code review:** The `code-reviewer` agent (or a human reviewer) performs a structured review covering correctness, security, performance, maintainability, and architecture compliance.
- **Security review:** The code-reviewer includes a branch-diff security vulnerability pass. CodeQL, Trivy, Semgrep, and Checkov scans run automatically in CI.
- **Plan closeout review:** For plan-backed work, the `docs-dev` agent verifies the plan's acceptance criteria against the implementation and updates canonical documentation before the plan is archived.

### What reviewers check

Reviewers verify:
- Pre-PR checklist items (see §1).
- Correctness and functional logic.
- Adherence to Clean Architecture and scoped instructions.
- Adequate test coverage.
- Documentation accuracy and completeness.
- No secrets or credentials.
- CI check results.

### Responding to review feedback

- Address all review comments, either by making the requested change or by explaining why the current approach is correct.
- Re-request review after pushing changes.
- Do not merge until all conversations are resolved and all required checks pass.

## 7. Post-merge steps

### For plan-backed work

When the PR is merged, the orchestrator follows the plan closeout process defined in the root `AGENTS.md`:

1. The orchestrator assigns a `docs-dev` plan-closeout task.
2. The documentation specialist verifies the plan's acceptance criteria against the implemented behavior.
3. The specialist updates the affected canonical documentation (if not already updated in the PR).
4. The specialist maintains the plan index (at the path defined by the **Plan Index** or **Component Catalog** properties in the root `AGENTS.md`).
5. The plan is archived under `6-Docs/archive/plans/` with status `Archived`.

The PR is not the final step for plan-backed work — documentation verification and archiving are required before the plan is considered complete.

### For non-plan work

- Verify that the PR description's "Documentation impact" section reflects the final state.
- If the change introduced new configuration, runtime dependencies, or operational procedures, ensure the relevant `6-Docs/` documentation is updated.
- Delete the feature branch after merge (GitHub offers a "Delete branch" button on merged PRs).

## 8. Common pitfalls

| Pitfall | Resolution |
|---|---|
| PR targets `main` but should target `develop` | Update the base branch in the PR before merging |
| Template section left blank or as placeholder | Fill it out — reviewers will request changes |
| Docs-quality check fails on link validation | Run `lychee --offline` against the changed `.md` files |
| Trivy reports a vulnerability in an indirect dependency | Update the dependency, or add a pinned transitive override in the owning project file with a justification comment |
| `dotnet build` succeeds locally but fails in CI | Check for platform-specific code, missing SDK workloads, or environment variable assumptions |
| No issue linked in the PR description | Find or create the tracking issue and add `Closes #n` to the description |

## See also

- Pull Request template (`.github/PULL_REQUEST_TEMPLATE.md`) — the template every PR must fill out.
- `create-pull-request-github` skill — mechanical steps for creating the PR via GitHub MCP or `gh` CLI.
- PR Gate workflow (`.github/workflows/pr-gate.yml`) — the CI pipeline that validates every PR.
- Documentation standard — defined by the **Documentation Standard** property in the root `AGENTS.md`.
- Component catalog — defined by the **Component Catalog** property in the root `AGENTS.md`.
- Architecture rules — defined by the **Clean Architecture Rules** property in the root `AGENTS.md`.
- `AGENTS.md` — repository-wide mandatory instructions.
- `setup-dev-environment` skill — dev environment setup and validation.
