---
id: todo/index
title: Todos
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-24
---

# Todos

This directory captures reminders of work not done now — findings during development, deferred fixes, or declined suggestions.

Every todo document under `docs/todo/` must be reachable from this inventory. `docs validate` reports one that is not as `KW-DOC-LIFECYCLE-002`.

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

### Open

Ranked by impact on the product: broken user-facing behaviour first, then release and
verification gaps, then content migration and hygiene.

| # | Todo | Component | Description |
|---|---|---|---|
| 1 | [claude-renderer-ask-narrowing.md](claude-renderer-ask-narrowing.md) | KyberSquad | Claude, Pi and ZCode narrow `ask` to deny, so the rendered `architect` and `product-owner` cannot save their plans and specs on those harnesses — the conductor's plan and spec paths stop at their first write. |
| 2 | [canon-multi-harness-session-gather.md](canon-multi-harness-session-gather.md) | KyberDash | Sessions whose canonical key spans several harnesses get a `${harness}:${key}` id that the record gather never matches, so they silently contribute no findings or run outcomes. |
| 3 | [kyberdash-local-release-loop.md](kyberdash-local-release-loop.md) | Distribution | The local release loop builds no `kyberdash` or tray, so their install and update paths are unverifiable offline. |
| 4 | [kyber-weave-docs-skill-vocabulary.md](kyber-weave-docs-skill-vocabulary.md) | DocGraph | The authoring skill's doc-type list lacks `todo` and `coding-standard`, so agents label a coding standard `reference`. |
| 5 | [install-sh-local-origin.md](install-sh-local-origin.md) | Distribution | `install.sh` is HTTPS-only, so the local update loop cannot exercise the first-install channel. |
| 6 | [portable-artifacts-carry-project-standards.md](portable-artifacts-carry-project-standards.md) | KyberSquad | Remove project-specific coding standards embedded in the canonical agents and the seven code-review references; `squad install` does not deploy the standards templates. |
| 7 | [squad-install-version-flag.md](squad-install-version-flag.md) | KyberSquad | Add a `--version` flag to `squad install` and `squad update`. |
| 8 | [stale-refresh-run-rows.md](stale-refresh-run-rows.md) | KyberDash | `refresh_run` rows stay `running` forever after their refresh process dies; diagnostic-only, not on the report data path. |
| 9 | [agent-spec-broken-reference-rule.md](agent-spec-broken-reference-rule.md) | ContextHygiene | `KW-AGENT-SPEC-004` is documented as "Broken file reference" but never raised — implement the check or withdraw the row. |
| 10 | [migrate-skill-resources-into-standards.md](migrate-skill-resources-into-standards.md) | KyberSquad | Migrate retained skill-resource knowledge content-by-content into standards, governed documentation, or another verified durable home. |
| 11 | [canon-test-fixture-consolidation.md](canon-test-fixture-consolidation.md) | KyberDash | `findings.test.ts` and `sessions.test.ts` duplicate the `CanonicalRecord` fixture helpers `tokens` and `turn`; test hygiene, no behaviour is wrong. |
| 12 | [shell-implies-write-live-verification-other-targets.md](shell-implies-write-live-verification-other-targets.md) | KyberSquad | `capability-not-isolable` on Claude, Pi, ZCode, Factory and OpenCode rests on code inspection; live verification would confirm it and could unlock withheld Antigravity tools. |
| 13 | [zcode-plugin-packaging.md](zcode-plugin-packaging.md) | KyberSquad | Packaging Squad as a ZCode plugin trades per-file receipt ownership for per-unit enablement — a deployment-model decision, not remaining ZCode work. |
| 14 | [kyberdash-windows-code-signing.md](kyberdash-windows-code-signing.md) | Distribution | The Windows tray installer ships without Authenticode signing, so SmartScreen warns; needs a certificate and a signing step. |
| 15 | [kyberdash-linux-tray.md](kyberdash-linux-tray.md) | KyberDash | The tray ships for macOS and Windows only; `kyberdash menubar` on Linux exits 1. |

### Closed

Archived under [`archive/todo/`](../archive/todo/). Kept for provenance, never current guidance.

