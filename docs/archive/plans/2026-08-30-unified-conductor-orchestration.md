---
id: archive/plans/2026-08-30-unified-conductor-orchestration
title: Unified Conductor Orchestration
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-31
---

# Unified conductor orchestration

**Status:** Archived
**Archive Date:** 2026-08-31
**Date:** 2026-08-30
**Development mode:** Test-first
**Approval:** Approved by the user on 2026-08-30 through the explicit instruction to implement the unified-conductor plan
**Goal:** Replace the version-split orchestration stack with one progressively disclosed conductor, architect, and task reviewer that can intake a plan, specification, todo, or open request and defaults execution to test-first development.

## 1. Problem / motivation

Kyber-Squad currently carries separate `conductor`/`conductor-v3`, `architect`/`architect-v3`,
and `task-reviewer`/`task-reviewer-v3` identities. The pairs duplicate routing, planning, and
completion-audit behavior while making test-first development an identity choice instead of the
normal execution mode. `product-owner` separately owns a gated specification workflow, but it
prompts the user itself rather than returning phase state to the conductor. Todos and open requests
therefore take a different entry path even though they ultimately need the same plan-or-spec choice,
approval gate, ready queue, task audit, council review, and closeout.

The agent and skill bodies also link to local reference files, but AgentIR loads only the principal
agent Markdown and each skill's `SKILL.md`. Native renderers consequently omit linked resources,
and the fallback renderer cannot preserve an agent's progressively disclosed material. Packaging
already retains recursive source trees, and deployment already hashes and transacts every
`SquadDeploymentFile`; the missing seam is a validated resource closure in the public source model
and deterministic projection of that closure into renderer output.

## 2. Approved decisions

- **D1 — one canonical orchestration stack.** Keep only `conductor`, `architect`, and
  `task-reviewer`; hard-delete the three `-v3` identities and every version alias. This is approved
  by the user's supplied plan and follow-up instruction to consolidate task reviewer.
- **D2 — three conductor paths.** The conductor routes Ready/Draft plans, Ready/in-progress specs,
  and todo/open-request intake. Intake recommends plan for bounded work within established
  architecture and spec for a new product, large feature, or undefined requirements/architecture,
  then requires the user's choice. This is approved by the supplied plan.
- **D3 — test-first default with explicit opt-out.** Persist
  `development-mode: test-first | standard` in plan and spec task artifacts. Omission means
  `test-first`; only an explicit user opt-out selects `standard`. Changing the mode after approval
  reopens approval for the affected test or verification contract. This is approved by the supplied
  plan.
- **D4 — headless planning specialists.** `architect` and `product-owner` persist artifacts and
  return structured status/gap digests; the conductor alone relays questions and approval gates.
  The final plan/spec prompt is “approve and execute.” Ready input begins immediately; Draft or
  partial input resumes from persisted state. This is approved by the supplied plan.
- **D5 — one audit ladder.** `task-reviewer` supports three passes in both modes. It requires the
  Test-contract plus matching RED/GREEN evidence only in test-first mode; standard mode requires
  the approved verification contract and current evidence. `code-reviewer` remains the one
  end-of-run council, and `docs-dev` performs plan/spec closeout. This is approved by the supplied
  plan.
- **D6 — progressive disclosure.** Shared routing and invariants remain in each principal agent
  body. Conditional workflows live in agent-owned Markdown references. `product-owner` keeps its
  requirements/design/tasks/closeout expertise in skill references, using Config Reg properties
  instead of directive `6-Docs` paths. This is approved by the supplied plan.
- **D7 — common resource closure.** Add public immutable `SquadResource(RelativePath, Content)` and
  expose `IReadOnlyList<SquadResource> Resources` on both `SquadAgent` and `SquadSkill`.
  `RelativePath` is the normalized portable path from the principal artifact's directory. Parse
  local links from Markdown with the existing Markdig dependency; URL, mail, and fragment-only
  links are not resources. Markdown resources recurse; non-Markdown resources are retained as
  UTF-8 leaf content. A missing target, active recursion cycle, root escape (including symlink
  escape), invalid UTF-8, or portable path alias collision is a source-validation error. Repeated
  links and shared descendants are de-duplicated by normalized ordinal path, then returned in
  ordinal order. This implements the approved resource-closure requirement without a new
  dependency.
