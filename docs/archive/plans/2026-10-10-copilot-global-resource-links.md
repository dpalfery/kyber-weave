---
id: plans/2026-10-10-copilot-global-resource-links
title: "Copilot global scope: agent reference links resolve outside the workspace"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-10
development-mode: test-first
---

# Copilot global scope: agent reference links resolve outside the workspace

**Status: Complete 2026-10-10.** Approved by the user (**approve and execute**) on 2026-10-10 after Q1–Q4 were answered, then executed test-first: T1–T4 and T6 delivered, T5 partly done (see [Closeout](#10-closeout)). Archived per KW-DOC-LIFECYCLE-003.

**Development mode:** `test-first`. This is the default, and no opt-out was supplied. Every implementation task needs a failing contract test before production code changes.

---

## 1. Problem and goal

### 1.1 User report

The user installed Squad for GitHub Copilot at global scope (`~/.copilot`) and has no local agents or skills. Running `conductor` then fails with this report:

> I'm blocked at the intake contract: the referenced conductor/references/intake-path.md is not present at the expected .agents/skills/conductor/references/ path, so I can't follow the required intake workflow or dispatch the architect.

### 1.2 Goal

At global scope, a Copilot agent must reach its own reference contracts no matter what the workspace is, with no harness-version luck involved. Project scope must not change.

## 2. Investigation findings

### 2.1 Code (gathered by hand, no CodeGraph)

There is no `.codegraph/` index in this checkout. All code discovery below was gathered by hand with text search and file reads.

- **The links are relative.** `products/kyber-squad/agents/conductor.md` lines 33–35 and 50 link `conductor/references/{plan-path,spec-path,intake-path,execution-and-review}.md` relatively.
- **The renderer copies the body unchanged.** `CopilotRenderer.RenderAgent` (`src/KyberWeave.Core/Squad/Rendering/CopilotRenderer.cs:212-213`) copies the body as is. At global scope, `ResolvePrefixedDirectory` (`:59-73`) changes only the output directory: `.github/agents` becomes `agents`.
- **Resources land beside the principal.** `SquadResourceProjection.Append` (`SquadRendererRegistry.cs:493-519`) projects resources next to the principal file. This is the documented contract in [architecture.md](../../kyber-squad/architecture.md) under "Resource projection": "authored relative links resolve verbatim in the deployed tree".
- **ZCode is the only rewriting target.** `ZCodeRenderer.RewriteResourceLinks` (`ZCodeRenderer.cs:536-604`, [ADR 0021](../../adr/0021-zcode-command-lowering-and-resource-relocation.md)) is the only per-target link rewrite.
  - Only link targets that exactly equal a declared resource path are rewritten. The rewrite uses the `MarkdownLinkTarget` regex (`:234`).
  - It emits a `resource-links-rewritten` degradation record.
  - Its output is still relative (`../skills/…`).
- **Renderers cannot see the global root.** The render input is `SquadRenderRequest(SourceDirectory, Targets, Scope, UserScopeDirectory, TranslationMode)` in `SquadRenderModels.cs:29`.
  - `UserScopeDirectory` is the Kyber-Weave app-data directory, not the target root.
  - The real Copilot root is `$COPILOT_HOME` or `~/.copilot`, resolved by `SquadGlobalRoots.ResolveGlobalRoot` (`SquadGlobalRoots.cs:112`).
- **Three call sites build render requests.** `SquadLifecycleService` install (`:149`) and update (`:298`), and `SquadDoctorCommand.ReportGlobalCollisions` (`src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs:362`).
  - Doctor compares on-disk bytes against a fresh global render.
  - So any root-dependent output must be produced the same way at all three sites, or doctor will report false collisions.
- **Project-scope digests are pinned in two places.** `SquadCanonicalContentTests.cs:766` and `Fixtures/kyber-squad-hotshot-golden.json:200` pin `.github/agents/conductor.agent.md`. A global-only change leaves both alone.
- **Existing tests encode the current layout.** `SquadPackAndReleaseTests.cs:332,680` and `SquadDeploymentStateTests.cs:742,804,1105` encode resources beside the principal. The layout stays the same; only link text changes, and only at Copilot global scope.
- **The installed artifact matches the source.** According to the conductor's investigator, `~/.copilot/agents/conductor.agent.md` links `conductor/references/intake-path.md`, and that file exists at `~/.copilot/agents/conductor/references/intake-path.md`. No rendered Copilot artifact contains `.agents/skills/`.

### 2.2 Copilot behaviour (research-agent, external sources)

- **Copilot CLI used to resolve agent links against the working directory.** [github/copilot-cli#4122](https://github.com/github/copilot-cli/issues/4122) (2026-07-14) says the harness "does not inline the linked markdown... resolves the path against the repo root (its working directory)".
  - The [CLI changelog 1.0.73](https://raw.githubusercontent.com/github/copilot-cli/main/changelog.md) (2026-07-20) records the fix: "Resolve relative links in custom agent instructions from the agent file location."
  - It is unverified whether that fix covers user-level `~/.copilot/agents` agents. #4122 was about repository agents.
  - Plugin-installed agents still have an open, related issue ([#3131](https://github.com/github/copilot-cli/issues/3131)).
- **VS Code documents file-relative resolution.** The [VS Code custom-agents docs](https://code.visualstudio.com/docs/copilot/customization/custom-agents) say: "Relative file paths resolve from the custom agent file." Nothing says whether the linked files are attached automatically.
- **No documentation tells the model where the agent file is.** None of the sources say the model is told the agent file's absolute path.
- **Skills differ: the CLI gives the model the skill's base directory** ([#4451](https://github.com/github/copilot-cli/issues/4451)). `.agents/skills` and `~/.agents/skills` are documented Copilot skill roots ([add-skills](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)). That likely explains the model's `.agents/skills/` guess: with no anchor for the link, it fell back to a skill-root convention.
- **The user's version rules out the upstream fix.** The user runs Copilot CLI **1.0.82** (reported 2026-10-10), which is past the 1.0.73 fix, and the failure still happened with a global-only install. So the upstream fix does not cover user-level `~/.copilot/agents` agents, or at least not this case. Kyber-Weave cannot rely on it.
- **Copilot's precedence is documented as the opposite of what the user expects.** The [CLI custom-agents docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli) say: "If personal and repository custom agents have the same ID, the personal agent is used." In other words, Copilot itself decides that a global (personal) conductor wins over a project one. Kyber-Weave does not control this. T5 records what actually happens.
- **Conclusion.** The hypothesis is confirmed for user-level agents on CLI 1.0.82. An absolute link is unambiguous on every Copilot surface and version. A relative link is correct only where the harness anchors it.

## 3. Design

The design below reflects the decisions approved on 2026-10-10: Q1 (a), Q2 (a), Q3 (b), Q4 (a).

- **Rewrite at Copilot global scope only.** Each link in a Copilot agent body whose target exactly equals one of that agent's declared resource paths is rewritten to an absolute path: `<copilot-global-root>/agents/<resource-path>`.
  - The resolved root uses forward slashes, Windows included.
  - A `#fragment` or `?query` suffix is kept.
  - A target that contains a space uses the CommonMark angle form `(<…>)`.
  - Prose that mentions a path, and links to anything outside the declared closure, are left alone.
  - A `resource-links-rewritten` degradation record is emitted, as ADR 0021 does.
- **The layout does not change.** Resources stay beside the principal. Only the principal body text changes.
- **Project scope is byte-identical.** Inside the workspace, the model can still find `.github/agents/conductor/references/…`, and VS Code and CLI 1.0.73+ anchor the link to the agent file. The pinned goldens prove project scope is untouched.
- **New render input.** `SquadRenderRequest` gains an optional `ISquadGlobalRootResolver? GlobalRoots`. It is filled at all three call sites (lifecycle install, lifecycle update, doctor) from the resolver they already hold. Install, receipt, and doctor all then see the same bytes.
- **Shared mechanics, one opt-in.** The closure-driven rewrite is moved out of `ZCodeRenderer` into an internal shared helper, `SquadResourceLinkRewriter`, which takes the resources plus a target-path mapping function. ZCode keeps its `../skills/` mapping with unchanged output. Copilot is the only new user. The shared `SquadResourceProjection` is not changed.
- **Resource files themselves are not rewritten.** Once the model reads a contract by absolute path, sibling links inside it, such as `execution-and-review.md`, have a known base. See risk R3.

## 4. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | What form should the fix take at Copilot global scope? | **(a)** Rewrite declared-resource links to absolute paths under the resolved Copilot global root. Project installs stay byte-identical: links stay relative and the files sit in the workspace. T5 also checks which conductor wins when both a project and a global one exist. | (a) works on every Copilot surface and version and follows the closure-driven ADR 0021 pattern. The cost is that rendered bytes depend on the machine (`$COPILOT_HOME`), which receipts handle per machine. (b) A `~/.copilot/...` tilde form: no new plumbing, but it is wrong whenever `COPILOT_HOME` is set, and it relies on the read tool expanding `~`. (c) Move agent resources into a skill directory: the model is still not told the agent's path, and it conflicts with the skill identity rules ADR 0021 had to guard. (d) Change no code and document "Copilot CLI ≥ 1.0.73": disproven, because the user's 1.0.82 still fails. Copilot documents that the personal agent wins over a repository agent with the same ID, so the precedence check in T5 is an observation rather than a pass condition. | ANSWERED — user decision 2026-10-10, relayed by the conductor |
| Q2 | Where does the rewrite live? | **(a)** A shared helper extracted from `ZCodeRenderer.RewriteResourceLinks`. Only Copilot opts in, at global scope ("just copilot for now"). The per-target probes for the other ten targets and zcode stay as unscheduled follow-ups, not tasks. | (a) One closure-driven implementation, no duplication, and other targets can opt in once a harness probe proves they need it. (b) Private code in `CopilotRenderer` only: smallest diff, but it duplicates the ZCode regex and closure logic. (c) Apply it inside `SquadResourceProjection` for every target: it changes ten unverified targets and every project-scope golden. | ANSWERED — user decision 2026-10-10, relayed by the conductor |
| Q3 | Does this need an ADR? | **(b)** An architecture.md bullet only. No ADR. **ADR waiver:** this is a narrow, target-specific refinement of the existing resource-projection rule that follows the pattern ADR 0021 already set. The rule and the new optional root-resolver render input are recorded in [architecture.md](../../kyber-squad/architecture.md). | It adds a second target-specific exception to "authored relative links resolve verbatim". It also introduces a new, expensive-to-revisit constraint: global-scope rendered content may embed the machine's resolved absolute root, and the render request carries the global-root resolver. (b) An architecture.md bullet only: possible under the plan index's ADR-waiver rule, but the root-dependent render input is a lasting constraint rather than a refinement. The user chose (b). | ANSWERED — user decision 2026-10-10, relayed by the conductor (`no ADR — ADR waiver`) |
| Q4 | What happens on a Copilot global render that has no global-root resolver? | **(a)** Fail closed with `SquadRenderValidationException` naming the missing input. | (a) Silent broken output is exactly what this bug is. All three production call sites supply the resolver. (b) Leave the links relative and emit a render warning: more forgiving to test callers, but it can ship the defect again. | ANSWERED — user decision 2026-10-10, relayed by the conductor |
| B1 | Is project scope in scope? | Unchanged: byte-identical output, relative links, files inside the workspace. | The workspace contains `.github/agents/…`, so the model can find the contracts. The pinned goldens (`SquadCanonicalContentTests`, `HotshotGoldenContractTests`) and T1 case 7 enforce it. | Confirmed with Q1, user 2026-10-10 |
| B2 | Which development mode applies? | `test-first` | This is the default. No user opt-out was supplied. | Baseline (default) |

## 5. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs` (new global-scope cases) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~CopilotRenderer"` | (1) A global render with a fake resolver whose root is `/home/u/.copilot` emits `agents/conductor.agent.md` with all four links to `/home/u/.copilot/agents/conductor/references/<file>.md`, and no relative `](conductor/references/` remains. (2) A `#anchor` suffix is kept, and a link outside the closure is unchanged. (3) A Windows-style root `C:\Users\u\.copilot` emits `C:/Users/u/.copilot/...`. (4) A root containing a space emits `](<…>)`. (5) A `resource-links-rewritten` degradation is recorded for target `copilot`, identity `conductor`. (6) The resource files are emitted at the unchanged `agents/conductor/references/*.md` paths. (7) The project-scope render of `.github/agents/conductor.agent.md` is byte-identical to today. (8) Per Q4, a global render with no resolver throws. | Cases 1–5 and 8 fail on `main` (links stay relative, no degradation, no throw). Cases 6–7 pass as guards. | The same tests pass with no assertion weakened. The `SquadCanonicalContentTests` and `HotshotGoldenContractTests` digests are unchanged. |
| T2 | `tests/KyberWeave.Tests/SquadHotshotLifecycleTests.cs` or the existing lifecycle fixture with a fake global-root resolver | `dotnet test … --filter "FullyQualifiedName~SquadHotshotLifecycle\|FullyQualifiedName~SquadDoctor"` | A global Copilot install through `SquadLifecycleService` writes a conductor agent whose links are absolute under the resolved root, and the receipt digest matches the written bytes. A follow-up `squad doctor --global` scan of that root reports no unmanaged collision for `agents/conductor.agent.md`. | The install assertion fails on `main` (relative links). The doctor assertion fails after T3 if doctor does not pass the resolver. | Both pass. |
| T3 | `tests/KyberWeave.Tests/ZCodeRendererContractTests.cs` (existing) | `dotnet test … --filter "FullyQualifiedName~ZCodeRenderer"` | ZCode `../skills/` rewriting is unchanged after the helper extraction. | No-RED task: a refactor guard. The existing suite must pass before and after. | Passes unchanged. |
| T4 | None (documentation) | `docs validate . --merge-ready`, `docs drift .` | architecture.md describes the rule and records the ADR waiver. | No-test task. Verified by the documentation gates and review. | Zero findings. |

## 6. Tasks

| Task | Objective | Files / symbols | Acceptance | Depends on | Skills |
|---|---|---|---|---|---|
| T1 | Write the RED renderer contract tests above. | `tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs` | Test-contract row T1 RED, with failure output captured. | Q1–Q4 approved | `test-dev` |
| T2 | Write the RED lifecycle and doctor tests. | Lifecycle and doctor test files named in row T2 | Row T2 RED captured. | Q1–Q4 approved | `test-dev` |
| T3 | Add the optional `ISquadGlobalRootResolver? GlobalRoots` to `SquadRenderRequest`. Extract `SquadResourceLinkRewriter` from `ZCodeRenderer.RewriteResourceLinks` and `MarkdownLinkTarget`, keeping ZCode output identical. In `CopilotRenderer`, apply the absolute-path mapping when `Scope == Global`, implement the Q4 behaviour, and record the degradation. Supply `GlobalRoots` in `SquadLifecycleService` install and update, and in `SquadDoctorCommand.ReportGlobalCollisions`. Add `<remarks>` explaining why. | `SquadRenderModels.cs`, `ZCodeRenderer.cs`, new `Squad/Rendering/SquadResourceLinkRewriter.cs`, `CopilotRenderer.cs`, `SquadLifecycleService.cs`, `SquadDoctorCommand.cs` | T1, T2, and T3 GREEN. Full suite and format gates pass. | T1, T2 | `csharp-dev` |
| T4 | Amend the [architecture.md](../../kyber-squad/architecture.md) "Resource projection" bullet. It must state the Copilot global-scope exception: absolute links under the resolved root, `resource-links-rewritten`, project scope unchanged. It must also cover the optional `GlobalRoots` render input with its fail-closed rule, and the ADR waiver (Q3). No ADR. | `docs/kyber-squad/architecture.md` | Zero findings from the documentation gates. | T3 | `docs-dev` |
| T5 | Manual live check by the owner on Copilot CLI 1.0.82. **(i)** Global only: `kyber-weave squad update --global --target copilot`, then run `conductor` from a directory with no local Squad files. **(ii)** Precedence: in a project that also has a project-scope Squad install (`.github/agents/conductor.agent.md`), run `conductor` and record which one loads, project or global. | None | (i) The conductor reads `intake-path.md` by its absolute path and dispatches the architect. This is a pass/fail gate. (ii) Record the observed precedence. Copilot documents that the personal (global) agent wins on an ID clash, so this is an observation, not a pass condition. If the user wants the project agent to win, that becomes a follow-up decision. | T3 | None (owner gate) |
| T6 | Whole-change review, then `docs-dev` closeout and plan archival. | This plan, `docs/plans/README.md` | Council APPROVE, harvest mapping recorded, plan archived. | T4, T5 | `code-review`, `docs-dev` |

**Dependency graph:** T1 and T2 run in parallel and are both RED. T3 depends on both. T4 and T5 follow T3 and can run in parallel. T6 is last. The new `SquadResourceLinkRewriter.cs` file comes from the Q2 choice; T3 also keeps ZCode's existing test suite green as a refactor guard.

**MAX_CONCURRENCY:** 2. T1 and T2 touch different test files, and T4 and T5 share no files.

## 7. Risks

- **R1. Rendered bytes depend on the machine.** Global Copilot content embeds `$COPILOT_HOME`. Receipts are per machine, so this is fine. Changing `COPILOT_HOME` already moves the whole tree, and an `update` re-renders it.
- **R2. Doctor and install could disagree.** If any render site omits the resolver, Q4 makes it fail loudly rather than drift. The T2 doctor case covers this.
- **R3. Links inside resource files stay relative.** They are resolved by the model from the absolute path it just read. If a live check (T5) shows this fails, the follow-up is to extend the rewrite to the resource bodies. That would change the resource digests, so it is out of scope here.
- **R4. Copilot permissions outside the workspace.** Copilot may still prompt before reading files outside the workspace. That is a harness permission prompt, not a missing file, and T5 records it.

## 8. Out of scope and follow-up

- **Project scope, for all targets.** Unchanged.
- **Skills.** Copilot gives the model the skill's base directory (#4451), and the Agent Skills spec makes links skill-relative. No change.
- **Unscheduled follow-ups ("just copilot for now", Q2).** These are not tasks in this plan, and a todo is created only if the user asks for one. Each is gated on a per-harness probe asking whether a global-scope agent body's relative resource link resolves from a foreign working directory:
  - targets with the same unchanged-body layout: `claude`, `codex`, `cursor`, `opencode`, `kilo`, `pi`, `factory`, `devin`, `warp`, `antigravity`;
  - `zcode`, whose ADR 0021 rewrite is still relative (`../skills/…`), so it has the same exposure at global scope.
- **Verification gates.** The full set in root `AGENTS.md` (format, build, test, the skill gates, `docs validate --merge-ready`, `docs drift`).

## 9. GAPS

- **G1. Resolved 2026-10-10.** The user runs Copilot CLI 1.0.82, and the failure still happened. See §2.2.
- **G2.** There is no CodeGraph index in this checkout, so discovery was gathered by hand and `docs drift` cannot be relied on locally.
- **G3.** The planner has no shell in this harness. The conductor runs `docs validate . --merge-ready` and `docs drift .` on this Draft.

## 10. Closeout

### Outcome

Delivered in development mode `test-first`. T1 and T2 were written RED and failed for the intended reasons, T3 turned them GREEN, and a follow-up test closed the `update` path that T2 left uncovered. The whole change then got one whole-change review pass, and its two actionable test findings (direct rewriter tests, a weak Windows-root assertion) were fixed. The full suite ran at 2625 tests with 0 failures and 1 environmental skip, and `dotnet format` whitespace and style were clean. The Decisions table is unchanged from the approved plan (Q1–Q4).

### T5, the live check, and what is still open

- **Done:** the global Copilot squad was updated with a build from this change. The Squad global receipt belongs to a different checkout (`/Users/dave/git/personal/kyber-weave`), so the update was run with `--path` pointing at that root. `~/.copilot/agents/conductor.agent.md` now links its four reference contracts by absolute path, and `squad status --global` reported every file matching the receipt.
- **Not yet confirmed:** T5(i), a global-only `conductor` run in Copilot reading `intake-path.md` and dispatching the architect, and T5(ii), the project-versus-global precedence observation. The owner records both outcomes on the pull request.
- **Review gates not run locally:** `docs drift` needs a CodeGraph index, the `ts-*` gates need `npm ci` under `dash/`, and the review gate's test step timed out on a busy machine. CI covers these.

### Durable facts and where they now live

| Decision | Destination |
|---|---|
| Q1, Q2, Q4 (Copilot global scope rewrites declared-resource links to absolute paths; shared `SquadResourceLinkRewriter`, only ZCode and Copilot-global use it; fail closed without a resolver) | [`docs/kyber-squad/architecture.md`](../../kyber-squad/architecture.md), "Resource projection" bullet. |
| Q3 (no ADR) | No ADR — ADR waiver: a narrow, target-specific refinement of the resource-projection rule, recorded in the architecture.md bullet. |
| Project scope stays byte-identical; per-harness probes for the other targets are unscheduled follow-ups | Section 8 of this plan. Open a todo when a probe is wanted. |

No documentation waiver was claimed: the behaviour change is described in canonical documentation.
