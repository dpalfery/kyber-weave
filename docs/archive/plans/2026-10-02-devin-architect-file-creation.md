---
id: archive/plans/2026-10-02-devin-architect-file-creation
title: "Devin harness: architect profile file-creation and artifact persistence (#161)"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# Devin harness: architect profile file-creation and artifact persistence (#161)

**Status: Complete.** Delivered per test-first development mode. Archived per `KW-DOC-LIFECYCLE-003`.
Addresses GitHub issue [#161](https://github.com/dpalfery/kyber-weave/issues/161): Devin renderer's architect subagent profile cannot create new files, blocking plan persistence.

Decisions Q1–Q3 harvested 2026-10-03 as [ADR 0028](../../adr/0028-devin-target-scoped-authoring-capability-profiles.md) (mechanism and scope; Q1 was resolved as the combined option (c) — the Devin-scoped profile is the mechanism and the conductor pre-creation protocol is the documented fallback — and Q2 is the scope boundary that keeps `docs-dev` and `task-reviewer` unwidened), with the behaviour in [Kyber-Squad architecture](../../kyber-squad/architecture.md) §8, [requirements](../../kyber-squad/requirements.md) KS-002 and the Devin matrix row, and [onboarding](../../kyber-squad/onboarding.md).

**Development mode:** `test-first`.

---

## 1. Decision Ledger

### [Q1] Mechanism for enabling new file creation on Devin for headless authoring roles
- **Context:** On Devin, `architect` is lowered to a subagent profile with `filesystem.write: allow` and `process.execute: ask`. Because Devin subagents cannot interactively prompt for permission confirmations, `ask` narrows to withheld (`safety-narrowed`), so `exec` is withheld. Devin CLI has no dedicated `write` tool (only `edit`, which fails when the target file does not exist, and `apply_patch`, which is restricted to `agent.codex_tools`). Consequently, `architect` cannot create new files such as `docs/plans/YYYY-MM-DD-*.md`, returning `STATUS: PLAN_WRITE_ERROR`.
- **Options:**
  - **(a) Target-specific profile envelope / grant `exec` for Devin authoring roles:**
    Define a target-specific capability profile (or renderer envelope) granting `process.execute: allow` to `architect` on Devin (matching Copilot's `architect-copilot`). Provide explicit instruction guidance in the agent body/reference that on Devin, when creating a nonexistent file, the agent initializes the path via `exec` (e.g. `touch <file>`) before applying `edit`. This also enables `architect` on Devin to run `docs validate` and `docs drift` as required by its completion contract.
  - **(b) Conductor pre-creation / degradation protocol:**
    Keep subagent boundaries strictly write-tool-only (no `exec` shell). Update the conductor's plan, spec, and doc paths (`conductor/references/plan-path.md`, `intake-path.md`, `spec-path.md`) so that when dispatching to Devin subagents (or any harness lacking native subagent file-creation tools), the parent conductor (which runs in the main Devin Local session with full shell access) pre-creates an empty file at the destination path before invoking the subagent.
  - **(c) Combined approach:**
    Grant `process.execute: allow` to `architect` on Devin (Option a) so it can create files and run documentation validation checks autonomously, while ALSO documenting the conductor degradation protocol (Option b) in conductor references as a fallback mechanism.
- **Recommendation:** **(a)** (or **(c)**) — Option (a) mirrors how Copilot solved this exact constraint (`architect-copilot`), keeps `architect` autonomous, and crucially allows `architect` on Devin to execute `docs validate` and `docs drift` to fulfill its required `PLAN_READY` gate contract.

### [Q2] Scope of roles to address in this issue
- **Context:** Issue #161 observes: "Related profiles worth auditing for the same gap: product-owner (writes specs), docs-dev (writes docs), task-reviewer (may only need read)."
- **Options:**
  - **(a) Scope to `architect` and `product-owner`:** Both are headless planning roles that persist governed artifacts under `docs/` (`docs/plans` and `docs/specs`) and block conductor intake flows when unable to create new files.
  - **(b) Scope to all governed document creators (`architect`, `product-owner`, `docs-dev`):** Ensures all write-permitted roles in Kyber-Squad on Devin can create their governed files (`task-reviewer` remains read-only).
  - **(c) Scope strictly to `architect` only:** Solve only the immediate bug reported on `architect` in #161, leaving `product-owner` and `docs-dev` for separate follow-up issues.
- **Recommendation:** **(a)** — Addresses both headless intake roles that blocked in practice without needlessly broadening worker roles like `docs-dev` before an observed failure.

### [Q3] Development Mode
- **Context:** The governing mode for implementation.
- **Options:**
  - **(a) test-first:** Tests written and failing before implementation edits.
  - **(b) standard:** Implementation with post-hoc verification.
- **Recommendation:** **(a) test-first** (matches user request constraint).

---

## 2. Problem and Goal

### 2.1 Context and Evidence
During a conductor intake run on Devin Desktop / CLI (`squad install --target devin`, Kyber-Squad 0.1.7-rc.14):
1. `architect` investigated the request and formulated a Draft plan (`docs/plans/2026-09-28-kyber-utilities-status-line-slice.md`).
2. When attempting to write the new plan file, `architect` called its only write tool (`edit`).
3. Devin's `edit` tool returned an error indicating failure on a nonexistent file path.
4. `architect` correctly rolled back its index entry in `docs/plans/README.md` and returned `STATUS: PLAN_WRITE_ERROR`.
5. The session required manual human intervention (creating an empty file) to proceed.

### 2.2 Root Cause
- In `products/kyber-squad/profiles/capabilities.yml`, `architect` has:
  ```yaml
  filesystem.write: allow
  process.execute: ask
  ```
- In `src/KyberWeave.Core/Squad/Rendering/DevinRenderer.cs`:
  - `filesystem.write` lowers to `["edit", "write", "apply_patch", "notebook_edit"]`.
  - `process.execute` lowers to `["exec", "get_output", "write_to_process", "kill_shell"]`.
  - Because Devin subagents cannot prompt for approval during background runs, `ask` narrows to withheld (`safety-narrowed`). `exec` is therefore omitted from `allowed-tools`.
- In Devin CLI / Desktop runtime:
  - Devin does not provide a native `write` tool to models.
  - `apply_patch` is only enabled when `agent.codex_tools` is configured.
  - The only tool exposed to Claude Opus (the `deep-planning` model pinned for `architect`) is `edit`.
  - Devin's `edit` tool modifies existing files and fails when the file does not exist.
  - Without `exec`, `architect` cannot initialize the file via `touch` or shell redirection.

### 2.3 Goal
Enable Devin `architect` (and `product-owner`) to reliably persist newly created plans and specs (`architect` via target profile and shell initialization, and `product-owner` via target profile and conductor pre-creation fallback), backed by automated tests.

---

## 3. Investigation Findings

1. **Copilot precedent:** Copilot encountered a similar constraint (no `ask` state, strict tool allow-list) and introduced `copilot-capability-profile: architect-copilot` in `capabilities.yml` with `process.execute: allow`.
2. **Execution requirement in plan authoring:** `architect/references/plan-authoring.md` requires running `docs validate` and `docs drift` before declaring `PLAN_READY`. With `process.execute` withheld, `architect` on Devin cannot run these checks anyway.
3. **Renderer degradation accounting:** If `process.execute` is `allow` and `filesystem.write` is `allow`, `CapabilityDegradations.BuildCapabilityNotIsolable` returns `null` (no degradation), because write access is already granted.
4. **Agent instructions:** If `exec` is granted on Devin, `architect/references/plan-authoring.md` includes explicit guidance for Devin on how to initialize nonexistent files before editing. `product-owner` has no equivalent shell-routing instruction in canonical source and depends on conductor pre-creation fallback to create new files.

---

## 4. Test Contract (test-first)

Automated tests in `tests/KyberWeave.Tests/`:
1. `DevinRendererContractTests`:
   - Verify that the rendered `architect` profile on Devin contains the necessary tools to create new files (e.g. `exec` if profile is updated).
   - Verify degradation records accurately reflect the new capability decisions.
2. `SquadSourceTests` / `SquadSourceValidator`:
   - Validate any new or updated capability profiles in `products/kyber-squad/profiles/capabilities.yml`.
   - Ensure validation rejects invalid target configurations.
3. Conductor / Architect contract tests:
   - Ensure documentation checks and artifact creation pathways pass clean validation.

---

## 5. Implementation Tasks

- [x] **T1: Failing Tests for Devin Architect Capabilities** (`tests/KyberWeave.Tests/DevinRendererContractTests.cs`)
- [x] **T2: Capability Profile & Rendering Updates** (`products/kyber-squad/profiles/capabilities.yml`, `src/KyberWeave.Core/Squad/Rendering/DevinRenderer.cs`)
- [x] **T3: Authoring Reference Guidance for Devin** (`products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`)
- [x] **T4: Documentation Checks and Regression Suite** (`docs validate .`, `docs drift .`, `dotnet test`)
- [x] **T5: PR Creation and Handoff** (Draft PR against trunk titled `[issue #161] [hal.hermes.agy] Devin architect can create new files`)

---

## 6. Verification and Gates

- `dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal`
- `dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal`
- `dotnet build KyberWeave.sln -c Release --no-restore`
- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