- **D8 — render without link rewriting.** For every principal output, append each resource's
  artifact-relative path to the directory containing that output. This preserves the authored
  relative link verbatim for Copilot, Cursor, Claude, Codex, and fallback role-skill output. Native
  skill resources remain beneath the skill identity directory; fallback agent resources remain
  beneath the lowered role skill's directory plus their canonical agent-relative path. Every
  emitted resource is a normal `SquadDeploymentFile` and therefore uses existing receipt hashing,
  drift, collision, transaction, and rollback behavior without a state-schema change.
- **D9 — package boundaries.** APM continues to include the complete canonical source recursively,
  including agent and skill resources. Agent Plugins continues to contain only `plugin.json`, MCP,
  and the recursive skill tree; it never gains agents or agent-owned resources. This is approved by
  the supplied plan.
- **D10 — historical provenance with reviewed evolution.** Keep
  `kyber-squad-hotshot-golden.json` unchanged. Exact golden equality continues for non-evolved
  artifacts. Explicitly evolved agents are `architect`, `conductor`, `product-owner`, and
  `task-reviewer`; retired golden agents are the three `-v3` identities. Explicitly evolved skills
  are `product-owner` and `bug-crusher`, the latter because it calls the consolidated roles. Fold
  each retired source hash into its unversioned migration report, refresh the final-body digest,
  delete the three retired reports, and keep all other golden provenance exact. This is approved by
  the supplied plan.
- **D11 — todo promotion.** Intake records the successor plan/spec in the todo, marks the todo
  superseded, updates the todo index, and archives the todo only when the successor reaches Ready.
  This is approved by the supplied plan.
- **D12 — generated-output boundary.** Do not change the tracked root `.github` self-deployment,
  its `.kyber-weave` state, or ignored local `.agents` copies. Exercise fresh renders only in
  disposable output through the normal release loop. This is approved by the supplied plan.

## 3. Investigation findings

- Baseline is clean at `96a657acefe1a18aa7a6cbb453d1b1141756eff7` on
  `feature/update-squad-for-copilot` before this plan and index entry are written.
- `SquadSourceLoader.LoadAgents` reads only top-level `agents/*.md`; `LoadSkills` reads only
  `skills/*/SKILL.md`. Both already enforce root containment, symlink containment, strict UTF-8,
  LF normalization, portable source paths, and actionable validation errors through
  `ReadSourceFile`.
- `SquadAgent` and `SquadSkill` are the public source records in `SquadSource.cs`. All five current
  renderers consume those records directly. `SquadRendererRegistry.ValidateRenderResult` already
  rejects non-canonical output paths and projection collisions.
- `SquadDeploymentPlan.NormalizeRenderedFiles` de-duplicates portable output identities and every
  install/update/uninstall receipt owns a SHA-256 per rendered file. `SquadTransaction` applies and
  rolls back the resulting generic mutations. Resource files need no separate deployment type.
- `SquadPacker.CollectApmEntries` already packages the whole product tree recursively and excludes
  only migrations/target output. `CollectPluginsEntries` already packages only MCP plus recursive
  `skills/`, which is the required plugin boundary.
- Current canonical inventory is 24 agents and 24 skills. Removing three versioned agents produces
  the approved 21-agent bundle. The active migration inventory is 22 reports; folding three reports
  produces 19 migrated canonical agents plus the two canonical-only review roles.
- Existing blanket golden tests are concentrated in `HotshotGoldenContractTests` and
  `SquadCanonicalContentTests`. Renderer contracts exist independently for Copilot, Cursor, Claude,
  Codex, and Antigravity; deployment hashing, drift, collisions, deletion, and rollback are covered
  in `SquadDeploymentStateTests`.
- Documentation MCP tools were unavailable in this session. Per root guidance, discovery fell back
  to the current documentation index, ontology, catalog, Kyber-Squad product overview, and exact
  files named by CodeGraph/text results. No archived document was used as current guidance.
- The expected change is substantial but bounded below the repository's 10,000-line review ceiling:
  three large duplicate bodies are removed, shared bodies are split into references, and no
  generated or vendored tree is added.

## 4. Test contracts

