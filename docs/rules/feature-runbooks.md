---
id: rules/feature-runbooks
title: Feature local run and test documentation standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-09-03
---

# Feature local run and test documentation standard

This rule mandates that every functional feature in Kyber-Weave provides reproducible,
unambiguous operational instructions for local execution and testing.

---

## Mandate

Every component declared with `Type: Feature` in [`docs/catalog.md`](../catalog.md) must satisfy
one of the following two criteria:

1. **Companion Execution Runbook**: Reference a companion `runbook.md` or `onboarding.md`
   document detailing step-by-step local prerequisites, build prerequisites, development runners,
   and test execution commands; **OR**
2. **Explicit Non-Executable Declaration**: Contain an explicit declaration in its
   [`docs/catalog.md`](../catalog.md) entry and overview documentation explaining why local execution
   is inapplicable (e.g., a pure domain model/class library, a CI gate diagnostic engine, or a
   build-time script) along with the command used to verify its test suite (e.g., `dotnet test`).

---

## Motivation

AI coding agents and human engineers alike depend on deterministic, copy-pasteable local run instructions.
When a feature lacks an operational runbook:
- Developers waste hours reverse-engineering `package.json` scripts, environment variables, or build flags.
- Autonomous coding agents fail during execution loops because they cannot infer implicit build prerequisites
  (such as compiling a CLI binary before launching an Electron desktop wrapper).
- Multi-surface tools (such as desktop apps, menubar tray processes, and web dashboards) drift out of alignment
  if engineers only test one surface.

Establishing a strict standard ensures that any contributor or automated agent can immediately pull the repository,
satisfy prerequisites, launch the feature locally, and run its test harness.

---

## Requirements

### R1: Runbook or Onboarding Companion
Any feature that exposes a runnable binary, CLI command, web server, desktop application, or interactive TUI
must maintain a companion runbook (e.g. `docs/<feature>/runbook.md` or `docs/<feature>/onboarding.md`).
The runbook must be indexed in [`docs/catalog.md`](../catalog.md) and cross-linked from the feature's architecture
and index documents.

### R2: Concrete Prerequisites & Build Steps
Runbooks must state exact toolchain prerequisites with version bounds:
- Runtime requirements (e.g., `Node.js >= 22.13.0`, `.NET SDK >= 9.0`, `Rust >= 1.80`).
- Mandatory build sequences that must precede dev execution (e.g., `npm --prefix dash run build:cli`).
- Required environment variables and local fallback paths (e.g., `CODEBURN_BIN`).

### R3: Dev Runners & Local Verification Commands
Runbooks must document the exact command lines to launch local dev modes:
- Hot-reloading development servers (e.g. `npm run dev`, `vite`).
- Local bridge or mock utilities (e.g. `node dash/app/demo-bridge.mjs`).
- Port allocations and local URLs (e.g. `http://127.0.0.1:5173`, `http://127.0.0.1:4900`).

### R4: Test Suite Commands
Every runbook must document the specific test runner commands for validating local changes:
- Unit test commands (e.g. `npm --prefix dash/app run test`, `cargo test --manifest-path ...`).
- Targeted test file invocations for fast local feedback loops.
- What specific scenarios the test suite guards (e.g., XSS escaping, layout breakpoints, version gating).

### R5: Multi-Surface Completeness
If a feature delivers user experiences across multiple surfaces (e.g., KyberDash's Electron app, Windows tray app,
Web dashboard, and Terminal TUI), the runbook must provide a dedicated section for each surface with its respective
prerequisites, runners, and tests.

---

## Exceptions

Certain components declared as features do not represent standalone executables or interactive applications.
Such components are exempt from providing a dedicated `runbook.md`, provided they fulfill the declaration requirement:

1. **Pure Class Libraries & Domain Models** (e.g., `ContextHygiene`):
   - Components that serve as shared governance libraries or domain abstractions consumed by other features.
   - *Requirement*: The catalog entry must explicitly designate the component as a library and state the command
     used to execute its test suite (e.g., `Governance library; tested via dotnet test`).
2. **Pipeline Engines & Gate Infrastructure** (e.g., `CI Pipelines`):
   - Diagnostic and reporting engines intended primarily for automated pipeline execution.
   - *Requirement*: Must reference a workflow runbook (e.g. `ci-pipelines/workflows-runbook.md`) documenting how
     the pipeline or gate is triggered and validated.
3. **CLI Control Planes with Self-Documented Onboarding** (e.g., `KyberSquad`, `ReviewCouncil`):
   - Features providing command-line tools whose primary invocation is documented via dedicated onboarding guides
     (e.g., `kyber-squad/onboarding.md`).

---

## Verification & Conformance

Compliance with this standard is verified through two mechanisms:

1. **Catalog Integrity Audit**: During code reviews and plan closeouts, the `Detailed documentation` column of
   [`docs/catalog.md`](../catalog.md) is inspected to confirm that every `Type: Feature` row either links to an
   operational runbook / onboarding guide or states an explicit library/engine exception.
2. **Ontology Validation**: Companion runbooks must conform to the repository documentation ontology (`KW-DOC-SPEC-003`),
   using valid frontmatter with `doc-type: runbook` and respecting the pairing invariant (omitting `source-root` and
   `code-refs` for process-only runbooks).
