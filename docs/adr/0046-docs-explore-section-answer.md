---
id: adr/0046-docs-explore-section-answer
title: docs_explore Returns One Section Answer, Not a Document List
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-30
component: DocGraph
code-refs:
  - DocumentIndex
decided-by: []
supersedes: []
---

# ADR 0046: docs_explore Returns One Section Answer, Not a Document List

## Status

Accepted, 2026-10-30.

## Context

`docs_explore` originally returned ranked **documents** with section excerpts chosen under a
shared `charBudget`. Agents still had to stitch partial sections together, and a plan that
repeated a query term could outrank the ontology that actually governed frontmatter. The MCP
runbook exposed `maxDocs` and `charBudget` on `docs_explore`, which encouraged callers to
tune truncation instead of asking a sharper question.

Operators need one authoritative answer per question: the best matching **leaf** (preamble or
`##` section, including nested `###` claims inside it), with honest confidence when another
topic nearly tied.

## Decision

1. **`docs_explore` returns one answer blob, not a document list.** After documents clear the
   relevance floor, leaves inside them score with claim coverage, BM25 body text, and an exact
   document-id lead bonus on the declared lead leaf. The winning leaf's prose is returned whole
   (subject to the ceiling below). `maxDocs` and `charBudget` are removed from `docs_explore`;
   other MCP tools keep their existing budgets.

2. **Section identity is `{doc-id}#{github-slug}`.** The slug is the GitHub auto-identifier for
   each `##` heading. The preamble uses slug `#` (leaf id `{doc-id}#`). Continuation cursors
   name the relative path, leaf id, and character offset when prose exceeds one response.

3. **Optional `lead` and `sections` frontmatter declare section identity.** `lead` names the
   slug returned for an exact document `id` query; default is `#`. `sections` maps each slug
   (including `"#":` for the preamble) to optional `keywords` and `code-refs` that roll up to
   the document for ranking and `docs_for_symbol`. Validation uses `KW-DOC-SPEC-008` through
   `KW-DOC-SPEC-010` and link fragments use `KW-DOC-LINK-001`; list and drift rules extend
   `KW-DOC-SPEC-002` and `KW-DOC-DRIFT-001` to section lists without renumbering existing ids.

4. **Confidence is the topic-score gap, not document rank.** Among qualifying documents, each
   `##` topic receives the best leaf score inside it. Confidence is `tanh((winningTopic −
   runnerTopic) / 2)`, rounded to two decimals, and halved when the answer is marked
   incomplete because prose hit the ceiling mid-leaf.

5. **The answer ceiling is 24,000 characters.** One call returns at most that many characters
   of the winning leaf; shorter sections return shorter answers without padding. Past the
   ceiling, prose is cut and the response marks incomplete with an optional cursor. The same
   ceiling is enforced at authoring time by `KW-DOC-SPEC-009` on the declared `lead` leaf.

## Alternatives Considered

- **Keep multi-document excerpts with a higher `charBudget`.** Rejected. Budget knobs let
  callers compensate for weak queries instead of fixing retrieval; they also hid which section
  was authoritative.
- **Heading markers or `{#id}` attributes in prose.** Rejected. Slugs already come from GitHub
  heading rules; duplicate identity systems would drift from link fragments.
- **Separate confidence from topic competition.** Rejected. Document-level rank alone could not
  express "right file, wrong section" when two topics scored closely.

## Consequences

- Agents should read the single returned section first; continuation cursors resume the same
  leaf, not a new ranking pass.
- Authors must keep declared `lead` sections under 24,000 characters or split them.
- Changing the ceiling, confidence formula, or leaf identity rules is a retrieval-contract
  change and must update [the documentation ontology](../documentation-ontology.md) and
  [retrieval and ranking](../docgraph/retrieval.md) when those documents describe the behavior.

## Related

- [The documentation ontology](../documentation-ontology.md) — `lead`, `sections`, and section rules
- [Retrieval and ranking](../docgraph/retrieval.md)
- [DocGraph MCP runbook](../docgraph/mcp-runbook.md)
- [ADR 0010](0010-keywords-prefix-coverage-and-oov-idf.md) — document-level keywords identity
