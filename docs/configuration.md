---
id: configuration
title: Configuration
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-08-15
code-refs:
  - KyberWeaveConfigLoader
  - OntologyConfig
  - SquadConfig
---

# Configuration

Kyber-Weave is opinionated, not rigid. Every default in
[the ontology](documentation-ontology.md) and every harness capability assumption is
overridable per repository from one file.

## Location

```
.kyber-weave/kyber-weave.yml
```

Config lives in a dot-folder so the repository root stays clean. A legacy root
`kyber-weave.yml` is still read when the dot-folder file is absent, and `--config <path>`
overrides both — an explicit path that does not exist is an error rather than a silent
fallback to defaults.

Absent any file, product defaults apply. A malformed file fails as **`KW-CONFIG-001`**
with the parse error, not a stack trace.

## Shape

```yaml
ontology:
  docs-root: docs
  excluded-segments: [archive, node_modules, obj, bin]
  excluded-files: []
  doc-types: [architecture, onboarding, requirements, adr, plan, spec,
              runbook, reference, rule, governance, index, coding-standard]
  statuses: [current, draft, needs-review, superseded]
  technologies: [dotnet]
  base-required-keys: [id, title, owner, last-reviewed, doc-type, status]
  required-keys:
    architecture: [component]
    onboarding: [component, source-root]
    coding-standard: [technology]
  catalog:
    component-column: 1
    owner-column: 6

config-reg:
  auth-design: docs/reference/auth-design.md

squad:
  bundle: full
  version: 1.2.3
  targets: [codex, cursor]
  exclusions: [warp]
  translation: best-effort

harness:
  profiles:
    cursor:
      directory-name: .cursor
      supports-native-parent-agents: false
      mapped-role-skill-overrides:
        reviewer: code-review

docs-analysis:
  statuses: [current]
  glossary-path: docs/glossary.md
  verdict-confidence: 0.80
  search:
    mode: hybrid
    min-claim-tokens: 5
    lexical-candidate-threshold: 0.45
    lexical-duplicate-threshold: 0.90
    semantic-candidate-threshold: 0.78
    semantic-duplicate-threshold: 0.92
    terminology-context-threshold: 0.30
    max-neighbors-per-claim: 10
    max-code-neighbors: 50
    max-candidates: 500
  embeddings:
    mode: off
    endpoint: http://127.0.0.1:1234/v1/embeddings
    model: configured-model-name
    dimensions: 768
    batch-size: 64
    timeout-seconds: 60
    api-key-env: LOCAL_EMBEDDING_TOKEN
```

## Ontology keys

| Key | Default | Effect |
|---|---|---|
| `docs-root` | `6-Docs` | Where the governed corpus lives. One directory, or a list of them |
| `catalog-path` | `<first root>/catalog.md` | The one catalog, when it lives somewhere else |
| `excluded-segments` | `archive, node_modules, obj, bin` | Path segments never scanned |
| `excluded-files` | five `DevOps/*` paths | Individual files to skip, relative to a docs root |
| `doc-types` | the 13 defaults | The closed doc-type vocabulary |
| `statuses` | the 4 defaults | The closed status vocabulary |
| `technologies` | empty | The stacks this repository has a coding standard for |
| `base-required-keys` | 6 keys | Required of every document |
| `required-keys` | per-type matrix | Extra keys required by doc-type |
| `catalog.component-column` | `1` | Index of the Component cell in a catalog row |
| `catalog.owner-column` | `6` | Index of the Owner cell in a catalog row |

### Merge semantics

Overrides **replace**, they do not append. Listing one doc-type in `doc-types` gives you a
vocabulary of exactly one. An empty list is a real value and clears the default — which is
how this repository drops the inherited `DevOps/*` exclusions:

```yaml
ontology:
  docs-root: docs
  excluded-files: []
```

Omitting a key entirely leaves the default in place. Under `required-keys`, a doc-type
mapped to an empty value clears that type's extra requirements; unlisted types keep
theirs. An unknown doc-type name there is a configuration error, so a typo cannot silently
disable a requirement.

### Technologies drive three things at once

`technologies` is the list of stacks this repository declares a coding standard for. It is
empty by default, because no repository has a standard until it says what it writes code in,
and a seeded list would scaffold standards for stacks a host does not have.

Declaring one and re-running `docs init`:

1. creates `<docs-root>/standards/<technology>/` with a standard to fill in;
2. publishes `<technology-coding-standard>` in the configuration registry;
3. legalizes `technology: <technology>` in that standard's frontmatter.

