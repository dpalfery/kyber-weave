---
id: plans/2026-11-16-standards-skill-rule-stitch
title: Stitched standards bundles from per-tech rules, skills, and standards
doc-type: plan
status: draft
component: DocGraph
owner: dpalfery
created: 2026-11-16
last-reviewed: 2026-11-16
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - DocGraph
---

# Stitched standards bundles from per-tech rules, skills, and standards

**Status: Draft.** Awaiting approval. On approval this plan executes on this branch and is archived in its own pull request.

## Problem and goal

Each `docs/standards/<technology>/` folder holds exactly one governed file today: the standard (`README.md`, `doc-type: coding-standard`). Tech-scoped rules live either inside the standard's prose, where the topic matcher may or may not surface them, or in `docs/rules/`, where they are loaded on every task for every technology. There is no home for per-tech procedures (skills), and `docs_get_coding_standard_for_technology` returns only the standard as text.

The goal: every technology folder holds three files — a skills file, a rules file, and the standards file — plus one `shared-rules.md` at the root of `docs/standards/`. The kw-docs MCP standards tool stitches shared rules, tech rules, skills, and the standard into a single XML bundle at call time, and that bundle is what a worker receives when it loads the standards tool at task start. Standards-family documents are discounted in retrieval ranking, because the worker already holds them in context.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What lives in each technology folder? | Three files: `rules.md`, `skills.md`, and `README.md` (the standard). | The three file types are stitched and inserted in context when the agent starts a task; one file per type keeps the stitch deterministic and each file reviewable on its own. | user: explicit decision 2026-11-16 |
| D2 | What does the standards tool return? | One XML bundle per technology: rule ids first, then shared rules, tech rules, skills, and the standard's sections, with omission and expected-cut notes. | The current tool returns text (`DocsTools.cs:220`, `DocsQueryService.cs:458`); agents parse either, and XML keeps the four parts addressable instead of smeared into prose. The provenance lead line stays the first line, unchanged, then one XML document. | user: explicit decision 2026-11-16 |
| D3 | How does the bundle reach the worker at task start? | The worker calls `docs_get_coding_standard_for_technology` in its first workflow step, as today, and receives the stitched bundle. Agent instructions name the tool, not files. | Slice 3 already routes all 13 agents and six dev skills through the tool and forbids file reads except when the tool is missing (`docs/standards/README.md`, "A standard is read through the tool"). No Squad render or dispatch change is needed: the stitch happens inside the tool the worker already calls. | user: explicit decision 2026-11-16; predecessor slice 3 carried the routing |
| D4 | Where do cross-tech rules live? | `docs/standards/shared-rules.md`: `doc-type: rule`, no `technology` key, stitched first in every bundle. | `ValidateTechnology` returns early when no `technology` key is declared (`DocSpecValidator.cs:392`), so a keyless rule at the standards root needs no validator change. One shared file, not one copy per tech. | user: explicit decision 2026-11-16 |
| D5 | Stitch at runtime or at index time? | At runtime, in `DocsQueryService`, from one new `DocumentIndex` bundle lookup. | Runtime stitch is always fresh and needs no index schema change or invalidation; it follows the existing `CodingStandards` path (`DocumentIndex.cs:238`). Index-time composition would persist a second copy of every part. | plan: recommendation, pending owner approval |
| D6 | How is a per-tech skills file expressed? | New `skill` doc-type, scoped by `technology`, required key `technology`, no `component` — mirroring `coding-standard`. | The closed set has no skill member (`DocType.cs:7`, `DocumentLoader.cs:441`, `OntologyConfig.cs:18`). Reusing `reference` mislabels procedure as description and corrupts the placement test; reusing `rule` erases the rule/skill distinction the stitch relies on. Adding a member is an ontology change, which this plan carries (T2). It is not widening to hide an error: it names a real new shape. | plan: recommendation, pending owner approval |
| D7 | How do per-tech rules coexist with `docs/rules/`? | `docs/rules/` keeps every-task global rules at full authority with no `technology` key. `docs/standards/<tech>/rules.md` is `doc-type: rule` with `technology: <tech>`, allowed only under `standards/<tech>/` with folder match. | `KW-DOC-SPEC-007` currently rejects `technology` on any non-`coding-standard` (`DocSpecValidator.cs:400`). The placement test gains a third row: global rule, tech-scoped rule, standard. One home per statement is preserved. | plan: recommendation, pending owner approval |
| D8 | What is the retrieval discount? | `Authority` demotes `coding-standard` to 0.55, `skill` to 0.55, and a `rule` carrying `technology` to 0.55. `shared-rules.md` (keyless rule) keeps 1.0. | 0.55 matches the existing work-artifact discount (`plan`, `spec`); exact-id matches still win outright, so nothing becomes unreachable. `shared-rules.md` keeps full authority deliberately: it is the one small file that always fits, so it is the fallback when the bundle's budget cut drops a section. All three signals derive from the document itself, which the static `Authority(DocumentIndex.cs:579)` signature can already see. | plan: recommendation, pending owner approval |
| D9 | What does the `<technology-coding-standard>` registry property point at? | Still the standard file (`standards/<tech>/README.md`). Unchanged. | The property resolves one readable file for humans and fallback readers; the bundle is reached through the tool, not the registry. No `ConfigRegConfig` change. | plan: recommendation, pending owner approval |
| D10 | Does this need an ADR? | No ADR — ADR waiver. | The change refines existing contracts (one tool's output shape, one validator rule, one scoring table, one folder's contents) and follows established patterns (technology scoping mirrors `coding-standard`). The durable record is the ontology, the runbook contract, and the retrieval reference (H1–H5). | plan: recommendation, pending owner approval |