| Contract | Test surface | RED behavior before implementation | GREEN acceptance |
|---|---|---|---|
| C1 — unified identities and workflow | `SquadCanonicalContentTests`, `HotshotGoldenContractTests` | The canonical source still exposes 24 agents, three `-v3` identities, the `conductor-v2` alias, split delegation/model profiles, exact blanket golden equality, and only two-pass standard auditing. | Exactly 21 agents; no versioned identity or alias in source, bundle, delegates, profiles, renders, or active migrations; the three-path intake and mode contracts are present; task reviewer has three mode-aware passes; only the reviewed evolution allowlist may differ from the unchanged fixture. |
| C2 — validated resource closure | new `SquadResourceClosureTests` | The public records expose no common resource type, local links are ignored, and invalid/missing/recursive references do not fail source loading. | Agent and skill closures expose normalized content in ordinal order; recursive and shared references resolve once; duplicate links dedupe; missing, escaping, symlink-escaping, invalid UTF-8, active-cycle, and portable-alias cases fail with actionable source paths/hints. |
| C3 — native and fallback rendering | the five renderer contract suites plus `SquadRenderingContractTests` | Render output contains only principal agent/skill files and leaves their progressive-disclosure links unresolved. | Copilot, Cursor, Claude, and Codex emit each reachable resource adjacent to its principal output using the artifact-relative path; Antigravity does the same for canonical skills and each lowered agent identity; no duplicate output or link rewrite occurs; repeated renders are byte/path deterministic. |
| C4 — lifecycle and packaging | `SquadDeploymentStateTests`, `SquadPackAndReleaseTests` | Receipts and archives do not prove behavior for newly rendered agent resources or removal/update of those resources. | Resource paths are receipt-owned with exact hashes; status detects drift; update/delete preserves locally edited conflict rules; rollback restores prior bytes; portable collisions fail preflight; APM contains all resources in ordinal order; Agent Plugins contains skill resources and excludes all agents/agent resources. |

## 5. Task list

