---
id: docgraph/retrieval
title: Retrieval and ranking
doc-type: reference
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-11-07
decided-by:
  - adr/0010-keywords-prefix-coverage-and-oov-idf
  - adr/0046-docs-explore-section-answer
sections:
  the-relevance-floor:
    keywords:
      - retrieval scoring
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

**`partial-identity` counts only for a document the question already touches.** It is zero
unless `exact-identity`, title similarity or body relevance is already above zero. A shared
prefix ranks the documents a question touches; it never introduces one.

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

That purpose is ranking, so partial identity is a **re-ranker and not an introducer**. It
ranks the documents the question already touches, and it never introduces one. "Touches"
means one of three things, all of them the document saying or declaring something the
question asks about:

- an exact match on what the document declares outright — its id, path, component,
  keyword, code-ref or endpoint;
- a term the question shares with the document's title;
- an informative term of the question that the document's prose contains.

When none of the three holds, the partial-identity contribution is zero and the document
cannot clear the relevance floor however much of its slug the question appears to cover.

Optional `keywords` (and the internal `aliases` synonym) sit in that same identity layer:
they are claims, not body mentions, which is why an exact keyword match outranks title and
prose.

### Authority

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

### Compound names and prefix coverage

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

The prefix rule and that floor are unchanged, and so is `Coverage` itself. What changed is
its **consequence for qualification**. A shared prefix no longer admits a document on its
own: `dashboard` still covers `dash`, but that coverage now only moves a document's rank
among the documents the question already touches. If no exact match, no title term and no
prose term ties the question to the document, the coverage multiplies nothing, because
there is nothing for it to add to.

These ranking weights and the prefix floor are the contract in
[ADR 0010](../adr/0010-keywords-prefix-coverage-and-oov-idf.md).

### The relevance floor

A document scoring below **0.25** is not returned at all, and a query where nothing clears
the floor returns an explicit miss rather than a best-effort list.

The floor is only reached by a document the question can touch in the first place. An exact
identity match, a shared title term, or an informative query term in the prose is what puts
a document in contention; partial identity then orders the contenders. A document with no
such tie scores zero on every contribution except the partial one, and the partial one is
withheld — so slug similarity can no longer lift an untouched document over 0.25.

That was a real defect, not a hypothetical. A question on a subject the corpus does not
cover was answered from a task item whose id carried a token that was a prefix of one of
the question's words: the shared prefix scored partial identity at 0.5625, title and body
contributed zero, the authority discount for a draft document scaled the total to 0.478,
and 0.478 is above the floor, so the item qualified and won. Nothing in that document —
prose, title or keywords — mentioned the question's subject. A document the question does
not touch is now unreachable by slug coverage alone.

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

## One leaf answer (`docs_answer_question_from_repo_documentation`)

After documents clear the relevance floor,
[`docs_answer_question_from_repo_documentation`](mcp-runbook.md) scores **leaves** inside them and
returns one answer blob. A leaf is the preamble, the prose before a `##`
section's first `###`, or a `###` subsection. Deeper headings stay inside their parent
`###`. Sibling leaves under one `##` compete for that topic's score; confidence compares
topics, not every leaf.

The qualification rule above applies at **document** level only. Inside a document that has
already cleared the floor, leaf selection adds partial document identity unguarded, which is
safe: it can only reorder documents that have already qualified.

The winning leaf comes back **whole**. **24,000 characters** is a ceiling, not a target —
a shorter leaf returns a shorter answer with no padding. Past the ceiling the response is
marked incomplete and a **stateless cursor** returns the next span of the **same** leaf. The
same question without a cursor starts again at the first span.

The tool accepts **`query`** and an optional **`cursor`** only. `maxDocs` and `charBudget`
are not parameters on it.

**Confidence** is `tanh((winning topic score − best other topic score) / 2)`, formatted
`0.00`–`1.00`, and halved when the reply is incomplete. The raw relevance score is not
returned. A caller should stop at **0.60 or higher** when the answer is not incomplete.

The source line is `source: {path} — {## heading} › {### heading}`, or the path alone for
the preamble.

Optional frontmatter **`lead`** and **`sections`** declare which leaf an exact document-id
query returns and attach keywords and code-refs to slugs; those claims roll up to the
document for ranking and for `docs_find_documents_citing_code_symbol`. **Index sections** —
plan inventories, the
ADR inventory, the todo inventory, the policy-line ledger, the reference inventory — are
not answer text: a query that matches an inventory row is answered from the linked
document's leaf instead.

The public contract is recorded in [ADR 0046](../adr/0046-docs-explore-section-answer.md) and
the [MCP runbook](mcp-runbook.md) tool section. ADR 0046 is titled for the tool's former name; the
decision it records is unchanged and the tool is now
`docs_answer_question_from_repo_documentation`.

## Excerpt budgeting (`docs_get_coding_standard_for_technology`)

`charBudget` (default 12000, floor 1000, ceiling 120000) still governs
**`docs_get_coding_standard_for_technology`**, not the explore tool. It is a **total across every
standard returned** for the technology, split with a 1500-character floor each.

Within a standard, `##` sections are ranked by rarity-weighted similarity, selected until
the budget runs out, then **emitted in document order** so the prose still reads. Short
sections are damped, because a three-word stub can overlap a query almost perfectly while
saying nothing.

Anything that did not fit is named in the response by heading. The caller learns what else
the document holds without opening it, and can ask again deliberately — and is told
whether a section was dropped for lack of budget or lack of relevance, since only the
former makes asking again with a larger `charBudget` worthwhile.

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
