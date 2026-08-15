---
id: docgraph/analysis
title: Documentation analysis and review
doc-type: reference
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-14
---

# Documentation analysis and review

DocGraph can identify repeated claims, potential contradictions, and terms whose meaning
changes across the corpus. Analysis is deliberately **graph-first and advisory**: it
shortlists evidence for a human or reviewing agent, but never rewrites, merges, or deletes
source documentation.

```bash
kyber-weave docs integrity-check .
kyber-weave docs review export . --out candidates.json
kyber-weave docs review import . --in verdicts.json
kyber-weave docs glossary .
```

The default path is English-first, selects `status: current`, uses bounded hybrid search,
does not call an embedding endpoint, and returns zero for findings. Operational failures
still return non-zero.

## Claims, not whole files

Markdown is parsed structurally with Markdig. A paragraph, individual list item, table row
with its header context, or fenced code block becomes a line-addressable claim beneath its
nearest `##` heading. Each claim retains document identity, component, section, source
lines, code references, a content hash, and a contextual hash.

Exact duplicate hashing uses claim text alone, so moving unchanged prose does not change
its identity. Candidate search uses the section heading plus the claim, because the same
sentence beneath two different headings may not mean the same thing. The configured
managed glossary is excluded from its own analysis.

The only inline suppressions are balanced, case-sensitive, non-nested wrappers:

```html
<kyber-ignore rule="duplicate">intentional repeated claim</kyber-ignore>
<kyber-ignore rule="conflict">scope-qualified apparent contradiction</kyber-ignore>
<kyber-ignore rule="terminology">intentional local usage</kyber-ignore>
<kyber-ignore rule="all">intentional example</kyber-ignore>
```

Wrappers inside fenced examples are literal. A wrapper cannot cross frontmatter or a `##`
boundary. Unknown, nested, unbalanced, or otherwise malformed wrappers fail analysis as
`KW-DOC-ANALYSIS-004`; suppression never fails open. Retrieval continues to see the
original prose.

## Graph-first candidate generation

Analysis reuses the immutable DocGraph projection behind export and retrieval. Claims are
neighbors when their documents share a document, component, endpoint, resolved code node,
or overlapping source root; are connected by `LINKS_TO`, `DECIDED_BY`, or an applicable
`SUPERSEDES`; or meet through one CodeGraph hop of `contains`, `calls`, `references`,
`instantiates`, `extends`, or `implements`.

`imports` is excluded because it is too broad to discriminate claims. A code node above
`max-code-neighbors` is skipped for the same reason. If CodeGraph is unavailable, one
`KW-DOC-ANALYSIS-005` warning is reported and document relationships plus bounded lexical
search continue.

Three modes control the cost/recall trade:

| Mode | Candidate pool | Cost character |
|---|---|---|
| `graph` | Global exact duplicates plus graph-neighbor comparisons | Lowest cost; disconnected paraphrases are not found |
| `hybrid` | `graph` plus corpus-wide sparse inverted-index top-k fallback | Default; finds disconnected lexical similarity without all-pairs work |
| `high-recall` | Broad lexical candidates plus global exact cosine top-k when cached embeddings are enabled | Explicit quadratic first pass; progress should be monitored |

Embeddings can rerank eligible claims, but exact duplicates stay deterministic and
model-free. The default `hybrid` path with embeddings off is designed for 1,000 documents
and 10,000 claims: bounded graph/top-k comparisons, at most 500 review candidates, and a
10-second / 512-MiB Release target on the CI reference runner. High-recall global
embedding search is explicitly outside that latency target.

## Findings and confidence

| Finding | Before review | After a high-confidence imported verdict |
|---|---|---|
| Duplicate | Exact clusters are Warning; near duplicates are Info | `duplicate` becomes Warning; `benign` suppresses unchanged evidence |
| Conflict | Info when graph/topic evidence and differing negation, obligation, number/version, path, command, or code literal make the pair plausible | `conflict` becomes Error |
| Terminology | Warning when one informative term occurs in divergent graph/context clusters | `distinct-senses` supplies glossary proposals; approved scoped senses can suppress it |

The rubric is intentionally narrow: a duplicate is substantively the same claim, not
merely the same topic; a conflict means both claims cannot be true in the same scope and
time; `distinct-senses` means one term denotes multiple concepts; `benign` covers
compatible scopes, intentional examples, and harmless overlap; `uncertain` means the
evidence is insufficient. Low-confidence and uncertain verdicts remain pending for review;
the visible finding severity continues to follow the kind-specific table above.

