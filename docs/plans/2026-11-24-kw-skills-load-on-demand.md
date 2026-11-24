---
id: plans/2026-11-24-kw-skills-load-on-demand
title: Kyber-Weave skills that agents load on demand
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-24
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - DocGraph
  - KyberArbiter
  - KyberDash
---

# Kyber-Weave skills that agents load on demand

**Status: Ready.**

## Request

> Author a Draft implementation plan at the PLAN_FILE above, promoted from the todo `docs/todo/kw-skills-load-on-demand.md` (user story settled with the owner on 2026-11-22; read it in full — it is the requirements source, including its In scope, Out of scope, Assumptions, Acceptance criteria and Suggested tests).
>
> Run settings:
> - development-mode: test-first
> - autonomy: autonomous (repository default). Decide open items yourself per your contract, record each in the permanent `## Decisions` table, and mark ones with no authoritative source `autonomous: low-confidence, no authoritative source`. Escalate only a true hard block (RECOMMENDED: none, with SOURCES_SEARCHED and COST_OF_WRONG).
>
> The plan must cover: the KW skill pool at standards/skills/<name>/SKILL.md and repository-over-shipped resolution; <scope>.skills.yml assignment with parent-technology inheritance; the dispatch-hook skills section for workers and role agents (one XML tag, opening instruction, omitted when empty, within the existing prompt budget); the kw-docs `load_skill` MCP tool; `docs validate` rules; `squad update` never deploying KW skills to harness folders; KyberDash recording and a separate KW skills section; and migration of every shipped <scope>.skills.md to .skills.yml (real guidance becomes a KW skill) including docs init and templates. Assign each task to the specialist whose file trigger matches (csharp-dev, typescript-dev, react-dev, test-dev, docs-dev, etc.), with RED test tasks by test-dev separate from GREEN implementation. Include the todo promotion bookkeeping (successor recorded in the todo, todo Superseded, index updated) as a docs-dev task.

The requirements source is [the todo](../todo/kw-skills-load-on-demand.md) (owner-settled 2026-11-22). Its In scope, Out of scope, Assumptions, Acceptance criteria and Suggested tests are binding; T1 moves it to the archive, and the archived copy keeps the text.

## Problem and goal

Today a technology folder's `<scope>.skills.md` is prose that the task prompt inlines whole (`<skills>` part, plus the parent's skills inside `<parent-rules>`), so every worker pays for guidance it may not need, and role agents dispatched without a task get nothing. The goal: one pool of **KW skills** (`<docs-root>/standards/skills/<name>/SKILL.md`, harness-skill shape), assigned per technology and role by a names-only `<scope>.skills.yml`, listed (name and description only) in one `<kw-skills>` part of every Squad agent's dispatch, loaded on demand through a new kw-docs `load_skill` tool, validated by `docs validate`, never deployed to a harness, and visible in KyberDash as its own section. Every shipped `.skills.md` is migrated: real guidance becomes a KW skill and the rest is deleted.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Draft plan at the PLAN_FILE, promoted from the todo | This file; T1 | Plan saved and indexed; T1 records the successor in the todo |
| Read the todo in full as the requirements source | Rows below; D1–D25 | Every todo acceptance criterion and suggested test has a row |
| KW skill pool at `standards/skills/<name>/SKILL.md` | T2, T3, T9 (D2, D3) | `KwSkillCatalogTests` pool cases; shipped and repository pools exist |
| Repository-over-shipped resolution | T3 (D4, D5) | `KwSkillCatalogTests.ARepositorySkillWinsOverTheShippedSkillOfTheSameName`; `McpLoadSkillTests.TheRepositoryCopyWinsOverTheShippedCopy` |
| `<scope>.skills.yml` assignment | T3, T8, T9 (D6) | `KwSkillCatalogTests` list cases; no `.skills.md` remains in `products/kyber-squad/standards` |
| Parent-technology inheritance | T3, T4 (D7) | `TaskPromptServiceTests.AChildTechnologyListsItsParentsSkillsThenItsOwn` |
| Skills section for workers | T4 | `TaskPromptServiceTests` kw-skills cases |
| Skills section for role agents, chosen from the agent name with no task and no technology | T4, T5 (D8, D9) | `TaskPromptServiceTests.ARoleDispatchWithNoTaskAndNoTechnologyCarriesItsRolesSkills`; `DispatchPromptInsertionTests.ARoleDispatchWithNoHeadersGetsItsRoleSkillsInserted` |
| One XML tag | T4 (D10) | `TaskPromptServiceTests.TheSkillsSectionIsOneKwSkillsTagThatOpensWithTheLoadSkillInstruction` |
| Opening instruction (KW not harness skills, load via `load_skill`, never the harness skill tool) | T4 (D10) | Same test asserts the exact instruction line |
| Omitted when empty | T4, T5 | `TaskPromptServiceTests.ATechnologyWithNoAssignedSkillsGetsNoSkillsSection`; `DispatchPromptInsertionTests.ARoleWithNoSkillsLeavesTheDispatchUnchanged` |
| Within the existing prompt budget, cut reported by left-out | T4 (D11) | `TaskPromptBudgetTests.OverTheBudgetTheKwSkillsListIsCutFirstAndNamedInLeftOut` |
| kw-docs `load_skill` tool | T6 (D12, D13) | `McpLoadSkillTests`; `McpToolNameContractTests` lists it |
| `load_skill`: name plus optional `projectPath` | T6 | `McpLoadSkillTests.AProjectPathRoutesToThatCheckout` |
| `load_skill`: body, folder, absolute reference paths, no reference contents | T3, T6 (D13, D14) | `McpLoadSkillTests.ItReturnsTheBodyTheFolderAndReferencePathsButNotTheirContents` |
| `load_skill`: unknown name lists existing names; malformed names what is missing; neither is a tool failure | T6 | `McpLoadSkillTests.AnUnknownNameListsTheSkillsThatExistAndIsNotAFailure`, `.AMalformedSkillNamesWhatIsMissingAndIsNotAFailure` |
| `load_skill` starts with the provenance line | T6 | `McpLoadSkillTests.TheReplyStartsWithTheProvenanceLine` |
| `docs validate` rules | T7 (D15) | `KwSkillValidationTests`, one finding per case |
| `squad update` never deploys KW skills to harness folders | T8 (D16) | `KwSkillPoolContentTests.NoKwSkillIsASquadSkillAndThePackCarriesNone`; `SquadKyberWeaveSkillsTests.GlobalInstallDeploysNoKwSkill` (CI) |
| KyberDash records each load with skill, agent, session and time | T11 (D17) | `SessionKwSkillLoadsTests` |
| KyberDash shows KW skill loads in their own section, separate from harness skills | T11, T12 (D18) | `SessionKwSkillLoadsTests.AKwSkillLoadIsNotAHarnessSkillInvocation`; `SessionKwSkillsSection.test.tsx` |
| Migrate every shipped `.skills.md` to `.skills.yml`; real guidance becomes a KW skill | T2, T8 (D19) | `KwSkillPoolContentTests.NoShippedStandardsFolderHoldsASkillsMarkdownFile` |
| `docs init` and the standards templates write the new shape | T8 | `DocsInitTemplateChoiceTests.DocsInitWritesSkillsYmlAndCopiesTheAssignedShippedSkills`; `StandardsFolderMigrationTests.AMissingSkillsListIsWrittenAsAnEmptyYamlList` |
| Assign each task to the specialist whose file trigger matches | Every task's `Technology:` and `Capabilities:` lines (D20) | Conflict with ADR 0080 recorded in D20 |
| RED test tasks by test-dev separate from GREEN implementation | Test contract; D21 | Conflict with the test-first execution contract recorded in D21; every task still shows its RED run |
| Todo promotion bookkeeping as a docs-dev task | T1 | Todo records this plan, is Superseded and archived; the todo index row points at the archive |
| development-mode test-first; autonomy autonomous | Frontmatter; Decisions provenance | `development-mode: test-first`, `autonomy: autonomous` |
| Todo AC: listed skill appears by name and description in a technology's prompt | T4 | `TaskPromptServiceTests.AListedSkillAppearsByNameAndDescription` |
| Todo AC: child lists parent's skills and its own | T4 | Row above |
| Todo AC: role agent with no task and no technology gets its role's section | T4, T5 | Rows above |
| Todo AC: one XML tag opening with the `load_skill` instruction | T4 | Row above |
| Todo AC: no assigned skills, no section | T4, T5 | Rows above |
| Todo AC: `load_skill` returns body, folder, reference paths, not contents | T6 | Row above |
| Todo AC: repository copy wins in `load_skill` and the section | T3, T4, T6 | `TaskPromptServiceTests.TheSectionUsesTheRepositoryCopysDescription`; MCP row above |
| Todo AC: unknown name lists names, not a tool failure | T6 | Row above |
| Todo AC: `docs validate` reports the four cases | T7 | Row above |
| Todo AC: after `squad update` no harness skill folder holds a KW skill | T8 | Row above |
| Todo AC: KyberDash shows the load in a KW skills section with skill, agent, session | T11, T12 | Rows above |
| Todo AC: no shipped standards folder has a `.skills.md`; `docs init` writes `.skills.yml` | T8 | Rows above |
| Todo Out of scope (harness skills, Arbiter rule, reference-reading tool, other selection keys, new content) | Out-of-scope boundaries | No task touches them |

