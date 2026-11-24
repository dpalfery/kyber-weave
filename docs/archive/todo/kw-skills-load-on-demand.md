---
id: archive/todo/kw-skills-load-on-demand
title: Kyber-Weave skills that agents load on demand
doc-type: todo
component: KyberSquad
status: superseded
owner: dpalfery
last-reviewed: 2026-11-24
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - DocGraph
  - KyberArbiter
  - KyberDash
---

# Kyber-Weave skills that agents load on demand

**Status:** Superseded and archived
**Archive Date:** 2026-11-24
Successor: implementation plan 2026-11-24-kw-skills-load-on-demand ("Kyber-Weave skills that agents load on demand").

User story for the conductor. Settled with the owner on 2026-11-22.

## Prerequisites

- The retired technology skills are gone (PR #658 merged), so no harness skill folder still holds a technology skill.
- The dispatch hook already inserts the shared rules and a technology's rules and standard into a task prompt, each
  inside a named tag.
- The kw-docs MCP server is available to every agent the Squad deploys.

## Description

As the owner of a repository that uses Kyber-Squad, I need each technology and role agent to see a short list of the
Kyber-Weave skills meant for it and load only the ones it needs, so that specialised guidance is available to every
agent without filling its context before the agent asks for it.

There are two kinds of skill, kept fully separate:

- **Harness skills** are the skills a harness provides and loads itself (Claude, Copilot and others). Nothing in this
  story changes them.
- **Kyber-Weave skills (KW skills)** live in the repository's standards tree. The hook injects only their list at
  agent start; an agent gets a skill's text by calling the kw-docs `load_skill` tool.

In scope:

- **One shared pool.** A KW skill is a folder `standards/skills/<name>/` holding `SKILL.md`. Its frontmatter carries
  `name` and `description`, the same shape harness skills use, so a skill copies between the two unchanged. A skill
  folder may also hold reference files (for example under `references/`).
- **Assignment by list.** Each technology and each role assigns zero or more skills in a `<scope>.skills.yml` file in
  its standards folder: a YAML list of skill names, nothing else. The description is read from the skill's own
  frontmatter and is written nowhere else. The `.yml` file replaces today's `<scope>.skills.md`.
- **Inheritance.** A child technology's skills are its parent's list plus its own, the same way rules inherit (react
  gets typescript's skills).
- **Shipped and repository skills.** Kyber ships a starter set of KW skills, and `docs init` copies them into the
  repository's pool. A repository may add its own. When a shipped skill and a repository skill share a name, the
  repository's skill wins.
- **Injection at agent start, for every agent.** The same dispatch hook that inserts rules and standards inserts a
  skills section for every Squad agent it starts, worker and role alike:
  - A worker gets the skills of its task's technology (and its parent's).
  - A role agent (architect, code-reviewer, plan-reviewer, docs-dev and the others with a role standards folder) gets
    its role's skills, chosen from the agent's name even when the dispatch names no task and no technology.
  - The section is wrapped in an XML tag and lists one entry per skill with its `name` and `description`.
  - The section opens with one instruction: these are Kyber-Weave skills, not harness skills; load one by calling
    kw-docs `load_skill` with its name when its description fits the work, and never through the harness's own skill
    tool.
  - An agent with no assigned skills gets no section.
- **`load_skill` on the kw-docs MCP server.**
  - Takes a skill name and an optional `projectPath`, the same way the other kw-docs tools do.
  - Returns the skill's `SKILL.md` body, the folder the skill was read from, and the absolute path of each reference
    file in the skill folder. The agent reads reference files with its own read tool; the server adds no read tool for
    them.
  - Resolves the repository copy before the shipped copy.
  - Answers an unknown name with the list of skill names that exist, and a malformed skill (no `name` or
    `description`) with what is missing; neither is a tool failure.
  - Starts with the provenance line every kw-docs response carries.
- **Validation.** `docs validate` reports:
  - a `.skills.yml` that is not a list of names;
  - a listed skill with no folder in the pool;
  - a `SKILL.md` with no `name` or no `description`, or whose `name` is not its folder's name.
- **Never deployed to a harness.** `squad update` does not copy KW skills into any harness skill folder.
- **KyberDash.** Each `load_skill` call is recorded with the skill, the agent, the session and the time. KyberDash
  shows KW skill loads in their own section, separate from harness skill use. Recording only: nothing is blocked or
  enforced.
- **Migration.** Every shipped `<scope>.skills.md` becomes a `<scope>.skills.yml`. Prose in it that is real guidance
  becomes a KW skill; the rest is deleted. `docs init` and the standards templates write the new shape.

## Out of scope

- Any change to harness skills, how harnesses load them, or the skills Kyber deploys as harness skills (conductor,
  code-review and the rest).
- An Arbiter rule that requires, blocks or counts skill loads.
- A kw-docs tool for reading or searching reference files.
- Choosing skills by anything other than the agent's technology or role (for example by file type or by task text).
- Writing new skill content beyond what migration moves out of today's `.skills.md` files.

## Assumptions

- Every Squad agent has a read tool, so reference-file paths are enough.
- Role names match the role standards folders the templates already ship (`architect`, `reviewer` and the others), and
  each role agent maps to one of them.
- The skills section is small enough to fit the existing task-prompt budget; when it does not, the budget's
  existing left-out reporting names it.

## Business case

Specialised guidance today either sits in every agent's context whether it is needed or not, or is missing. Loading on
demand lets a repository give each agent as much guidance as it needs at no context cost until the agent uses it, and
KyberDash shows which skills agents actually load, so unused skills can be cut and missing ones spotted.

## Acceptance criteria

- A skill at `standards/skills/<name>/SKILL.md` with `name` and `description` frontmatter, listed in a technology's
  `.skills.yml`, appears by name and description in the skills section of a task prompt for that technology.
- A child technology's task prompt lists its parent's skills and its own.
- A role agent dispatched with no task and no technology gets its role's skills section.
- The skills section is wrapped in one XML tag and opens with the instruction to use kw-docs `load_skill`.
- An agent whose technology or role assigns no skills gets no skills section.
- `load_skill <name>` returns the body of `SKILL.md`, the folder it came from, and the absolute paths of the skill's
  reference files; it does not return the reference files' contents.
- When the repository and the shipped set both hold a skill with the same name, `load_skill` and the skills section
  both use the repository's copy.
- `load_skill` with an unknown name returns the names that exist and is not a tool failure.
- `docs validate` reports a listed skill that has no folder, a `SKILL.md` missing `name` or `description`, a `name`
  that differs from its folder, and a `.skills.yml` that is not a list of names.
- After `squad update`, no harness skill folder contains a KW skill.
- After an agent calls `load_skill`, KyberDash shows the load in a KW skills section, separate from harness skills,
  with the skill, agent and session.
- No shipped standards folder contains a `.skills.md`; each has a `.skills.yml`, and `docs init` writes `.skills.yml`.

## Suggested tests

- Build a task prompt over an in-memory standards tree with a parent and a child technology, and assert the skills
  section lists both technologies' skills in a single XML block with the instruction line.
- Build the prompt for a role dispatch with no task id and no technology, and assert it carries the role's skills.
- Call `load_skill` against a tree holding a repository copy and a shipped copy of one name, and assert the
  repository body and folder come back, with reference paths and no reference contents.
- Call `load_skill` with an unknown name and assert the reply lists the existing names and is not a tool failure.
- Run `docs validate` over fixtures for each validation case and assert one finding each.
- Run `squad update` into a scratch home and assert no harness skill folder holds a KW skill.
- Feed KyberDash a session containing a `load_skill` call and assert it appears in the KW skills section and not
  among harness skills.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| KW-1 | Plan or direct change? | Promoted to the implementation plan named under the title; the story's In scope, Out of scope, Assumptions and acceptance criteria were its requirements source. | The plan records its own decisions D1 to D27 and AP1. | ANSWERED: owner settled the story 2026-11-22; conductor promoted it 2026-11-24 |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| KW-1, KW-H1 | [ADR 0093](../../adr/0093-kw-skills-load-on-demand-through-kw-docs.md) and [the archived plan](../plans/2026-11-24-kw-skills-load-on-demand.md), whose Closeout mapping names every canonical document the story's content now lives in. |
| KyberSquad | [Kyber-Squad architecture](../../kyber-squad/architecture.md#13-kw-skills-the-on-demand-pool) |
| DocGraph | [DocGraph architecture](../../docgraph/architecture.md#standards-folders-and-the-kw-skill-pool) |
| KyberArbiter | [Kyber Arbiter architecture](../../kyber-arbiter/architecture.md#kw-skills-in-the-dispatch-prompt) |
| KyberDash | [KyberDash architecture](../../dash/architecture.md#kw-skill-loads) |

## Design harvest

The story's design is harvested by its successor plan, whose Design harvest and Closeout mapping name every destination.

| ID | Content | Destination |
|---|---|---|
| KW-H1 | The KW skill pool, assignment lists, dispatch part, `load_skill`, validation rules and KyberDash section | [ADR 0093](../../adr/0093-kw-skills-load-on-demand-through-kw-docs.md), which links each canonical document |