## Investigation findings

Verified against this tree (branch `feat/standards-rule-skill-stitch`, off `b26ca736`), not assumed from the summary. Discovery: codegraph 6, direct reads 5, shell enumerations 3.

**The tool and its path.** `DocumentIndex.CodingStandards(technology, topic, charBudget)` sits at `src/KyberWeave.Application/Docs/Search/DocumentIndex.cs:238`: it matches `DocType.CodingStandard` plus the `technology` key, scores every hit 1.0, and returns whole-or-topic excerpts. `DocsQueryService.AgentCodingStandards` at `src/KyberWeave.Application/Docs/Mcp/DocsQueryService.cs:458` renders the text reply: provenance lead, `AppendStandardCount`, identity block, rule-id index capped at 50 (`RuleIdCap`), sections, omitted headings, the expected-cut note, `StandardMiss` for unknown technologies, `StandardUsage` for an empty one. The tool surface is `DocsTools.AgentCodingStandards` at `src/KyberWeave.Mcp/DocsTools.cs:220`; it accepts a bare technology or the `<name-coding-standard>` property form.

**The weight to move.** `Authority` at `DocumentIndex.cs:579` is `plan|spec → 0.55`, `adr → 0.9`, everything else 1.0, times the status multiplier. `DocumentAuthorityTests.ACodingStandardCarriesUndemotedAuthority` (`tests/KyberWeave.Tests/DocumentIndexTests.cs:201`) pins today's 1.0 for standards — T1's RED flips it. `CodingStandardsLookupTests` (`DocumentIndexTests.cs:440`) pins the lookup T4 extends.

**The validator to relax.** `ValidateTechnology` (`src/KyberWeave.Domain/Docs/Validation/DocSpecValidator.cs:392`): empty key returns early; non-`coding-standard` with a key is `KW-DOC-SPEC-007`; declared-technology check reads `ontology.technologies` (csharp, test, typescript, rust, tauri, go, github-actions); folder must equal the technology. `DocGovernanceTests` covers the misplaced-technology cases; `KyberWeaveDocsSkillVocabularyTests` (policy project) pins the authoring skill's vocabulary.

**The ontology to extend.** `DocType` (`src/KyberWeave.Domain/Docs/Model/DocType.cs:7`) has no `Skill`; `ParseDocType` (`src/KyberWeave.Data/Docs/Parsing/DocumentLoader.cs:441`) maps thirteen spellings, no skill; `DefaultDocTypes` (`src/KyberWeave.Domain/Configuration/OntologyConfig.cs:18`) matches; `CreateDefaultRequiredKeysByType` (`OntologyConfig.cs:188`) requires `technology` only of `coding-standard`. The ontology prose (`docs/documentation-ontology.md:55-151`) states the `technology` key is "rejected on any other doc-type", lists the doc types, and carries the Rules-and-standards table this plan revises. The authoring skill points at the ontology rather than copying the list (`SKILL.md:289`), but its `references/rules.md:69-72` and `references/retrofit.md:48-84` spell the technology rules out and join T2's file list.