## Scale

`documentation-impact: extends-component` over KyberSquad (standards folders, shipped pool, agent bodies, squad deployment guard), DocGraph (`docs validate` rules, docs init, the kw-docs tools), KyberArbiter (the dispatch hook's role insertion) and KyberDash (the KW skills section). No new component.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Plan or spec? | Plan: the todo is a settled user story with acceptance criteria, and every change extends an existing seam. | Conductor promoted it as a plan. | ANSWERED — autonomous: recommendation accepted |
| D2 | Where does the pool live? | Repository pool `<docs-root>/standards/skills/<name>/SKILL.md` for every configured docs root (first root in `ontology.docs-roots` order wins a name); shipped pool `products/kyber-squad/standards/skills/<name>/`. Add `DocsLayout.SkillsPool(docsRoot)`, `SkillsListSuffix = ".skills.yml"` and `SkillsListFile(docsRoot, name)`; `SkillsFile`/`SkillsSuffix` stay as the legacy `.skills.md` names for the KW-DOC-SKILL-005 warning. | The todo fixes the path. A technology or role may not be named `skills` (KW-DOC-SKILL-006). | ANSWERED — source: todo In scope |
| D3 | How does the governed corpus treat the pool's `SKILL.md` files, which carry harness frontmatter only? | `DocumentLoader` skips every file beneath a docs root's `standards/skills/`, and the standards-folder shape walk skips the `skills` folder. The pool is checked only by the KW-DOC-SKILL rules. | Otherwise every pool file fails `KW-DOC-SPEC-001` and the pool folder fails `KW-DOC-SPEC-012`. The todo requires harness shape "so a skill copies between the two unchanged". | ANSWERED — source: todo In scope; `DocumentLoader.IsExcluded`, `DocSpecValidator` folder walk |
| D4 | How does the binary reach the shipped pool? | Embed `products/kyber-squad/standards/skills/**` in `KyberWeave.Application` (logical name `KwSkills/<name>/<relative path>`, forward slashes) plus `skills.manifest.yml`, which lists each skill and its files. `IEmbeddedResources` reads by name only, so the manifest is the enumeration; a content test pins manifest = tree. | `IEmbeddedResources` exposes `OpenRead`/`ReadAllText` only (seam register). Same pattern as the toolchain roster guard. | ANSWERED — autonomous: low-confidence, no authoritative source (cheap to reverse) |
| D5 | Shipped skills have no file on disk, but `load_skill` must return an absolute folder and reference paths. | On a shipped hit, `load_skill` materializes the skill once under `<home>/.kyber-weave/kw-skills/<name>-<first 12 hex of SHA-256 over the manifest-ordered files>/` through `IWritableFileSystem` (atomic write, existing identical folder reused) and reports that folder with `source: shipped`. The prompt list reads only the embedded `SKILL.md` frontmatter and writes nothing. | Home comes from `IProcessEnvironment`; `~/.kyber-weave/` is the existing Kyber state root. A content hash, not the version, keys the folder, so no version seam is needed. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D6 | Shape of `<scope>.skills.yml`? | A YAML sequence of skill-name strings and nothing else; `[]` is a valid empty list. Any other shape is KW-DOC-SKILL-001, and the catalog then treats the file as listing nothing. A missing file lists nothing. | Todo: "a YAML list of skill names, nothing else". | ANSWERED — source: todo |
| D7 | Inheritance order and depth? | One level, like rules: the parent's (`builds-on`) list, then the child's own, then the role's (D8), duplicates dropped at first occurrence. | `DocumentIndex.StandardsFor` reads one parent level for rules. | ANSWERED — source: `StandardsFor`, todo |
| D8 | Which role folder does an agent name map to? | A role folder is `standards/<role>/` where `<role>` is in `ontology.roles` or `KyberStandardsTemplates.Roles`. The agent name maps to the folder of the same name; otherwise a shipped alias table maps `code-reviewer`, `plan-reviewer`, `review-lens` and `review-triage` to `reviewer`. No match gives no role skills (`docs-dev`, `product-owner`, `research-agent`, the developer tiers). | Todo assumption: role names match the shipped role folders (`architect`, `conductor`, `devops`, `reviewer`). | ANSWERED — autonomous: low-confidence, no authoritative source |
| D9 | How does the hook learn the role agent? | `DispatchPromptQuery` gains `Target` (the dispatched agent: `subagent_type` or the harness equivalent), and `DispatchPromptInsertion.Expand(prompt, context, target)` takes it from every adapter. `TaskPromptRequest` gains `RoleAgent`; the service uses `RoleAgent ?? Agent` for the role lookup only, so the tiered-developer refusal still reads `Agent` alone. A dispatch with no `TASK`/`TECHNOLOGY` but a target whose role has skills gets a role-only prompt (just `<kw-skills>`) put in front of its unchanged text; a role with no skills, or any refusal on a role-only query, leaves the dispatch unchanged and is never a stop. | The hook passes `Agent: null` today (`HookCommand.PromptExpander`), and `RefusedTechnologyDeveloper` keys on `request.Agent`. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D10 | Tag and exact text? | Tag `<kw-skills>`, placed where `<skills>` is today (after `<technology-rules>`, before `<standard>`); role-only prompts carry it alone. First line exactly: `These are Kyber-Weave skills, not harness skills. When a skill's description fits your work, load it by calling the kw-docs load_skill tool with its name; never load one through your harness's own skill tool.` Then one line per skill: `- <name>: <description>`. `<kw-skills>` joins `DispatchPromptInsertion.ExpandedTags`; `<skills>` stays there so older expanded dispatches stay idempotent. The `<skills>` part and the parent skills inside `<parent-rules>` are removed. | Todo fixes the content of the instruction; the wording is ours. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D11 | Budget behaviour? | The `<kw-skills>` list is one cut unit, cut before any standard section (where the skills units are today); when cut, the part reads `The KW skills list was left out: the prompt budget did not fit it. The last part names what was cut.` and `<left-out>` names `KW skills (<n> listed)`. | Todo assumption; `TaskPromptService.Fit`/`Units`. | ANSWERED — source: todo Assumptions |
| D12 | Tool name? | Exactly `load_skill`, as the request and todo state, although every other kw-docs tool uses a long `docs_…` name. Added to `McpToolNameContractTests.AddedTools` and to the `kw-docs` roster in `products/kyber-squad/toolchain.yml`, so every renderer grants it. | Explicit request beats a naming convention that no rule bans; it fits the 64-character limits. | ANSWERED — source: request |
| D13 | `load_skill` reply? | The provenance line, then `skill: <name>`, `source: repository` or `source: shipped`, `folder: <absolute path>`, `references:` with one `- <absolute path>` per file in the folder other than `SKILL.md` (recursive, no links followed, ordinal order) or `(none)`, a blank line, then the `SKILL.md` body below its frontmatter. Unknown name: `No KW skill is named '<name>'. Skills that exist: <comma-separated names>.` Malformed: `The KW skill '<name>' in <folder> cannot be loaded: its SKILL.md has no <name/description>.` Neither is an MCP error. | Todo wording; the provenance line comes from the existing `Provenance` composition in `DocsQueryService`. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D14 | The skill name comes from an MCP caller: how is the path kept safe? | The name must match `^[a-z0-9]+(-[a-z0-9]+)*$` and be one of the names the catalog listed before any path is built from it; files are read only through `EnumerateContainedFiles` (no links), and materialization writes only beneath its own `kw-skills` folder. | csharp rules: "Never build a path from a client-supplied filename". | ANSWERED — source: csharp rules |
| D15 | `docs validate` rule ids? | New family, permanent ids: KW-DOC-SKILL-001 `.skills.yml` is not a list of names (error); -002 a listed name has no folder in the repository or shipped pool (error); -003 a pool `SKILL.md` has no `name` or no `description` (error); -004 `name` differs from its folder (error); -005 a legacy `<scope>.skills.md` remains (warning, hint: move real guidance into a KW skill, then delete it); -006 a technology or role is named `skills` (error). KW-DOC-SPEC-012 now expects `<scope>.skills.yml`; KW-DOC-SPEC-015 no longer checks a skills file. | csharp rules: ids are permanent, add, never reuse. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D16 | Does `squad update` need a code change to keep KW skills out of harness folders? | No: `SquadSourceLoader` and `SquadPacker` read only `products/kyber-squad/skills` and `.apm/skills`, never `standards/`. T8 adds guards: KW skill names are disjoint from every squad and APM skill name, no pack entry sits under `standards/skills`, and a global install into a scratch home writes no KW skill (integration, CI). | `SquadPacker` step 3 reads `skills/` only; `SquadReferenceLocator.Folders` is `agents, droids, skills`. | ANSWERED — source: code |
| D17 | How does KyberDash record a load? | From the tool calls it already stores; no schema change. A load is a `tool_call` whose `tool_call_mcp.server_name` normalizes (lower case, `_`→`-`) to `kw-docs` and whose `server_tool_name` is `load_skill`, or, with no MCP split, whose `tool_name` is `mcp__kw-docs__load_skill`, `mcp__kw_docs__load_skill`, `kw-docs_load_skill`, `kw-docs__load_skill` or `kw-docs/load_skill`. Skill = the `name` string in `tool_call_input_text` JSON, else null; agent = the session's `agent_name`; time = the record's `started_at_utc`. | Todo: "Recording only". `CollectedToolCall` carries the MCP server and tool names and the input text. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D18 | KyberDash API and placement? | `GET /api/kyber/session/{id}/kw-skills?page=&limit=` (default 25), 404 for an unknown session, and `{ "rows": [ { "skill": string\|null, "agent": string\|null, "session_id": string, "span_id": string, "loaded_at": string\|null } ], "total", "limit", "offset", "page" }` in call order. The web shows it as its own section, `KW skills`, right after the Skills section, with skill (or "not recorded"), agent, session and time columns and the existing loading/error/empty states. The section has no number, so the numbers of the other sections stay as they are. | Fixed here so T11 and T12 run in parallel, as ADR 0092's section routes do. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D19 | Migration of each shipped `.skills.md` (products/kyber-squad/standards)? | Becomes a KW skill: csharp Commands + github-actions CI diagnosis → `dotnet-build-diagnostics` (csharp, github-actions); aws → `aws-cli-queries`; azure → `azure-resource-inspection`; go gates → `go-gates`; maui doctor → `maui-environment-doctor`; python Pylance → `pylance-tooling`; rust cross-build → `rust-cross-build` (rust; tauri inherits); sql-server → `sql-server-reference`; tauri docs → `tauri-v2-reference`. Deleted with an empty list: architect, conductor, reviewer (they point at harness skills), azure-devops, data-access-layer, devops, github, gitlab, jira, pulumi, react, typescript (one-line pointers or text that repeats the standard), and every "What this folder covers" and "Hand-off" section. | Todo: "Prose in it that is real guidance becomes a KW skill; the rest is deleted"; out of scope: new content beyond what migration moves. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D20 | The request says to assign each task to a named specialist; ADR 0080 says plan tasks name capabilities and the conductor chooses the agent. | Honour ADR 0080: no `Role:` line. Each task carries exactly one `Technology:` and a `Capabilities:` line that names its file trigger (C# service code, .tsx React UI, Markdown docs), so the conductor's pick is unambiguous: csharp → csharp-dev or the C# tier, react → react-dev, docs → docs-dev. | Autonomous rule 3: prefer the accepted decision and record the conflict. Under the shipped tiered roster the hook refuses a named technology developer (`TieredDeveloperRefusal`), so a `Role: csharp-dev` would stop the dispatch. | ANSWERED — autonomous: recommendation accepted (conflict recorded) |
| D21 | The request asks for RED test tasks by test-dev, separate from GREEN implementation. | Not split. Each behaviour is one task in which its owner writes the failing tests, runs `task-gate --red`, then implements. The request is kept in spirit: every Test contract row names the failing run, and no implementation starts before its tests fail. | `delivery-execution` Test-first mode: "No task writes only failing tests for another to implement", proven by two task-gate reports per task (`KW-ARB-MODE-002`); a tests-only task cannot produce the passing report. This is a mechanically enforced ban, so it outranks the request. | ANSWERED — autonomous: recommendation accepted (conflict recorded) |
| D22 | The phrase "Kyber-Weave skills" already names the harness skills Kyber-Squad ships from `.apm/skills` (ADR 0049). | Code, docs and the prompt say "KW skill" for the new kind, and the glossary defines it against ADR 0049's term. | Avoids two meanings for one name. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D23 | Does this repository's own `docs/standards` migrate too? | Yes (T9): its eight `.skills.md` become `.skills.yml`, and their real guidance becomes repository KW skills `kw-dotnet-inner-loop` (csharp), `kw-ci-gates` (github-actions, beside shipped `dotnet-build-diagnostics`), `kw-tray-rust-loop` (rust, including the tauri IPC note), `kw-canon-schema-change` (sqlite), `kw-test-commands` (test) and `kw-dash-web-loop` (typescript), while react and tauri list only what they inherit or the shipped `tauri-v2-reference`. | The repository runs its own gates; leaving them would raise KW-DOC-SKILL-005 and KW-DOC-SPEC-012. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D24 | What does `docs init` copy into the repository pool? | The shipped skills named by the `.skills.yml` files it writes (with the parent's), never overwriting a skill folder that already exists. `docs standards migrate` writes `[]` for a missing `.skills.yml` and leaves a legacy `.skills.md` for its KW-DOC-SKILL-005 warning. | Todo: "`docs init` copies them into the repository's pool". | ANSWERED — autonomous: low-confidence, no authoritative source |
| D25 | Where does `docs_get_coding_standard_for_technology` stand? | Its skills prose becomes the same KW skills list the prompt carries (same text), and the `StandardsBundle` `Skills`/`ParentSkills` members are removed once no caller reads them. | One source: "the docs tool and the task prompt both call this, so they cannot disagree" (`StandardsFor` remarks). | ANSWERED — source: code |
| D26 | Arbiter KW-ARB-ROSTER-001 refused `csharp-dev` for T3 and T11 (2026-11-24): can the task file switch to `worker-roster: technology`? | No. The task file is unchanged. Each worker task is dispatched to the developer tier the Arbiter names (T3 `dev-medium`, T11 `dev-easy`; docs tasks stay with `docs-dev`). The tier agents must first be installed in the harness by redeploying the current Squad (`kyber-weave squad update`), which is the owner's action outside the repository. | `tiered` is the only roster: `KyberWeaveConfigAssembler.ParseWorkerRoster` defaults an absent `worker-roster` to `tiered` and throws on `technology` ("names the worker roster that was removed"). `PlanTaskFileValidator` reads a task file recording `technology` as `tiered` (warning only), so editing the file cannot change the verdict. `.kyber-weave/kyber-weave.yml` sets no `worker-roster`, and the task file records none and no per-task agent. `products/kyber-squad/agents` ships `dev-easy`, `dev-medium` and `dev-hard`, so the installed Claude roster predates the removal. | ANSWERED — source: code; the harness redeploy is escalated to the owner |
| AP1 | Approve and execute | Approved: execute the plan as written, D20 and D21 deviations accepted as recorded. | Conductor verified PLAN_READY on 2026-11-24 (Decisions complete, test-first, extends-component scale with Design harvest). | ANSWERED — autonomous: recommendation accepted (AUTONOMOUS_APPROVAL: AP1, 2026-11-24) |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberSquad | architecture | KW skills: pool, assignment and resolution | D2–D8, D19, D22–D24: pool paths, `.skills.yml`, inheritance, role mapping, shipped vs repository |
| KyberSquad | architecture | Never deployed to a harness | D16 and its guards |
| KyberArbiter | architecture | Dispatch prompt insertion | D9–D11: `<kw-skills>`, role-only insertion, the target, budget |
| DocGraph | mcp-runbook | `load_skill` | D12–D14: arguments, reply, failures, materialization |
| DocGraph | architecture | Standards folder files and the KW skill rules | D3, D6, D15; the rule reference rows for KW-DOC-SKILL-001 to -006 |
| KyberDash | architecture | Read-model register and REST API table | D17, D18: the `kw-skills` section route and how a load is recognized |
| ContextHygiene | reference | Glossary | D22: the term "KW skill" |
| KyberSquad | adr | ADR 0093 | KW skills are a separate pool loaded on demand through kw-docs, never deployed as harness skills (rejected: inline prose, harness deployment) |

## Investigation findings

- Revision read: worktree `claude/feat/kw-skills-load-on-demand` at `e72558fb` (base `49cb1c66`), through the worktree's own CodeGraph index.
- `TaskPromptService.Compose` writes `<skills>` from `StandardsBundle.Skills` and appends parent skills inside `<parent-rules>`; `Units` cuts skills first. `DocumentIndex.StandardsFor` builds the bundle from corpus documents with `doc-type: skill`.
- `DispatchPromptInsertion.Expand` returns `None` when neither `TASK` nor `TECHNOLOGY` is present, and the adapters call it without the target, although each one has it (`subagent_type` and its equivalents). `HookCommand.PromptExpander` sends `Agent: null`.
- `DocumentLoader` walks every `*.md` under each docs root; `DocSpecValidator` walks every `standards/<scope>` folder for the four files.
- `KyberStandardsTemplates.FileName`/`ResourceName` and `Docs.props` embed one `.skills.md` per shipped folder (23 in `products/kyber-squad/standards`, 8 in this repository's `docs/standards`). `ArbiterCommandAllowListPromptTests` and `DocsTaskPromptFixture` read `.skills.md`; `PlatformAgentContentTests` pins `<skills>` in `platform-researcher.md`, and `dev-easy`, `dev-medium`, `dev-hard` and `platform-researcher` describe the `<skills>` part.
- KyberDash stores tool calls with MCP server and tool names and the input text (`tool_call`, `tool_call_mcp`, `tool_call_input_text`); harness skill invocations come only from `Skill` tool calls (`SkillExtraction.ExtractInvoked`), so a `load_skill` call is not one.
- The kw-docs roster in `products/kyber-squad/toolchain.yml` is pinned to the `[McpServerTool]` names by `SquadToolchainRosterGuardTests`, and the names by `McpToolNameContractTests`.
- Discovery: codegraph 7, docs 0 (the kw-docs MCP tools are not exposed in this planning session, so standards and rules were read directly: `docs/standards/csharp/csharp.standards.md`, `csharp.rules.md`, `test/test.rules.md`), direct reads 41: 19 bodies elided by CodeGraph gaps (`TaskPromptService`, `DispatchPromptInsertion`, `HookCommand`, adapters, `DocSpecValidator`, `DocumentLoader`, KyberDash bridge and reader), 15 unindexed Markdown and YAML files (todo, skills files, toolchain, agent bodies, skills SKILL.md), 7 confirmations (test namespaces, catalog, ADR numbers).

## Test contract (development-mode: test-first)

Every row is run by its own task's owner: tests written first, `task-gate --red` showing them fail, then the implementation (D21). An existing test keeps its assertions. A worker may narrow a locator or fixture that the new work made ambiguous and names each such edit. A test whose subject this plan replaces (the inlined `<skills>` prose, `.skills.md` fixtures) is rewritten to the new behaviour in the task that replaces it, and the report names it.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | none (documentation bookkeeping) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` | No-test task: the read-only check is `docs validate .` and `docs drift .` reporting no finding about the todo or the todo index. | Not applicable (no behaviour). | Both checks report no todo or index finding. |
| T2 | none (shipped content authored as Markdown and YAML; its guards are T8's `KwSkillPoolContentTests`) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` | No-test task: the read-only check is that every SKILL.md has `name` = folder and a `description`, and that the manifest lists every file. T8's content tests pin it mechanically. | Not applicable. | T8's content tests pass over this content. |
| T3 | new `tests/KyberWeave.Application.Tests/Docs/KwSkills/KwSkillCatalogTests.cs` (namespace `KyberWeave.Application.Tests.Docs.KwSkills`, over `InMemoryFileSystem` and `FakeEmbeddedResources`; copy the fixture style of `TaskPromptServiceTests`); extend `tests/KyberWeave.Application.Tests/Docs/Parsing/MultipleDocsRootTests.cs` | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Docs.KwSkills.KwSkillCatalogTests\|FullyQualifiedName~KyberWeave.Tests.MultipleDocsRootTests"` | `ATechnologyListsTheSkillsItsSkillsYmlNames` (name and description from frontmatter); `AMissingSkillsYmlListsNothing`; `AnEmptyListListsNothing`; `ASkillsYmlThatIsNotAListOfNamesListsNothingAndReportsWhy`; `AChildListsItsParentsSkillsThenItsOwnWithoutDuplicates`; `ARoleAgentNameMapsToItsRoleFolder` and `TheReviewerAliasesMapToReviewer`, `AnUnmappedAgentHasNoRoleSkills`; `ARepositorySkillWinsOverTheShippedSkillOfTheSameName` (list and load); `AShippedSkillResolvesFromTheManifestWhenTheRepositoryHasNone`; `LoadReturnsTheBodyBelowTheFrontmatterTheFolderAndReferencePathsSorted` (and no reference content); `LoadingAShippedSkillMaterializesItOnceUnderTheHomeFolder` (second load writes nothing); `AMaterializationFailureStillReturnsTheBody` (folder reads `(not materialized: <reason>)`, no references, not a failure); `AnUnknownNameReturnsTheExistingNames`; `AMalformedSkillNamesWhatIsMissing`; `ANameThatIsNotASkillNameIsUnknownAndBuildsNoPath` (`../x`, `A`, blank, whitespace-trimmed `x ` is `x`); `ALinkedReferenceFileIsNotListed`; loader: `TheSkillsPoolIsNotPartOfTheCorpus`. | New types do not compile (`KwSkillCatalog`, `DocsLayout.SkillsPool`); the loader test fails because pool files load. | All named tests pass; non-zero count. |
| T4 | extend `tests/KyberWeave.Application.Tests/Docs/Validation/TaskPromptServiceTests.cs` and `TaskPromptBudgetTests.cs`; update `tests/KyberWeave.Testing/DocsTaskPromptFixture.cs` and the constructor call in `tests/KyberWeave.Content.Tests/ArbiterCommandAllowListPromptTests.cs` | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.TaskPromptServiceTests\|FullyQualifiedName~KyberWeave.Tests.TaskPromptBudgetTests"` | `AListedSkillAppearsByNameAndDescription`; `AChildTechnologyListsItsParentsSkillsThenItsOwn` (one `<kw-skills>` block, no skills inside `<parent-rules>`); `TheSkillsSectionIsOneKwSkillsTagThatOpensWithTheLoadSkillInstruction` (exact D10 line); `ATechnologyWithNoAssignedSkillsGetsNoSkillsSection` (no `<kw-skills>`, no `<skills>`); `TheSectionUsesTheRepositoryCopysDescription`; `ARoleDispatchWithNoTaskAndNoTechnologyCarriesItsRolesSkills` (prompt is `<kw-skills>` only); `ARoleWithNoSkillsAndNoTaskBuildsAnEmptyPrompt` (Built true, empty); `ATaskDispatchToARoleAgentListsTechnologyThenRoleSkills`; `ATieredDeveloperRefusalStillReadsOnlyAgent` (RoleAgent `csharp-dev` with Agent null is not refused); budget: `OverTheBudgetTheKwSkillsListIsCutFirstAndNamedInLeftOut`, `EveryPartFitsTheDefaultBudget` kept. | `TaskPromptRequest.RoleAgent` and the catalog constructor argument do not compile; `<kw-skills>` assertions fail. | All named tests pass; non-zero count. |
| T5 | extend `tests/KyberWeave.Arbiter.Tests/DispatchPromptInsertionTests.cs` | `dotnet test tests/KyberWeave.Arbiter.Tests/KyberWeave.Arbiter.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.DispatchPromptInsertionTests"` | `ARoleDispatchWithNoHeadersGetsItsRoleSkillsInserted` (Claude `subagent_type: architect`; the skills block in front, original text unchanged below); `ARoleWithNoSkillsLeavesTheDispatchUnchanged` (no warn, no stop); `ARoleOnlyRefusalNeverStopsTheDispatch`; `ADispatchAlreadyOpeningWithKwSkillsIsNotExpandedAgain`; `TheTargetReachesThePromptQueryOnEveryRewritingHarness` (one case per adapter that calls `Expand`); `ATaskDispatchStillExpandsAsBefore`. | `DispatchPromptQuery.Target` and the `Expand` overload do not compile. | All named tests pass; existing cases unchanged. |
| T6 | new `tests/KyberWeave.Mcp.Tests/McpLoadSkillTests.cs` (namespace `KyberWeave.Mcp.Tests`; copy `McpAgentCodingStandardsTests` setup); extend `McpToolNameContractTests.cs`, `McpAgentCodingStandardsTests.cs`; `tests/KyberWeave.Content.Tests/SquadToolchainRosterGuardTests.cs` (runs unchanged) | `dotnet test tests/KyberWeave.Mcp.Tests/KyberWeave.Mcp.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Mcp.Tests.McpLoadSkillTests\|FullyQualifiedName~KyberWeave.Tests.McpToolNameContractTests\|FullyQualifiedName~KyberWeave.Mcp.Tests.McpAgentCodingStandardsTests"` | `ItReturnsTheBodyTheFolderAndReferencePathsButNotTheirContents`; `TheRepositoryCopyWinsOverTheShippedCopy`; `AShippedSkillReportsItsMaterializedFolder`; `AnUnknownNameListsTheSkillsThatExistAndIsNotAFailure` (IsError false); `AMalformedSkillNamesWhatIsMissingAndIsNotAFailure`; `ABlankNameIsAnsweredWithUsage`; `TheReplyStartsWithTheProvenanceLine`; `AProjectPathRoutesToThatCheckout`; name contract lists `load_skill`; coding-standard tool: `TheBundleListsKwSkillsInsteadOfSkillsProse`. | `load_skill` is not exposed; assertions fail. | All named tests pass; `SquadToolchainRosterGuardTests` passes with the roster updated. |
| T7 | new `tests/KyberWeave.Application.Tests/Docs/Validation/KwSkillValidationTests.cs` (namespace `KyberWeave.Application.Tests.Docs.Validation`; copy `StandardsFolderShapeTests` fixtures); extend `StandardsFolderShapeTests.cs` | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Docs.Validation.KwSkillValidationTests\|FullyQualifiedName~KyberWeave.Application.Tests.Docs.Validation.StandardsFolderShapeTests"` | One finding each: `ASkillsYmlThatIsAMapIsKwDocSkill001`, `…AScalar…`, `…AListOfNonStrings…`; `AListedSkillWithNoFolderIsKwDocSkill002` (a name only the shipped pool has is not reported); `ASkillMdWithNoNameIsKwDocSkill003`, `…NoDescription…`; `ANameThatDiffersFromItsFolderIsKwDocSkill004`; `ALegacySkillsMdIsKwDocSkill005Warning`; `AFolderNamedSkillsDeclaredAsATechnologyIsKwDocSkill006`; `AnEmptyListIsValid`; shape: `AFolderWithSkillsYmlHasAllFourFiles`, `AFolderMissingSkillsYmlIsKwDocSpec012`, `ThePoolFolderIsNotAStandardsFolder`. | Rule constants do not compile; the shape test still wants `.skills.md`. | All named tests pass. |
| T8 | extend `tests/KyberWeave.Application.Tests/Docs/Scaffolding/DocsInitTemplateChoiceTests.cs`, `StandardsFolderMigrationTests.cs`, `StandardsFolderMigrationScenarioTests.cs`; new `tests/KyberWeave.Content.Tests/KwSkillPoolContentTests.cs` (namespace `KyberWeave.Tests`, through `ICorpusSource`); update `ArbiterCommandAllowListPromptTests.cs` file list; extend `tests/KyberWeave.Integration.Tests/Squad/SquadKyberWeaveSkillsTests.cs` (CI only) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Docs.Scaffolding.DocsInitTemplateChoiceTests\|FullyQualifiedName~KyberWeave.Application.Tests.Docs.Scaffolding.StandardsFolderMigrationTests\|FullyQualifiedName~KyberWeave.Application.Tests.Docs.Scaffolding.StandardsFolderMigrationScenarioTests"` and `dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.KwSkillPoolContentTests\|FullyQualifiedName~KyberWeave.Tests.ArbiterCommandAllowListPromptTests"` | `DocsInitWritesSkillsYmlAndCopiesTheAssignedShippedSkills` (with the parent's; an existing repository skill folder is not overwritten); `DocsInitWritesNoSkillsMd`; `AMissingSkillsListIsWrittenAsAnEmptyYamlList`; `ALegacySkillsMdIsLeftInPlace`; content: `NoShippedStandardsFolderHoldsASkillsMarkdownFile`, `EveryShippedFolderHasASkillsYmlListOfNames`, `EveryListedShippedSkillIsInTheShippedPool`, `EveryShippedSkillHasNameEqualToFolderAndADescription`, `TheManifestListsExactlyThePoolsFiles`, `NoKwSkillIsASquadSkillAndThePackCarriesNone` (names disjoint from `products/kyber-squad/skills` and `.apm/skills`; no `standards/skills` pack entry); integration: `GlobalInstallDeploysNoKwSkill` (CI; not in the task gate). | Content tests fail (23 `.skills.md` remain, no `.skills.yml`); scaffolding tests fail (init writes `.skills.md`). | All named gate tests pass; the integration case passes in CI. |
| T9 | none (this repository's documentation and its KW skills, Markdown and YAML) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` | No-test task: the read-only check is `docs validate .` showing no KW-DOC-SKILL finding and no KW-DOC-SPEC-012, with `docs/standards/*` holding `.skills.yml` and no `.skills.md`. | Not applicable. | Both docs checks clean apart from the open-plan lifecycle finding. |
| T10 | extend `tests/KyberWeave.Content.Tests/PlatformAgentContentTests.cs` and `DeveloperAgentContentTests.cs` | `dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.PlatformAgentContentTests\|FullyQualifiedName~KyberWeave.Tests.DeveloperAgentContentTests"` | Agent bodies name `<kw-skills>` and `load_skill`, and none names a `<skills>` part or a skills file: `ThePlatformResearcherNamesTheKwSkillsPart`, `EveryDeveloperTierNamesTheKwSkillsPartAndLoadSkill`. | The updated assertions fail on today's bodies. | Both classes pass. |
| T11 | new `dash/server.tests/KyberDash.Application.Tests/Sessions/SessionKwSkillLoadsTests.cs` (namespace `KyberDash.Application.Tests.Sessions`, over `InMemoryRelationalStore`); extend `dash/server.tests/KyberDash.Data.Tests/Repositories/SqliteRelationalStoreReadTests.cs`; extend `dash/server.tests/KyberDash.Presentation.Tests/MinimalApiEndpointTests.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.Sessions.SessionKwSkillLoadsTests\|FullyQualifiedName~KyberDash.Data.Tests.Repositories.SqliteRelationalStoreReadTests\|FullyQualifiedName~MinimalApiEndpointTests"` | `AnMcpSplitLoadSkillCallIsALoadWithSkillAgentSessionAndTime`; one case per D17 tool-name spelling; `AnotherServersLoadSkillIsNotALoad`; `AMissingOrUnparsableInputGivesANullSkill`; `ASessionWithNoLoadsAnswersAnEmptyPage`; `PagesAreInCallOrderWithTheTotal`; `AnUnknownSessionIs404`; `AKwSkillLoadIsNotAHarnessSkillInvocation` (the skills section is unchanged); reader: `SessionKwSkillLoadsReadsOnlyThatSessionsToolCalls`; endpoint: route answers the D18 shape. | `SessionKwSkillLoads` and the route do not exist. | All named tests pass. |
| T12 | new `dash/web/src/components/session/SessionKwSkillsSection.test.tsx`; extend `dash/web/src/lib/kyberApi.sections.test.ts` | `npm --prefix dash run test:related -- dash/web/src/components/session/SessionKwSkillsSection.tsx dash/web/src/lib/kyberApi.ts` | Renders loading, error with retry, empty ("No KW skills were loaded in this session."), and rows with skill (null shows "not recorded"), agent, session and time; pages through the route; mounted after the Skills section and apart from it; `fetchKyberSessionKwSkills` requests `/api/kyber/session/:id/kw-skills?page=` and a malformed envelope is an error. | The component and fetcher do not exist. | Named tests pass; `typecheck` and `lint` clean. |
| T13 | none (closeout documentation) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate . --merge-ready` | No-test task: closeout gates. | Not applicable. | Zero findings after archive. |

## Tasks

### T1: Promote the todo — record the successor, mark it Superseded, update the index

- **Objective:** The todo names this plan as its successor and is Superseded and archived, and the todo index points at the archive.
- **Capabilities:** Documentation in Markdown under the governed-docs rules.
- **Difficulty:** easy
- **Session:** `docs-promotion`
- **Depends on:** none
- **Files:**
  - `docs/todo/kw-skills-load-on-demand.md`: move to `docs/archive/todo/kw-skills-load-on-demand.md` (`git mv`) without editing its body text; frontmatter `id: archive/todo/kw-skills-load-on-demand`, `status: superseded`, `last-reviewed` to the day; under the title add `**Status:** Superseded and archived`, `**Archive Date:**` and the line `Successor: implementation plan 2026-11-24-kw-skills-load-on-demand ("Kyber-Weave skills that agents load on demand").`.
  - `docs/todo/README.md`: the row links `../archive/todo/kw-skills-load-on-demand.md` without other row changes beyond status `archived` and a description saying it was promoted to an implementation plan on 2026-11-24.
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .`
- **Acceptance:**
  - Neither check reports a finding about the todo or the todo index.
  - The archived todo names the successor plan.

### T2: Author the shipped KW skill pool from the shipped `.skills.md` prose

- **Objective:** `products/kyber-squad/standards/skills/` holds the nine D19 skills, each `SKILL.md` with `name` (equal to its folder) and a `description` saying what it does and when to load it, moved verbatim from today's `.skills.md` sections and lightly edited only to stand alone, plus `skills.manifest.yml`.
- **Capabilities:** Documentation in Markdown and YAML; Agent Skills frontmatter.
- **Difficulty:** easy
- **Session:** `kw-skill-content`
- **Depends on:** none
- **Files:**
  - New `products/kyber-squad/standards/skills/<name>/SKILL.md` for `dotnet-build-diagnostics`, `aws-cli-queries`, `azure-resource-inspection`, `go-gates`, `maui-environment-doctor`, `pylance-tooling`, `rust-cross-build`, `sql-server-reference`, `tauri-v2-reference`; sources per D19 (for example `csharp/csharp.skills.md` Commands plus `github-actions/github-actions.skills.md` "Diagnosing a failed .NET build in CI" and its table go into `dotnet-build-diagnostics`).
  - New `products/kyber-squad/standards/skills/skills.manifest.yml`: `skills:` sequence of `{ name, files }`, `files` relative to the skill folder, `SKILL.md` first.
  - The `.skills.md` files are read, not edited or deleted (T8 deletes them).
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .`
- **Acceptance:**
  - Nine skill folders, each `name` = folder, lowercase-hyphen and at most 64 characters, `description` at most 1024 characters.
  - No new guidance beyond what D19 moves; the manifest lists every file.

### T3: KW skill catalog — lists, inheritance, roles, pools and load

- **Objective:** One `IKwSkillCatalog` answers which KW skills a technology and role agent get and loads one skill by name, repository before shipped; the docs corpus skips the pool.
- **Behaviour:**
  - Repository pool: `<docs-root>/standards/skills/<name>/SKILL.md` for each configured docs root, first root wins a name. Shipped pool: embedded resources `KwSkills/<name>/<relative path>` enumerated by the embedded `KwSkills/skills.manifest.yml` (`skills:` list of `{ name, files }`). A repository skill wins the whole name over the shipped one.
  - `<scope>.skills.yml` is a YAML sequence of name strings; `[]` and a missing file list nothing; any other shape lists nothing and is returned as a problem with the file path.
  - List order: the parent technology's (`builds-on`, one level) names, then the technology's, then the role's; duplicates dropped at first occurrence. Each entry carries name, description (from the `SKILL.md` frontmatter, read with `SkillFrontmatter`) and source (`repository` or `shipped`).
  - Role folder: `standards/<role>/` with `<role>` in `ontology.roles` or `KyberStandardsTemplates.Roles`. The agent name maps to the folder of the same name, else `code-reviewer`, `plan-reviewer`, `review-lens`, `review-triage` map to `reviewer`; anything else has no role skills.
  - `Load(name)`: found → the `SKILL.md` body below its frontmatter, the absolute folder, the source, and the absolute path of every other file in the folder (recursive, links not followed, ordinal order), never their contents. A shipped hit is written once to `<home>/.kyber-weave/kw-skills/<name>-<first 12 hex of SHA-256 over the manifest-ordered files>/` (an existing identical folder is reused) and that folder is reported; a failed write still returns the body with folder `(not materialized: <reason>)` and no references. Unknown → every existing name. Missing `name` or `description` → which one is missing.
- **Capabilities:** C# application service code over the file-system, environment and embedded-resource seams, with xUnit unit tests over the shared fakes; MSBuild embedded resources.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `kw-catalog`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Configuration/DocsLayout.cs`: add `SkillsPool(docsRoot)`, `SkillsListSuffix`, `SkillsListFile(docsRoot, name)`; document `SkillsSuffix`/`SkillsFile` as the legacy name.
  - New `src/KyberWeave.Contracts/Services/IKwSkillCatalog.cs`: `SkillsFor(string? technology, string? roleAgent)` → `KwSkillList` (entries `Name`, `Description`, `Source`; plus problems for D6), `Load(string name)` → `KwSkillLoadResult` (found: body, folder, source, reference paths; unknown: existing names; malformed: missing fields), `Names()`.
  - New `src/KyberWeave.Application/Docs/KwSkills/KwSkillCatalog.cs` (+ `KwSkillRoles.cs` holding the alias table, `ShippedKwSkills.cs` reading the manifest through `IEmbeddedResources` and materializing): YAML through the existing YamlDotNet use; frontmatter through `SkillFrontmatter`; files through `IFileSystem.EnumerateContainedFiles`; writes through `IWritableFileSystem`; home through `IProcessEnvironment`.
  - `src/KyberWeave.Application/Docs/Parsing/DocumentLoader.cs`: `IsExcluded` skips paths beneath `<docsRoot>/standards/skills/`.
  - `src/KyberWeave.Application/Areas/Docs.props`: embed `..\..\products\kyber-squad\standards\skills\**\*` with logical name `KwSkills/%(RecursiveDir)%(Filename)%(Extension)` (the reader normalizes `\` to `/`).
  - `src/KyberWeave.Application/ServiceCollectionExtensions.cs`: register the catalog factory per repository root (the catalog is built for a root and config, like `TaskPromptServiceFactory`).
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T3 --red`
  - `kyber-weave review gates . --agent <worker> --task T3`
- **Acceptance:**
  - T3 rows pass.
  - Failure mode: a caller-supplied name reaches the file system. Mitigation: (1) validate the name against `^[a-z0-9]+(-[a-z0-9]+)*$` after trimming; (2) require it to be in `Names()` before building any path; (3) enumerate with no links followed; (4) write only beneath `<home>/.kyber-weave/kw-skills/`, checked with `IPathResolver.IsWithin`.

### T4: Task prompt — one `<kw-skills>` part for workers and role agents

- **Objective:** `TaskPromptService` writes the D10 `<kw-skills>` part from the catalog (technology, parent and role per D7–D9), omits it when the list is empty, builds a role-only prompt for a dispatch that names only a role agent, and cuts the list first under the budget (D11); `<skills>` and the parent skills in `<parent-rules>` are gone.
- **Capabilities:** C# application service code with xUnit unit tests over the shared fakes.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `kw-catalog`
- **Depends on:** T3 (`IKwSkillCatalog`)
- **Files:**
  - `src/KyberWeave.Contracts/Services/ITaskPromptService.cs`: `TaskPromptRequest` gains `RoleAgent` (optional, last).
  - `src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs`: constructor takes `IKwSkillCatalog`; `Build` accepts no task and no technology when `RoleAgent ?? Agent` is set and returns the role-only prompt (empty and Built when no skills); `PromptContent` carries the skill list instead of `Skills`/`ParentSkills`; `Compose`, `Units`, `SkillsPart`, `ParentPart` and `UncarriedFileNote` per D10–D11.
  - `src/KyberWeave.Application/Docs/Validation/TaskPromptServiceFactory.cs`: pass the catalog for the root.
  - `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`: nothing new beyond compiling (its `--agent` reaches the role lookup through `Agent`).
  - `tests/KyberWeave.Testing/DocsTaskPromptFixture.cs`, `tests/KyberWeave.Content.Tests/ArbiterCommandAllowListPromptTests.cs` (constructor call only), and tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T4 --red`
  - `kyber-weave review gates . --agent <worker> --task T4`
- **Acceptance:**
  - T4 rows pass; the D10 instruction text is byte-identical to the Decisions row.
  - `docs packet` and the dispatch hook still build the same prompt for the same task.

### T5: Dispatch hook — pass the target and insert role skills

- **Objective:** Every adapter passes the dispatched agent to `DispatchPromptInsertion.Expand`, which builds a role-only prompt when a dispatch has no `TASK`/`TECHNOLOGY` and inserts it in front of the unchanged text, never stopping a role dispatch (D9); `<kw-skills>` keeps insertion idempotent.
- **Capabilities:** C# hook adapter code across the harness adapters, with xUnit unit tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `kw-hook`
- **Depends on:** T4 (`TaskPromptRequest.RoleAgent`, role-only build)
- **Files:**
  - `src/KyberWeave.Domain/Arbiter/Hooks/DispatchInsertionTypes.cs`: `DispatchPromptQuery` gains `Target` (optional, last).
  - `src/KyberWeave.Application/Arbiter/Hooks/DispatchPromptInsertion.cs`: `Expand(prompt, context, target = null)`; `ExpandedTags` gains `<kw-skills>`; the no-header branch per D9.
  - `src/KyberWeave.Application/Arbiter/Hooks/HookCommand.cs`: `PromptExpander` sets `RoleAgent: query.Target`.
  - Every caller of `Expand`: `Adapters/ClaudeHookAdapter.cs` (`RenderPromptInsertion`, `HandlePre`), `Adapters/PluginHookAdapter.cs` (`TargetOf(envelope)`), `Adapters/AntigravityHookAdapter.cs`, `Adapters/CodexHookAdapter.cs`, `Adapters/DevinHookAdapter.cs`, plus any other the build finds; a harness with no target passes null.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T5 --red`
  - `kyber-weave review gates . --agent <worker> --task T5`
- **Acceptance:**
  - T5 rows pass; every existing `DispatchPromptInsertionTests` case keeps its assertions.
  - Failure mode: a role dispatch is stopped or doubly wrapped. Mitigation: (1) a role-only query maps any not-built or empty result to `None`; (2) `IsExpanded` recognizes `<kw-skills>`; (3) no-target harnesses behave as today.

### T6: kw-docs `load_skill` tool and the coding-standard tool's KW skills list

- **Objective:** kw-docs exposes `load_skill(name, projectPath?)` answering per D13 through the catalog, and `docs_get_coding_standard_for_technology` lists the same KW skills list in place of skills prose (D25); the bundle's skill members are removed.
- **Capabilities:** C# MCP tool and query-service code with xUnit tests; the shipped toolchain roster YAML.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `kw-catalog`
- **Depends on:** T4 (the last reader of `StandardsBundle.Skills` moved off it)
- **Files:**
  - `src/KyberWeave.Mcp/DocsTools.cs`: `[McpServerTool(Name = "load_skill", ReadOnly = false, OpenWorld = false)]` (it may materialize a shipped skill), `Description` saying when to call it, `projectPath` routed through `Redirect` as the sibling tools do.
  - `src/KyberWeave.Contracts/Services/IDocsQueryService.cs` and `src/KyberWeave.Application/Docs/Mcp/DocsQueryService.cs`: `LoadSkill(name)` with the provenance line; `AgentCodingStandards` prints the KW skills list.
  - `src/KyberWeave.Mcp/DocsToolsComposition.cs`: wire the catalog for the root.
  - `src/KyberWeave.Mcp/DocsServerInstructions.cs`: one sentence naming `load_skill`.
  - `src/KyberWeave.Application/Docs/Search/DocumentIndex.cs`: `StandardsBundle` loses `ParentSkills` and `Skills`; `StandardsFor` stops reading `DocType.Skill`.
  - `products/kyber-squad/toolchain.yml`: add `load_skill` to `required-mcp-tools.kw-docs`.
  - `tests/KyberWeave.Application.Tests/Docs/Search/DocumentIndexTests.cs` (bundle assertions on removed members only) and tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T6 --red`
  - `kyber-weave review gates . --agent <worker> --task T6`
- **Acceptance:**
  - T6 rows pass; `SquadToolchainRosterGuardTests` passes.

### T7: `docs validate` — KW skill rules and the `.skills.yml` folder shape

- **Objective:** `docs validate` reports KW-DOC-SKILL-001 to -006 per D15, the folder shape expects `<scope>.skills.yml`, and the pool folder is not a standards folder.
- **Capabilities:** C# validator code with xUnit unit tests over the docs fixtures.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `kw-validate`
- **Depends on:** T3 (`IKwSkillCatalog` list problems and pool reads, `DocsLayout.SkillsListFile`)
- **Files:**
  - `src/KyberWeave.Application/Docs/Validation/DocSpecValidator.cs`: rule constants; the folder walk skips `skills`; the four-file check uses `SkillsListFile` and drops the skills frontmatter check; legacy warning.
  - New `src/KyberWeave.Application/Docs/Validation/KwSkillRulesValidator.cs` if the rules do not fit the existing class cleanly, called from `DocSpecValidator` as the other folder checks are.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T7 --red`
  - `kyber-weave review gates . --agent <worker> --task T7`
- **Acceptance:**
  - T7 rows pass; each case yields exactly one finding with a fix hint.

### T8: Templates, `docs init`, `docs standards migrate` and the shipped `.skills.yml` migration

- **Objective:** Every shipped standards folder carries `<scope>.skills.yml` (D19 lists) and no `.skills.md`; `docs init` writes `.skills.yml` and copies the assigned shipped skills (D24); `docs standards migrate` writes `[]` for a missing list; content guards pin the pool and that no KW skill reaches a harness (D16).
- **Capabilities:** C# scaffolding code, MSBuild embedded resources and shipped YAML content, with xUnit unit and content tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `kw-templates`
- **Depends on:** T2 (the shipped pool the lists name), T4 (the `ArbiterCommandAllowListPromptTests` constructor change)
- **Files:**
  - `products/kyber-squad/standards`: in each of the 23 scope folders delete `<scope>.skills.md` and add `<scope>.skills.yml`: `csharp` and `github-actions` `[dotnet-build-diagnostics]`, `aws` `[aws-cli-queries]`, `azure` `[azure-resource-inspection]`, `go` `[go-gates]`, `maui` `[maui-environment-doctor]`, `python` `[pylance-tooling]`, `rust` `[rust-cross-build]`, `sql-server` `[sql-server-reference]`, `tauri` `[tauri-v2-reference]`, and `[]` for architect, azure-devops, conductor, data-access-layer, devops, github, gitlab, jira, pulumi, react, reviewer, typescript.
  - `src/KyberWeave.Application/Areas/Docs.props`: each `Standards.<scope>.skills.md` resource becomes `Standards.<scope>.skills.yml`.
  - `src/KyberWeave.Application/Docs/Scaffolding/KyberStandardsTemplates.cs`: `FileName`/`ResourceName` for `Part.Skills` give `.skills.yml`.
  - `src/KyberWeave.Application/Docs/Scaffolding/KyberStandardsTemplateReader.cs`: the skills part is written raw, like `ArbiterRules`.
  - `src/KyberWeave.Application/Docs/Scaffolding/DocsScaffolder.cs`: after writing folders, copy the listed shipped skills (with the parent's) into `<docs-root>/standards/skills/` through the catalog's shipped reader, skipping existing folders.
  - `src/KyberWeave.Application/Docs/Scaffolding/StandardsFolderMigration.cs`: stub `[]` for a missing `.skills.yml`; the frontmatter repair no longer touches skills files.
  - `src/KyberWeave.Application/Docs/Scaffolding/StandardsTemplateDiff.cs`: compares `.skills.yml`.
  - Any other walk over `products/kyber-squad/standards/*` that the build or content tests show treating `skills` as a scope folder skips it.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T8 --red`
  - `kyber-weave review gates . --agent <worker> --task T8`
- **Acceptance:**
  - T8 gate rows pass; `GlobalInstallDeploysNoKwSkill` passes in CI.
  - Failure mode: a resource name and its file disagree after the rename and the build or `docs init` breaks. Mitigation: (1) resources, `FileName` and `ResourceName` change in this one task; (2) `EveryShippedFolderHasASkillsYmlListOfNames` reads through the same template reader `docs init` uses.

### T9: Migrate this repository's `docs/standards` to `.skills.yml` and repository KW skills

- **Objective:** `docs/standards/*` hold `.skills.yml` and no `.skills.md`, and `docs/standards/skills/` holds the D23 repository skills carrying the moved guidance.
- **Capabilities:** Documentation in Markdown and YAML; Agent Skills frontmatter.
- **Difficulty:** easy
- **Session:** `kw-skill-content`
- **Depends on:** T7 (the validator accepts `.skills.yml`), T8 (shipped names the lists refer to)
- **Files:**
  - `docs/standards/csharp/csharp.skills.md` → `docs/standards/csharp/csharp.skills.yml`: `[kw-dotnet-inner-loop, dotnet-build-diagnostics]`.
  - `docs/standards/github-actions/github-actions.skills.md` → `docs/standards/github-actions/github-actions.skills.yml`: `[kw-ci-gates, dotnet-build-diagnostics]`.
  - `docs/standards/rust/rust.skills.md` → `docs/standards/rust/rust.skills.yml`: `[kw-tray-rust-loop, rust-cross-build]`.
  - `docs/standards/tauri/tauri.skills.md` → `docs/standards/tauri/tauri.skills.yml`: `[tauri-v2-reference]` (the IPC note moves into `kw-tray-rust-loop`).
  - `docs/standards/sqlite/sqlite.skills.md` → `docs/standards/sqlite/sqlite.skills.yml`: `[kw-canon-schema-change]`.
  - `docs/standards/test/test.skills.md` → `docs/standards/test/test.skills.yml`: `[kw-test-commands]`.
  - `docs/standards/typescript/typescript.skills.md` → `docs/standards/typescript/typescript.skills.yml`: `[kw-dash-web-loop]`.
  - `docs/standards/react/react.skills.md` → `docs/standards/react/react.skills.yml`: `[]` (its text only lists what the standard covers).
  - New `docs/standards/skills/kw-dotnet-inner-loop/SKILL.md`, `docs/standards/skills/kw-ci-gates/SKILL.md`, `docs/standards/skills/kw-tray-rust-loop/SKILL.md`, `docs/standards/skills/kw-canon-schema-change/SKILL.md`, `docs/standards/skills/kw-test-commands/SKILL.md`, `docs/standards/skills/kw-dash-web-loop/SKILL.md`, each with `name` equal to its folder and a `description` of what it does and when to load it.
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .`
- **Acceptance:**
  - No KW-DOC-SKILL or KW-DOC-SPEC-012 finding; only the open-plan lifecycle finding remains.
  - Each moved section appears in exactly one repository skill.

### T10: Agent bodies describe the `<kw-skills>` part and `load_skill`

- **Objective:** `dev-easy`, `dev-medium`, `dev-hard` and `platform-researcher` describe the `<kw-skills>` list and loading through kw-docs `load_skill`, not a `<skills>` part or a skills file, and their content tests pin it.
- **Capabilities:** Documentation in Markdown (shipped agent bodies) with C# content tests.
- **Difficulty:** easy
- **Session:** `kw-skill-content`
- **Depends on:** T4 (the part's final name)
- **Files:**
  - `products/kyber-squad/agents/dev-easy.md`, `dev-medium.md`, `dev-hard.md`: the numbered list of parts and the precedence sentence ("rules beat skills…") become `<kw-skills>` plus "load a listed skill with kw-docs `load_skill` when its description fits".
  - `products/kyber-squad/agents/platform-researcher.md`: the same, and the line saying the skills file explains sign-in now points at the listed KW skills.
  - `products/kyber-squad/README.md` and `products/kyber-squad/standards/README.md`: the folder's four files are standard, rules, `.skills.yml`, Arbiter rules; the pool is described.
  - `tests/KyberWeave.Content.Tests/PlatformAgentContentTests.cs`, `DeveloperAgentContentTests.cs` per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T10 --red`
  - `kyber-weave review gates . --agent <worker> --task T10`
- **Acceptance:**
  - T10 rows pass; no shipped agent body names a `<skills>` part.

### T11: KyberDash engine — KW skill loads read and the `kw-skills` section route

- **Objective:** The engine derives KW skill loads from stored tool calls (D17) and answers `GET /api/kyber/session/{id}/kw-skills` in the D18 shape, apart from the harness skills section.
- **Capabilities:** C# contract, SQLite read-path and application read-path work in the KyberDash engine, with read-path and endpoint tests over the shared fake.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `dash-server`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: `SessionKwSkillLoads(string sessionKey)` → `StoredKwSkillLoad(SpanId, ToolName, McpServerName, McpToolName, InputText, StartedAtUtc)`.
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: one query scoped to the session over `tool_call`, `tool_call_mcp`, `tool_call_input_text`, `record`, `record_session_link`, filtering the D17 names in SQL.
  - New `dash/server/KyberDash.Application/Sessions/KwSkillLoads.cs` (recognizer and name parse, pure), used by `dash/server/KyberDash.Application/KyberBridgeEngine.cs` `GetSessionKwSkills` (copy `GetSessionSkills` paging and 404).
  - `dash/server/KyberDash.Contracts/Services/IDashServices.cs`, `dash/server/KyberDash.Application/Services/DashboardQueryService.cs`, `dash/server/KyberDash.Api/Endpoints/DashEndpointMappings.cs` (`/session/{id}/kw-skills`).
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs` and any `FaultingSessionReader`/`DelegatingReader` copies: implement the new member.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T11 --red`
  - `kyber-weave review gates . --agent <worker> --task T11`
- **Acceptance:**
  - T11 rows pass; no canon schema change and no `SchemaVersion` bump.

### T12: KyberDash web — the KW skills section

- **Objective:** The session view shows a `KW skills` section after Skills, fed by `fetchKyberSessionKwSkills`, in the D18 shape.
- **Capabilities:** React and TypeScript UI over TanStack Query and the shared API client (.tsx component and its client module), with Vitest and Testing Library tests.
- **Difficulty:** easy
- **Technology:** react
- **Session:** `dash-web`
- **Depends on:** none (the route shape is fixed by D18; tests mock it)
- **Files:**
  - `dash/web/src/lib/kyberApi.ts`: `KyberSessionKwSkillsPage` type, `parseSessionKwSkillsPage`, `fetchKyberSessionKwSkills(sessionId, page)` through `sectionPath(sessionId, 'kw-skills', page)`.
  - New `dash/web/src/components/session/SessionKwSkillsSection.tsx` (copy `SessionSkillsSection.tsx` and `useSectionPage`, with `SectionLoading`/`SectionError` and the shared pager).
  - `dash/web/src/components/AgentSessionDashboard.tsx`: mount it right after `SessionSkillsSection`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T12 --red`
  - `kyber-weave review gates . --agent <worker> --task T12`
- **Acceptance:**
  - T12 rows pass; `npm --prefix dash run typecheck` and `lint` are clean in the gate.

### T13: docs-dev closeout — harvest, ADR 0093, glossary

- **Objective:** Harvest every Design harvest row into canonical docs, write ADR 0093, define "KW skill" in the glossary, fill the Closeout mapping and archive the plan.
- **Capabilities:** Documentation in Markdown under the governed-docs rules.
- **Difficulty:** easy
- **Session:** `docs-closeout`
- **Depends on:** T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12
- **Files:**
  - `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`: KW skills and never-deployed sections.
  - `docs/kyber-arbiter/architecture.md`: dispatch prompt insertion.
  - `docs/docgraph/mcp-runbook.md`, `docs/docgraph/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/documentation-ontology.md`, `docs/standards/README.md`, `docs/cli-command-surface.md` (`docs init`, `docs standards migrate`).
  - `docs/context-hygiene/skills.md` and the managed glossary: "KW skill" against ADR 0049's term.
  - `.apm/skills/kyber-docs-authoring/references/rules.md`, `references/retrofit.md`: `.skills.yml` and the KW-DOC-SKILL rules.
  - `docs/dash/architecture.md`: read-model register and REST API table row.
  - New `docs/adr/0093-kw-skills-load-on-demand-through-kw-docs.md`, its row in `docs/adr/README.md`, and a cross-reference from ADR 0049.
  - This plan's Closeout mapping.
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate . --merge-ready`
  - `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .`
- **Acceptance:**
  - Both gates report zero findings after the archive; every Design harvest row and decision has a Closeout mapping row or a stated waiver.

## Dependency graph

```
T1 promotion ───────────────────────────────────────────────┐
T2 shipped pool ──────────────┐                             │
T3 catalog ──┬─> T4 prompt ─┬─> T5 hook                     │
             │              ├─> T6 load_skill + bundle      │
             │              ├─> T10 agent bodies            │
             │              └─> T8 templates/init (also T2)─┤
             └─> T7 validate ──────────────> T9 repo docs (T7, T8)
T11 dash server ───────────────────────────────────────────┤
T12 dash web ──────────────────────────────────────────────┴─> T13 closeout
```

First wave: T1, T2, T3, T11 (cap 4), then T12. T4, T6 run in the `kw-catalog` session after T3. T5, T7, T8 and T10 then run side by side: their files do not overlap (hooks, validator, scaffolding and content, agent bodies).

## Risks

| Risk | Mitigation |
|---|---|
| A pool `SKILL.md` or a reference `.md` leaks into the governed corpus and fails frontmatter checks. | D3 loader exclusion, `TheSkillsPoolIsNotPartOfTheCorpus` (T3), `ThePoolFolderIsNotAStandardsFolder` (T7). |
| Some other walk over `products/kyber-squad/standards/*` (template lists, content tests, skill lint) treats `skills` as a scope folder or lints KW skills as squad skills. | T8 scope note; `KwSkillPoolContentTests`; the build and content gates in T8. |
| A KW skill name collides with a harness skill name and an agent loads the wrong one. | `NoKwSkillIsASquadSkillAndThePackCarriesNone` (T8); D10 instruction forbids the harness tool. |
| A harness records `load_skill` under a spelling D17 does not list, so KyberDash misses the load. | One test per spelling; a missed spelling only under-counts (recording only); the closeout runbook names the recognized shapes. |
| The conductor's main session is never dispatched, so the `conductor` role list reaches no one through the hook. | Accepted: the todo selects by dispatch; noted in the KyberSquad architecture harvest. |
| Materializing a shipped skill under the home folder fails (read-only home). | `load_skill` then answers with the body and `folder: (not materialized: <reason>)`, no failure; T3 test `AMaterializationFailureStillReturnsTheBody`. |
| `kyber-weave` on PATH (0.1.8-rc.30) lacks the new rules during T9. | Every docs command runs the source CLI (`dotnet run --project src/KyberWeave.Cli`). |

## Out-of-scope boundaries

- Harness skills, how harnesses load them, and the harness skills Kyber deploys (todo Out of scope).
- Any Arbiter rule that requires, blocks or counts skill loads.
- A kw-docs tool for reading or searching reference files.
- Choosing skills by anything other than technology or role.
- New skill content beyond what D19 and D23 move.
- KyberDash MCP (`dash_*`) tools and cross-session KW skill analytics. PR note: a global KW skills view is a follow-up.
- Host repositories' own legacy `.skills.md` files are reported (KW-DOC-SKILL-005), not converted automatically. PR note: `docs standards migrate` could convert them in a follow-up.

## Verification gates

- Each developer task ends with its own `task-gate` run (the Commands above), test-first: the `--red` run before implementation, then the passing run. No dev agent runs the full suite.
- `SquadKyberWeaveSkillsTests.GlobalInstallDeploysNoKwSkill` is an integration test: it runs in CI, not in a task gate.
- Documentation tasks run `docs validate .` and `docs drift .` through the source CLI; closeout runs `--merge-ready`.
- The end-of-run council runs the full gates on the integrated head.

## Review

The end-of-run review council reviews the whole change under the `code-review` skill, measured against base `49cb1c66`. Scale: `extends-component` over KyberSquad, DocGraph, KyberArbiter and KyberDash; the Design harvest table names the destinations.

## docs-dev closeout

- Execute every Design harvest row (T13), including ADR 0093 (mandatory: it constrains every later skill, had rejected alternatives, and would be expensive to revisit) and the glossary entry (D22).
- Fill the Closeout mapping with one row per Design harvest row and per decision; D1, D20, D21 and AP1 carry documentation waivers (run provenance).
- Archive the plan and remove its task file in the same commit.
