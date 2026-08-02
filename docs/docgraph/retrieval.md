---
id: docgraph/retrieval
title: Retrieval and ranking
doc-type: reference
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-01
code-refs:
  - DocumentIndex
  - DocumentCorpus
  - TextVectorizer
---

# Retrieval and ranking

Retrieval is the half of [DocGraph](architecture.md) that agents actually consume. Its
design assumption is that **an agent asking a question is not grepping** — it wants the
passage that answers, plus enough signal to know whether to trust it.

## Scoring

Every document is scored on four independent contributions, then scaled by authority:

```
score = exact-identity + partial-identity + (2.0 x title similarity) + body relevance
score = score x authority
```

| Contribution | Weight | Fires when |
|---|---|---|
| Exact `id` or path match | 6.0 | the query names the document outright |
| Exact `code-refs` match | 5.0 | the query is a symbol the document claims |
| Exact `api-endpoints` match | 5.0 | the query is a route the document claims |
| Partial `id` coverage | up to 4.5 | the query names part of the id slug |
| Exact `component` match | 3.0 | the query is a component name |
| Partial `component` coverage | up to 2.5 | the query names part of a component |
| Title similarity | up to 2.0 | cosine overlap with the title |
| Body relevance | up to 1.0 | BM25 over the prose, squashed to 0..1 |

Frontmatter identity deliberately outranks prose. A document that *formally claims* the
query term is a better answer than one that merely discusses it — and only the ontology
carries that claim, which is the entire reason retrieval is built on it.

Partial identity exists because ids are structured slugs (`docgraph/architecture`,
`context-hygiene/skills`), which makes them the closest thing the corpus has to a
controlled vocabulary. Without it, "DocGraph architecture" returned every architecture
document in the repository: several files sharing one generic word, ranked above the one
actually asked for.

## Authority

Term statistics measure wordiness, not standing. Asked what frontmatter keys a document
needs, BM25 correctly preferred a plan that says "frontmatter" twenty-five times over the
standard that says it seven times — and answered from a work artifact instead of the rule.

So relevance is scaled by how far a document counts as current guidance:

| | Multiplier |
|---|---|
| `plan`, `spec` | 0.55 |
| `adr` | 0.9 |
| every other doc type | 1.0 |
| `superseded` | 0.4 |
| `draft`, `needs-review` | 0.85 |
| `current` | 1.0 |

The two multiply. A superseded plan lands at 0.22 of its raw relevance — present, but
never the first thing an agent reads. A demoted document still wins when it is named
outright, because an exact id match scores far above the discount.

## Compound names

Both queries and bodies are vectorised with **adjacent token pairs fused**, at half
weight. Text writing "Web UI" therefore remains reachable from a query writing "WebUI",
which is how people type it. Fusion also gives a query a weak phrase signal: "logged out"
yields the term `loggedout`, which a troubleshooting runbook has and a spec that merely
"logged an error" does not.

Half weight is deliberate — a fused pair is a bridge for compound names, not evidence in
its own right, and counting it fully would let an incidental adjacency outweigh a real
term match.

## The relevance floor

A document scoring below **0.25** is not returned at all, and a query where nothing clears
the floor returns an explicit miss rather than a best-effort list.

This matters more than it looks. Callers are told to try retrieval before grepping; if a
miss comes back as three weak results, the caller has no signal to fall back and will
answer from whatever was nearest. Saying "nothing cleared the threshold" is the whole
point of having one.

Terms appearing in more than half the corpus are dropped outright before scoring, so a
question made entirely of them scores zero and is honestly reported as a miss instead of
returning three confident results about nothing.

## Budget, not truncation

`charBudget` (default 12000, floor 1000, ceiling 120000) is a **total across all returned
documents**, split between them with a 1500-character floor each. Narrowing `maxDocs`
therefore deepens each result rather than merely shortening the list — asking for one
document gets depth, asking for five gets breadth, from one knob.

Within a document, `##` sections are ranked by rarity-weighted similarity, selected until
the budget runs out, then **emitted in document order** so the prose still reads. Short
sections are damped, because a three-word stub can overlap a query almost perfectly while
saying nothing.

Anything that did not fit is named in the response by heading. The caller learns what else
the document holds without opening it, and can ask again deliberately — and is told
whether a section was dropped for lack of budget or lack of relevance, since only the
former makes asking again worthwhile.

## Related

- [DocGraph architecture](architecture.md) — how the index is built and kept fresh
- [MCP server runbook](mcp-runbook.md) — the tools that expose this
- [The documentation ontology](../documentation-ontology.md) — the identity ranking reads
