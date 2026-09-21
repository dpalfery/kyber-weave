---
id: todo/zcode-skill-description-block-scalar
title: A ZCode description containing a double quote round-trips with the backslash visible
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# A ZCode description containing a double quote round-trips with the backslash visible

`ZCodeRenderer` emits frontmatter scalars exactly as ZCode's own writer does
(`services/src/subagents/subagentMarkdown.ts`): a plain scalar when safe, otherwise a
double-quoted one with `\\`, `\"`, `\n`, and `\r` escaped.

ZCode's readers do not unescape. Both `unquoteScalar` (agents and commands) and the skill
adapter's `parseScalar` slice the outer quote pair and stop, so a description containing a
literal `"` is held by the harness with the backslash still attached. Apostrophes are
unaffected, because they need no escaping inside double quotes.

This is upstream's own round-trip behaviour for its own output, so the renderer reproduces it
rather than inventing an encoding only ZCode's reader would accept. It is pinned by
`ZCodeRendererContractTests.RenderAsync_ZCode_EscapesEmbeddedQuotesTheWayZCodeEscapesItsOwn`.

One canonical description is affected today: `second-brain`, which quotes the phrases it
triggers on. The effect is cosmetic — the description reads `mentions \"second brain\"` — and
does not change routing.

## The lossless alternative, for skills only

The skill frontmatter parser supports block scalars (`parseBlockScalarStyle` / `readBlockScalar`
in `adapters/src/skills/index.ts`), so a skill description could be emitted as

```yaml
description: >-
  … mentions "second brain", "config registry" …
```

with no quoting and no escaping at all. **The agent parser does not**: a source comment there
records that the agent side reads only the top-level `description: >` and skips the indented
continuation, which is why every agent and command value must stay a single physical line.

Adopting this would mean two emission styles in one renderer, for one cosmetic artifact in one
of 24 skills. Worth doing only if a future canonical description makes the artifact
user-visible in a way that matters.

Verified against [`zai-org/ZCode`](https://github.com/zai-org/ZCode) **3.14.0** on 2026-09-21.
