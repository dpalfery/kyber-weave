---
id: plans/2026-10-05-product-owner-model-alignment
title: "Squad: Align product-owner with the architect model profile and remove haiku everywhere (#286)"
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-05
development-mode: test-first
---

# Squad: Align product-owner with the architect model profile and remove haiku everywhere (#286)

**Status: Ready.** Approved by Hal (approve-and-execute gate exercised 2026-10-05).  
**Date:** 2026-10-05  
**Development mode:** `test-first`  
**Goal:** Address GitHub issue [#286](https://github.com/dpalfery/kyber-weave/issues/286): align the `product-owner` subagent model profile with `architect` across every harness, remove `haiku` from Kyber-Squad models across all harnesses, and raise `general.claude` to `sonnet` so general profile consumers (`dal-dev`, `github-devops`, `pulumi-dev`, `tauri-dev`) do not silently regress or leak `architect` top-tier models.

---

## 1. Decision Ledger

### [Q1] Model Profile Selection for `product-owner`
- **Context:** The `product-owner` subagent is responsible for headless feature specification authoring (requirements, design, tasks). It currently uses `model-profile: general`, which resolves to `haiku` on Claude. The intake requires `product-owner` to resolve to the same model as `architect` on every harness. The dedicated `architect` profile in `products/kyber-squad/profiles/models.yml` provides exact parity across harnesses, whereas `deep-planning` differs on Antigravity (`flash` vs `claude-opus-4-6`).
- **Options:**
  - **(a) Move `product-owner` to dedicated `architect` profile:** Exact model parity on all 11 harnesses (Antigravity: `claude-opus-4-6`, Claude: `opus`, Codex: `gpt-5.6-sol`, Copilot: `GPT-5.6 Sol (copilot)`, Cursor: `gpt-5.6-sol[...]`, Devin: `claude-opus-5-5-high`, Kilo: `glm5.3`, OpenCode: `zai-coding-plan/glm-5.3`, Pi: `zai/glm-5.3[thinking=high]`, ZCode: `glm-5.3`). *Leak property note:* On Antigravity, `product-owner` resolves to `claude-opus-4-6` instead of `flash`. This is analogous to `architect` and appropriate for complex spec planning.
  - **(b) Move `product-owner` to `deep-planning` profile:** Matches `architect` on 10 harnesses, but uses `flash` on Antigravity, violating exact parity.
- **Recommendation:** **(a)** Move to `architect` profile (intake preference; code inspection confirms no blockers).
- **Status:** ANSWERED (Option a).

### [Q2] Disposition of the `general` Profile and Other General Consumers
- **Context:** `dal-dev`, `github-devops`, `pulumi-dev`, and `tauri-dev` also declare `model-profile: general`. The intake mandates removing `haiku` everywhere in the squad (where `general.claude` is currently the only occurrence), while ensuring other general consumers do not silently regress. Per Issue #209 precedent, `architect` was given a dedicated profile so top-tier Opus reasoning would not leak to worker peers.
- **Options:**
  - **(a) Raise `general.claude` from `haiku` to `sonnet` and keep other general consumers on `general`:** Eradicates `haiku` from `models.yml`, elevates general workers to a capable Sonnet tier on Claude (consistent with `fast`, `reviewer`, and `orchestration`), and prevents leaking `architect` Opus/Sol models to domain workers.
  - **(b) Move other general consumers to `architect`:** Regresses the boundary established in Issue #209 by assigning top-tier Opus/Sol models to routine domain tasks.
  - **(c) Move other general consumers to `fast`:** Changes their model assignments on Copilot (`MAI-Code-1.1-Flash` vs `Grok 4.6`) and Kilo.
- **Recommendation:** **(a)** Raise `general.claude` to `sonnet` and retain `dal-dev`, `github-devops`, `pulumi-dev`, and `tauri-dev` on `general`.
- **Status:** ANSWERED (Option a).

### [Q3] Development Mode
- **Context:** Repository policy requires `test-first` as default.
- **Options:**
  - **(a) `test-first`:** RED test contract written and verified failing before source modifications.
  - **(b) `standard`:** Code changes with post-implementation verification.
- **Recommendation:** **(a) `test-first`**.
- **Status:** ANSWERED (Option a).

---

## 2. Problem and Scope

### 2.1 Problem Statement
The `product-owner` subagent in Kyber-Squad currently specifies `model-profile: general`. In `products/kyber-squad/profiles/models.yml`, `general.claude` is configured as `haiku`. As a result, when executing on the Claude harness, the agent that produces feature requirements, design documents, and test-first implementation tasks runs on Anthropic's smallest model (`haiku`), leading to lower quality specification output compared to `architect`, which runs `opus`.

Furthermore, `haiku` remains in `models.yml` as the only occurrence across all profiles and harnesses.

### 2.2 Scope Boundaries
- **In Scope:**
  - `products/kyber-squad/agents/product-owner.md`: update `model-profile` from `general` to `architect`.
  - `products/kyber-squad/profiles/models.yml`: raise `general.claude` from `haiku` to `sonnet`.
  - Repository-wide sweep of `products/`, `templates/`, rendered fixtures, and tests to confirm zero `haiku` model resolutions.
  - Update `tests/KyberWeave.Tests/SquadSourceTests.cs` profile assertions.
  - Add dedicated contract assertions in `tests/KyberWeave.Tests/ClaudeRendererContractTests.cs` pinning `product-owner` to `opus`, general workers to `sonnet`, and asserting zero `haiku` presence.
  - Audit and update migration receipts (`products/kyber-squad/migration/*.md`) and canonical content hashes (`SquadCanonicalContentTests.cs`).
- **Out of Scope:**
  - Changing prompt bodies or instruction text of `product-owner` or other agents.
  - Modifying non-Claude harness models for other profiles.

---

## 3. Investigation Findings

1. **Model Profile Parity:** Moving `product-owner` to `model-profile: architect` achieves exact parity with `architect` across all 11 harnesses (Antigravity, Claude, Codex, Copilot, Cursor, Devin, Kilo, OpenCode, Pi, ZCode, and default).
2. **Haiku Occurrences:** `git grep -in "haiku"` confirms that `products/kyber-squad/profiles/models.yml:45` (`general.claude: haiku`) is the sole model resolution to `haiku` in the entire product and template catalog. All other matches are historical plan archives or code comments.
3. **General Consumers:** `dal-dev`, `github-devops`, `pulumi-dev`, and `tauri-dev` remain on `general`. Raising `general.claude` to `sonnet` improves their quality on Claude without altering their assignments on other harnesses (e.g. Grok 4.6 on Copilot/Cursor, SWE-2 on Devin, GLM-5.3-flash on ZCode).
4. **Golden Contract Tests:** `HotshotGoldenContractTests.cs` already exempts `product-owner` under `EvolvedAgentIdentities`. `product-owner` is not present in `ModelEvolvedAgentIdentities`, but because it is skipped by `EvolvedAgentIdentities` in both `CanonicalSourcePreservesGoldenContractOutsideReviewedEvolution` and `CopilotRenderMatchesCheckedInHotshotGoldenContract`, changing its model profile introduces no golden test mismatch.
5. **Canonical Content and Migration Receipts:** `SquadSourceLoader` computes `agent.BodyDigest` as the SHA-256 of the content after the frontmatter closing delimiter (`\n---\n`). While updating `model-profile` in frontmatter does not modify `frontmatter.Body`, migration receipts and `SquadCanonicalContentTests.cs` are audited and verified during task execution.

---

## 4. Test Contract (test-first)

Automated tests in `tests/KyberWeave.Tests/`:

### 4.1 RED Test Phase (T1)
1. **`SquadSourceTests.cs`:**
   - Update `[InlineData("general", "claude", "haiku")]` to `[InlineData("general", "claude", "sonnet")]`.
   - Add new test `ModelsYmlCarriesNoHaikuResolutionAcrossAllProfiles`: iterate over all profiles and harnesses in `models.yml` asserting none contains `"haiku"`.
2. **`ClaudeRendererContractTests.cs`:**
   - Add `RenderAsync_Claude_ProductOwnerRunsOnArchitectModel`: asserts that `.claude/agents/product-owner.md` renders with `model: opus`.
   - Add `RenderAsync_Claude_GeneralProfileWorkersRunOnSonnet`: asserts that `dal-dev`, `github-devops`, `pulumi-dev`, and `tauri-dev` render with `model: sonnet`.
   - Add `RenderAsync_Claude_NoAgentRunsOnHaiku`: asserts that no rendered `.claude/agents/*.md` file contains `model: haiku`.

Running `dotnet test` against HEAD verifies that these assertions fail RED as expected before implementation edits.

### 4.2 GREEN Test Phase (T2)
1. Update `products/kyber-squad/agents/product-owner.md`: set `model-profile: architect`.
2. Update `products/kyber-squad/profiles/models.yml`: set `general.claude: sonnet`.
3. Running `dotnet test` verifies all contract tests transition to GREEN.

---

## 5. Implementation Tasks

| Task | Title | Objective | Target Files | Acceptance Criteria | Dependencies | Required Skills |
|---|---|---|---|---|---|---|
| **T1** | Test Contract (RED) | Author failing unit tests enforcing Sonnet for general.claude, Opus for product-owner, and complete elimination of haiku. | `tests/KyberWeave.Tests/SquadSourceTests.cs`<br>`tests/KyberWeave.Tests/ClaudeRendererContractTests.cs` | `dotnet test` executes and fails specifically on the new/updated assertions. | None | `test-dev` |
| **T2** | Model Alignment & Haiku Removal (GREEN) | Update product-owner model profile to architect and elevate general.claude to sonnet. | `products/kyber-squad/agents/product-owner.md`<br>`products/kyber-squad/profiles/models.yml` | `dotnet test` passes for `SquadSourceTests` and `ClaudeRendererContractTests`. | T1 | `csharp-dev` |
| **T3** | Migration Receipts & Canonical Verification | Verify migration receipts and canonical content test suite. | `products/kyber-squad/migration/*.md`<br>`tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` | `SquadCanonicalContentTests` and `HotshotGoldenContractTests` pass 100%. | T2 | `test-dev` |
| **T4** | Documentation & Quality Gates | Run full CI gate suite and verify documentation integrity. | `docs/plans/2026-10-05-product-owner-model-alignment.md` | `dotnet build`, `dotnet test`, `docs validate .`, and `docs drift .` pass with zero findings. | T3 | `csharp-dev` |

---

## 6. Dependency Graph & Concurrency

- **Graph:** `T1 -> T2 -> T3 -> T4`
- **MAX_CONCURRENCY:** 1 (sequential test-first delivery).

---

## 7. Risks and Out-of-Scope Boundaries

- **Risk:** Unintended haiku references in documentation or comments causing false positives.  
  *Mitigation:* Sweeps differentiate between executable profile mappings (which must contain zero haiku) and historical text in archived plans or docstrings.
- **Risk:** Antigravity model cost/capability shift on product-owner.  
  *Mitigation:* Documented leak property accepted in Q1: product-owner requires high-capacity reasoning for spec generation.
- **Out of Scope:** Modifying any agent's prompt text, changing other profiles, or altering release infrastructure.

---

## 8. Verification and Closeout Protocol

1. `PATH="/Users/hal/.dotnet:$PATH" /Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release`
2. `PATH="/Users/hal/.dotnet:$PATH" /Users/hal/.dotnet/dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
3. `PATH="/Users/hal/.dotnet:$PATH" /Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
4. `PATH="/Users/hal/.dotnet:$PATH" /Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
5. On approval, archive plan per `KW-DOC-LIFECYCLE-003` to `docs/archive/plans/2026-10-05-product-owner-model-alignment.md` and update `docs/plans/README.md`.