**The scaffolder to grow.** `DocsLayout` (`src/KyberWeave.Domain/Configuration/DocsLayout.cs:14`): `Standards = "standards"`, seven folders, `TechnologyStandard` → `standards/<tech>/README.md`. `DocsScaffolder.Scaffold` (`src/KyberWeave.Application/Docs/Scaffolding/DocsScaffolder.cs:139`) writes one standard per declared technology; `DocsScaffoldTemplates.TechnologyStandard` (`:183`) is its stub. No `skill` string exists anywhere under `src/` as a doc concept (only harness permission names in Squad renderers).

**The corpus to reshape.** `docs/standards/` holds `README.md` plus one folder per tech, each with only `README.md`. `docs/rules/README.md:33-37` states a rule document "says what to do" and "carries full retrieval authority" — the sentence D7 scopes to global rules. The server instructions name the tool as the route to "the rule ids that standard states" (`src/KyberWeave.Mcp/DocsServerInstructions.cs:24-25`). The retrieval reference's Authority table (`docs/docgraph/retrieval.md:99-110`) and excerpt-budgeting section (`:237-274`) are T4/T5's harvest targets. `McpAgentCodingStandardsTests.cs` carries 24 test attributes pinning today's text shape — T1's RED rewrites their expectations to XML.

**The injection seam that needs no change.** Workers already call the tool in their first workflow step (slice 3, D4). `SquadWorkerBaselineInjector` (`src/KyberWeave.Domain/Squad/Rendering/SquadWorkerBaselineInjector.cs:33`) injects only static baseline text and cannot call tools, so it is correctly out of scope: D3 keeps injection at the existing tool call. No agent body describes the tool's text shape (verified by search: bodies name the tool and the registry property only), so T5 expects zero agent-body edits; `SquadCanonicalContentTests` guards that expectation.

**The runner commands.** Restated from the root `AGENTS.md` working rules (direct `dotnet`, no process wrapper; `fast-loop.sh --touched` while iterating) and the two CLI doc gates:

- **PRODUCT-RUN** — build once, then run the named classes:
  `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`
- **POLICY-RUN** — build once, then run the named classes:
  `dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`
- **DOCS-GATES** — `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`

## Design

**Folder layout.** Every declared technology gains two files beside its standard; the standards root gains one:

```
docs/standards/shared-rules.md            doc-type: rule, no technology, status: draft→current per T6
docs/standards/<tech>/README.md           doc-type: coding-standard, technology: <tech> (unchanged)
docs/standards/<tech>/rules.md             doc-type: rule, technology: <tech>
docs/standards/<tech>/skills.md            doc-type: skill, technology: <tech>
```

Frontmatter ids: `standards/shared-rules`, `standards/<tech>-rules`, `standards/<tech>-skills`. New files ship `status: draft`; promotion to `current` is follow-up, not this slice. Per-tech READMEs are untouched: the trio is documented once in `docs/standards/README.md`.

**Stitch order and budget (T5).** One `DocumentIndex` bundle lookup returns, for the requested technology: the standard hit(s) as today, the tech `rules.md` / `skills.md` hits, and `shared-rules.md`. `DocsQueryService` renders:

```xml
<standards-bundle technology="csharp" status="current" char-budget="12000">
  <rule-ids>
    <id>KW-CSHARP-001</id>
  </rule-ids>
  <shared-rules path="docs/standards/shared-rules.md">…prose…</shared-rules>
  <technology-rules path="docs/standards/csharp/rules.md">…prose…</technology-rules>
  <skills path="docs/standards/csharp/skills.md">…prose…</skills>
  <standard path="docs/standards/csharp/README.md" omitted="Commands, …">…topic sections…</standard>
  <notes>
    <note>…expected-cut statement…</note>
  </notes>
</standards-bundle>
```

Ordering general → specific → reference. The rule-id index stays first and keeps the 50-id cap with the existing withheld elision. The `topic` selects standard sections exactly as today (words, 0.02 floor, miss fallback); rules/skills/shared render whole — they are short by design — and if they ever exceed the budget they are cut from the end with an `omitted` attribute and an expected-cut note. The 12000 default and 1000–120000 clamp are unchanged, and the clamp now governs the whole bundle. Unknown technology keeps today's `StandardMiss` text; empty technology keeps `StandardUsage`. The tool description, server instructions line, and runbook contract are rewritten to the XML shape.

