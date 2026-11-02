---
id: plans/2026-10-29-response-format-mandatory-load
title: "Make loading the response-format skill mandatory for conductor and bug-crusher replies"
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-29
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberSquad]
---

# Make loading the response-format skill mandatory for conductor and bug-crusher replies

**Status: Archived — complete 2026-10-29.** Autonomy: `autonomous`. Development mode: `test-first`.
Scale: `extends-component`, impact component KyberSquad.

**Archive date:** 2026-10-29. **Canonical destinations:** [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md), [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md).

## 1. Problem and goal

The owner observed that the `conductor` does not invoke the `response-format` skill when it
talks to them. Investigation (bug-crusher-investigator; not reproduced, no transcript) found
the cause in the canonical prompts, not in any renderer:

- `products/kyber-squad/agents/conductor.md:27` says "Load the `response-format` skill and
  follow it for every user-facing response", but line 32 then declares intent routing the
  conductor's "first action on every request", so the load is never the first step and is easy
  to skip.
- `products/kyber-squad/agents/bug-crusher.md:23-32` and
  `products/kyber-squad/skills/bug-crusher/SKILL.md:27-31` load the role skill first and the
  format skill as a passive instruction.
- `products/kyber-squad/skills/response-format/SKILL.md:3` has a passive description
  ("Carries the shape…", "Use when composing…"), unlike mandatory-trigger skills such as
  `csp-security` ("MUST be loaded whenever…"), so a model deciding from descriptions alone
  rarely selects it.
- Antigravity, Claude and Copilot renderers project canonical text unchanged, so no renderer
  code change is needed.

**Goal.** For `conductor` and `bug-crusher`, loading `response-format` is an explicit,
mandatory step before every reply addressed to the human, and the skill's description is a
mandatory trigger. `architect` stays headless and unchanged.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Plan or spec? | **Plan.** Bounded prompt-text change in one product plus its pin tests. | Investigator tripwires (multiple layers, changes existing assertions, changes an agent behaviour contract) argue against a direct fix, not for a spec: no new architecture. | ANSWERED. Intake by architect 2026-10-29 (autonomous: covered by intake rules). |
| D2 | Fix the description only, or the agent bodies too? | **Both.** The owner's stated scope: update conductor and bug-crusher to make the call mandatory, and strengthen the skill description. | Description alone leaves the body instruction passive; body alone leaves description-based selection weak. | ANSWERED. User decision 2026-10-29 (request verbatim). |
| D3 | Keep the verbatim invocation sentence? | **Yes.** Keep "Load the `response-format` skill and follow it for every user-facing response." unchanged on all three surfaces and add a separate mandate sentence beside it. | The existing pins (`CarriesResponseFormatInvocation`, `ConductorLoadsResponseFormatForEveryUserFacingResponse`, `BugCrusherSurfacesLoadTheResponseFormatSkill`, `TheResponseFormatInvocationMustBeTheWholeSentence`) require it verbatim; keeping it avoids weakening any approved assertion. | ANSWERED. autonomous: covered by existing tests and `docs/kyber-squad/requirements.md:65-79`. |
| D4 | How does the conductor's "first action is intent routing" rule coexist with a load-first rule? | **Load first, then route.** The router text becomes: after loading `response-format` at the start of the turn, the first *routing* action is to read intent. Dispatch still happens only after the contract read. | Removes the conflict the investigator found. The load is a skill load, not project work, so the conductor's no-project-work authority is unaffected. | ANSWERED. autonomous: low-confidence, no authoritative source; cheap to reverse (prompt text). |
| D5 | Mandate wording and pin. | **One mandate sentence per surface**, identical on conductor body, bug-crusher body and bug-crusher skill: "Loading the response-format skill is mandatory: do it at the start of every turn, before any other action and before writing any reply a human will read, even a one-line reply." A new test pins it verbatim (emphasis-stripped, case-sensitive). The sentence must not use the shape vocabulary banned by `RestatedShapeRule` (title line, bottom line, at most three sentences). | A verbatim pin matches the established style and cannot be satisfied by incidental co-occurrence. | ANSWERED. autonomous: low-confidence, no authoritative source; cheap to reverse. |
| D6 | New description text. | Starts with a mandatory trigger: "MANDATORY: load this skill at the start of every turn, before writing any reply to a human through a Squad agent. …" then keeps the existing shape summary and the "Do not use for digests consumed only by other agents" exclusion. A test pins that the description begins with `MANDATORY` and keeps the exclusion. | Mirrors `csp-security` trigger style; the exclusion keeps headless specialists out of scope. | ANSWERED. autonomous: covered by `csp-security/SKILL.md:9-12` pattern. |
| D7 | Scope of agents. | `conductor` and `bug-crusher` (agent body and skill) only. `architect`, `product-owner` and other headless specialists unchanged. | Human decision; matches the archived plan's A3 scope. | ANSWERED. User decision 2026-10-29. |
| D8 | Development mode. | `test-first`. | Default; no user opt-out supplied. | ANSWERED. Default, no opt-out. |
| AP1 | Approve and execute? | **Approved.** | Plan validated; docs_validate and docs_drift pass. | ANSWERED. autonomous: recommendation accepted (`AUTONOMOUS_APPROVAL: AP1`), 2026-10-29. |

