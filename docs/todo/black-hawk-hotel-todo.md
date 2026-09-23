---
id: todo/black-hawk-hotel-todo
title: Black Hawk Hotel — handover of in-flight harness fidelity work
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: superseded
---

# Black Hawk Hotel — handover of in-flight harness fidelity work

**Superseded by:** [2026-09-21-pi-thinking-and-antigravity-native-agents](../plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md) (Draft)

Tasks A and C from this handover (emit `thinking` for Pi, and reclassify Antigravity from fallback role-skill lowering to a native per-agent target) are now tracked by that plan.

---

This is a **handover, not a plan**. It records what was delivered, what was found but not
built, and what the next agent needs in order to start cold. Three tasks are open; task C is
the large one.

Everything here was verified against a live harness or its installed source on **2026-09-21**.
Re-verify before treating any of it as current — two of the three findings exist precisely
because a previously verified fact went stale.

---

## 1. What is already delivered

Branch `claude/zcode-harness-subagents-e2fff4`, three commits, all 14 declared gates passing.

| Commit | What |
|---|---|
| `65e7cf37` | ZCode added as the eleventh harness target (`ZCodeRenderer`, native subagents + skills, conductor lowered to a slash command) |
| `a56e679e` | Closed a ZCode tool-list widening trap; added the `storage.dir` config tier; lossless block-scalar skill descriptions |
| `ac6a6f90` | Pinned every OpenCode permission key, because an omitted key inherits OpenCode's default-allow |

See [the ZCode plan](../plans/2026-09-21-zcode-harness-target.md) and
[ADR 0021](../adr/0021-zcode-command-lowering-and-resource-relocation.md).

### The machine state this work left behind

A global deployment of **nine targets** was installed from this working tree: `claude`,
`cursor`, `antigravity`, `zcode`, `opencode`, `kilo`, `copilot`, `codex`, `pi` — 1017 files
under one receipt. `squad status --global` reports every file matching.

Three older global receipts were uninstalled first (they were installed from
`/Users/dave/git/personal/kyber-weave`, `/Users/dave`, and `/Users/dave/.gemini`). All 565 of
their files were verified byte-identical to their recorded hashes before removal, so nothing
hand-edited was lost.

**42 pre-existing unmanaged files** were backed up to
`~/.kyber-weave-preinstall-backup/20260921-171012/` before being replaced — 20 Pi agents, 2 Pi
skills, 20 OpenCode agents. They are relevant to task C: they are the evidence for what the
previous generation of tooling emitted.

### Deploying from a working tree

`squad install` renders locally but downloads canonical *source* from a GitHub release matching
the CLI's own assembly version, so a working-tree deployment needs the local release loop:

```bash
./scripts/release-local.sh --version 0.1.0 --out .local-release
python3 scripts/local-release-server.py --root .local-release   # prints a loopback port
KYBER_WEAVE_RELEASE_ORIGIN=http://127.0.0.1:<port> \
  dotnet run --project src/KyberWeave.Cli --no-build -c Release -- \
  squad install --global --target <targets>
```

`KYBER_WEAVE_RELEASE_ORIGIN` accepts loopback authorities only. The version must match
`GetAssemblyVersion()` (currently `0.1.0`), because the release asset name is
`kyber-squad-<version>.zip` and the version is not overridable by design.

---

## 2. Task A — emit `thinking` for Pi

**Status: designed, not built. The working tree is clean; a half-finished edit was reverted
deliberately so the next agent starts from a consistent state.**

### Why

`PiRenderer` emits no reasoning-effort key. Pi supports one. The previously deployed Pi files
carried `reasoningEffort: high`, but that key is inert on Pi (see task B), so the intent was
never actually reaching the harness.

### The verified facts

`@tintinweb/pi-subagents` **0.19.0**, installed at
`~/.pi/agent/npm/node_modules/@tintinweb/pi-subagents`, maps frontmatter in
`src/custom-agents.ts`:

```ts
thinking: str(fm.thinking) as ThinkingLevel | undefined,
```

`ThinkingLevel` is re-exported from `@earendil-works/pi-ai`. The accepted values observed in
the package are `off`, `minimal`, `low`, `medium`, `high`, `max`.

### The design that was chosen

`models.yml` holds **one opaque string per harness**, and each renderer owns how it reads its
own. Cursor already carries bracket options (`gpt-5.6-sol[context=272k,reasoning=high,fast=false]`)
and `CursorRenderer` passes the whole string through verbatim, because Cursor parses the
brackets itself. Pi needs a *separate frontmatter key*, so `PiRenderer` must split the string
rather than pass it through.

So: append an optional `[thinking=<level>]` suffix to the `pi:` value, and have `PiRenderer`
parse it off.

The reverted `models.yml` edit was exactly:

| Profile | `pi:` value |
|---|---|
| `deep-planning` | `zai/glm-5.3[thinking=high]` |
| `reviewer` | `opencode-go/kimi-k2.7-code[thinking=high]` |
| `general` | `opencode/muse-spark-1.3-contributor-free[thinking=medium]` |
| `fast` | `opencode/muse-spark-1.3-contributor-free[thinking=low]` |
| `orchestration` | `inherit` — left alone; the conductor lowers to a Pi skill, which carries no frontmatter model |