Candidate ids hash the rule kind, normalized term where applicable, sorted claim-content
hashes, and analyzer/rubric versions. Moving prose does not invalidate a verdict; changing
the claim or rubric does.

## CLI and exit behavior

```bash
kyber-weave docs integrity-check . [--fail-on none|warning|error]
```

JSON, SARIF, and Markdown output include related locations for clustered evidence; table
output shows the primary location and a related-location count. When the table would bury
warnings under informational rows, Info is omitted from the table and counted in the
summary — use `--format json` to list every finding. Every format includes
local cost metrics: extracted claims, comparisons and candidates by source, truncation,
embedding cache hits/misses, and provider usage when returned. `none` is the default.
`warning` gates Warning and Error findings; `error` gates Error findings. Operational
errors always return non-zero.

```bash
kyber-weave docs review export . --out candidates.json
kyber-weave docs review import . --in verdicts.json
```

Export omits deterministic exact duplicates and includes bounded excerpts, graph evidence,
scores, content hashes, the rubric, and a candidate-set hash under
`kyber-weave.docs-review.candidates/v1`. It exports unreviewed, uncertain, low-confidence,
or changed candidates.

Import accepts `kyber-weave.docs-review.verdicts/v1`. The entire bundle is checked before
one transaction: schema and analyzer versions, candidate ids and set hash, current claim
hashes, applicable labels, confidence, evidence ids, and glossary-sense shape. One stale or
malformed item rejects the whole import as `KW-DOC-REVIEW-001` and writes nothing.

## Local cache and embedding privacy

Reusable vectors and imported verdicts live in
`.kyber-weave/cache/docs-analysis.sqlite3`, accessed through the existing `sqlite3` CLI
rather than a new native dependency. Vectors are normalized and keyed by contextual claim
hash, provider fingerprint, model, dimensions, and float encoding. Credentials and
authorization headers are never stored.

Persistence is allowed only when `.kyber-weave/.gitignore` effectively protects the
narrow `cache/` path and no cache entry is already tracked. `docs init` creates or safely
merges that ignore entry; it does not create an empty glossary. On an existing host without
the protection, deterministic analysis still runs without persistence. If embeddings were
requested, `embeddings.mode: prefer` warns, skips embeddings, and falls back to lexical
analysis; `embeddings.mode: required` and `docs review import` fail before writing or
sending text.

Embedding endpoints must be absolute HTTP(S) and resolve **only to loopback** (`localhost`,
`127.0.0.0/8`, or `::1`). Resolution is checked again when connecting to close a DNS
rebinding window, and redirects are disabled even when a local server redirects elsewhere.
The optional bearer token is read from the configured environment-variable name and is
never included in diagnostics. Kyber-Weave never calls the endpoint when results cannot be
persisted safely.

## Managed glossary

```bash
kyber-weave docs glossary .          # preview Markdown
kyber-weave docs glossary . --write  # merge proposals only
```

The default path is `<first-docs-root>/glossary.md`, overridable with `glossary-path`. A
new glossary is a conformant `reference` document with `status: needs-review`, today's UTC
date, and the first catalog row's owner. It is not a new document type.

Each `## <term>` section contains a managed table:

```markdown
| Sense ID | Status | Definition | Scope | Aliases |
|---|---|---|---|---|
| loop-a1b2c3d4 | proposed |  | component:Gameplay | gameplay loop |
```

Sense status is exactly `proposed`, `approved`, or `rejected`. Approved senses need a
definition and at least one semicolon-separated `component:<catalog value>` or
`code-ref:<symbol>` scope. Humans approve or reject rows, update `last-reviewed`, and
return the document to `current` when review is complete.

`--write` preserves approved/rejected rows, human definitions and prose, aliases, owner,
and the existing review date. It adds or refreshes proposals, demotes the document to
`needs-review` when proposals change, and removes only untouched generated proposals whose
evidence disappeared. `docs validate` checks the managed shape as
`KW-DOC-GLOSSARY-001`. `docs export-graph` exports approved Term/Sense nodes and `HAS_SENSE`,
`ALIAS_OF`, `SCOPED_TO`, and `EVIDENCED_BY` edges; proposed and rejected senses do not
enter the exported graph.

## Related

- [Configuration](../configuration.md) — thresholds, search modes, and embedding endpoint
- [Documentation governance](governance.md) — rule ids and CI use
- [MCP runbook](mcp-runbook.md) — capped conversational analysis and glossary tools
- [DocGraph architecture](architecture.md) — the shared projection and CodeGraph join