**Precedence.** On conflict, technology rules beat shared rules, and rules beat skills. `shared-rules.md` states this once; per-tech files do not restate it.

**Authority (T4).** `coding-standard → 0.55`, `skill → 0.55`, `rule` with a non-empty `technology → 0.55`; keyless rules (including `shared-rules.md`) stay 1.0 as the budget-cut fallback. The retrieval reference's Authority table records the new rows and the reason.

**Validation (T2).** `technology` is legal on `coding-standard` (unchanged), on `skill` (required: declared technology, folder match), and on `rule` only when the document sits under `<docs-root>/standards/<tech>/` with folder match; anywhere else it stays `KW-DOC-SPEC-007`. Required-key matrix gains `skill → [technology]`; `rule` gains no required key. Ontology prose, the placement test, the authoring-skill references, and the retrofit heuristics move with it.

**Scaffolding (T3).** `DocsLayout` gains `TechnologyRules`, `TechnologySkills`, and `SharedRules` paths; the scaffolder writes `rules.md`/`skills.md` stubs per technology and `shared-rules.md` once; the registry property still points at the standard README (D9).

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `skill` doc-type, the `technology` key's new scope, the three-row placement test, the matrix row | DocGraph | reference | `docs/documentation-ontology.md` |
| H2 | The trio layout, the tool-only route to the bundle, shared-rules precedence, no separate `## Commands` rule already there | DocGraph | index | `docs/standards/README.md` |
| H3 | Global rules stay in `docs/rules/` at full authority; tech-scoped rules live in the tech folder | DocGraph | index | `docs/rules/README.md` |
| H4 | XML bundle contract: schema, order, budget, topic and miss behaviour | DocGraph | runbook | `docs/docgraph/mcp-runbook.md`, the `docs_get_coding_standard_for_technology` section |
| H5 | Authority rows for `coding-standard` / `skill` / tech-scoped `rule`, and the shared-rules exception with its reason | DocGraph | reference | `docs/docgraph/retrieval.md`, Authority and excerpt-budgeting sections |

## ADR disposition

