---
id: todo/kyber-squad-renderer-coverage
title: Kyber-Squad Renderer Coverage
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-14
status: draft
---

# Kyber-Squad renderer coverage — what's left

`squad install`/`squad update` render canonical Squad source into a harness's native files
through `ISquadRenderer` (see [architecture.md §8](../kyber-squad/architecture.md#8-rendering)).
`copilot` (native), `cursor` (native), `claude` (native), `codex` (native), `antigravity` (fallback role-skill lowering to `.agents/skills/`), `opencode` (native), and `kilo` (native) have
renderers today; every other approved target fails in preflight, before any network call,
naming the gap and pointing here.

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
| `factory` | Native | Pending | [factory.md](factory.md) |
| `warp` | Fallback (role-skill lowering) | Pending | [warp.md](warp.md) |
| `pi` | Fallback (role-skill lowering); not yet declared in the target catalog | Pending | [pi.md](pi.md) |

### Target Checklist

- [x] `copilot` — `CopilotRenderer` (`.github/agents/*.agent.md`, `.github/skills/*/SKILL.md`)
- [x] `cursor` — `CursorRenderer` (`.cursor/agents/*.md`, `.cursor/skills/*/SKILL.md`)
- [x] `claude` — `ClaudeRenderer` (`.claude/agents/*.md`, `.claude/skills/*/SKILL.md`)
- [x] `codex` — `CodexRenderer` (`.codex/agents/*.toml`, `.codex/skills/*/SKILL.md`)
- [x] `antigravity` — `AntigravityRenderer` (`.agents/skills/role-*/SKILL.md`, `.agents/skills/*/SKILL.md`)
- [x] `opencode` — `OpenCodeRenderer` (`.opencode/agents/*.md`, `.opencode/skills/*/SKILL.md`)
- [x] `kilo` — `KiloRenderer` (`.kilo/agents/*.md`, `.kilo/skills/*/SKILL.md`)
- [ ] `factory` — Pending ([factory.md](factory.md))
- [ ] `warp` — Pending ([warp.md](warp.md))
- [ ] `pi` — Pending ([pi.md](pi.md))

Each page for pending targets is **context for planning that target's renderer, not a plan** — what's known
from the canonical source and the codebase, what's assumed and needs verifying against that
harness's real documentation, the code seam to implement against, and how to verify the result.

`kyber-weave squad doctor` reports current renderer coverage against this same roster.

## Other known gaps

Found while verifying the Copilot renderer end-to-end, not renderer-coverage gaps:

| Gap | Page |
|---|---|
| `squad install`/`squad update` have no `--version` flag — they can only install whatever release matches the running CLI's own build | [squad-install-version-flag.md](squad-install-version-flag.md) |
| `squad` commands' `path` is a positional argument, not `--path` — a plausible flag guess silently defaults to the current directory instead of erroring | [squad-path-argument-safety.md](squad-path-argument-safety.md) |