One list, so the three cannot disagree — a standard whose folder, registry property and
frontmatter came from separate sources is one that drifts the first time a name changes. A
value is a slug: lowercase letters, digits and single hyphens, because it becomes a directory
name.

## The configuration registry

`config-reg` publishes repository paths under stable names, rendered by `docs init` into the
generated block of the repository root `AGENTS.md`. A portable skill resolves
`<component-catalog>` rather than embedding `../../docs/catalog.md`, so it stays correct in a
repository that puts its documentation somewhere else.

Everything `docs init` creates is **derived**, not stored: the docs root and the declared
technologies produce `<docs-root>`, `<documentation-index>`, `<component-catalog>`,
`<standards-root>`, `<plan-index>`, `<specification-index>`, `<todo-index>`, `<adr-index>`,
`<rules-index>`, `<reference-index>`, and one property per technology. A stored copy would go
stale the day `docs-root` moved, and the operator would be repairing values they never wrote.

Name only what is yours:

```yaml
config-reg:
  auth-design: docs/reference/auth-design.md
  azure-naming-standard: docs/reference/azure-naming-standards.md
```

An addition may reuse a built-in name, which replaces that entry in place — a repository that
keeps its ADRs elsewhere says so here. What it cannot do is remove a name, because a skill
resolving properties by name cannot tell a missing one from a typo. Paths are
repository-relative and must stay inside it; property names are slugs, since a skill types
them by hand.

`docs validate` reports a property pointing at nothing as `KW-CONFIG-REG-001` and a rendered
block that no longer matches configuration as `KW-CONFIG-REG-002`, but only once a repository
has adopted the registry — see [governance](docgraph/governance.md).

### Several documentation roots

Not every repository keeps its documentation in one tree. A component's `README.md` often
lives next to the code it describes, and moving it under `docs/` to bring it under
governance trades away the thing that keeps it honest — its proximity to the code. So
`docs-root` takes a list as readily as a directory:

```yaml
ontology:
  docs-root:
    - docs
    - network_manager
    - automation
    - lab
```

Every root is walked with the same rules, and four things follow from the order:

- **The first root is the primary one.** `docs init` scaffolds into it, and it is the root
  named in the `KW-DOC-SPEC-001` hint that points an author at the ontology reference.
- **The catalog is still one file.** By default `<first root>/catalog.md`; `catalog-path`
  moves it. A `catalog.md` in any other root is an ordinary document contributing no
  vocabulary — a component invented in one module's table must not become valid in every
  other. A catalog outside every root is still parsed and validated as a document, so it
  does not lose its own frontmatter checks by moving.
- **`excluded-files` entries stay relative to a root**, so `vendored/upstream.md` skips
  that file under whichever root it appears in. `excluded-segments` applies everywhere, as
  before.
- **Ids are unique across all roots.** Two roots holding a `README.md` need two ids;
  `KW-DOC-SPEC-006` fails the collision either way, and prefixing with the root —
  `automation/readme` — is the convention that scales.

Roots may overlap. A document under two of them is loaded once, and a duplicate root is
dropped rather than reported. A root that is absolute or escapes the repository is
**`KW-CONFIG-001`**.

`--docs-root` overrides the configured roots for one run and repeats: `--docs-root docs
--docs-root automation`.

### The archive exclusion earns its default

`archive` is excluded because an archived plan is not current guidance, and a corpus that
returns one has actively misled its caller. This pairs with the authority weighting in
[retrieval](docgraph/retrieval.md), which demotes plans and superseded documents that are
still in scope.

## Documentation analysis

The entire `docs-analysis` section is optional. Presets establish the defaults below; the
individual values are advanced overrides rather than a required tuning exercise.

| Key | Default | Effect |
|---|---|---|
| `statuses` | `[current]` | Existing ontology statuses eligible for claim extraction |
| `glossary-path` | `<first-docs-root>/glossary.md` | Managed glossary, always beneath a configured docs root |
| `verdict-confidence` | `0.80` | Minimum imported confidence for a durable classification or suppression |
| `search.mode` | `hybrid` | `graph`, bounded `hybrid`, or explicitly expensive `high-recall` |
| `search.min-claim-tokens` | `5` | Claims below this token count are not compared |
| `search.lexical-candidate-threshold` | `0.45` | Minimum lexical evidence for ordinary candidacy |
| `search.lexical-duplicate-threshold` | `0.90` | Lexical near-duplicate threshold |
| `search.semantic-candidate-threshold` | `0.78` | Minimum semantic evidence for candidacy |
| `search.semantic-duplicate-threshold` | `0.92` | Semantic near-duplicate threshold |
| `search.terminology-context-threshold` | `0.30` | Maximum contextual similarity for divergent senses |
| `search.max-neighbors-per-claim` | `10` | Per-source top-k bound |
| `search.max-code-neighbors` | `50` | Code nodes above this degree are non-discriminating and skipped |
| `search.max-candidates` | `500` | Hard analysis/review candidate cap |

