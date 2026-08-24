---
id: todo/index
title: Todos
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-23
---

# Todos

This directory captures reminders of work not done now — findings during development, deferred fixes, or declined suggestions.

## When to use a todo

A todo is a reminder of work not done now. An agent or contributor that identifies such work, or declines a suggestion rather than acting on it, adds a todo rather than letting it evaporate. A todo is usually the seed that later becomes a spec (when greenfield upfront design is needed) or a plan (when concrete implementation tasks can be sequenced directly) once someone picks it up.

- **Todo**: Captures deferred work, findings, or declined suggestions as seeds for future work.
- **Spec**: Defines requirements, architecture, and design upfront for greenfield or large-scale initiatives.
- **Plan**: Sequences concrete implementation tasks and verification steps once the architecture is known.

## Required frontmatter

Every todo file in this directory must have `doc-type: todo` and include a `component` field indicating the system area:

```yaml
---
id: todo/<descriptive-name>
title: <Human-readable Title>
doc-type: todo
component: <ComponentId>
status: draft
owner: dpalfery
last-reviewed: YYYY-MM-DD
---
```

## Todo inventory

| Todo | Component | Status | Description |
|---|---|---|---|
| [agent-spec-broken-reference-rule.md](agent-spec-broken-reference-rule.md) | ContextHygiene | current | `KW-AGENT-SPEC-004` is documented in the rule reference as "Broken file reference" but no diagnostic with that id is ever raised — implement the check or withdraw the row. |
| [portable-artifacts-carry-project-standards.md](portable-artifacts-carry-project-standards.md) | KyberSquad | draft | Remove project-specific coding standards embedded in the 23 canonical agents and the seven code-review references, now that standards have a home. |
| [kyber-weave-docs-skill-vocabulary.md](kyber-weave-docs-skill-vocabulary.md) | DocGraph | draft | The authoring skill states a doc-type vocabulary missing `todo` and `coding-standard`. |
| [embeddings-endpoint-loopback-check.md](embeddings-endpoint-loopback-check.md) | DocGraph | draft | A non-loopback HTTPS embeddings endpoint is accepted where the test asserts rejection. |
| [kyber-squad-renderer-coverage.md](kyber-squad-renderer-coverage.md) | KyberSquad | draft | Overview of remaining Kyber-Squad renderer target coverage and CLI gaps. |
| [factory.md](factory.md) | KyberSquad | draft | Add a native Factory (factory-droids) renderer to Kyber-Squad. |
| [install-sh-local-origin.md](install-sh-local-origin.md) | Distribution | draft | `install.sh` is HTTPS-only, so the local update loop cannot exercise the first-install channel. |
| [mistyped-release-tag.md](mistyped-release-tag.md) | Distribution | draft | The mistyped `v1.0.6-rc.6` tag outranks every real release in version-sorted resolution. |
| [kilo.md](kilo.md) | KyberSquad | draft | Add a native Kilo renderer to Kyber-Squad. |
| [opencode.md](opencode.md) | KyberSquad | draft | Add a native OpenCode renderer to Kyber-Squad. |
| [squad-install-version-flag.md](squad-install-version-flag.md) | KyberSquad | draft | Add a `--version` flag to squad install and squad update. |
| [squad-path-argument-safety.md](squad-path-argument-safety.md) | KyberSquad | draft | squad commands' positional path argument can silently target the wrong directory. |
| [squad-hardcoded-docs-root.md](squad-hardcoded-docs-root.md) | KyberSquad | draft | Replace the hardcoded `6-Docs` path with a resolvable docs-root across Kyber-Squad's canonical instructions. |
| [warp.md](warp.md) | KyberSquad | draft | Add a native Warp renderer to Kyber-Squad. |