No ADR — ADR waiver: D10. The change refines existing contracts and follows the established technology-scoping pattern; the durable record is H1–H5 in canonical documentation.

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. No row runs the full suite — every command below is narrowed by class, and an unfiltered run from a dev agent is refused. A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces. `SquadCanonicalContentTests` in T5 is the guard that proves zero agent-body edits were needed: it runs and passes unchanged.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/` — `McpAgentCodingStandardsTests`, `CodingStandardsLookupTests`, `DocumentAuthorityTests`, `DocGovernanceTests`, `OntologyConfigTests`, `DocsScaffolderTests`, `RetrievalRegressionTests`; `tests/KyberWeave.RepoPolicy.Tests/` — `KyberWeaveDocsSkillVocabularyTests` | PRODUCT-RUN over the product classes; POLICY-RUN over the policy class | The XML expectations, the flipped authority assertion, the tech-scoped-rule/skill validation cases, the new-file scaffolder cases, and the updated vocabulary case are written and fail for the reason each names | The XML assertions fail against today's text output; `ACodingStandardCarriesUndemotedAuthority` fails once the test expects 0.55; new `technology`-on-rule/skill cases fail with `KW-DOC-SPEC-007`; scaffolder cases fail with missing files; the vocabulary case fails on the unknown `skill` type | T1's acceptance: rows T2–T5 are written and fail for the reason each names |
| T2 | `tests/KyberWeave.Tests/` — `OntologyConfigTests`, `DocGovernanceTests` (`DocLinkFragmentValidationTests` if the ontology edits move anchors); `tests/KyberWeave.RepoPolicy.Tests/` — `KyberWeaveDocsSkillVocabularyTests` | PRODUCT-RUN + POLICY-RUN | `skill` parses, requires `technology`, and validates folder match; `technology` on a rule passes only under `standards/<tech>/`; authoring-skill references describe the new scope | Covered by T1's RED | T2's acceptance: D6 and D7 hold in code and in the authoring skill |
| T3 | `tests/KyberWeave.Tests/` — `DocsScaffolderTests`, `DocsInitCommandTests` | PRODUCT-RUN | `docs init` writes `rules.md`/`skills.md` per tech and `shared-rules.md` once; registry property still targets the standard | Covered by T1's RED | T3's acceptance: layout and stubs land, registry unchanged |
| T4 | `tests/KyberWeave.Tests/` — `CodingStandardsLookupTests`, `DocumentAuthorityTests`, `RetrievalRegressionTests` | PRODUCT-RUN | The bundle lookup returns all four parts; authority demotes the three tech-scoped shapes and spares keyless rules; retrieval expectations match the new table | Covered by T1's RED | T4's acceptance: D8 holds in the index and in the reference |
| T5 | `tests/KyberWeave.Tests/` — `McpAgentCodingStandardsTests`, `McpDocsProvenanceTests`, `SquadCanonicalContentTests` | PRODUCT-RUN | The tool returns provenance lead plus one XML bundle in the specified order and budget; unknown/empty technology replies are unchanged; no agent body changed | Covered by T1's RED | T5's acceptance: D2, D3, D5 hold; zero agent-body edits |
| T6 | No test class — corpus seeding | DOCS-GATES | `shared-rules.md` plus per-tech `rules.md`/`skills.md` exist with valid frontmatter; the live tool returns them in the bundle | None — documentation task | T6's acceptance: fifteen new documents, validate and drift at zero |
| T7 | No test class — documentation and closeout | DOCS-GATES | Harvest H1–H5, plan row removed on archive, zero findings | None — documentation task | T7's acceptance: harvest, plan archived, zero findings |

## Tasks

- **T1: RED for the bundle**
  - **Files:** `tests/KyberWeave.Tests/McpAgentCodingStandardsTests.cs`, `tests/KyberWeave.Tests/DocumentIndexTests.cs`, `tests/KyberWeave.Tests/DocGovernanceTests.cs`, `tests/KyberWeave.Tests/OntologyConfigTests.cs`, `tests/KyberWeave.Tests/DocsScaffolderTests.cs`, `tests/KyberWeave.Tests/RetrievalRegressionTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/KyberWeaveDocsSkillVocabularyTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T2–T5 are written and fail for the reason each names.
- **T2: Ontology and validation**
  - **Files:** `src/KyberWeave.Domain/Docs/Model/DocType.cs`, `src/KyberWeave.Data/Docs/Parsing/DocumentLoader.cs`, `src/KyberWeave.Domain/Configuration/OntologyConfig.cs`, `src/KyberWeave.Domain/Docs/Validation/DocSpecValidator.cs`, `docs/documentation-ontology.md`, `.apm/skills/kyber-docs-authoring/references/rules.md`, `.apm/skills/kyber-docs-authoring/references/retrofit.md`, `.apm/skills/kyber-docs-authoring/SKILL.md` (only if it spells the old scope out)
  - **Required skills:** `csharp-dev`
  - **Depends on:** T1
  - **Acceptance:** D6 and D7. `skill` is a closed-vocabulary member scoped by `technology`; `technology` on a rule passes only under `standards/<tech>/`. T2's row holds.
- **T3: Layout and scaffolding**
  - **Files:** `src/KyberWeave.Domain/Configuration/DocsLayout.cs`, `src/KyberWeave.Domain/Docs/Scaffolding/DocsScaffoldTemplates.cs`, `src/KyberWeave.Application/Docs/Scaffolding/DocsScaffolder.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T2
  - **Acceptance:** `docs init` writes the trio per tech plus `shared-rules.md`; the registry property still targets the standard. T3's row holds.
- **T4: Bundle lookup and authority**
  - **Files:** `src/KyberWeave.Application/Docs/Search/DocumentIndex.cs`, `docs/docgraph/retrieval.md`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T2
  - **Acceptance:** D8. One bundle lookup serves the tool; the three tech-scoped shapes score 0.55 and keyless rules keep 1.0. T4's row holds.
- **T5: XML tool output and instructions**
  - **Files:** `src/KyberWeave.Application/Docs/Mcp/DocsQueryService.cs`, `src/KyberWeave.Mcp/DocsTools.cs`, `src/KyberWeave.Mcp/DocsServerInstructions.cs`, `docs/docgraph/mcp-runbook.md`, `docs/standards/README.md`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T3, T4
  - **Acceptance:** D2, D3, D5. Provenance lead plus one XML bundle; miss replies unchanged; zero agent-body edits with `SquadCanonicalContentTests` green to prove it. T5's row holds.
- **T6: Seed the corpus**
  - **Files:** `docs/standards/shared-rules.md`, `docs/standards/csharp/rules.md`, `docs/standards/csharp/skills.md`, `docs/standards/test/rules.md`, `docs/standards/test/skills.md`, `docs/standards/typescript/rules.md`, `docs/standards/typescript/skills.md`, `docs/standards/rust/rules.md`, `docs/standards/rust/skills.md`, `docs/standards/tauri/rules.md`, `docs/standards/tauri/skills.md`, `docs/standards/go/rules.md`, `docs/standards/go/skills.md`, `docs/standards/github-actions/rules.md`, `docs/standards/github-actions/skills.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T5
  - **Acceptance:** fifteen seeded documents, statements moved not copied (one home per statement), validate and drift at zero. T6's row holds.