| Todo | Component | Status | Description |
|---|---|---|---|
| [embeddings-endpoint-loopback-check.md](../archive/todo/embeddings-endpoint-loopback-check.md) | DocGraph | superseded | A non-loopback HTTPS embeddings endpoint was accepted where the test asserts rejection (fixed: the loader now requires loopback resolution). |
| [kyber-squad-renderer-coverage.md](../archive/todo/kyber-squad-renderer-coverage.md) | KyberSquad | superseded | Kyber-Squad renderer target coverage (complete: all eleven targets have renderers; the CLI gaps have their own todos). |
| [factory.md](../archive/todo/factory.md) | KyberSquad | superseded | Add a native Factory (factory-droids) renderer to Kyber-Squad (superseded by [plan](../archive/plans/2026-09-14-factory-native-renderer.md)). |
| [menu-bar-fix.md](../archive/todo/menu-bar-fix.md) | KyberDash | superseded | The tray popover hung on its loading state and the status glyph was blank — delivered by the [menu-bar runtime wiring plan](../archive/plans/2026-09-20-kyberdash-menu-bar-runtime-wiring.md); owner confirmed the deployed popover, selector, and glyph 2026-09-22. |
| [kilo.md](../archive/todo/kilo.md) | KyberSquad | superseded | Add a native Kilo renderer to Kyber-Squad (completed via [plan](../archive/plans/2026-09-14-kilo-native-renderer.md)). |
| [opencode.md](../archive/todo/opencode.md) | KyberSquad | superseded | Add a native OpenCode renderer to Kyber-Squad (superseded by [plan](../archive/plans/2026-09-14-opencode-native-renderer.md)). |
| [squad-hardcoded-docs-root.md](../archive/todo/squad-hardcoded-docs-root.md) | KyberSquad | superseded | Replace the hardcoded `6-Docs` path with a resolvable docs-root across Kyber-Squad's canonical instructions (completed: `second-brain` now resolves **<docs-root>** through the Config Reg). |
| [squad-path-argument-safety.md](../archive/todo/squad-path-argument-safety.md) | KyberSquad | superseded | `squad` commands' positional path argument could silently target the wrong directory (superseded by [plan](../archive/plans/2026-09-23-squad-path-argument-safety.md); delivered via PR #113). |
| [harness-specific-instruction-inserts.md](../archive/todo/harness-specific-instruction-inserts.md) | KyberSquad | superseded | Harness-conditional blocks in canonical agent instructions (motivating case gone: the unified conductor makes every role headless on every harness). |
| [warp.md](../archive/todo/warp.md) | KyberSquad | superseded | Add a native Warp renderer to Kyber-Squad (superseded by WarpRenderer implementation). |
| [black-hawk-hotel-todo.md](../archive/todo/black-hawk-hotel-todo.md) | KyberSquad | superseded | Handover of in-flight harness fidelity work: emit `thinking` for Pi, and reclassify Antigravity from fallback role-skills to its native agent primitive (superseded by [plan](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md)). |
| [antigravity-native-agents.md](../archive/todo/antigravity-native-agents.md) | KyberSquad | superseded | Antigravity has a native agent primitive at `agents/<name>/agent.md`, but the renderer still lowers every role to a skill; the verified format, paths and capability mapping are recorded (superseded by [plan](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md)). |
| [antigravity-capability-verification-evidence.md](../archive/todo/antigravity-capability-verification-evidence.md) | KyberSquad | superseded | Live verification evidence for the Antigravity capability→tool mapping and model enum domains against agy 1.2.7; part of plan C0 gate per [2026-09-21-pi-thinking-and-antigravity-native-agents.md](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md). |
| [docs-validate-todo-index-gap.md](../archive/todo/docs-validate-todo-index-gap.md) | DocGraph | superseded | `docs validate` passed when a todo had no row in this index (fixed: `KW-DOC-LIFECYCLE-002`, emitted by `TodoInventoryValidator`). |
| [pi.md](../archive/todo/pi.md) | KyberSquad | superseded | Add Pi as a Kyber-Squad harness target (delivered: `PiRenderer`). |
| [claude-code.md](../archive/todo/claude-code.md) | KyberSquad | archived | Add a native Claude Code renderer to Kyber-Squad (delivered: `ClaudeRenderer`). |
| [analyzer-debt-ca1307-1308-1716.md](../archive/todo/analyzer-debt-ca1307-1308-1716.md) | CI Pipelines | archived | Fix pre-existing CA analyzer debt. |
| [mistyped-release-tag.md](../archive/todo/mistyped-release-tag.md) | Distribution | superseded | The mistyped `v1.0.6-rc.6` tag outranked every real release (fixed: tag deleted, dispatch mints versions from existing tags, and both resolvers take the highest SemVer). |
| [macos-developer-id-signing.md](../archive/todo/macos-developer-id-signing.md) | Distribution | superseded | Developer ID signing and notarization (closed: the macOS tray ships signed and notarized from `0.1.7-rc.13`; CLI binary signing not pursued). |
