---
id: todo/index
title: Todos
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-22
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
| [migrate-skill-resources-into-standards.md](migrate-skill-resources-into-standards.md) | KyberSquad | draft | Preserve and package retained skill resources while migrating their knowledge content-by-content into standards, governed documentation, or another verified durable canonical home. |
| [portable-artifacts-carry-project-standards.md](portable-artifacts-carry-project-standards.md) | KyberSquad | draft | Remove project-specific coding standards embedded in the 21 canonical agents and the seven code-review references, now that standards have a home. |
| [kyber-weave-docs-skill-vocabulary.md](kyber-weave-docs-skill-vocabulary.md) | DocGraph | draft | The authoring skill states a doc-type vocabulary missing `todo` and `coding-standard`. |
| [embeddings-endpoint-loopback-check.md](embeddings-endpoint-loopback-check.md) | DocGraph | draft | A non-loopback HTTPS embeddings endpoint is accepted where the test asserts rejection. |
| [kyber-squad-renderer-coverage.md](kyber-squad-renderer-coverage.md) | KyberSquad | draft | Overview of remaining Kyber-Squad renderer target coverage and CLI gaps. |
| [factory.md](factory.md) | KyberSquad | superseded | Add a native Factory (factory-droids) renderer to Kyber-Squad (superseded by [plan](../archive/plans/2026-09-14-factory-native-renderer.md)). |
| [install-sh-local-origin.md](install-sh-local-origin.md) | Distribution | draft | `install.sh` is HTTPS-only, so the local update loop cannot exercise the first-install channel. |
| [kyberdash-local-release-loop.md](kyberdash-local-release-loop.md) | Distribution | draft | The local release loop builds no `kyberdash`, so the KyberDash install and update path is unverifiable offline. |
| [menu-bar-fix.md](menu-bar-fix.md) | KyberDash | superseded | The tray popover hung on its loading state and the status glyph was blank — delivered by the [menu-bar runtime wiring plan](../archive/plans/2026-09-20-kyberdash-menu-bar-runtime-wiring.md); owner confirmed the deployed popover, selector, and glyph 2026-09-22. |
| [stale-refresh-run-rows.md](stale-refresh-run-rows.md) | KyberDash | draft | `refresh_run` rows stay `running` forever after their refresh process dies (observed on the deployed build 2026-09-21/22); diagnostic-only today, not on the report data path. |
| [macos-developer-id-signing.md](macos-developer-id-signing.md) | Distribution | draft | Every macOS binary ships ad-hoc signed; set up team `J2UNNQ466J` Developer ID and notarization credentials (needed first by the KyberDash tray) and sign the CLI binaries. |
| [mistyped-release-tag.md](mistyped-release-tag.md) | Distribution | draft | The mistyped `v1.0.6-rc.6` tag outranks every real release in version-sorted resolution. |
| [kilo.md](kilo.md) | KyberSquad | superseded | Add a native Kilo renderer to Kyber-Squad (completed via [plan](../plans/2026-09-14-kilo-native-renderer.md)). |
| [opencode.md](opencode.md) | KyberSquad | superseded | Add a native OpenCode renderer to Kyber-Squad (superseded by [plan](../plans/2026-09-14-opencode-native-renderer.md)). |
| [claude-renderer-ask-narrowing.md](claude-renderer-ask-narrowing.md) | KyberSquad | draft | Claude and Pi both narrow `ask` to deny, so the rendered `architect` and `product-owner` cannot save their plans and specs on those harnesses. |
| [squad-install-version-flag.md](squad-install-version-flag.md) | KyberSquad | draft | Add a `--version` flag to squad install and squad update. |
| [squad-path-argument-safety.md](squad-path-argument-safety.md) | KyberSquad | draft | squad commands' positional path argument can silently target the wrong directory. |
| [squad-global-multi-target-receipt-collision.md](squad-global-multi-target-receipt-collision.md) | KyberSquad | draft | A global install of `cursor,antigravity` fails at receipt write because the receipt keys files by relative path alone; the dry run does not predict it. |
| [squad-global-receipt-bound-to-cwd.md](squad-global-receipt-bound-to-cwd.md) | KyberSquad | draft | A global receipt is bound to the `[path]` argument (default: current directory), so a deployment made from a worktree is orphaned when the worktree goes. |
| [squad-global-status-and-uninstall-roots.md](squad-global-status-and-uninstall-roots.md) | KyberSquad | draft | `status -g` reports installed files missing and `uninstall -g --dry-run` reports nothing to remove; directories are left behind. |
| [antigravity-native-agents.md](antigravity-native-agents.md) | KyberSquad | draft | `agy` 1.2.2 has native agents (`~/.gemini/config/agents/<name>/agent.md`), but the Antigravity renderer still lowers every role to a skill. |
| [squad-hardcoded-docs-root.md](squad-hardcoded-docs-root.md) | KyberSquad | draft | Replace the hardcoded `6-Docs` path with a resolvable docs-root across Kyber-Squad's canonical instructions. |
| [warp.md](../archive/todo/warp.md) | KyberSquad | superseded | Add a native Warp renderer to Kyber-Squad (superseded by WarpRenderer implementation). |