Those levels are a proposal, not a requirement from the owner. Confirm them before building.

### Why the half-finished state was dangerous

`PiRenderer.ResolvePiModel` returns the harness string **verbatim**
(`src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs:355-358`). With `models.yml` edited and the
renderer untouched, Pi would receive `model: zai/glm-5.3[thinking=high]` as a literal model id
and fail to resolve it. Land both halves together, or neither.

### What to build

1. `PiRenderer.ResolvePiModel`: split an optional trailing `[thinking=<level>]`, returning the
   bare model id and the level separately.
2. Validate the level against the six accepted values and **fail closed** on anything else —
   an unknown level silently ignored by Pi is the failure mode this renderer already avoids
   elsewhere.
3. Emit `thinking:` only when a level is present. `thinking` is in the accepted key list
   documented in `PiRenderer`'s own remarks, so no other renderer contract changes.
4. `PiRendererContractTests`: pin the split, the six-value domain, the fail-closed path, and
   that a model with no suffix still emits `model` and no `thinking`.
5. Update the `PiRenderer` remarks — they currently list `thinking` among the accepted keys
   this renderer does *not* emit.

---

## 3. Task B — Pi `model` and permissions

**Status: answered, no work required. Recorded so it is not re-investigated.**

**`model`: already emitted.** `PiRenderer` resolves the `pi` harness override and falls back to
the target-neutral `default`, omitting the key when either is `inherit`. Rendered output
carries `model: zai/glm-5.3` today.

**Permissions: already expressed, and they are enforced.** Pi's permission surface is the
`tools` frontmatter key — a closed seven-name vocabulary
(`read, grep, find, ls, edit, write, bash`) that `PiRenderer` always emits, plus
`extensions: false`. There is no separate permission map. Omitting `tools` grants every
built-in, which is why the renderer never omits it.

**The `permission:` block in the previously deployed Pi files was inert.** `custom-agents.ts`
never reads `fm.permission`, `fm.mode`, or `fm.reasoningEffort`; `reasoningEffort` appears zero
times in the whole package. Those files were OpenCode-format agents sitting in Pi's directory —
near-identical to the OpenCode ones, differing only in the model id. They are preserved in the
backup named above if anyone wants to confirm.

The practical consequence: a `permission:` block on a Pi agent looks like it denies things and
does not. `tools` is the only thing Pi enforces.

---

## 4. Task C — Antigravity has native agents; the renderer still lowers every role to a skill

**Status: fully specified, not built. Every open question is closed — see
[antigravity-native-agents.md](antigravity-native-agents.md) for the verified spec.**

`AntigravityRenderer` is a fallback renderer: every canonical role becomes a skill under
`skills/`, `role-`-prefixed on a name clash. Antigravity has had an agent primitive since
before this was noticed, so a delegated Antigravity session cannot load a Squad role *as an
agent* — it can only be told to follow the role's skill. The conductor suffers most, since it
exists to spawn specialists.

The companion page carries the whole contract: both discovery paths, the loading precedence,
the frontmatter schema with value domains, the tool vocabulary and its proposed capability
mapping, the code seam, and the deployment consequence. The headline facts:

| | |
|---|---|
| Global path | `~/.gemini/config/agents/<name>/agent.md` |
| Project path | `<workspace>/.agents/agents/<name>/agent.md` |
| Output shape | a **directory** per agent holding `agent.md` — no existing target does this |
| `model` | a closed tier enum (`inherit`/`flash`/`pro`), not a model id |
| `reasoning_effort` | `minimal`/`low`/`medium`/`high` — no `max` |
| `enable_write_tools` | spans `filesystem.write` **and** `process.execute` in one switch |

Two things a builder should not skim past. The single `enable_write_tools` switch covering both
write and execute means an agent entitled to one but not the other cannot be expressed by the
switch alone and must be narrowed through `tools`. And the capability→tool mapping is inferred
from hand-authored files rather than read from a published schema — the tool names are certain,
the capability each lowers from is a judgement to confirm against a live session.

Verified 2026-09-21 against `agy` 1.2.7 from three independent sources: 21 hand-authored agents
preserved at `~/.gemini/config/agents.bak.20260918/`, the `agy` binary's embedded changelog and
Go struct tags, and the built-in `agy-customizations` skill.

---

## Related

- [Renderer coverage](kyber-squad-renderer-coverage.md) — the eleven-target roster this work
  sits inside
- [ZCode plugin packaging](zcode-plugin-packaging.md) — the other open ZCode decision
- [Claude/Pi ask-narrowing](claude-renderer-ask-narrowing.md) — the `ask` lowering that makes
  `architect` and `product-owner` unable to save their own plans on those harnesses; related in
  spirit to task B
- [Antigravity native agents](antigravity-native-agents.md) — the full verified spec for
  task C, originally written on the PR #96 branch and brought onto this one