## Design harvest

**Declared scale:** `documentation-impact: extends-component`, `impact-components: [KyberSquad]`.

| ID | Target component | Target doc type | Destination | Durable design to harvest |
|---|---|---|---|---|
| H1 | KyberSquad | requirements | `docs/kyber-squad/requirements.md` | The response-format requirement now states that loading the skill is a mandatory first step of every turn for `conductor` and `bug-crusher`, and that the skill description is a mandatory trigger. |
| H2 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` | The skill-carrier mechanism: invocation sentence plus mandate sentence on three surfaces, load-before-route ordering for the conductor, headless specialists exempt. |

## Investigation findings

- Discovery: codegraph 0, docs 0, direct reads 8. Reason: the packet carried the investigator's
  file and line evidence; the architect confirmed only the exact sentences to edit
  (`conductor.md`, `bug-crusher.md`, `skills/bug-crusher/SKILL.md`, `skills/response-format/SKILL.md`,
  `SquadCanonicalContentTests.cs` pins, `csp-security/SKILL.md`, the plan index, and the
  archived `2026-10-28-bluf-sbar-response-format` plan) via narrow reads of non-code markdown
  and one test file.
- Existing pins to preserve: `ResponseFormatInvocation` constant (`SquadCanonicalContentTests.cs:106`),
  `CarriesResponseFormatInvocation` (line 1853), tests at lines 662-669 and 911-946, and the
  `RestatesTheShape` guard that bans shape vocabulary in agent bodies and skills other than
  `response-format` (so the mandate sentence must avoid it).
- Out of scope for investigation: deployed copies under `~/.factory/` are render outputs and
  update on the next release/`squad update`.

## Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SquadCanonicalContentTests"` | (a) A theory over `agents/conductor.md`, `agents/bug-crusher.md`, `skills/bug-crusher/SKILL.md` asserts each carries the D5 mandate sentence verbatim (emphasis-stripped) and the D3 invocation sentence. (b) A fact asserts the `response-format` frontmatter description begins with `MANDATORY` and still contains the "Do not use for digests consumed only by other agents" exclusion. (c) A fact asserts `conductor.md` no longer calls intent routing the first action unconditionally: the router paragraph references loading `response-format` before routing. (d) A self-test asserts the mandate pin rejects a paraphrase and a lowercased sentence, as `TheResponseFormatInvocationMustBeTheWholeSentence` does. (e) Existing `RestatesTheShape` guard tests still pass. | The new tests fail on the current tree for the missing mandate sentence, passive description and router wording; existing four pins pass. | The same tests pass after T2 with no assertion weakened or removed; full `SquadCanonicalContentTests` green. |
| T2 | Same test file (GREEN only) | same | Content edits satisfy T1. | n/a (consumes T1 RED). | As T1 GREEN, plus `dotnet build -c Release` clean and `task-gate` pass. |
| T3 | No-test task (documentation) | `docs_validate`, `docs_drift` | Harvest H1, H2 lands; docs checks pass. | n/a: documentation only; replaced by `docs_validate` and `docs_drift` with 0 findings. | Both checks pass. |

## Tasks

