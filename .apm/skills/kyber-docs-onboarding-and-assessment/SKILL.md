---
name: kyber-docs-onboarding-and-assessment
description: "Bootstraps or audits a repository's documentation architecture — root AGENTS.md, canonical tree, catalog, config registry, and plan lifecycle. Also runs owner-started assessment and remediation of archived plans and specs, classifying each as harvested, partial, or not harvested and grouping docs-dev fixes into a batch delivered as a pull request; and runs the onboarding step a stopped run sends the operator to. Use when setting up documentation for a new or existing repo, auditing whether docs match a standard, mentioning config registry, documentation architecture, ADR, plan lifecycle, or catalog, when project facts are hardcoded in skills instead of canonical docs, when a run reports the repository's onboarding setting is unset, or when the owner asks to assess archived plans and specs for harvest gaps. Not for authoring a single governed document — that is kyber-docs-authoring."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Documentation architecture, onboarding, and assessment

Sets up or audits the *system* that keeps a repo's documentation trustworthy for both humans and agents: one canonical home per fact, a lookup layer so instructions don't hardcode project details, and a lifecycle that migrates in-progress decisions into permanent docs instead of losing them.

This is architecture-level. If the task is writing or updating one component's actual docs (README, onboarding, architecture, requirements), that's a narrower job — check whether this repo already has an `app-docs-standard`-style skill for that and use it instead of duplicating its content here.

## Three modes

**Bootstrap** — the repo has little or no structured documentation. Scaffold the minimum viable version of the architecture below, adapted to this project's name and stack. Read [references/templates.md](references/templates.md) for the file skeletons; don't write 300-line documents on day one — start minimal and let it grow.

**Audit** — the repo already has some of this. Walk the checklist below, report what's missing or drifted (a fact duplicated in two places, a skill hardcoding something the registry should own, a plan sitting `Completed` but never archived), and fix only what you're authorized to fix. Don't rewrite what's already correct.

