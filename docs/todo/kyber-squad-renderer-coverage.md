---
id: todo/kyber-squad-renderer-coverage
title: Kyber-Squad Renderer Coverage
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# Kyber-Squad renderer coverage — what's left

`squad install`/`squad update` render canonical Squad source into a harness's native files
through `ISquadRenderer` (see [architecture.md §8](../kyber-squad/architecture.md#8-rendering)).
`copilot` (native), `cursor` (native), `claude` (native), `codex` (native), `antigravity` (fallback role-skill lowering to `.agents/skills/`), `opencode` (native), `kilo` (native), `pi` (native, with the conductor lowered to a skill), `factory` (native), `warp` (fallback role-skill lowering to `.warp/skills/`), and `zcode` (native, with the conductor lowered to a slash command) have
renderers today. All eleven declared harness targets are covered.

## Coverage Status

| Target | Kind | Status | Implementation / Page |
|---|---|---|---|
| `copilot` | Native | Completed | `CopilotRenderer` (`src/KyberWeave.Core/Squad/Rendering/CopilotRenderer.cs`) |
| `cursor` | Native | Completed | `CursorRenderer` (`src/KyberWeave.Core/Squad/Rendering/CursorRenderer.cs`) |
| `claude` | Native | Completed | `ClaudeRenderer` (`src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs`) |
| `codex` | Native | Completed | `CodexRenderer` (`src/KyberWeave.Core/Squad/Rendering/CodexRenderer.cs`) |
| `antigravity` | Fallback (role-skill lowering) | Completed | `AntigravityRenderer` (`src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs`) |
| `opencode` | Native | Completed | `OpenCodeRenderer` (`src/KyberWeave.Core/Squad/Rendering/OpenCodeRenderer.cs`) |
| `kilo` | Native | Completed | `KiloRenderer` (`src/KyberWeave.Core/Squad/Rendering/KiloRenderer.cs`) · [kilo.md](kilo.md) |
| `pi` | Native (conductor lowered to skill) | Completed | `PiRenderer` (`src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs`) · [archived todo](../archive/todo/pi.md) |
| `factory` | Native | Completed | `FactoryRenderer` (`src/KyberWeave.Core/Squad/Rendering/FactoryRenderer.cs`) · [factory.md](factory.md) |
| `warp` | Fallback (role-skill lowering) | Completed | `WarpRenderer` (`src/KyberWeave.Core/Squad/Rendering/WarpRenderer.cs`) · [archived todo](../archive/todo/warp.md) |
| `zcode` | Native (conductor lowered to slash command) | Completed | `ZCodeRenderer` (`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs`) · [ADR 0020](../adr/0020-zcode-command-lowering-and-resource-relocation.md) · [deferred primitives](zcode-deferred-primitives.md) |

### Target Checklist

- [x] `copilot` — `CopilotRenderer` (`.github/agents/*.agent.md`, `.github/skills/*/SKILL.md`)
- [x] `cursor` — `CursorRenderer` (`.cursor/agents/*.md`, `.cursor/skills/*/SKILL.md`)
- [x] `claude` — `ClaudeRenderer` (`.claude/agents/*.md`, `.claude/skills/*/SKILL.md`)
- [x] `codex` — `CodexRenderer` (`.codex/agents/*.toml`, `.codex/skills/*/SKILL.md`)
- [x] `antigravity` — `AntigravityRenderer` (`.agents/skills/role-*/SKILL.md`, `.agents/skills/*/SKILL.md`)
- [x] `opencode` — `OpenCodeRenderer` (`.opencode/agents/*.md`, `.opencode/skills/*/SKILL.md`)
- [x] `kilo` — `KiloRenderer` (`.kilo/agents/*.md`, `.kilo/skills/*/SKILL.md`)
- [x] `pi` — `PiRenderer` (`.pi/agents/*.md`, `.pi/skills/*/SKILL.md`; conductor as skill)
- [x] `factory` — `FactoryRenderer` (`.factory/droids/*.md`, `.factory/skills/*/SKILL.md`; `--global` under `~/.factory`)
- [x] `warp` — `WarpRenderer` (`.warp/skills/role-*/SKILL.md`, `.warp/skills/*/SKILL.md`)
- [x] `zcode` — `ZCodeRenderer` (`.zcode/agents/*.md`, `.zcode/skills/*/SKILL.md`, `.zcode/commands/*.md`; `--global` under `$ZCODE_STORAGE_DIR`, default `~/.zcode`)

All eleven declared targets are covered. `zcode` is covered by `ZCodeRenderer`
(`src/KyberWeave.Core/Squad/Rendering/ZCodeRenderer.cs`); the ZCode primitives it deliberately
does not deploy are listed in [zcode-deferred-primitives.md](zcode-deferred-primitives.md).

`kyber-weave squad doctor` reports current renderer coverage against this same roster.

## Other known gaps

Found while verifying the Copilot renderer end-to-end, not renderer-coverage gaps:

| Gap | Page |
|---|---|
| `squad install`/`squad update` have no `--version` flag — they can only install whatever release matches the running CLI's own build | [squad-install-version-flag.md](squad-install-version-flag.md) |
| `squad` commands' `path` is a positional argument, not `--path` — a plausible flag guess silently defaults to the current directory instead of erroring | [squad-path-argument-safety.md](squad-path-argument-safety.md) |
| ZCode's global root can be moved by `~/.zcode/cli/config.json`, which the environment-only resolver cannot read | [zcode-storage-dir-config-override.md](zcode-storage-dir-config-override.md) |