| ID | Objective | Exact files or symbols | Acceptance criteria | Depends on | Required skills |
|---|---|---|---|---|---|
| T1 | Write the failing contract tests from the Test contract. | `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` (new constant for the mandate sentence, new theory, description fact, router fact, pin self-test; reuse `WithoutEmphasis`). | Tests compile, fail for the intended missing content, RED output captured. Existing assertions untouched. | none | xUnit testing, csharp |
| T2 | Make the content changes. (1) `conductor.md`: in Capabilities keep the D3 sentence and add the D5 mandate sentence; reword the Input router first-action sentence to "after loading `response-format`, your first routing action…" (D4). (2) `agents/bug-crusher.md`: add the mandate sentence after the D3 sentence and state it precedes the role skill's steps. (3) `skills/bug-crusher/SKILL.md` Response format section: add the mandate sentence. (4) `skills/response-format/SKILL.md`: replace the description per D6; add a short "Trigger" note if needed without restating the shape outside the skill's own text. | T1 tests green; no `RestatesTheShape` hits outside `response-format`; frontmatter still valid; no renderer or C# product code touched. | T1 | markdown authoring, squad canonical content |
| T3 | `docs-dev` closeout: harvest H1 and H2, update `docs/kyber-squad/requirements.md:65-79` and the architecture section on response format; archive the plan per the index policy. | `docs/kyber-squad/requirements.md`, `docs/kyber-squad/architecture.md`, `docs/plans/README.md` | `docs_validate` (mergeReady true at archive) and `docs_drift` pass. | T2 | docs-dev |

## Dependency graph

```text
T1 (RED) --> T2 (GREEN) --> T3 (docs closeout) --> review
```

All tasks touch overlapping content/test pins, so they are sequential; no safe parallelism.

## Risks

- A mandate sentence that uses the shape vocabulary trips `RestatesTheShape`; D5 avoids it and T1 covers it.
- Prompt text cannot force a model to call a tool; this raises likelihood, not determinism (no hook layer exists; archived plan A2). Residual risk reported at review.
- Deployed copies are stale until the next `squad update`/release; noted in the report.

## Out of scope

Renderer or C# product code, hooks, `architect`/`product-owner`/other specialists, the user-home guidance file, and any change to the format itself.

## Verification gates

Each developer task ends with the worker's `task-gate` run. After T3: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build` for `SquadCanonicalContentTests`, `docs_validate`, `docs_drift`, then the `code-reviewer` council over the whole change, then `docs-dev` closeout (T3).

## Closeout mapping

| ID | Destination |
|---|---|
| D1 | Documentation waiver — plan-only choice; no surviving architectural or behavioral invariant beyond the prompt contract itself. |
| D2 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) (`R3 — Who is bound and when`) and [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md); no ADR — ADR waiver: this refines the existing response-format contract without creating a new architecture boundary. |
| D3 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) (`R3 — Who is bound and when`); no ADR — ADR waiver: this is a contract clarification within the existing human-facing response standard. |
| D4 | [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md) (`mandatory load-before-route ordering`); no ADR — ADR waiver: this is a routing-order refinement under the existing conductor contract. |
| D5 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) and [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md); no ADR — ADR waiver: it tightens an existing human-facing contract, not a new architecture. |
| D6 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) and [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md); no ADR — ADR waiver: the trigger pattern extends the existing skill contract rather than changing the system boundary. |
| D7 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) (`R3`) and [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md); no ADR — ADR waiver: this is an interpretation of the existing role boundary, not a new architecture. |
| D8 | Documentation waiver — default mode statement only; no surviving product behavior beyond the default `test-first` development mode already in force. |
| AP1 | Documentation waiver — approval provenance only; it is not a durable design decision and no lasting architectural record is required. |
| H1 | [docs/kyber-squad/requirements.md](../../kyber-squad/requirements.md) (`User-facing response format`) |
| H2 | [docs/kyber-squad/architecture.md](../../kyber-squad/architecture.md) (`Home-owned files and the user-home response guidance` + response-format contract) |

**Documentation-impact review:** `DOCS_REVIEW: update` — the change touched `products/kyber-squad/migration/conductor.md` as migration metadata to align the migrated conductor body digest with the edited canonical source. That file is justified because it records the final-body hash for the migrated conductor asset; without the update, the migration record would be stale relative to the shipped body. No additional canonical docs were required beyond the requirements and architecture harvest above.
