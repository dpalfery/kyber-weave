---
id: docgraph/governance
title: Documentation governance gates
doc-type: governance
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-14
code-refs:
  - DocSpecValidator
  - DocDriftLinter
---

# Documentation governance gates

Two gates keep the corpus worth querying. **Schema** checks that documents conform to
[the ontology](../documentation-ontology.md). **Drift** checks that what they claim about
code is still true.

```bash
kyber-weave docs validate .
kyber-weave docs drift .
kyber-weave docs catalog .
kyber-weave docs integrity-check .
```

Starting from an ungoverned tree? Run [`docs init`](onboarding.md) first — these gates
report against a schema and a catalog that have to exist before their findings mean
anything.

## Schema conformance — `docs validate`

Needs no code index. Exits non-zero on any error.

| Rule | Fires when |
|---|---|
| `KW-DOC-SPEC-001` | No frontmatter block, or one that could not be parsed |
| `KW-DOC-SPEC-002` | `doc-type`, `status`, or `last-reviewed` outside its closed vocabulary or format |
| `KW-DOC-SPEC-003` | A required key is missing or empty for this doc-type |
| `KW-DOC-SPEC-004` | `component` or `owner` is not a row in [catalog.md](../catalog.md) |
| `KW-DOC-SPEC-005` | `source-root` names a path that does not exist |
| `KW-DOC-SPEC-006` | Duplicate `id`, or `decided-by` / `supersedes` referencing an unknown id |

`KW-DOC-SPEC-004` and `-006` carry a **nearest-match hint** computed by edit distance,
offered only when the distance is plausibly a typo rather than a different word. A
mistyped component says which one you probably meant.

When the configured managed glossary exists, `docs validate` also checks its table,
statuses, approved definitions, and component/code scopes as `KW-DOC-GLOSSARY-001`.

## Entity drift — `docs drift`

Requires a CodeGraph index and the `sqlite3` CLI on PATH.

| Rule | Severity | Fires when |
|---|---|---|
| `KW-DOC-DRIFT-001` | Error | A `code-refs` symbol resolves to nothing in the index |
| `KW-DOC-DRIFT-002` | Error | An `api-endpoints` route matches no indexed route |
| `KW-DOC-DRIFT-003` | Warning | `source-root` exists but nothing beneath it is indexed |

A missing index is reported once as **critical** rather than as a per-document error
storm, with the remedy: run `codegraph index` at the repository root, or restore the
cached index in CI.

`-003` is a warning rather than an error because the honest cause is ambiguous: either the
path is wrong, or the index is stale, and the tool cannot tell which.

### Why drift needs its own gate

This is the failure mode the ontology exists to prevent. After a rename, **prose still
reads correctly** — the sentence about `DocumentIndex` is as fluent as it ever was, and
nothing surfaces the break. No linter, reviewer, or test notices. Only resolving the
declared symbol against a real index does.

That is also why `code-refs` is a claim of ownership rather than a prose mention. A
mention cannot be checked. A claim can.

## Coverage — `docs catalog`

Reports doc-type coverage by component: which components have architecture documents,
which have runbooks, and which have nothing. Advisory; it gates nothing.

## Analysis — `docs integrity-check`, `docs review`, and `docs glossary`

Analysis identifies evidence; it never edits source documentation. Exact duplicates are
deterministic, while near duplicates, conflicts, and divergent term senses can be exported
for bounded agent review and imported as content-addressed verdicts.

```bash
kyber-weave docs integrity-check .                         # advisory by default
kyber-weave docs integrity-check . --fail-on warning       # gate warnings and errors
kyber-weave docs review export . --out candidates.json
kyber-weave docs review import . --in verdicts.json
kyber-weave docs glossary .                        # preview
kyber-weave docs glossary . --write                # merge proposals only
```

`--fail-on` accepts `none`, `warning`, or `error`; `none` is the default. Operational
errors always return non-zero regardless of that setting. Review import validates the
entire versioned bundle before one transaction. Glossary writes are confined to the
configured glossary and preserve reviewed/human-owned content.

| Rule | Severity | Fires when |
|---|---|---|
| `KW-DOC-ANALYSIS-001` | Info / Warning | Pending near duplicate, or exact/confirmed duplicate cluster |
| `KW-DOC-ANALYSIS-002` | Info / Error | Pending potential conflict, or high-confidence confirmed conflict |
| `KW-DOC-ANALYSIS-003` | Warning | One informative term has divergent unresolved senses |
| `KW-DOC-ANALYSIS-004` | Operational Error | Ignore markup is malformed or unsupported |
| `KW-DOC-ANALYSIS-005` | Warning | CodeGraph is unavailable; bounded lexical/document analysis continues |
| `KW-DOC-ANALYSIS-006` | Warning / Operational Error | Embeddings unavailable in `prefer` / `required` mode |
| `KW-DOC-REVIEW-001` | Operational Error | Review verdict bundle is invalid, stale, or cannot be persisted safely |
| `KW-DOC-GLOSSARY-001` | Operational Error | Managed glossary structure or approved scope is invalid |

See [documentation analysis and review](analysis.md) for claim extraction, graph blocking,
cost modes, cache privacy, the review schemas, and glossary lifecycle.

## Wiring into CI

Both gates emit stable rule ids and render to SARIF for GitHub code scanning. See
[CI Pipelines](../ci-pipelines/architecture.md) for the diagnostic engine and
[the workflow runbook](../ci-pipelines/workflows-runbook.md) for a copy-ready gate.

## Related

- [The documentation ontology](../documentation-ontology.md) — the schema being enforced
- [DocGraph architecture](architecture.md) — how the code graph join works
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-*` id in one table