**Onboarding** — the repo has the scaffolding but has never chosen which technologies, gates and standards it wants. See [Onboarding mode](#onboarding-mode-the-choice-a-run-stops-for) below; a stopped run sends the operator here.

**Assessment and remediation** — owner-started: assess archived plans and specs for harvest gaps, report for owner picks, then remediate in batches. See below.

Ask the user which mode applies if it isn't obvious from the request.

## Onboarding mode (the choice a run stops for)

A repository that has never answered the onboarding question records no `onboarding:` value in `.kyber-weave/kyber-weave.yml`, and `kyber-weave run start` stops before it checks anything else. This mode is what answers it. It runs once per repository.

**What makes the question worth asking.** A gate suite and a set of coding standards arrived from defaults — the ones `docs init` wrote — and nobody has looked at them. Every later run is held to those choices, so the stop happens before the work rather than at the end of the first run.

1. **Find the technologies.** Read `ontology.technologies` from `.kyber-weave/kyber-weave.yml`; that list is the declared set. Mark each one whose standard exists under the standards folder (**<standards-root>**, from the registry), so a technology declared with no standard is visible as a gap rather than as a default.
2. **Show each technology's default.** For the gates, run `kyber-weave review catalogue . --technology <slug>`: it prints the exact `review:` block a scaffold would write, and writes nothing, so accepting a default and changing one cost the same. For the finish commands, read that technology's standard `## Completion` section through `docs_get_coding_standard_for_technology(technology, topic: "completion")`. If the repository holds no standard for it yet, show what `kyber-weave docs init . --kyber-standards` would scaffold instead of inventing one.
3. **Ask, per technology, whether to accept or change it.** Present the gates and the finish commands together, because they answer one question: what does a worker run here, and how does it finish. Record a change as a concrete edit — a gate's `include`, a command's argv, a line in the `## Completion` section — not as an intention.
4. **Write the answers where they live.** Gates go in `review.gates` (the whole change) and `review.task-gates` (per agent, per task) in `.kyber-weave/kyber-weave.yml`. Finish commands go in that technology's `## Completion` section, one per standard, with no separate `## Commands` section beside it. Never put a project's commands in a portable agent or skill: the standard and the configuration are the host's, and the portable tree ships to every repository.
5. **Record the answer.** `kyber-weave docs onboarding done .` once the choices are written, or `kyber-weave docs onboarding declined .` if the user declines. Either one ends the question, so no later run asks it again; `declined` is the honest record of a repository that chose not to have project gates, and a report of `TASK_GATE: NONE_DECLARED` then says so. The verb writes the value in place, keeping every comment and other key, and re-splices the registry block that publishes it.
6. **Report what you changed and what you left at its default.** The operator should be able to tell an accepted default from a chosen change without reading the diff.

**What this mode does not do.** It does not choose the repository's documentation root, its components or its ADR set — those are the other modes. It does not add a technology; that is `ontology.technologies` plus `kyber-weave docs init .`. And it does not reopen the question when a technology appears later: the worker that needs that standard returns its own question, and the answer is to add the technology and answer for it, not to stop every run again.

## The architecture

Documentation paths below are relative to **<docs-root>**, resolved through the Config Reg in the repository root `AGENTS.md`. Never assume a fixed documentation root. When bootstrapping a repo that has no registry yet, agree the root with the user and declare it as **<docs-root>** in the registry you create.

| Layer | Purpose | Lives at |
|---|---|---|
| Root policy | Mandatory, repo-wide rules; read before any work | `AGENTS.md` (or `CLAUDE.md`) at repo root |
| Scoped policy | Narrower rules for a subtree; can't weaken root | `AGENTS.md` in subtrees that need it |
| Documentation index | Canonical entry point, links everything below | `<docs-root>/README.md` |
| Documentation standard | The rules this checklist enforces — required doc shape, ownership, review cadence | `<docs-root>/documentation-standard.md` |
| Component catalog | One row per maintained component: owner, doc path, last-reviewed, status | `<docs-root>/catalog.md` |
| Config registry | Flat table: fact name → its one true location. Skills and agents look up facts by name here instead of hardcoding them | a table inside root `AGENTS.md`, or its own file if it grows large |
| ADRs | Durable "why the architecture is this way" records — outlive any single plan | `<docs-root>/adr/` |
| Plans | Working documents for one unit of work, with an explicit lifecycle | `<docs-root>/plans/`, indexed by `<docs-root>/plans/README.md` |
| Archive | Superseded/completed material — never authoritative | `<docs-root>/archive/` |

Each layer answers a different question. An agent or human should be able to walk down exactly as far as a task requires without re-reading the whole tree.

## Bootstrap steps

1. Confirm the repo's stack and existing conventions — don't impose an unrelated structure on top of what's already there.
2. Create `<docs-root>/README.md` (index), `<docs-root>/system/architecture.md` (architecture), `<docs-root>/documentation-standard.md` (rules), and `<docs-root>/catalog.md` (one row for the first component) from [references/templates.md](references/templates.md).
3. Add or extend root `AGENTS.md` with a short config-registry table, starting with **<docs-root>** — even 3-4 entries is a fine start. Put it in `AGENTS.md` even when the repo's root policy lives in `CLAUDE.md`: that is where Squad's skills resolve the registry, so a `CLAUDE.md` should point to it rather than hold it.
4. Create `<docs-root>/adr/` and `<docs-root>/plans/` (with a `README.md` plan index) even if empty — the structure signals where things go.
5. Report what you created and what the user should fill in next (owner names, first ADR, first cataloged component). Don't invent content you can't verify from the repo.

## Audit checklist

- Does `<docs-root>/README.md` actually link to everything under `<docs-root>/`? Orphaned files are a discoverability failure.
- Does every row in `catalog.md` point at a doc path that exists, with a plausible last-reviewed date?
- Do any skills or agent instruction files hardcode a fact (a threshold, a path, a naming convention) that appears anywhere else in the repo? That's a config-registry candidate — flag it, don't silently fix a large batch without confirming with the user first.
- Are there `Completed` or `Superseded` plans still sitting in `<docs-root>/plans/` instead of `<docs-root>/archive/plans/`, with the plan index still listing them as active?
- Does any ADR-worthy decision exist only inside a plan that's about to be archived? Flag it for promotion before the plan goes stale.
- Is anything in `<docs-root>/archive/` being linked to or treated as current guidance elsewhere? That's a correctness bug, not a style nit.

## Assessment and remediation mode (owner-started)

Runs only when the owner asks for it. Assesses archived plans and specs for harvest gaps, reports, waits for owner picks, then remediates in batches.

1. Read each archived plan and spec in scope through its governed identity or path, with enough character budget for the complete file.
2. Classify each as harvested, partial, or not harvested using the `KW-DOC-LIFECYCLE-004` `C0`–`C9` conditions as the rubric (canonical table: `<docs-root>/docgraph/governance.md`). The mechanical half may come from a change-scoped `kyber-weave docs validate . --merge-ready --base <parent of the commit that archived the plan>`: the gate diffs that archiving change through `git merge-base`, so a clean 004 result is evidence toward harvested and a finding is evidence toward partial or not harvested. Never rescope with `--base <first commit>`: that diffs the whole history instead of the archiving change. The semantic half (whether the mapped content truly describes the shipped design) is judged by reading, never by the gate alone.
3. Report one row per plan or spec with its state and evidence, and stop. The owner picks which gaps to fix and in what order. Do not edit, re-archive, or open a pull request until the owner decides.
4. `docs-dev` fixes the picked gaps in batches, each batch landing as one pull request. Keep each batch reviewable on its own: one pull request per coherent group of plans or specs, with closeout evidence and validation (`docs validate`, `docs drift`) per batch.

## Plan lifecycle (reference for all modes)

`Draft → Ready → In progress → Blocked → Review required → Completed/Superseded → Archived`. Only `Ready`, `In progress`, and `Blocked` are implementation-actionable; `Draft` is planning-only; everything after `Review required` is historical. A plan does not archive itself — closing it out means verifying its acceptance criteria against what was actually built, promoting anything durable (a new convention, a registry entry, an ADR-worthy decision) into canonical docs, and only then moving it to `<docs-root>/archive/plans/` with status `Archived`. See [references/templates.md](references/templates.md) for a minimal plan template if bootstrapping.

## Where the agents fit (reference only — don't embed their instructions here)

This skill defines the paper trail, not who walks it. In a project with specialist agents, expect roughly this division of labor, and point to whatever the project's actual agents are named rather than assuming these exact names:

- A **planning/architect agent** does the discovery and decision-making that produces a plan, and is generally the one best positioned to flag which of its own decisions are ADR-worthy on close-out.
- A **documentation specialist agent** (e.g. this repo's `docs-dev`) owns writing the canonical docs themselves and performing plan closeout — verifying acceptance criteria, updating docs, maintaining the plan index, archiving.
- An **orchestrator/conductor skill** sequences the above rather than doing the work itself — the project's own orchestration skill (often named `conductor` or a versioned variant) is the right thing to invoke for any non-trivial documentation-architecture change that spans multiple files.

If the project has no such agents yet, this skill still applies — a human just plays those roles manually.

## Extensibility note

Future versions of this skill may source templates and conventions from a Palfery Agent Package Manager (APM) package instead of the local [references/templates.md](references/templates.md). Nothing to do about that yet — just don't build anything here that assumes a fixed local-only template path if it can reasonably be avoided.
