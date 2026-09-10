---
id: docgraph/retrieval
title: Retrieval and ranking
doc-type: reference
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-09-08
decided-by:
  - adr/0010-keywords-prefix-coverage-and-oov-idf
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

Every document is scored on independent identity, title, and body contributions, then scaled by authority:

```
score = exact-identity + partial-identity + (2.0 x title similarity) + body relevance
score = score x authority
```

| Contribution | Weight | Fires when |
|---|---|---|
| Exact `id` or path match | 6.0 | the query names the document outright |
| Exact `code-refs` match | 5.0 | the query is a symbol the document claims |
| Exact `api-endpoints` match | 5.0 | the query is a route the document claims |
| Exact `keywords` match | 4.0 | the query equals a declared keyword or alias |
| Partial `id` coverage | up to 4.5 | the query names part of the id slug |
| Partial `keywords` coverage | up to 4.5 | keyword coverage × 3.0, capped at a 1.5 multiplier |
| Exact `component` match | 3.0 | the query is a component name |
| Partial `component` coverage | up to 2.5 | the query names part of a component |
| Title similarity | up to 2.0 | cosine overlap with the title, ignoring question scaffolding |
| Body relevance | up to 1.0 | BM25 over the prose, squashed to 0..1 |

Frontmatter identity deliberately outranks prose. A document that *formally claims* the
query term is a better answer than one that merely discusses it — and only the ontology
carries that claim, which is the entire reason retrieval is built on it.

Partial identity exists because ids are structured slugs (`docgraph/architecture`,
`context-hygiene/skills`), which makes them the closest thing the corpus has to a
controlled vocabulary. Without it, "DocGraph architecture" returned every architecture
document in the repository: several files sharing one generic word, ranked above the one
actually asked for.

Optional `keywords` (and the internal `aliases` synonym) sit in that same identity layer:
they are claims, not body mentions, which is why an exact keyword match outranks title and
prose.

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

## Compound names and prefix coverage

Both queries and bodies are vectorised with **adjacent token pairs fused**, at half
weight. Text writing "Web UI" therefore remains reachable from a query writing "WebUI",
which is how people type it. Fusion also gives a query a weak phrase signal: "logged out"
yields the term `loggedout`, which a troubleshooting runbook has and a spec that merely
"logged an error" does not.

Half weight is deliberate — a fused pair is a bridge for compound names, not evidence in
its own right, and counting it fully would let an incidental adjacency outweigh a real
term match.

Identity coverage goes further than fusion. CamelCase identities are split on letter-case
boundaries before they are vectorised, so `KyberDash` is tokens `kyber` and `dash`, not one
opaque `kyberdash`. Coverage then counts a hit when a query token and an identity token
share a prefix of at least three characters either way — `dashboard` covers `dash`. Shorter
stems are ignored so `it` cannot match `iteration`.

These ranking weights and the prefix floor are the contract in
[ADR 0010](../adr/0010-keywords-prefix-coverage-and-oov-idf.md).

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

Title cosine drops the scaffolding half of that filter but not the ubiquity half. Sharing
only a scaffolding word with a title is no longer a hit — an off-topic question whose one
scaffolding word appeared in a task plan's title used to clear the floor at 0.30 on that
alone. Corpus-ubiquitous terms are kept, because the two filters are not the same
judgement: a term in every *body* cannot say which body is the answer, but a title is a
curated six-word label, and the document titled "Skills" is the one about skills however
often this corpus says "skill". Filtering titles by ubiquity too cost that document a
third of its score for the query "skill authoring".

Note for anyone editing this page: the retrieval regression suite searches the real
corpus, including this file. Spelling out an off-topic test query here makes that query
answerable and fails the suite.

Body BM25 still uses Robertson–Sparck Jones IDF, with one calibration: a query term that
appears in **no** document body is not given the theoretical maximum IDF. It is treated as
if it occurred in 20 percent of the corpus. One novel technical term therefore penalises
coverage without dragging an otherwise matching document under the 0.25 floor. Several
such terms still accumulate in `askedFor`, so an off-topic question remains an explicit
miss.

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

## Retrieval is not analysis

Retrieval ranks documents for a caller's question; documentation analysis compares
line-addressable claims to find duplication, conflicts, and divergent terminology. They
reuse the same parsed documents, declared identity, and DocGraph relationships, but their
candidate algorithms and budgets are separate.

Analysis starts with graph neighbors and exact content hashes, then optionally adds a
sparse lexical top-k fallback or cached semantic reranking. The default never performs an
all-pairs scan and never calls an embedding endpoint. See
[Documentation analysis and review](analysis.md) for search modes and their cost bounds.

## Related

- [DocGraph architecture](architecture.md) — how the index is built and kept fresh
- [MCP server runbook](mcp-runbook.md) — the tools that expose this
- [Documentation analysis and review](analysis.md) — claim comparison and review workflow
- [The documentation ontology](../documentation-ontology.md) — the identity ranking reads
- [ADR 0010](../adr/0010-keywords-prefix-coverage-and-oov-idf.md) — keywords, prefix coverage, OOV IDF
