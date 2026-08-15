---
name: code-review
description: "Universal code review skill. Reviews code for correctness, security, performance, maintainability, and tech-specific best practices (.NET, Python, React, SQL, Pulumi, Azure, GitHub Actions). Enforces a mandatory pre-merge test + coverage gate via run-comprehensive-tests. Includes a branch-diff security-vulnerability review — the single skill for all code review."
license: MIT
metadata:
  author: David R Palfery
  version: 3.2.0
---

# Code Review Instructions for Code Review Agent

**Goal:** Ensure all code changes meet universal and technology-specific quality standards, **and** that the full test suite plus mandatory unit-coverage thresholds pass before the change is approved to merge. You are the Code Review Agent. Your sole responsibility is to evaluate code against the following standards and provide structured feedback.

## Step-by-Step Procedure

1. **Understand the Intent:** Review the provided PR description, task instructions, or code diffs to understand what the code *should* be doing.
2. **Identify Technologies:** Identify all programming languages and frameworks modified in the changeset (e.g., C#, Python, React, SQL).
3. **Load Specific References:** For each identified technology, you MUST read its corresponding detailed checklist in the `references/` folder before proceeding:
   - [.NET (C#)](references/dotnet.md)
   - [Python](references/python.md)
   - [React](references/react.md)
   - [SQL](references/sql.md)
   - [Pulumi](references/pulumi.md)
   - [Azure](references/azure.md)
   - [GitHub Actions](references/github-actions.md)
4. **Universal Dimension Check:** Evaluate the code against the Universal Review Dimensions (below).
5. **Technology-Specific Check:** Evaluate the code against the checklists found in the references loaded in Step 3.
6. **Security Review (always):** Invoke the `security-review` skill to perform a branch-diff vulnerability pass — identify HIGH-CONFIDENCE (≥8/10) exploitable vulnerabilities newly introduced by the change, applying its false-positive exclusions. Do not duplicate that skill's methodology here.
7. **Pre-Merge Test & Coverage Gate (always — blocking):** Run the unified test + coverage suite to confirm every test passes **and** the mandatory unit-coverage thresholds declared in the coverage config (path declared as **Test Coverage Config** in the repository root `AGENTS.md`) are met. This gate is **non-negotiable** for an Approve verdict; failing it downgrades the verdict to `Needs Changes` regardless of how clean the other findings are.
   - **macOS / Linux (default):**
     ```bash
     bash 5-Test/scripts/run-comprehensive-tests.sh
     ```
   - **Windows (PowerShell):**
     ```powershell
     pwsh -File 5-Test/scripts/run-comprehensive-tests.ps1
     ```
   - **Coverage-only fast path (use while iterating locally — not a substitute for the full run before approving):**
     ```bash
     bash 5-Test/scripts/run-comprehensive-tests.sh --unit-coverage --coverage-threshold 85
     ```
   - **Threshold enforcement:** the script reads `thresholds.fileLinePercent` and `thresholds.classLinePercent` from the path declared as **Test Coverage Config** in the root `AGENTS.md` registry. Treat the higher of the two configured values as the mandatory floor for this gate and pass it via `--coverage-threshold` only when an override is required.
   - **What MUST pass to approve:**
     - Build succeeds (`dotnet build` step inside the script).
     - Unit tests (`.NET` `MotorcycleRAG.UnitTests.slnf`, Python `local-processing-service`, Admin Desktop Node, WebUI Node) all green.
     - Integration tests (`5-Test/MotorcycleRAG.IntegrationTests`) all green.
     - End-to-end tests (`5-Test/MotorcycleRAG.EndToEndTests`, `Category!=Integration`) all green.
     - Unit-coverage report at or above the configured `fileLinePercent` / `classLinePercent` thresholds.
   - **What is opt-in (do not block on these unless the change touches the area):**
     - `--azure-integration-tests` — requires live Azure credentials; only required if the diff touches Azure-integrated code paths.
     - `--load-tests` — requires the API running locally; only required for performance-sensitive changes.
   - **On failure:** record each failing suite/coverage shortfall as a `Critical` finding in the "Pre-Merge Gate Findings" section of the report, including the exact failing test path, the command that was run, and the threshold gap. Do **not** return `Approve` until the gate is re-run green.
8. **Compile Feedback:** Create a structured output of findings as requested, folding Pre-Merge Gate and security-review findings into the same report. The Pre-Merge Gate status (pass/fail) MUST appear in the Overall Assessment.

## Universal Code Review Dimensions

Evaluate all code against these universal dimensions:
- **Correctness & Functional Logic:** Does it meet requirements and handle edge cases? Are tests present and passing?
- **Code Design & Maintainability:** Does it follow architecture rules? Is it clear, modular, and DRY?
- **Performance & Efficiency:** Will it perform well and scale? (Look for N+1 queries, heavy loops, missing indexes).
- **Security & Compliance:** Are inputs validated? Are secrets secure? Are authorizations checked?
- **Observability:** Are errors and critical events logged appropriately with enough context?
- **Testing & CI/CD Integration:** Is there adequate test coverage? Does the CI pipeline catch issues?
- **Developer Experience (DX):** Does the code improve overall codebase health? Are comments and docs updated?

## Expected Output Format

When generating the review, use the following structured format:

### Pre-Merge Gate Findings
Record the result of Step 7 first — it is the gating verdict. A failing gate forces `Needs Changes` even if the rest of the review is clean.
- **[Critical] [Test suite or coverage threshold failure]**
  - **Command Run:** the exact `run-comprehensive-tests` invocation (script path + flags).
  - **Result:** pass / fail + the specific suite(s) or metric(s) that failed (e.g. `dotnet-unit: 3 failed`, `fileLinePercent: 81.2% < 85% threshold`).
  - **Location:** failing test path(s) and/or under-covered file(s) from the generated report (`TestResults/UnitCoverage/...`).
  - **Explanation:** Why the failure blocks merge (regression risk, coverage regression, threshold breach).
  - **Suggestion:** Actionable fix — failing test remediation, added unit test for uncovered branch, or threshold-rationale discussion if the floor is genuinely unattainable.
- If the gate passes, emit a single line: `Pre-Merge Gate: PASS — run-comprehensive-tests green; coverage ≥ the Test Coverage Config threshold, file/class line.`

### Findings
List each issue found clearly:
- **[Severity (Critical/Major/Minor)] [Title]**
  - **Location:** `path/to/file.ext:LineNumber`
  - **Explanation:** Why this is an issue.
  - **Suggestion:** Actionable recommendation to fix it.

### Overall Assessment
- **Verdict:** (Approve / Needs Changes)
- **Pre-Merge Gate:** (PASS / FAIL) — reference the `run-comprehensive-tests` output artifact path.
- **Coverage:** file-line / class-line percentages vs. the **Test Coverage Config** floor.
- **Summary:** A brief summary of the overall code quality and a clear next step. When the Pre-Merge Gate is FAIL, the next step is the remediation actions listed in the Pre-Merge Gate Findings section, not additional code-style polish.
