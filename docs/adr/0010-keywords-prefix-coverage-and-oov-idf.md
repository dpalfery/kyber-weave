---
id: adr/0010-keywords-prefix-coverage-and-oov-idf
title: Keywords Identity, Compound Prefix Coverage, and Calibrated OOV IDF
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-03
---

# ADR 0010: Keywords Identity, Compound Prefix Coverage, and Calibrated OOV IDF

## Status

Accepted, 2026-09-03.

## Context

Natural-language queries that named a surface or stack (for example `"dashboard dev environment tauri"` targeting `dash/architecture`) missed the document. Three independent retrieval defects stacked:

1. Frontmatter identity had no place to register domain synonyms. A document whose slug is `dash` could not claim the query term `dashboard` or `tauri` except by repeating those words in the body.
2. `DocumentIndex.Coverage` treated camelCase identities as a single token and required exact token equality (or adjacent fusion). Query `dashboard` did not cover identity token `dash`, and `KyberDash` did not split into `Kyber` + `Dash`.
3. Robertson–Sparck Jones IDF for a term absent from every document body (`n = 0`) evaluated to \(\ln(2N + 2)\), the theoretical maximum. One out-of-vocabulary term inflated `askedFor` in `ScoreBody` until the coverage ratio dropped the whole query below the 0.25 relevance floor.

## Decision

1. **`keywords` is an optional identity field.** Authors list domain synonyms, acronyms, or search aliases. YamlDotNet also deserializes `aliases` as an internal synonym; `DocumentModel.Keywords` is the unified view (`Frontmatter.Keywords ?? Frontmatter.Aliases`). Empty or whitespace-only entries fail `KW-DOC-SPEC-002`.
2. **Keywords participate in ranking.** `DocumentIndex` indexes them case-insensitively. An exact keyword match adds `KeywordWeight = 4.0`. Partial keyword coverage uses `KeywordPartialWeight = 3.0`, scaled by matched coverage and capped at a 1.5 multiplier (maximum 4.5).
3. **Coverage splits camelCase and matches prefixes of length ≥ 3.** Identity strings are split on camelCase boundaries before vectorization. A query token and an identity token match when either is a prefix of the other and \(\min(|p|, |q|) \ge 3\).
4. **OOV IDF is calibrated to a ~20% corpus-share baseline**, not the raw \(n = 0\) maximum:

   \[
   IDF_{\text{oov}} = \ln\left(1 + \frac{N - \max(1, \lfloor 0.2N \rfloor) + 0.5}{\max(1, \lfloor 0.2N \rfloor) + 0.5}\right)
   \]

   Unanswerable queries with several OOV terms still collapse below the 0.25 floor.

## Alternatives Considered

- **Neural or API embeddings for specialized terms.** Rejected. Retrieval in this repository is offline and deterministic; adding a network-dependent embedding path would break that constraint.
- **A full stemming library (Porter/Snowball).** Rejected. Prefix matching plus existing `TextVectorizer.Normalize` covers the miss without a new dependency.
- **Changing `TextVectorizer` stop words.** Rejected. The vectorizer is shared with skill routing and agent drift detection; stop-word edits would shift linter baselines.
- **Leaving OOV IDF uncalibrated and requiring every alias in body prose.** Rejected. It punishes documents that correctly keep specialized names in identity metadata rather than stuffing the body.

## Consequences

- Authors can route queries without repeating every alias in prose. The field is optional; documents without keywords keep the previous identity ranking.
- Prefix matching can over-hit if the three-character floor is lowered; that floor is part of this decision.
- Changing `KeywordWeight`, `KeywordPartialWeight`, the prefix length floor, or the 20% OOV baseline is a retrieval-contract change and must update [retrieval and ranking](../docgraph/retrieval.md) in the same change.

## Related

- [Retrieval and ranking](../docgraph/retrieval.md)
- [The documentation ontology](../documentation-ontology.md)
- [DocGraph architecture](../docgraph/architecture.md)
- [KyberDash architecture](../dash/architecture.md)
