# Documentation rule reference

Every rule `kyber-weave docs validate` and `docs drift` can emit, and what clears it.

## Schema — `docs validate`

Needs no code index.

### `KW-DOC-SPEC-001` — no frontmatter, or unparseable

The file has no `---` block, or the YAML inside it failed to parse.

Clear it by adding the six base keys. If the block exists but fails, the usual causes are
tab indentation, an unquoted value containing `:`, or a list written inline where `- `
items are expected. Quote any title containing a colon:

```yaml
title: "Skills in your ALM: a governance playbook"
```

### `KW-DOC-SPEC-002` — outside a closed vocabulary

`doc-type` or `status` is not in the closed set, or `last-reviewed` is not ISO
`yyyy-MM-dd`. `2026-8-1` fails; `2026-08-01` passes. `Current` fails on the value, not the
case — comparison is case-insensitive, so the real cause is a value like `active` or
`published` that simply is not in the set.

### `KW-DOC-SPEC-003` — required key missing

A base key, or a key this `doc-type` requires. Before supplying it, check the `doc-type`
is right: this rule fires most often on a document mislabelled `architecture` that cannot
name a `component`.

For `architecture` and `runbook` it also enforces the pairing invariant — `source-root`
present without `code-refs`, or the reverse.

### `KW-DOC-SPEC-004` — value not in the catalog

`component` or `owner` is not a row in the repository's one catalog —
`<docs-root>/catalog.md`, or wherever `ontology.catalog-path` points. The diagnostic
suggests the nearest catalog value when the edit distance is plausible.

Two fixes, and picking the right one matters: if the value is a typo, correct the
document. If the unit genuinely exists and is absent from the catalog, add the row. Do not
retype the frontmatter to match an unrelated component just to pass.

### `KW-DOC-SPEC-005` — source-root does not exist

The path is not a directory or file in the repository. Paths are repository-relative and
forward-slashed — not relative to the document, and not absolute.

### `KW-DOC-SPEC-006` — bad reference

Either two documents declare the same `id`, or `decided-by` / `supersedes` names an `id`
no document declares.

Ids are permanent and unique. When two collide, the newer document changes — renaming an
established id breaks every reference to it.

## Drift — `docs drift`

Requires a CodeGraph index at `.codegraph/codegraph.db` and the `sqlite3` CLI.

### `KW-DOC-DRIFT-001` — code-refs symbol unresolved

The symbol is not in the index. Either it was renamed or deleted, or it never existed.

When the index itself is missing, this is reported **once as critical** rather than per
document. That is an environment problem, not a documentation problem: build or restore
the index.

Otherwise the diagnostic names the nearest surviving symbol when one is close. Update the
reference, or drop it if the document no longer covers that code. Dropping is legitimate
and often correct — a stale claim is worse than no claim.

### `KW-DOC-DRIFT-002` — api-endpoints route unmatched

Route strings are exact, including method and full path template:

```yaml
api-endpoints:
  - GET /api/me/usage
```

`/api/me/usage` alone fails. So does `GET /api/me/usage/` and a concrete
`GET /api/me/123` where the indexed route is templated.

### `KW-DOC-DRIFT-003` — source-root indexed nothing

Warning, not error. The path exists but the index holds no file beneath it. Either the
path is wrong, or the index is stale — the tool cannot tell which, which is why it does
not fail the build.

## Exit codes

`docs validate` and `docs drift` exit non-zero on any **error**. Warnings and info do not
gate. `--no-info` hides informational findings; `--format sarif` emits SARIF for code
scanning.

## Analysis — `docs integrity-check`

Analysis is advisory by default and never edits source documentation.

### `KW-DOC-ANALYSIS-001` — duplicate cluster

Info for a pending near duplicate; Warning for a deterministic exact cluster or a
high-confidence imported duplicate verdict. Confirm that the claims are substantively the
same, not merely about the same topic.

### `KW-DOC-ANALYSIS-002` — potential conflict

Info while pending; Error only after a high-confidence imported `conflict` verdict.
Confirm that both claims cannot be true in the same scope and time before choosing a
canonical source.

### `KW-DOC-ANALYSIS-003` — ambiguous terminology

Warning when one informative term occurs in divergent contexts not fully accounted for by
approved scoped glossary senses. Preview proposals with `docs glossary .`; do not rename
terms automatically.

### `KW-DOC-ANALYSIS-004` — invalid ignore markup

Operational Error. `<kyber-ignore>` must be balanced, case-sensitive, non-nested, use
`duplicate`, `conflict`, `terminology`, or `all`, and stay within frontmatter/`##`
boundaries. Fix the markup; suppression never fails open.

### `KW-DOC-ANALYSIS-005` — CodeGraph unavailable

Warning. Analysis continues with document relationships and bounded lexical search. Build
or restore `.codegraph/codegraph.db` for code-neighborhood evidence.

### `KW-DOC-ANALYSIS-006` — embedding unavailable

Warning in `prefer`, operational Error in `required`. Embeddings remain off by default and
are never invoked unless the local cache path is safely ignored. Restore the loopback
provider/safe cache, use `prefer` for lexical fallback, or use `off`.

### `KW-DOC-REVIEW-001` — invalid or stale verdict bundle

Operational Error. Regenerate candidates from the current corpus and validate every
candidate id, claim hash, evidence id, label, confidence, and glossary proposal. Import is
atomic; one invalid item writes nothing.

### `KW-DOC-GLOSSARY-001` — invalid managed glossary

Operational Error. Keep the document a conformant `reference`; use only `proposed`,
`approved`, or `rejected` row status. Approved senses require a definition and at least
one valid `component:<catalog value>` or `code-ref:<symbol>` scope.

`docs integrity-check --fail-on none|warning|error` controls finding gating. Operational errors
always return non-zero.
