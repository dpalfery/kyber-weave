---
id: plans/2026-10-30-docs-explore-answer
title: docs_explore returns one answer
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-31
development-mode: test-first
documentation-impact: extends-component
impact-components: [DocGraph]
---

# docs_explore returns one answer

**Status: Archived.** Closeout 2026-10-31. AP4 approved execution; `RetrievalRegressionTests` 19/19 at closeout (12 answerable, 7 unanswerable). Frontmatter `status` is `archived`. Harvest in canonical docs and the Closeout mapping below.

**Development mode:** `test-first`.  
**Autonomy:** `autonomous` (packet; matches Config Reg `<autonomy>`).  
**Scale:** `extends-component`, component `DocGraph`.

> Discovery: codegraph 5, docs 8, direct reads 14.
>
> - CodeGraph (`codegraph explore`, index present): `DocsTools.Explore`, `DocumentIndex` weights and `DefaultCharBudget`, `WholeExcerpt` / `Excerpt` (the method body of `Explore` and `Excerpt` was gapped), `DocumentExcerpt`, blast radius of `Explore` to `DocsTools`, `DocumentIndexTests`, and `RetrievalRegressionTests`. Two explores named `RetrievalRegressionTests` and returned `DocumentModel` instead of the test file.
> - Docs: `docs_status` root `/Users/dave/git/personal/cursor/kyber-weave` rev `fad3247` matches this checkout. Explored plan inventory and ontology, retrieval budgeting, and the `docs_explore` runbook section. `docs_for_symbol DocsTools` names `docs/docgraph/mcp-runbook.md`. Coding standards for `csharp` and `test`. Glossary has no sense for "docs explore".
> - Direct reads, and why an index was not enough:
>   1. `docs/plans/README.md` — plan-authoring requires the index; Active Plans was the empty sentence.
>   2. `docs/archive/plans/2026-10-29-local-ci-checks-optimization.md` — frontmatter and Decisions table shape only; not implementation authority.
>   3. `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` `Explore` (187–239) — CodeGraph gapped the method.
>   4. `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs` `Excerpt` (706–785) — CodeGraph gapped the method. Force-includes the top section and emits omitted headings.
>   5. `src/KyberWeave.Core/Docs/Search/DocumentExcerpt.cs` and `DocumentHit.cs` — excerpt flags and the raw `Score`.
>   6. `tests/KyberWeave.Tests/RetrievalRegressionTests.cs` — CodeGraph did not return this file. `TopN = 3`, document id in the hit list, separate unanswerable theory.
>   7. `src/KyberWeave.Mcp/DocsTools.cs` `AppendExcerpt` / `Truncate` — `SectionCharCap = 6000` and the "ask again with a larger charBudget" line. Shared with `agent_coding_standards`.
>   8. `src/KyberWeave.Mcp/DocsServerInstructions.cs` — session text still says ranked sections and code locations.
>   9. `tests/KyberWeave.Tests/DocumentIndexTests.cs` `ExcerptBudgetTests` — pins the old per-document budget.
>   10. `docs/docgraph/retrieval.md` "Budget, not truncation" — the documented 12000 / `maxDocs` contract.
>   11. `PlanInventoryValidator` — a plan counts as listed when the index links it.
>   12. `McpPackagingTests` — `docs_explore` description must keep "how to", "runbook", "before grepping", "docs/" and must not say "governed", "corpus", "frontmatter", or "code joins".
>   13. `docs/code-review/architecture.md` headings — `## The whole system` is the section a live call returned; `## Configuration` contains `### Council gates`.
>   14. `docs/catalog.md` DocGraph row and `docs/adr/README.md` — next ADR number after 0045; `review.max-concurrent-workers` is 4.
>
> Circuit-breaker revision, 2026-10-30. Discovery this pass: codegraph 0, docs 0, direct reads 4.
> 1. This plan — task text that froze the withdrawn formula.
> 2. `tests/KyberWeave.Tests/RetrievalRegressionTests.cs` — the eval asserts the phrase, no incomplete marker, confidence `>= 0.60`, and one source line containing the expected document id. Those assertions stay.
> 3. `docs/kyber-squad/architecture.md` frontmatter — id is `squad/architecture`.
> 4. `src/KyberWeave.Mcp/DocsTools.cs` `FormatExploreAnswer` — the source line prints `RelativePath`, so `docs/kyber-squad/architecture.md` already contains `squad/architecture`.
>
> T5 revision, 2026-10-30. Discovery this pass: codegraph 0, docs 0, direct reads 2 (this plan's D9, AP2, and T5 text, plus the index row). No new code was read. The score uses the `Explore` and `Excerpt` constants already recorded above.
>
> T6 revision, 2026-10-30. Discovery this pass: codegraph 0, docs 0, direct reads 2 (this plan's D9, AP3, and T6 text, plus the index row). No new code was read. Highest `paragraphPeak` and the cross-document runner-up are measured failures.
>
> User design, 2026-10-30. Discovery this pass: codegraph 0, docs 0, direct reads 3. The read-only section design (leaf ids, weights, seven documents, six anchors, build order) was taken from that assessment. `docs/reference/README.md` Inventory was read to classify it with the other index tables. `docs/dash/architecture.md` headings were read to confirm the tray leaf sits inside Surface Layer, so the returned unit is the winning leaf and not the whole 55,000-character topic.

## 1. Problem and goal

`docs_explore` (`DocsTools.Explore`) tells the caller the returned text is already read. What it returns is a ranked pile: up to five documents, a forced chunk of each winner, every skipped heading, and up to twenty code-join lines. A default call for how the review council chooses gates returned the mermaid overview "The whole system" from `docs/code-review/architecture.md`, named the sections that actually say how gates are chosen, and then added four other documents. The class comment on `DocsTools` says a retrieval tool that returns a whole corpus is a slower grep. This shape is that failure.

The goal is one blob of text that answers the question. Score `###` leaves, return the winning leaf in full under its `##` topic, and set confidence from the difference between topics. The eval file is not edited. A miss stays a miss.

Cursor replaces a tool result with a file path when the concatenated UTF-8 text exceeds 40,000 bytes. The published `charBudget` ceiling of 120,000 and the line "ask again with a larger charBudget" are what produce that spill. The answer cap stays inside what returns as text.

## 2. Scope and boundary

### 2.1 In scope

- `DocumentIndex.Explore` becomes one answer: leaf scoring, the whole winning leaf, difference confidence, and a cursor only past the ceiling.
- `DocsTools.Explore` formats that answer as one blob. Tool name stays `docs_explore`.
- The tool schema for this tool only: `query` and optional `cursor`. `maxDocs` and `charBudget` leave this tool.
- Real-corpus eval and fixture tests listed in the Test contract.
- The DocGraph pages, ADR, server instructions, MCP project notes, root README tool row, and the one skill sentence that still tells an agent to pull a whole file through `maxDocs` and `charBudget`.

### 2.2 Out of scope

- `agent_coding_standards`, `docs_for_symbol`, `docs_glossary`, `docs_analysis_candidates`, `docs_validate`, and `docs_drift` keep their parameters, budgets, omitted-heading markers, and code joins.
- `Excerpt`, `WholeExcerpt`, `SectionCharCap`, and `DefaultCharBudget` stay in force for the coding-standard path.
- The 0.25 document relevance floor stays. Unanswerable questions still return nothing.
- No second document, no runner-up section, and no code-join list in a `docs_explore` hit.
- No server-side session. The cursor is stateless.
- The integrity-review skill is not redesigned. Only the sentence that uses `docs_explore` as a whole-file fetch changes.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What does `docs_explore` return? | One blob of text that answers the question. The caller treats that blob as already read and does not open the files. A ranked pile of documents, skipped headings, and code locations is the wrong shape. | User decision. Host tool name stays `docs_explore`. `DocsTools` class comment: a retrieval tool that returns a whole corpus is a slower grep. | User decision 2026-10-30 |
| D2 | What is the happy path? | Score `###` leaves. The answer is the whole winning leaf, labeled by its `##` topic. One source line: `source: {path} — {## heading} › {### heading}`. | The earlier "few thousand characters of matching passages, then the next `##`" shape is withdrawn by D3 and D7. A ranked pile of documents remains wrong. | User decision 2026-10-30 |
| D3 | What text is returned once a section wins? | The whole winning section, not a paragraph window. The paragraph-window decision is withdrawn. A shorter section comes back short. Do not pad. Past the 24,000-character ceiling, cut, mark the reply incomplete, and continue with the stateless cursor. A silent prefix cut of a section that fits is still forbidden. | The 2026-10-30 paragraph window (keep the match, drop the intro) is withdrawn by the user. The ceiling is D7. The cursor is D6, and it resumes the same section rather than a neighbour. | User decision 2026-10-30 |
| D4 | What confidence is returned? | A score from 0 to 1. High means stop. Low means another topic was close or the reply was cut. The ratio formula is withdrawn. Confidence is `tanh((winner topic − best other topic) / 2)`, halved when the reply is incomplete. Subsections of the same `##` are not rivals. Do not return the raw relevance figure. Stop at 0.60 or higher when the reply is not incomplete. | The user replaced the ratio after measured second-document scores sat at 0.48–0.71 of the winner, which cannot clear 0.60. A difference cancels evidence both topics share. `tanh(1.39 / 2)` is about 0.60, so a confident answer needs a claim lead of more than prose alone. | User decision 2026-10-30 |
| D5 | How is the change judged? | `tests/KyberWeave.Tests/RetrievalRegressionTests.cs` stays unchanged: every question, expected document id, phrase, 0.60 floor, source line, and every unanswerable miss. Do not retune phrases, floors, expected ids, or source lines to fit the ranker. The eval is the requirement. | The file is not edited. Phrase text stays in the test, not in this plan. A hit on this plan fails the source-id assertion. `ExploreAnswerTests` is a separate approved change, D19. | User decision 2026-10-30 |
| D6 | What does a follow-up call return when the reply was cut? | The next span of that same section, still one blob, via a stateless `cursor`. A section that fits has no cursor. The same question without a cursor returns the first span again. A complete low-confidence reply does not switch topics; the reading rule tells the caller to ask a narrower question. | The cursor no longer walks matching paragraphs or a following `##`. It resumes the cut at the 24,000-character ceiling. No session state. An invalid cursor is a one-line error for this question. | User decision 2026-10-30 |
| D7 | How large is the cap, and what happens to `charBudget`? | 24,000 characters is a ceiling, not a target. A shorter section comes back short. Do not pad. Past the ceiling, cut and mark incomplete. `charBudget` stays off `docs_explore` and cannot raise the ceiling. The 3,000-character cap and the 8,000-character whole-reply cap are withdrawn. | The user set 24,000. Typical prose at that length stays under the 40,000-byte spill. Do not tell the caller to raise a budget. `KW-DOC-SPEC-009` uses this same ceiling for a declared lead. | User decision 2026-10-30 |
| D8 | Which parameters does `docs_explore` keep? | `query` and optional `cursor`. Remove `maxDocs` and `charBudget` from this tool's method and schema. A stale caller that still sends them must not be able to raise the cap: delete the parameters rather than clamp a 120,000 request into a larger answer. Update every in-repo call to pass `query` only, plus `cursor` where the follow-up test needs it. | `agent_coding_standards` and the gate tools keep `charBudget`. The runbook line that says `maxDocs` and `charBudget` interact applies to the old explore contract and is rewritten for this tool only. `McpPackagingTests` jargon bans still apply to the description. | autonomous: recommendation accepted |
| D9 | How does section selection and the 0–1 score work so the existing eval can pass? | Not the implementation contract after T6. Highest `paragraphPeak` inside the winning document failed all 12 answerable cases (right document, wrong `##` section) and confidence stayed under 0.60 when another document cleared 0.25. That winner rule is a measured failure. D14, now a user decision, replaces it. The rest of this cell is the record of what was tried. The vague rule is withdrawn. It named no score and no runner-up. What follows in this cell was tried and is not the contract. The contract is D14. It uses only constants already in `DocumentIndex`. No new coefficients. `documentScore` is the pre-change `Explore` score: `Authority(doc) * (ScoreExact + ScorePartialIdentity + TitleWeight * ScoreTitle + BodyWeight * ScoreBody)`, with `IdWeight` 6, `CodeRefWeight` 5, `EndpointWeight` 5, `KeywordWeight` 4, `ComponentWeight` 3, `TitleWeight` 2, `BodyWeight` 1, partials 4.5 / 3.0 / 2.5, and the existing authority multipliers. If the best `documentScore` is below `MinRelevanceScore` (0.25), the call is a miss. `paragraphPeak` is not added before that check. The winning document is the highest `documentScore` at or above 0.25. Only its `##` sections compete. `paragraphPeak` is the max, over blank-line paragraphs with a fenced block kept whole, of `Cosine(rarityWeightedQuery, VectorizeFused(paragraph)) * (mass / (mass + SectionSaturation))`. `SectionSaturation` stays 40. `mass` is the sum of that paragraph vector. The query vector is the rarity-weighted fused query `Excerpt` already uses. An empty vector scores 0. The heading is not copied onto every paragraph. `sectionScore = documentScore + paragraphPeak`, coefficient 1 on the peak. The winning section is the highest `paragraphPeak` in the winning document. Tie-break: more query terms present in that peak paragraph, then earlier heading line, then path. The runner-up is the highest `sectionScore` in any other document. A section in the winning document is not the runner-up. A document below 0.25 does not compete. If every other document is below 0.25, the runner-up score is 0. `confidence = clamp((winner - runner) / winner, 0, 1)` when `winner > 0`, else 0. Incomplete multiplies it by 1/2. Format `0.00`–`1.00`. Do not print `documentScore` or `sectionScore`. A same-document gap of 0.2–2% does not set confidence. A complete reply clears 0.60 when the runner-up `sectionScore` is at most 40% of the winner, including when the runner-up score is 0. Do not add a fudge, a new term, or a lower floor when a second document is too close. `ParagraphInclusionRatio` is 0.5. The adaptive 0.45–0.20 path is withdrawn. The ratio may only increase, by 0.05, up to 0.80, and only after the winning section contains the phrase and the 0.5 window drops that phrase or exceeds 3000 characters. Never decrease it. `AnswerCharCap` stays 3000. Do not add BM25, heading coverage, or body affinity. Phrases, the 0.60 floor, the source line, and the unanswerable miss stay. | T5, first attempt, is withdrawn with the vague rule. It used an unapproved blend of BM25, peak paragraph cosine, heading coverage, and body affinity, and an adaptive ratio from 0.45 down to 0.20. `ExploreAnswerTests` stayed 11/11. `RetrievalRegressionTests` fell from 7 passed / 12 failed to 4 passed / 15 failed. Three previously green unanswerable cases regressed. Same-document near-ties still scored about 0.15–0.40, and a global runner-up needs the winner about 2.5 times the runner to clear 0.60. `TASK_GATE` PASS, agent `csharp-dev`, gates 3, failed none, report `.agent_scratchpad/t5/task-gate.json`, runs 1. Do not ship that blend. Withdrawn earlier formula, kept here so it is not reused: `Authority(doc) * (ScoreExact + ScorePartialIdentity + TitleWeight * ScoreTitle + length-damped section cosine)`, confidence on the cosine gap, exact-id confidence 1 when complete and 0.5 when incomplete. The shared identity term flattened every section of a document. Measured gaps were about 0.2–2%, so confidence stayed under 0.60, and the tiny remainder often picked the wrong heading. `ExploreAnswerTests` 11/11. `RetrievalRegressionTests` 7 passed / 12 failed. Unanswerable cases stayed green. `ParagraphInclusionRatio` stayed 0.5 because the winner was the wrong section. McpPackaging for `docs_explore` is green. Last `TASK_GATE` PASS, agent `csharp-dev`, gates 3, failed none, report `.agent_scratchpad/t2/task-gate.json`, runs 4. Right section, still failing the floor or the incomplete marker: retrieval scoring (`The relevance floor` 3.157 vs preamble 3.150); frontmatter keys (`Frontmatter` 3.621 vs `The required-key matrix` 3.554). Right section, phrase not in the window: parallel council lenses (preamble). Wrong section: council gates (`Modes` 5.253, phrase section `Configuration`); `code-review/architecture` (`The parts, and where they live`); `squad/architecture` (`4. Deployment Scopes…`); `dash/architecture` (preamble tied with `High-level architecture` at 15.750, phrase section `Surface Layer`); diagnostic engine (preamble, phrase section `Stable rule ids`); dashboard dev environment (`Repository layout`, phrase section `Surface Layer`); `documentation-ontology` (preamble, phrase section `Frontmatter`); `docgraph/architecture` (not the preamble that holds the phrase). `multi-harness agent and skill deployment` wins the preamble, which holds the phrase. That document's id is `squad/architecture`. The source line prints `docs/kyber-squad/architecture.md`, which already contains `squad/architecture`. Do not edit the expected id. Treat that case as a floor or incomplete failure. `AnswerCharCap` 3000 and the incomplete-reply rule stay. D1–D5 are not reopened. `MinRelevanceScore` 0.25 stays the miss floor. Record the shipped section score and ratio in `docs/docgraph/retrieval.md` in the same change as the code. If the score cannot both select the phrase section and open a gap that clears 0.60 without breaking the unanswerable miss, stop and return this plan to Draft again. T6 then implemented the paragraphPeak winner in this cell: `TASK_GATE` PASS, agent `csharp-dev`, gates 3, failed none, report `.agent_scratchpad/t6/task-gate.json`, runs 1. `ExploreAnswerTests` 11/11. MCP suites green. `RetrievalRegressionTests` 0/12 answerable cases passed. Example: `documentation-ontology` landed on Frontmatter rather than the section that contains the required phrase. Council-gates still missed Configuration. Confidence stayed under 0.60 whenever another document cleared 0.25. | WITHDRAWN after T6. Earlier provenance autonomous: recommendation accepted. The user replaced this cell with D14 on 2026-10-30. |
| D10 | What stays on the coding-standard path? | `CodingStandards`, `Excerpt`, `WholeExcerpt`, `SectionCharCap`, omitted-heading markers, and `charBudget` on `agent_coding_standards` are unchanged. `docs_explore` stops calling `AppendExcerpt` and `AppendJoins`. | Those helpers are shared. Changing them to kill the explore pile would break the coding-standard contract and `McpAgentCodingStandardsTests`. | autonomous: recommendation accepted |
| D11 | Does this need an ADR? | Yes. Next free number after 0045 (0046 if 0045 is still last when the ADR is written). Subject: `docs_explore` returns one answer, not a document list. | The public tool contract changes, the multi-document budget was rejected, and agents are told to stop on the blob. An ADR waiver would hide a contract later work must not revert. ADR 0010 stays as-is; it covers keywords, prefix coverage, and OOV IDF, not this response shape. | autonomous: recommendation accepted |
| D12 | Autonomy | Packet autonomy is `autonomous`. D6–D11 are decided under that mode. D1–D5 are user decisions and were not reopened. | Config Reg `<autonomy>` is `autonomous`. | autonomous: recommendation accepted |
| AP1 | Approve and execute the Ready plan? | Withdrawn. The 2026-10-30 `approve-and-execute` approval does not cover the revised D9. Execution is not approved. Scale and harvest targets are unchanged: `documentation-impact: extends-component`, `impact-components: [DocGraph]`, H1 `docs/docgraph/retrieval.md`, H2 `docs/docgraph/mcp-runbook.md`, H3 the next ADR after 0045. | Gate `approve-and-execute` was recorded under autonomy autonomous, then withdrawn the same day after cluster `retrieval-answer-12` showed the D9 formula cannot pass the eval. D1–D8 and D10–D12 are unchanged. Renewal waits for a later conductor `AUTONOMOUS_APPROVAL`. | autonomous: recommendation accepted |
| AP2 | Approve and execute the revised Draft? | Withdrawn. The approval of the vague D9 does not cover a score that fails the eval and regresses the miss. Execution is not approved. Scale and harvest targets are unchanged: `documentation-impact: extends-component`, `impact-components: [DocGraph]`, H1 `docs/docgraph/retrieval.md`, H2 `docs/docgraph/mcp-runbook.md`, H3 the next ADR after 0045. | Gate `approve-and-execute` was recorded under autonomy autonomous for the revised D9, then withdrawn the same day after T5. `RetrievalRegressionTests` went from 7/12 to 4 passed / 15 failed, and three unanswerable cases that had been green regressed. This row does not erase AP1 or the fact that AP2 was once an approval. Renewal waits for a later conductor `AUTONOMOUS_APPROVAL` of the concrete score and runner-up now in D9. | autonomous: recommendation accepted |
| AP3 | Approve and execute the concrete D9 score and runner-up? | Withdrawn. The approval of highest `paragraphPeak` inside the winning document does not cover a rule that fails every answerable case. Execution is not approved. Scale and harvest targets are unchanged: `documentation-impact: extends-component`, `impact-components: [DocGraph]`, H1 `docs/docgraph/retrieval.md`, H2 `docs/docgraph/mcp-runbook.md`, H3 the next ADR after 0045. | Gate `approve-and-execute` was recorded under autonomy autonomous, then withdrawn the same day after T6. All 12 answerable cases failed on phrase and/or confidence. This row does not erase the AP1 or AP2 withdrawals, or the fact that AP3 was once an approval. | autonomous: recommendation accepted |
| D14 | What within-document rule replaces highest `paragraphPeak`? | Score `###` leaves. A leaf is the preamble, the lead-in before a `##` section's first `###`, or a `###`. `####` stays inside its `###`, and those slugs are aliases. Identity is `{doc-id}#{github-slug}` from Markdig `UseAutoIdentifiers` GitHub mode, including the `-1` suffix. The preamble is `{doc-id}#`. A topic's score is its best leaf. Confidence compares topics, not sibling leaves. Leaf score is authority times (document score + claim + body + lead). Claim uses the existing coverage function: 3.0 per fully named label, cap 4.5, exact label 4.0, exact section code-ref 5.0. Labels are the heading path, declared section keywords, declared code-ref names, and filtered inbound link text. Body is leaf BM25 squashed to 0–1 with `SectionSaturation` 40. An exact document id or path gives the lead leaf `IdWeight` 6.0. The document stage and the 0.25 floor stay, so an unanswerable question still misses. Preamble labels are only its `#` declarations, not the document title. No production code names an eval query, phrase, or heading. Delete the uncommitted heading skip list. Do not ship it. | Highest `paragraphPeak` and the ratio confidence are measured failures, recorded in D9 and AP3. The weights 3.0, 4.5, 4.0, 5.0, and 6.0 already exist. What is new is the leaf, the label claim, and `tanh` of the topic difference. | User decision 2026-10-30 |
| D15 | How are sections declared? | Two optional frontmatter keys. `lead` is the section an exact document-id question returns; default is the preamble. `sections` attaches keywords and code-refs to a slug. Those keywords and code-refs roll up to the document, so the document score and `docs_for_symbol` stay stable. New ids only: `KW-DOC-SPEC-008` error when `lead` or a `sections` key names no heading, with the nearest slug as the hint; `KW-DOC-SPEC-009` error when a declared lead is longer than the 24,000-character ceiling; `KW-DOC-SPEC-010` warning when none of a section keyword's words appear in that section; `KW-DOC-LINK-001` error when a link `#fragment` matches no heading, with the nearest slug as the hint. Extend `KW-DOC-SPEC-002` and `KW-DOC-DRIFT-001` to section lists and section code-refs. Do not renumber. | Markers under headings and `{#id}` attributes were rejected. The key is the slug links already use. A renamed heading fails loudly. | User decision 2026-10-30 |
| D16 | What does the parser and the graph keep? | The parser keeps each link's path, fragment, link text, and source line, including same-document `#` links. Leaves come from Markdig's syntax tree, which also treats `~~~` as a fence. Existing `Sections` and `BodyLinks` stay, because excerpts, inventory reachability, and archive closeout use them. One section-graph builder serves retrieval and the graph projection: section nodes, parent `##` edges, section-to-section edges carrying link text and line, and code-refs on the declaring section. Inbound link text is a leaf label except file names, paths, the target's own title or id, and generic text. Do not weight by inbound link count. | Four links reading "Review council architecture" would otherwise give the shipped-gate-catalogue subsection the same claim as Council gates. Canonical model has more inbound anchors than Surface Layer, so a count would point the wrong way. | User decision 2026-10-30 |
| D17 | Which corpus edits are required? | Seven documents get declarations. Three of those get small splits: the tray subsection in `docs/dash/architecture.md`, council gates in `docs/code-review/architecture.md`, and scoring in `docs/docgraph/retrieval.md`. The user signed two of them: the dash lead is the tray subsection, not the overview; the retrieval-scoring keyword sits on the relevance-floor section because the Scoring intro never says scoring. Six broken anchors are fixed: `adr/0037`, three `cli-command-surface` links to `#three-commands`, `dash/telemetry-inventory`, and `plans/README`. Markdig's slug in the parser task is authoritative if the approximate list disagrees. Build in the order below, and rerun the eval after each corpus document. | Declarations, not code, name the sections. `docs/dash/architecture.md` lead slug `the-kyberdash-tray-dashtray`, and `tauri` moves from the document keywords to that section. `docs/code-review/architecture.md` promotes the repository gate-scoping `####` to `###`, adds preamble keywords `specialist lenses` and `parallel lenses`, and moves `CouncilGateSelection` to `sections.council-gates.code-refs`. `docs/docgraph/retrieval.md` nests Authority, compound names, and the relevance floor as `###` under Scoring, with keyword `retrieval scoring` on `the-relevance-floor`. `docs/documentation-ontology.md` lead `frontmatter` and keyword `frontmatter keys`. `docs/docgraph/architecture.md` lead `retrieval-has-no-database`. `docs/kyber-squad/architecture.md` moves `multi-harness`, `deployment`, `agent`, and `skill` onto the preamble. `docs/ci-pipelines/architecture.md` adds keyword `rule ids` on `stable-rule-ids`. Order: ontology, `docgraph/architecture`, squad, ci-pipelines, code-review, retrieval, dash. | User decision 2026-10-30 |
| D18 | Which sections are indexes rather than answer text? | An index section is a different node. Each row is an edge to the document it names, carrying title and status. A query that matches an index follows those edges and answers from the target document's section. The index page stays the human-readable list. Index sections: `docs/plans/README.md` Active Plans and Archived Plans; `docs/adr/README.md` Inventory; `docs/todo/README.md` Todo inventory; `docs/kyber-squad/skill-resource-dispositions.md` Policy-line ledger; `docs/reference/README.md` Inventory. Not indexes, even when longer than 24,000 characters: `docs/dash/architecture.md` Surface Layer, Canonical model, and Ingest layer; `docs/dash/runbook.md` Telemetry Ingest, Canonical Store, and CLI Operations, and Web Dashboard; `docs/kyber-squad/architecture.md` 8. Rendering. | Archived plan decision ledgers are outside the corpus because `docs/archive/` is excluded. Active Plans is the same row shape as Archived Plans, so it is the same node kind. The reference inventory is a short table of the same shape. Long architecture and runbook prose stays a section, and the ceiling cuts it. | User decision 2026-10-30 |
| D19 | May `ExploreAnswerTests` change? | Yes. Re-derive its expected numbers for the difference formula and the 24,000-character ceiling. That is an approved test-contract change. `RetrievalRegressionTests` is not. | The 11 tests were written for the ratio formula and the old caps. The assertions that a close pair of documents stays under 0.60 and a unique section clears 0.60 stay, with numbers recomputed from `tanh` of the topic difference. | User decision 2026-10-30 |
| AP4 | Approve and execute the revised Draft? | Yes. Execution is approved (`development-mode: test-first`). The contract is D1–D8 and D10–D19. D9 stays withdrawn. D14 is the section score. The answer is the whole winning leaf under a 24,000-character ceiling. Index sections are edges, per D18. `RetrievalRegressionTests` is not edited. Scale confirmed: `documentation-impact: extends-component`, `impact-components: [DocGraph]`. Harvest H1 `docs/docgraph/retrieval.md`, H2 `docs/docgraph/mcp-runbook.md`, H3 the next ADR after 0045, H4 `docs/documentation-ontology.md`. | Gate `approve-and-execute`. This row does not reuse or reopen AP1, AP2, or AP3. Those stay withdrawn. | autonomous: recommendation accepted |

## 3. Behaviour

### 3.1 Leaves and topics

A leaf is the preamble, the prose before a `##` section's first `###`, or a `###`. `####` and deeper headings stay inside their `###`. Their slugs are aliases so existing links still resolve. A topic is a `##` section, or the preamble. A topic's score is its best leaf. Confidence compares topics. Sibling leaves under one `##` are not rivals.

Identity is `{doc-id}#{github-slug}` from Markdig `UseAutoIdentifiers(AutoIdentifierOptions.GitHub)`. The preamble id is `{doc-id}#`.

### 3.2 Score

The document stage is unchanged, including the 0.25 floor, plus the keyword and code-ref roll-up. Leaves are scored only inside documents that clear the floor. An unanswerable question still returns the miss, with no confidence line.

Leaf score is authority times (document score + claim + body + lead):

- Claim: existing coverage. 3.0 for a fully named label, capped at 4.5. A query equal to a label adds 4.0. A query equal to a section code-ref adds 5.0. Labels are the heading path, declared section keywords, declared code-ref names, and filtered inbound link text.
- Body: BM25 over leaves, squashed to 0–1 with `SectionSaturation` 40.
- Lead: a query equal to the document id or path adds `IdWeight` 6.0 on the lead leaf only. The default lead is the preamble. A document that wants another leaf declares `lead`.

Preamble labels are only its `#` declarations. The document title is not a preamble label.

### 3.3 Answer

Return the winning leaf in full. Do not select paragraphs inside it. A leaf shorter than 24,000 characters comes back short. Do not pad. Past 24,000 characters, cut, mark the reply incomplete, and put the remainder behind the stateless cursor. The source line is `source: {path} — {## heading} › {### heading}`, or the path alone for the preamble. An exact-id question returns the lead leaf in full, under the same ceiling.

An index section is not answer text. Follow its row edges and answer from the target document's leaf. The index page stays the human-readable list. The index sections are D18. Surface Layer, Canonical model, Ingest layer, the two long runbook sections, and squad Rendering stay leaves under the ceiling. They are not indexes.

### 3.4 Confidence

`tanh((winning topic − best other topic) / 2)`, halved when the reply is incomplete. Format `0.00`–`1.00`. Do not print the raw score. Stop at 0.60 or higher when the reply is not incomplete.

### 3.5 What production code must not contain

No eval query, phrase, or heading name. Delete the uncommitted heading skip list. Do not ship it.

## 4. Test contract

`development-mode: test-first`. `RetrievalRegressionTests.cs` is not edited. `ExploreAnswerTests.cs` is re-derived for D4 and D7. That re-derivation is written before the retrieval change is claimed done.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T7 | new leaf and link tests under `tests/KyberWeave.Tests/` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~LeafSplitTests|FullyQualifiedName~LinkFragmentTests"` | A `###` is its own leaf, a `####` stays inside it, a fence does not split a heading, and a link keeps its fragment and text. | Tests compile and fail on today's `##`-only split. | Same tests pass after T8. |
| T11 | `tests/KyberWeave.Tests/ExploreAnswerTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ExploreAnswerTests"` | Expected numbers match `tanh` of the topic difference and a 24,000-character ceiling. A short section is not padded. A section past the ceiling is incomplete and the cursor returns the rest. A close pair of topics stays under 0.60. A unique topic clears 0.60. | The re-derived assertions fail on the ratio formula and the old cap. | Same file passes after T12. The case count is not reduced. |
| T12 | `tests/KyberWeave.Tests/RetrievalRegressionTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~RetrievalRegressionTests"` | Unchanged file. Twelve answerable cases keep phrase, id, source line, and 0.60. Seven unanswerable cases stay misses. | The file is not rewritten. It stays red until the corpus steps finish. | 12 answerable cases pass and 7 unanswerable cases pass after T21, and still after T22. |

## 5. Implementation tasks

```
[T7 test-dev: leaf and link tests]     [T11 test-dev: re-derive ExploreAnswerTests]
            |                                          |
            v                                          |
[T8 csharp-dev: parser]                                |
            |                                          |
            v                                          |
[T9 csharp-dev: KW-DOC-LINK-001]                       |
            |                                          |
            v                                          |
[T10 docs-dev: six anchors]                            |
            |                                          |
            +--------------------+---------------------+
                                 v
                    [T12 csharp-dev: retrieval]
                                 |
                                 v
                    [T13 csharp-dev: lead, sections, rules]
                                 |
                                 v
                    [T14 docs-dev: ontology page and ADR]
                                 |
                                 v
              [T15..T21 docs-dev: one corpus document each]
                                 |
                                 v
              [T22 csharp-dev: graph, index edges, section code-refs]
                                 |
                                 v
                    [T23 docs-dev: retrieval and runbook]
                                 |
                                 v
                         [T4 docs-dev: closeout]
```

T7 and T11 may run together. Their files do not overlap. T12 waits for both T8 and T11. Each corpus task waits for the one before it and reruns the eval. `review.max-concurrent-workers` is 4. Do not resume cluster `retrieval-answer-12`. T1 through T6 are not re-dispatched.

### Task T7: Leaf and link tests

- **Skill:** `test-dev`
- **Files:** new test files under `tests/KyberWeave.Tests/` for leaf splits and link fragments. Do not edit `RetrievalRegressionTests.cs`.
- **Objective:** Pin D14 and D16 before the parser changes: `###` leaves, `####` kept inside, GitHub slug shape, fragment and link text retained.
- **Acceptance:** The new tests compile and fail on the current `##`-only splitter. Task-gate for `test-dev` passes on build and format.
- **Dependencies:** none

### Task T8: Parser leaves

- **Skill:** `csharp-dev`
- **Files:** `src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs`, `src/KyberWeave.Core/Docs/Model/DocumentModel.cs`.
- **Objective:** Build leaves from Markdig's syntax tree with GitHub ids. Keep existing `Sections` and `BodyLinks`. Record link path, fragment, text, and line, including same-document fragments. Delete any uncommitted heading skip list. Do not change ranking yet.
- **Acceptance:** T7 passes. The seven unanswerable eval cases still pass. Task-gate for `csharp-dev` passes.
- **Dependencies:** T7

### Task T9: Broken-anchor rule

- **Skill:** `csharp-dev`
- **Files:** the doc-spec validator that emits `KW-DOC-*` ids, and a test that the six known fragments fail `KW-DOC-LINK-001` before they are fixed.
- **Objective:** Add `KW-DOC-LINK-001`. Hint with the nearest slug. Do not renumber any existing id.
- **Acceptance:** The new test names the six fragments in D17 and fails until T10. Task-gate for `csharp-dev` passes.
- **Dependencies:** T8

### Task T10: Fix the six anchors

- **Skill:** `docs-dev`
- **Files:** the documents that contain the six fragments in D17. No other prose.
- **Objective:** Point each fragment at the heading Markdig actually emits.
- **Acceptance:** `docs_validate` with `mergeReady` false reports no `KW-DOC-LINK-001`. Task-gate for `docs-dev` passes.
- **Dependencies:** T9

### Task T11: Re-derive fixture expectations

- **Skill:** `test-dev`
- **Files:** `tests/KyberWeave.Tests/ExploreAnswerTests.cs` only.
- **Objective:** Replace ratio and old-cap expectations with D4 and the 24,000-character ceiling. Do not delete the close-topic or unique-topic cases. Do not edit `RetrievalRegressionTests.cs`.
- **Acceptance:** The file compiles. The re-derived assertions fail on the current ranker. Task-gate for `test-dev` passes on build and format.
- **Dependencies:** none

### Task T12: Leaf ranking and the whole-section answer

- **Skill:** `csharp-dev`
- **Files:** `src/KyberWeave.Core/Docs/Search/DocumentIndex.cs`, `src/KyberWeave.Core/Docs/Search/ExploreAnswer.cs`, `src/KyberWeave.Mcp/DocsTools.cs`.
- **Objective:** Implement D14, D3, D4, D6, and D7. Remove paragraph selection and `ParagraphInclusionRatio`. Return the winning leaf in full. Confidence is the topic difference. Exact id selects the lead, which is the preamble until T13. No heading skip list.
- **Acceptance:** T11 passes. The seven unanswerable eval cases still pass. Answerable eval cases are not weakened to go green. Task-gate for `csharp-dev` passes.
- **Dependencies:** T8, T11

### Task T13: Lead, section claims, and the new rule ids

- **Skill:** `csharp-dev`
- **Files:** frontmatter parsing, `KW-DOC-SPEC-002`, `KW-DOC-SPEC-008`, `KW-DOC-SPEC-009`, `KW-DOC-SPEC-010`, and `KW-DOC-DRIFT-001`. Tests for those ids.
- **Objective:** Parse `lead` and `sections`. Roll keywords and code-refs up to the document. `KW-DOC-SPEC-009` uses 24,000 characters. Do not renumber.
- **Acceptance:** A missing slug, an over-long lead, an ungrounded keyword, and a section code-ref that does not resolve each fail with the id D15 names. Task-gate for `csharp-dev` passes.
- **Dependencies:** T12

### Task T14: Ontology page and ADR

- **Skill:** `docs-dev`
- **Files:** `docs/documentation-ontology.md`, the next ADR after 0045, `docs/adr/README.md`.
- **Objective:** Document `lead` and `sections`, the leaf, and the difference confidence. Do not quote eval phrases.
- **Acceptance:** `docs_validate` and `docs_drift` pass with `mergeReady` false. Task-gate for `docs-dev` passes.
- **Dependencies:** T13

### Task T15: Ontology declarations

- **Skill:** `docs-dev`
- **Files:** `docs/documentation-ontology.md` frontmatter and the new key rows only, as D17.
- **Objective:** Declare `lead: frontmatter` and the section keyword. Keep the leaf under the ceiling.
- **Acceptance:** Rerun `RetrievalRegressionTests`. Record the pass count. Do not edit the test. Task-gate for `docs-dev` passes.
- **Dependencies:** T14

### Task T16: DocGraph architecture lead

- **Skill:** `docs-dev`
- **Files:** `docs/docgraph/architecture.md` frontmatter only.
- **Objective:** Declare `lead: retrieval-has-no-database`.
- **Acceptance:** Rerun the eval and record the count. Task-gate for `docs-dev` passes.
- **Dependencies:** T15

### Task T17: Squad preamble keywords

- **Skill:** `docs-dev`
- **Files:** `docs/kyber-squad/architecture.md` frontmatter only.
- **Objective:** Move `multi-harness`, `deployment`, `agent`, and `skill` onto the preamble entry.
- **Acceptance:** Rerun the eval and record the count. Task-gate for `docs-dev` passes.
- **Dependencies:** T16

### Task T18: CI pipelines keyword

- **Skill:** `docs-dev`
- **Files:** `docs/ci-pipelines/architecture.md` frontmatter only.
- **Objective:** Add the `rule ids` keyword on `stable-rule-ids`.
- **Acceptance:** Rerun the eval and record the count. Task-gate for `docs-dev` passes.
- **Dependencies:** T17

### Task T19: Council gates split

- **Skill:** `docs-dev`
- **Files:** `docs/code-review/architecture.md`.
- **Objective:** Promote the repository gate-scoping `####` to `###`. Add the preamble keywords and move `CouncilGateSelection` as D17 states.
- **Acceptance:** Rerun the eval and record the count. The council-gates source line contains `Configuration`. Task-gate for `docs-dev` passes.
- **Dependencies:** T18

### Task T20: Scoring split

- **Skill:** `docs-dev`
- **Files:** `docs/docgraph/retrieval.md`.
- **Objective:** Nest Authority, compound names, and the relevance floor under Scoring. Put `retrieval scoring` on `the-relevance-floor`.
- **Acceptance:** Rerun the eval and record the count. Task-gate for `docs-dev` passes.
- **Dependencies:** T19

### Task T21: Tray split and dash lead

- **Skill:** `docs-dev`
- **Files:** `docs/dash/architecture.md`.
- **Objective:** Split the tray subsection after its third paragraph. Declare `lead: the-kyberdash-tray-dashtray`. Move `tauri` onto that section.
- **Acceptance:** The eval is 12 answerable cases and 7 unanswerable misses. Task-gate for `docs-dev` passes.
- **Dependencies:** T20

### Task T22: Graph, index edges, and section code-refs

- **Skill:** `csharp-dev`
- **Files:** the section-graph builder, graph export, and `docs_for_symbol` lookup. Index classification is the D18 list, not a scan of long sections.
- **Objective:** Section nodes and edges, filtered inbound link text, index-row edges, and section code-refs visible to `docs_for_symbol`. Document-level code-refs stay.
- **Acceptance:** The eval is still 12 and 7. An index query answers from the target document, not from the inventory table. Task-gate for `csharp-dev` passes.
- **Dependencies:** T21

### Task T23: Retrieval and MCP contract pages

- **Skill:** `docs-dev`
- **Files:** `docs/docgraph/retrieval.md`, `docs/docgraph/mcp-runbook.md`, `README.md` tool row, `.apm/skills/kyber-docs-integrity-review/SKILL.md` sentence that still asks for a whole file through `maxDocs`.
- **Objective:** Describe leaves, the whole-leaf answer, the 24,000-character ceiling, difference confidence, and `docs_explore(query, cursor)`. Leave the coding-standard budget alone. Do not quote eval phrases.
- **Acceptance:** `docs_validate` and `docs_drift` pass with `mergeReady` false. Task-gate for `docs-dev` passes.
- **Dependencies:** T22

### Task T4: Closeout

- **Skill:** `docs-dev`
- **Files:** this plan and `docs/plans/README.md`.
- **Objective:** After T23, map D1–D19, the withdrawn AP1–AP3, AP4, and H1–H4, then archive the plan. Do not do this before execution is approved and the eval is green.
- **Acceptance:** `docs validate . --merge-ready` and `docs drift .` report 0 findings. The Decisions table is still present.
- **Dependencies:** T23

## Design harvest

```yaml
documentation-impact: extends-component
impact-components: [DocGraph]
```

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| H1 | DocGraph | reference | `docs/docgraph/retrieval.md` | Leaves, topic confidence, whole-leaf answer, 24,000-character ceiling |
| H2 | DocGraph | runbook | `docs/docgraph/mcp-runbook.md` | `docs_explore(query, cursor)` and the source line |
| H3 | DocGraph | adr | next ADR after 0045 | Section identity, `lead` and `sections`, difference confidence |
| H4 | DocGraph | reference | `docs/documentation-ontology.md` | `lead`, `sections`, and the new rule ids |

No documentation waiver. Execution is approved by AP4. AP1, AP2, and AP3 stay withdrawn.

## 7. Risks

- This plan is in the corpus and is demoted. Do not paste eval phrases into it.
- A second topic within about 1.39 points keeps confidence under 0.60. That is D4. Do not lower the floor.
- Surface Layer, Canonical model, and the long runbook section exceed 24,000 characters as whole `##` bodies. The answer is the winning leaf, so those bodies are not returned whole.
- `KW-DOC-SPEC-010` is the guard against stuffing an eval question into a keyword that the section does not contain.

## 8. Verification

- `RetrievalRegressionTests` after T15 through T22, without editing that file.
- `ExploreAnswerTests` after T12.
- `docs_validate` and `docs_drift` with `mergeReady` false after T10, T14, and T23.
- Closeout uses `docs validate . --merge-ready` only after the plan is finished.

## 9. Review

The conductor runs the council over the whole change after T23. This draft does not authorize that run. Task-gates are not that review.

## Closeout mapping

Each decision and Design harvest item maps to its destination. Documentation-impact is
`extends-component`, impact component is DocGraph, last-reviewed is 2026-10-31 for every
row, and status is archived.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D2 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null) | extends-component | DocGraph | 2026-10-31 | archived |
| D3 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D4 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D5 | Documentation waiver: eval contract lives in `RetrievalRegressionTests.cs` (not edited); requirement stated in plan only. | extends-component | DocGraph | 2026-10-31 | archived |
| D6 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D7 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D8 | [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D9 | Documentation waiver: withdrawn `paragraphPeak` winner rule; shipped scoring is D14 at [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore). | extends-component | DocGraph | 2026-10-31 | archived |
| D10 | [mcp-runbook.md](../../docgraph/mcp-runbook.md#agent_coding_standardstechnology-topic--null-charbudget--12000); [retrieval.md](../../docgraph/retrieval.md#excerpt-budgeting-agent_coding_standards) | extends-component | DocGraph | 2026-10-31 | archived |
| D11 | [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D12 | Documentation waiver: autonomy provenance for AP4; no separate canonical page. | extends-component | DocGraph | 2026-10-31 | archived |
| D14 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D15 | [documentation-ontology.md](../../documentation-ontology.md); [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| D16 | [ADR 0046](../../adr/0046-docs-explore-section-answer.md); graph behaviour in implementation (H3) | extends-component | DocGraph | 2026-10-31 | archived |
| D17 | [documentation-ontology.md](../../documentation-ontology.md); [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [code-review/architecture.md](../../code-review/architecture.md); [dash/architecture.md](../../dash/architecture.md); [kyber-squad/architecture.md](../../kyber-squad/architecture.md); [ci-pipelines/architecture.md](../../ci-pipelines/architecture.md); [docgraph/architecture.md](../../docgraph/architecture.md) (corpus `lead` and `sections` declarations per D17) | extends-component | DocGraph | 2026-10-31 | archived |
| D18 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore); [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null) | extends-component | DocGraph | 2026-10-31 | archived |
| D19 | Documentation waiver: approved change to `ExploreAnswerTests` only; behaviour described in retrieval and ADR. | extends-component | DocGraph | 2026-10-31 | archived |
| AP1 | Documentation waiver: withdrawn approval provenance only. | extends-component | DocGraph | 2026-10-31 | archived |
| AP2 | Documentation waiver: withdrawn approval provenance only. | extends-component | DocGraph | 2026-10-31 | archived |
| AP3 | Documentation waiver: withdrawn approval provenance only. | extends-component | DocGraph | 2026-10-31 | archived |
| AP4 | [ADR 0046](../../adr/0046-docs-explore-section-answer.md) (AP4 execution approval for the shipped contract) | extends-component | DocGraph | 2026-10-31 | archived |
| H1 | [retrieval.md](../../docgraph/retrieval.md#one-leaf-answer-docs_explore) | extends-component | DocGraph | 2026-10-31 | archived |
| H2 | [mcp-runbook.md](../../docgraph/mcp-runbook.md#docs_explorequery-cursor--null) | extends-component | DocGraph | 2026-10-31 | archived |
| H3 | [ADR 0046](../../adr/0046-docs-explore-section-answer.md) | extends-component | DocGraph | 2026-10-31 | archived |
| H4 | [documentation-ontology.md](../../documentation-ontology.md) | extends-component | DocGraph | 2026-10-31 | archived |
