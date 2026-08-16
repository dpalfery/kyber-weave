---
id: documentation-ontology
title: The documentation ontology
doc-type: reference
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-15
---

# The documentation ontology

The ontology is the opinion at the centre of Kyber-Weave. It says that a repository's
documentation is not a folder of Markdown but a **typed, owned, cross-referenced corpus**
— and that the difference is enforceable.

Everything else in [DocGraph](docgraph/architecture.md) follows from this file: the
conformance gates check documents against it, and the retrieval ranking reads the identity
it declares.

## Frontmatter

Every governed document opens with a YAML frontmatter block. Keys are hyphenated and bind
to the parser by name; unknown keys are ignored, so adding your own metadata never breaks
parsing.

```yaml
---
id: docgraph/architecture
title: DocGraph architecture
doc-type: architecture
status: current
component: DocGraph
source-root: src/KyberWeave.Core/Docs
owner: dpalfery
last-reviewed: 2026-08-01
code-refs:
  - DocumentIndex
  - DocumentCorpus
---
```

| Key | Meaning |
|---|---|
| `id` | Permanent, unique slug. Other documents reference this, never the file path. |
| `title` | Human title. Distinct from the H1 only when the H1 needs to be shorter. |
| `doc-type` | One of the closed set below. Decides which other keys are required. |
| `status` | Currency of the document, from the closed set below. |
| `component` | The unit of the system this document covers. Must exist in [catalog.md](catalog.md). |
| `source-root` | Repository-relative path to that component's source. Must exist on disk. |
| `owner` | Who answers for it. Must exist in [catalog.md](catalog.md). |
| `last-reviewed` | ISO `yyyy-MM-dd`. Any other format is an error, not a warning. |
| `code-refs` | Symbols this document formally claims to describe. Resolved against the code graph. |
| `api-endpoints` | Exact route strings, e.g. `GET /api/me/usage`. Resolved against the code graph. |
| `decided-by` | Ids of the ADRs that decided this document's content. |
| `supersedes` | Ids of documents this one replaces. |

## Closed vocabularies

`doc-type` and `status` are **closed sets**. A value outside them is an error, because an
open vocabulary is not a vocabulary — it is a text field that drifts until two documents
of the same kind carry different labels and neither can be found by the other's name.

**Doc types** — `architecture`, `onboarding`, `requirements`, `adr`, `plan`, `spec`, `todo`,
`runbook`, `reference`, `rule`, `governance`, `index`

**Statuses** — `current`, `draft`, `needs-review`, `superseded`

Adding a member is a change to the ontology, made in
[`.kyber-weave/kyber-weave.yml`](../.kyber-weave/kyber-weave.yml) — not an authoring
decision made mid-document.

The managed glossary introduced by documentation analysis conforms to this ontology. It
is a `reference` document whose lifecycle uses the existing `needs-review` and `current`
statuses; `proposed`, `approved`, and `rejected` describe sense rows, not document status.
No glossary-specific document type or ontology widening is required.

## The required-key matrix

Requirements vary by doc-type, because what makes an architecture document complete does
not make a reference document complete.

**Required of every document**: `id`, `title`, `owner`, `last-reviewed`, `doc-type`, `status`

| Doc type | Additionally required |
|---|---|
| `architecture` | `component` |
| `onboarding` | `component`, `source-root` |
| `requirements` | `component` |
| `runbook` | `component` |
| `plan` | `component` |
| `spec` | `component` |
| `todo` | `component` |
| `adr`, `reference`, `rule`, `governance`, `index` | — base keys only |

## The pairing invariant

For `architecture` and `runbook` documents, `source-root` and `code-refs` travel together:

- An **architecture** document with a `source-root` must name at least one `code-refs`
  symbol, and one carrying `code-refs` must declare a `source-root`.
- A **runbook** with a `source-root` must name the symbols it operates.

Naming a source root without symbols claims coverage the document does not have. Naming
symbols without a root leaves nothing to check them against. Both halves are what make
drift detection possible at all.

## Identity, not prose

Two rules follow from the ontology being about *declared identity* rather than text:

**Ids are permanent and unique.** Two documents claiming one id is an error naming both
files. Cross-references in `decided-by` and `supersedes` resolve against ids, so a
renamed file breaks nothing and a retired id breaks loudly.

**`code-refs` is a claim of ownership, not a mention.** A document that discusses
`DocumentIndex` in prose has not claimed it. A document listing it in `code-refs` has,
and is answerable for it when the symbol is renamed. That distinction is what makes
[`docs_for_symbol`](docgraph/mcp-runbook.md) a reverse lookup rather than a grep.

## Configuring the ontology

Every value above is a default, overridable per host in
[`.kyber-weave/kyber-weave.yml`](../.kyber-weave/kyber-weave.yml). This repository
overrides exactly two of them. See [configuration.md](configuration.md) for the full
surface.

## In a host repository

`kyber-weave docs init` writes a copy of this reference to
`<docs-root>/documentation-ontology.md`, which is the path every `KW-DOC-SPEC-001`
diagnostic names. See [adoption](docgraph/onboarding.md).

## Related

- [DocGraph architecture](docgraph/architecture.md) — how the corpus becomes a graph
- [Documentation analysis and review](docgraph/analysis.md) — how claims and terminology are compared
- [Documentation governance](docgraph/governance.md) — the gates that enforce this file
- [Component and owner catalog](catalog.md) — the vocabulary this file defers to
