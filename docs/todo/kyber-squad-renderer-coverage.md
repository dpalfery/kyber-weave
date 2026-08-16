---
id: todo/kyber-squad-renderer-coverage
title: Kyber-Squad Renderer Coverage
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# Kyber-Squad renderer coverage — what's left

`squad install`/`squad update` render canonical Squad source into a harness's native files
through `ISquadRenderer` (see [architecture.md §8](../kyber-squad/architecture.md#8-rendering)).
Only Copilot has a renderer today; every other approved target fails in preflight, before any
network call, naming the gap and pointing here.

Each page below is **context for planning that target's renderer, not a plan** — what's known
from the canonical source and the codebase, what's assumed and needs verifying against that
harness's real documentation, the code seam to implement against, and how to verify the result.

| Target | Kind | Page |
|---|---|---|
| `codex` | Native | [codex.md](codex.md) |
| `cursor` | Native | [cursor.md](cursor.md) |
| `claude` | Native | [claude-code.md](claude-code.md) |
| `opencode` | Native | [opencode.md](opencode.md) |
| `kilo` | Native | [kilo.md](kilo.md) |
| `factory` | Native | [factory.md](factory.md) |
| `gemini` | Fallback (role-skill lowering) | [gemini.md](gemini.md) |
| `antigravity` | Fallback (role-skill lowering) | [antigravity.md](antigravity.md) |
| `warp` | Fallback (role-skill lowering) | [warp.md](warp.md) |

`copilot` is covered by `CopilotRenderer`
(`src/KyberWeave.Core/Squad/Rendering/CopilotRenderer.cs`) and is not listed here — it's the
one target this doesn't need to be written for.

`kyber-weave squad doctor` reports current renderer coverage against this same roster.

## Other known gaps

Found while verifying the Copilot renderer end-to-end, not renderer-coverage gaps:

| Gap | Page |
|---|---|
| `squad install`/`squad update` have no `--version` flag — they can only install whatever release matches the running CLI's own build | [squad-install-version-flag.md](squad-install-version-flag.md) |
| `squad` commands' `path` is a positional argument, not `--path` — a plausible flag guess silently defaults to the current directory instead of erroring | [squad-path-argument-safety.md](squad-path-argument-safety.md) |
