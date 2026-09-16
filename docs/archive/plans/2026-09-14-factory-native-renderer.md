---
id: archive/plans/2026-09-14-factory-native-renderer
title: Implement native Factory (factory-droids) renderer
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-14
component: KyberSquad
---

# Implement native Factory (factory-droids) renderer

**Status:** Archived  
**Date:** 2026-09-14  
**Goal:** Implement and register an `ISquadRenderer` for `SquadTarget.Factory` (`factory` /
`factory-droids`) so `kyber-weave squad install --target factory` succeeds with native
Factory agent and skill layouts, without inventing an unverified permission mapping.

## Decisions

| Id | Decision |
|---|---|
| D1 | Claim `SquadTarget.Factory`; project-scope agents at `.factory/agents/<name>.md`, skills at `.factory/skills/<name>/SKILL.md`. |
| D2 | Frontmatter emits `name`, `description`, and optional `model` only. Markdown-with-YAML shape matches sibling native Markdown harnesses; Factory's public schema was not verified on 2026-09-14. |
| D3 | No `tools` / `permission` / `permissions` field. Unverified mapping would risk silent widening. |
| D4 | Record `permission-not-expressible` for every agent whose capability profile has any non-deny decision (Codex pattern / Copilot degradation-over-guessing). |
| D5 | Model resolution uses `models.yml` harness key `factory`; omit when missing/`inherit` (today no `factory:` entries). |
| D6 | Suppress skill projections for profile-declared shared identities (single-projection rule). |
| D7 | Register in `SquadCommandComposition.ResolveRenderer()`; add registry path helpers only — no coverage/dispatch reimplementation. |

## Verification

- Contract tests render `products/kyber-squad` and assert against the loaded `SquadSource`.
- `squad doctor` lists `factory` under renderers available.
- Declared gate suite passes.
