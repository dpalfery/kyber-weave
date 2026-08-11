---
id: configuration
title: Configuration
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-08-04
code-refs:
  - KyberWeaveConfigLoader
  - OntologyConfig
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
              runbook, reference, rule, governance, index]
  statuses: [current, draft, needs-review, superseded]
  base-required-keys: [id, title, owner, last-reviewed, doc-type, status]
  required-keys:
    architecture: [component]
    onboarding: [component, source-root]
  catalog:
    component-column: 1
    owner-column: 6

harness:
  profiles:
    cursor:
      directory-name: .cursor
      supports-native-parent-agents: false
      mapped-role-skill-overrides:
        reviewer: code-review
```

## Ontology keys

| Key | Default | Effect |
|---|---|---|
| `docs-root` | `6-Docs` | Where the governed corpus lives. One directory, or a list of them |
| `catalog-path` | `<first root>/catalog.md` | The one catalog, when it lives somewhere else |
| `excluded-segments` | `archive, node_modules, obj, bin` | Path segments never scanned |
| `excluded-files` | five `DevOps/*` paths | Individual files to skip, relative to a docs root |
| `doc-types` | the 11 defaults | The closed doc-type vocabulary |
| `statuses` | the 4 defaults | The closed status vocabulary |
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

Kyber-Weave governs its own documentation with exactly two overrides —
[`.kyber-weave/kyber-weave.yml`](../.kyber-weave/kyber-weave.yml). The docs root moves to
`docs/`, and the inherited exclusions are cleared. Everything else is product default,
which is the intended shape of a host config.

## Related

- [The documentation ontology](documentation-ontology.md) — what these keys configure
- [Agent harness governance](context-hygiene/agents.md) — what the profiles affect
