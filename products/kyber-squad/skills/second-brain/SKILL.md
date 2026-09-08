---
name: second-brain
description: Bootstrap or audit a repository's documentation architecture — root AGENTS.md, a canonical 6-Docs/ tree (system/component docs, ADRs, plans, catalog, config registry), and the plan lifecycle that keeps agent and human context durable instead of drifting. Use whenever the user wants to set up a documentation structure for a new or existing repo, audit whether current docs match a standard, mentions "second brain", "config registry", "documentation architecture", "ADR", "plan lifecycle", "catalog", or has skills/agent instructions with project-specific facts hardcoded instead of living in canonical docs. Also trigger when onboarding a repo that has AGENTS.md/CLAUDE.md files but no structured doc tree behind them.
license: MIT
---

# Second Brain: Documentation Architecture

Sets up or audits the *system* that keeps a repo's documentation trustworthy for both humans and agents: one canonical home per fact, a lookup layer so instructions don't hardcode project details, and a lifecycle that migrates in-progress decisions into permanent docs instead of losing them.

This is architecture-level. If the task is writing or updating one component's actual docs (README, onboarding, architecture, requirements), that's a narrower job — check whether this repo already has an `app-docs-standard`-style skill for that and use it instead of duplicating its content here.

## Two modes

**Bootstrap** — the repo has little or no structured documentation. Scaffold the minimum viable version of the architecture below, adapted to this project's name and stack. Read `references/templates.md` for the file skeletons; don't write 300-line documents on day one — start minimal and let it grow.

**Audit** — the repo already has some of this. Walk the checklist below, report what's missing or drifted (a fact duplicated in two places, a skill hardcoding something the registry should own, a plan sitting `Completed` but never archived), and fix only what you're authorized to fix. Don't rewrite what's already correct.

Ask the user which mode applies if it isn't obvious from the request.

## The architecture

| Layer | Purpose | Lives at |
|---|---|---|
| Root policy | Mandatory, repo-wide rules; read before any work | `AGENTS.md` (or `CLAUDE.md`) at repo root |
| Scoped policy | Narrower rules for a subtree; can't weaken root | `AGENTS.md` in subtrees that need it |
| Documentation index | Canonical entry point, links everything below | `6-Docs/README.md` |
| Documentation standard | The rules this checklist enforces — required doc shape, ownership, review cadence | `6-Docs/documentation-standard.md` |
| Component catalog | One row per maintained component: owner, doc path, last-reviewed, status | `6-Docs/catalog.md` |
| Config registry | Flat table: fact name → its one true location. Skills and agents look up facts by name here instead of hardcoding them | a table inside root `AGENTS.md`, or its own file if it grows large |
| ADRs | Durable "why the architecture is this way" records — outlive any single plan | `6-Docs/adr/` |
| Plans | Working documents for one unit of work, with an explicit lifecycle | `6-Docs/plans/`, indexed by `6-Docs/plans/README.md` |
| Archive | Superseded/completed material — never authoritative | `6-Docs/archive/` |

Each layer answers a different question. An agent or human should be able to walk down exactly as far as a task requires without re-reading the whole tree.

## Bootstrap steps

1. Confirm the repo's stack and existing conventions — don't impose an unrelated structure on top of what's already there.
2. Create `6-Docs/README.md` (index), `6-Docs/system/architecture.md` (architecture), `6-Docs/documentation-standard.md` (rules), and `6-Docs/catalog.md` (one row for the first component) from `references/templates.md`.
3. Add or extend root `AGENTS.md`/`CLAUDE.md` with a short config-registry table — even 3-4 entries is a fine start.
4. Create `6-Docs/adr/` and `6-Docs/plans/` (with a `README.md` plan index) even if empty — the structure signals where things go.
5. Report what you created and what the user should fill in next (owner names, first ADR, first cataloged component). Don't invent content you can't verify from the repo.

## Audit checklist

- Does `6-Docs/README.md` actually link to everything under `6-Docs/`? Orphaned files are a discoverability failure.
- Does every row in `catalog.md` point at a doc path that exists, with a plausible last-reviewed date?
- Do any skills or agent instruction files hardcode a fact (a threshold, a path, a naming convention) that appears anywhere else in the repo? That's a config-registry candidate — flag it, don't silently fix a large batch without confirming with the user first.
- Are there `Completed` or `Superseded` plans still sitting in `6-Docs/plans/` instead of `6-Docs/archive/plans/`, with the plan index still listing them as active?
- Does any ADR-worthy decision exist only inside a plan that's about to be archived? Flag it for promotion before the plan goes stale.
- Is anything in `6-Docs/archive/` being linked to or treated as current guidance elsewhere? That's a correctness bug, not a style nit.

## Plan lifecycle (reference for both modes)

`Draft → Ready → In progress → Blocked → Review required → Completed/Superseded → Archived`. Only `Ready`, `In progress`, and `Blocked` are implementation-actionable; `Draft` is planning-only; everything after `Review required` is historical. A plan does not archive itself — closing it out means verifying its acceptance criteria against what was actually built, promoting anything durable (a new convention, a registry entry, an ADR-worthy decision) into canonical docs, and only then moving it to `6-Docs/archive/plans/` with status `Archived`. See `references/templates.md` for a minimal plan template if bootstrapping.

## Where the agents fit (reference only — don't embed their instructions here)

This skill defines the paper trail, not who walks it. In a project with specialist agents, expect roughly this division of labor, and point to whatever the project's actual agents are named rather than assuming these exact names:

- A **planning/architect agent** does the discovery and decision-making that produces a plan, and is generally the one best positioned to flag which of its own decisions are ADR-worthy on close-out.
- A **documentation specialist agent** (e.g. this repo's `docs-dev`) owns writing the canonical docs themselves and performing plan closeout — verifying acceptance criteria, updating docs, maintaining the plan index, archiving.
- An **orchestrator/conductor skill** sequences the above rather than doing the work itself — the project's own orchestration skill (often named `conductor` or a versioned variant) is the right thing to invoke for any non-trivial documentation-architecture change that spans multiple files.

If the project has no such agents yet, this skill still applies — a human just plays those roles manually.

## Extensibility note

Future versions of this skill may source templates and conventions from a Palfery Agent Package Manager (APM) package instead of the local `references/templates.md`. Nothing to do about that yet — just don't build anything here that assumes a fixed local-only template path if it can reasonably be avoided.