- **T7: Docs and closeout**
  - **Files:** `docs/documentation-ontology.md` (if any prose remains from T2), `docs/rules/README.md`, `docs/docgraph/retrieval.md` (if any prose remains from T4), `docs/docgraph/mcp-runbook.md` (if any prose remains from T5), `docs/plans/README.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T6
  - **Acceptance:** harvest H1–H5, plan archived, zero findings.

## Dependency graph

- T1 stands alone.
- T2 depends on T1.
- T3 depends on T2. T4 depends on T2.
- T5 depends on T3 and T4.
- T6 depends on T5.
- T7 depends on T6.

## Risks

- **XML breaks a consumer that scrapes the text shape.** Only agents read this tool, and no agent body describes the text shape (verified by search; `SquadCanonicalContentTests` in T5 proves it). The runbook contract is rewritten in the same change.
- **Four documents under one budget.** Rules, skills, and shared rules are short by design; the budget cut lands on standard sections first, and the `omitted` attribute plus the expected-cut note say so. A bundle that silently dropped the rules would be worse than today's text.
- **Tech-scoped rules duplicate global rules or standard prose.** T6 moves statements, never copies them; the one-home-per-statement rule is enforced by review, and the duplication analysis names any twin that slips through.
- **New files ship as drafts.** Honest: seed content is a proposal until tech owners review it. Drafts stay retrievable and bundle-served, so nothing is silently missing.
- **Shared rules at full authority look inconsistent next to discounted tech rules.** Deliberate (D8): the small always-fits file is the fallback when the bundle is cut. The retrieval reference states the reason so the next reader does not "fix" it.

## Out of scope

- Agent-body, skill-body, and migration-report edits — none needed; the routing already exists.
- The review side: `code-reviewer.md` and the `code-review` skill keep pointing at the declared standard as review input.
- Registry property changes — the property still targets the standard file (D9).
- Promoting the fifteen seed files to `current` — follow-up per tech owner.
- `--kyber-standards` template content for rules/skills — stubs only; the fourteen canonical templates are untouched.
- Index-time stitching (D5 decided runtime).
- KyberDash display — no doc-type mapping exists there (verified by search).

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — the full suite belongs to the review council's `test` gate and CI.
- While iterating, `scripts/fast-loop.sh --touched` routes to the affected checks; the Test contract's named-class runs are the evidence.
- `docs validate .` and `docs drift .` run after every plan save and after each docs task (T6, T7), each at zero findings. `docs validate . --merge-ready` is **not** the check while this plan is open: `KW-DOC-LIFECYCLE-003` fails an open plan by design, and the pull request CI runs it, which is what archives this plan at closeout.

## Review

This plan's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review, its command derived from the `code-review` skill with `--base` — never copied into this plan.

## Closeout

Closeout has not run. `docs-dev` fills this section at closeout, verifying the delivered tree rather than the plan's own account of it, and records the gates (`docs validate . --merge-ready`, `docs drift .`) after the archive move.

## Closeout mapping

To be completed at closeout: every decision D1–D10 and every Design harvest item H1–H5 maps to its destination below with documentation-impact, impact components, last-reviewed, and status. `kyber-weave docs closeout <plan>` names what is still missing.