| # | Component | Objective | Files/symbols | Acceptance criteria | Depends on | Skills |
|---|---|---|---|---|---|---|
| T1 | Canonical contracts | Establish C1 RED before changing product content. | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`; `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` | Pin the 21-agent inventory, zero aliases/versioned identities, unified delegates/model profile, three conductor paths, headless product-owner statuses, default/opt-out mode, three audit passes, unchanged fixture, evolved/retired allowlists, and folded migration provenance. Run the C1 RED command and record failures caused by current split identities/contracts. | — | test-dev |
| T2 | Source model and parsing | Establish C2 RED with filesystem-backed closure fixtures. | new `tests/KyberWeave.Tests/SquadResourceClosureTests.cs`; fixture-local agent/skill/resource trees | Cover positive agent/skill closure, recursive Markdown, leaf content, duplicate links, shared descendants, ordinal output, URL/anchor exclusion, missing targets, cycles, traversal, symlink escape, invalid UTF-8, and portable aliases. Run the C2 RED command and record current ignore/no-model failures. | — | test-dev |
| T3 | Rendering and lifecycle | Establish C3/C4 RED against native, fallback, receipt, rollback, and package boundaries. | `tests/KyberWeave.Tests/{CopilotRendererContractTests,CursorRendererContractTests,ClaudeRendererContractTests,CodexRendererContractTests,AntigravityRendererContractTests,SquadRenderingContractTests,SquadDeploymentStateTests,SquadPackAndReleaseTests}.cs` | Add resource-bearing fixtures and assert exact paths/bytes, deterministic ordering, link resolution, no output collisions, receipt hashes/drift/update/delete/rollback, and APM-versus-plugin boundaries. Run both RED commands and retain the failing output. | — | test-dev |
| T4 | AgentIR | Implement C2 through one common public resource model and loader closure. | `src/KyberWeave.Core/Squad/Model/SquadSource.cs`: `SquadResource`, `SquadAgent.Resources`, `SquadSkill.Resources`; `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`: `LoadAgents`, `LoadSkills`, new closure/link helpers | Use Markdig to traverse local Markdown links under each owning artifact root; enforce D7 containment/UTF-8/cycle/alias rules; return immutable ordinal closures; keep current source diagnostics and no new dependency. C2 is GREEN. | T2 | dotnet-dev |
| T5 | Canonical orchestration content | Consolidate the three agent pairs, make product-owner headless, and install progressive-disclosure references. | `products/kyber-squad/agents/{conductor,architect,task-reviewer,product-owner}.md`; new `agents/{conductor,architect,task-reviewer}/references/*.md`; delete the three `*-v3.md`; `skills/product-owner/{SKILL.md,references/*.md}`; `skills/bug-crusher/SKILL.md`; `bundles/full.yml`; `profiles/models.yml`; `migration/{conductor,architect,task-reviewer}.md`; delete three `migration/*-v3.md` | Main bodies retain shared invariants and link to the approved path/mode references. Conductor implements plan/spec/intake, Ready/Draft resumption, approve-and-execute, todo promotion, ready queue, findings, council, and closeout. Architect emits mode-aware plan/test-or-verification contracts. Product-owner returns structured phase/gap markers and never prompts directly. Task reviewer applies three passes with mode-specific evidence. Remove `test-first-orchestration`, all version aliases, versioned delegates, files, and reports; fold source hashes and refresh final digests. | T1, T4 | product-owner, app-docs-standard |
| T6 | Renderers | Project every validated closure through native and fallback outputs. | `src/KyberWeave.Core/Squad/Rendering/{CopilotRenderer,CursorRenderer,ClaudeRenderer,CodexRenderer,AntigravityRenderer,SquadRendererRegistry}.cs` | Centralize or consistently apply D8 path projection; emit agent and skill resources as ordinary `SquadDeploymentFile` values; preserve principal-body links and current shared/collision lowering; reject duplicate portable outputs in registry validation. C3 and C4 are GREEN without a receipt/transaction schema change. | T3, T4 | dotnet-dev |
| T7 | Provenance and public contract tests | Reconcile C1/C4 tests with the completed source while keeping the historical fixture immutable. | Test files owned by T1 and T3; `tests/KyberWeave.Tests/Fixtures/kyber-squad-hotshot-golden.json` is read-only | Non-evolved agents/skills remain exact to the fixture; evolved agents/skills have named contract assertions; retired entries remain fixture history but are absent canonically; resources participate in render/deployment/package tests; all C1–C4 focused commands pass and the full test host exits zero. | T5, T6 | test-dev |
| T8 | Canonical documentation and todos | Update current Kyber-Squad guidance and deferred-work records to the verified 21-agent/resource-aware behavior. | `products/kyber-squad/README.md`; `docs/{README.md,distribution.md}`; `docs/kyber-squad/{README.md,architecture.md,onboarding.md,requirements.md}`; `docs/context-hygiene/{agents.md,skills.md}`; `docs/code-review/architecture.md`; affected active target todos plus `docs/todo/{README.md,migrate-skill-resources-into-standards.md,squad-hardcoded-docs-root.md,portable-artifacts-carry-project-standards.md}` | Counts, capabilities, delegation, fallback, plan/spec/todo taxonomy, resource delivery, package boundaries, and closeout are current. Resource-migration todo states rendered-reference delivery is fixed while content placement remains deferred; docs-root todo records the completed product-owner slice without claiming broader completion. Count/reference sweep finds no current versioned identities or stale 24-agent claims. `docs validate` and `docs drift` return zero findings. | T5, T6, T7 | app-docs-standard |
| T9 | Verification, review, and closeout | Run the full release loop, review the accumulated change, and retire this plan only after evidence is complete. | Repository-wide read/build outputs; then this plan and `docs/plans/README.md` through `docs-dev` closeout | All commands in section 7 pass; fresh disposable renders contain resolvable resources for all five supported targets; root `.github`, root `.kyber-weave` state, ignored `.agents`, and the golden fixture remain unchanged. `code-reviewer` returns APPROVE after any bounded remediation. `docs-dev` migrates final durable facts, updates the plan index, and archives this plan. | T8 | code-review, security-review, app-docs-standard |

## 6. Sequencing / dependency graph

```text
T1 → T5                 T5 consumes the unified identity/workflow contract established by C1.
T2 → T4                 T4 consumes the resource-closure cases established by C2.
T3 → T6                 T6 consumes the renderer/lifecycle/package cases established by C3/C4.
T4 → T5                 T5's new agent references must load through the public resource closure.
T4 → T6                 T6 consumes SquadAgent.Resources and SquadSkill.Resources.
T5 → T7                 T7 reconciles provenance and contract expectations against final content.
T6 → T7                 T7 proves final render/deployment/package behavior.
T5 → T8                 Documentation describes the verified unified content.
T6 → T8                 Documentation describes the verified resource projection.
T7 → T8                 Documentation count/provenance claims follow passing final contracts.
T8 → T9                 Closeout consumes verified implementation and canonical documentation.
```

`MAX_CONCURRENCY: 3` — T1, T2, and T3 start together. T4 starts when T2 completes; after T4,
T5 and T6 may run concurrently once their own RED contracts are present. Their file scopes are
disjoint. No dependency is a component-level batch barrier.

## 7. RED and verification commands

Record the expected pre-implementation failure for each contract before its GREEN task starts:

```bash
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadCanonicalContentTests|FullyQualifiedName~HotshotGoldenContractTests"
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadResourceClosureTests"
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~CopilotRendererContractTests|FullyQualifiedName~CursorRendererContractTests|FullyQualifiedName~ClaudeRendererContractTests|FullyQualifiedName~CodexRendererContractTests|FullyQualifiedName~AntigravityRendererContractTests|FullyQualifiedName~SquadRenderingContractTests"
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadDeploymentStateTests|FullyQualifiedName~SquadPackAndReleaseTests"
```

After GREEN/refactor, run the repository gates and release loop:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
./scripts/update-loop.sh
```

Use `./scripts/update-loop.sh --keep` only when retained disposable output is needed for manual
path inspection. Verify that every local link in each rendered principal or Markdown resource
resolves within that target output, and compare `git status --short` before/after to prove the
tracked root self-deployment and state were not touched.

## 8. Residual risks and stop conditions

- **Markdown link semantics:** Markdig AST links, not regexes, define resource discovery. Stop if a
  current resource uses a non-Markdown construct that cannot be represented without changing the
  approved closure contract; report the exact source link before widening behavior.
- **Output collisions:** Different owners may legally reference identically named resources only
  while their rendered owner directories remain distinct. Any portable output alias collision is a
  source/render error; do not resolve it by overwriting or last-write-wins ordering.
- **Mode evidence:** Standard mode removes only the requirement for historical RED evidence. It
  never removes automated tests, current verification, task audit, or final council review.
- **Plan/spec state:** Do not execute a Draft artifact. Do not archive a promoted todo until its
  successor is Ready. Do not change development mode after approval without reopening its affected
  contract gate.
- **Review size:** If the accumulated diff crosses 5,000 changed lines, raise the split decision
  before more tasks are dispatched; the hard review ceiling remains 10,000 lines.
- **Generated output:** Any diff beneath root `.github/agents`, root `.github/skills`,
  `.kyber-weave/squad.lock.yml`, `.kyber-weave/squad.receipt.json`, or the golden fixture is
  blocking and must be reverted by the owning implementation worker without broad reset commands.

## 9. Out of scope

- Migrating the semantic content of all retained resources into standards or other durable homes;
  that broader work remains in `todo/migrate-skill-resources-into-standards`.
- Adding renderers for OpenCode, Kilo, Warp, or Factory. Their active todos remain the authority.
- Changing the lock/receipt schema, package manifest schema, or target catalog; existing generic
  file ownership is sufficient.
- Refreshing the repository's tracked self-deployment, publishing an RC, tagging, or releasing.
- Modifying the historical Hotshot fixture or its source repository.

## 10. Required skills

- `test-dev`
- `dotnet-dev`
- `product-owner`
- `app-docs-standard`
- `code-review`
- `security-review`

## 11. Closeout

After T9 passes, `docs-dev` verifies every approved decision and C1–C4 against current code, tests,
render output, packaging output, and review evidence. It migrates any final durable behavior into
the current Kyber-Squad architecture/onboarding/requirements pages, moves this plan from the active
inventory to `docs/archive/plans/`, and records those canonical replacements in the archive row.
The plan is not complete while any versioned identity remains active, any supported render has a
dangling local reference, any required gate is red, or the root self-deployment changed.