`graph` compares global exact duplicates plus graph neighbors. `hybrid` adds a sparse
corpus-wide inverted-index fallback without an all-pairs scan and is the default.
`high-recall` broadens lexical comparison and, when embeddings are enabled, performs a
global exact cosine first pass. That first pass is explicitly quadratic and is outside
the 10-second default-path target. See [analysis and review](docgraph/analysis.md).

### Embeddings are local, optional, and persistence-gated

`embeddings.mode` is one of:

| Mode | Behavior |
|---|---|
| `off` | Default. Never constructs or invokes an embedding provider. |
| `prefer` | Uses cached/local embeddings when safe; warns and falls back to lexical analysis otherwise. |
| `required` | Treats an unavailable provider or unsafe cache as an operational error. |

When mode is `prefer` or `required`, `endpoint` and `model` are required. `dimensions` is
optional; the compatible request always sends batched string input, the model, and
`encoding_format: float`. `batch-size` defaults to 64 and `timeout-seconds` to 60.
`api-key-env` names an environment variable; it is not the token itself.

The endpoint must be an absolute HTTP(S) URI whose every resolved address is loopback:
`localhost`, the full `127.0.0.0/8` range, or `::1`. Kyber-Weave validates again when the
socket connects and disables redirects, so a local name or response cannot escape to a
remote endpoint. Credentials and headers are not logged or persisted.

Embedding calls are also gated by `.kyber-weave/.gitignore` effectively protecting the
narrow `cache/` path and by the cache not already being tracked. Without that proof,
Kyber-Weave sends no document text. `prefer` falls back; `required` fails. `docs init`
safely merges the ignore entry for new and existing hosts.

## Squad configuration

The `squad:` section governs agent and skill deployment via `kyber-weave squad`.

| Key | Default | Effect |
|---|---|---|
| `bundle` | `full` | The Squad bundle to install (`full` in v1) |
| `version` | `null` | Optional exact version pin; must match the running CLI version |
| `targets` | `[]` | Explicit targets to deploy; non-empty list replaces auto-detection |
| `exclusions` | `[]` | Target harnesses to exclude from deployment |
| `translation` | `best-effort` | Capability translation mode (`best-effort` in v1) |

### Merge and override semantics

- **Targets**: An omitted or empty `targets` list leaves target selection to filesystem auto-detection. Specifying a non-empty `targets` list in configuration disables auto-detection. CLI `--target` flags completely replace configured targets.
- **Exclusions**: Configured exclusions and CLI `--exclude` arguments are combined (unioned) after expanding `all`. Exclusion can only narrow the deployed target set.
- **Version**: When omitted, installation uses the running CLI version. When specified, `squad.version` must match the CLI's semantic `X.Y.Z` version.
- **Validation**: Unrecognized bundles, invalid target tokens, or unsupported translation modes fail immediately with `KW-CONFIG-001`.

## Harness profiles

Harnesses differ in what they can express, so [agent parity](context-hygiene/agents.md)
cannot mean byte equality.

| Key | Effect |
|---|---|
| `directory-name` | The folder this harness reads, e.g. `.cursor` |
| `supports-native-parent-agents` | Whether the harness expresses parent/sub-agent relationships natively |
| `mapped-role-skill-overrides` | Role-name mappings where a harness names the same role differently |

Set these so a legitimate capability difference is not reported as `KW-AGENT-SYNC-002`
drift.

## This repository's own config

Kyber-Weave governs its own documentation with three overrides —
[`.kyber-weave/kyber-weave.yml`](../.kyber-weave/kyber-weave.yml). The docs root moves to
`docs/`, the inherited exclusions are cleared, and one technology is declared: this repository
is C# and nothing else. Everything else is product default, which is the intended shape of a
host config.

## Related

- [The documentation ontology](documentation-ontology.md) — what these keys configure
- [Kyber-Squad architecture](kyber-squad/architecture.md) — deployment and lowering engine
- [Kyber-Squad onboarding](kyber-squad/onboarding.md) — usage and lifecycle guide
- [Agent harness governance](context-hygiene/agents.md) — what the profiles affect
