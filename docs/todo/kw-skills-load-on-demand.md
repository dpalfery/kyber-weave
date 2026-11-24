---
id: todo/kw-skills-load-on-demand
title: Kyber-Weave skills that agents load on demand
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-11-22
---

# Kyber-Weave skills that agents load on demand

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
  folder may also hold reference files (for example under `references/`). A skill name is lowercase letters, digits
  and hyphens, starting with a letter or digit (`^[a-z0-9][a-z0-9-]*$`); no other name is a skill.
- **Assignment by list.** Each technology and each role assigns zero or more skills in a `<scope>.skills.yml` file in
  its standards folder: a YAML list of skill names, nothing else. The description is read from the skill's own
  frontmatter and is written nowhere else. The `.yml` file replaces today's `<scope>.skills.md`. A scope with no
  `.skills.yml` assigns no skills; an empty list means the same.
- **Inheritance.** A child technology's skills are its parent's list plus its own, the same way rules inherit (react
  gets typescript's skills). The merged list keeps the parent's names first, then the child's, and lists a name once,
  at its first position.
- **Shipped and repository skills.** Kyber ships a starter set of KW skills, and `docs init` copies them into the
  repository's pool. A repository may add its own. When a shipped skill and a repository skill share a name, the
  repository's skill wins, in the injected list (its description) and in `load_skill` (its body and folder) alike.
  The repository copy wins even when it is unusable (malformed, or outside the pool): the shipped copy is never a
  silent fallback, and the skill is reported as malformed or outside the pool instead.
- **Injection at agent start, for every agent.** The same dispatch hook that inserts rules and standards inserts a
  skills section for every Squad agent it starts, worker and role alike:
  - A worker gets the skills of its task's technology (and its parent's).
  - A role agent gets its role folder's skills, even when the dispatch names no task and no technology. The agent's
    name maps to a role folder by exact match on this fixed table: `conductor` → `conductor`, `architect` →
    `architect`, `code-reviewer` → `reviewer`, `plan-reviewer` → `reviewer`. An agent not in the table (for example
    `docs-dev` today) has no role folder and gets no role skills; a new role folder adds its row to the table.
    `docs validate` reports a shipped role folder with no table row and a table row whose folder does not exist, so
    the table cannot drift from the folders unnoticed.
  - The section is wrapped in one `<kyber-weave-skills>` tag and lists one entry per skill with its `name` and
    `description`, each XML-escaped, so a description cannot close or forge the tag.
  - A listed name that is not a skill name, or a listed skill that has no folder or a malformed `SKILL.md`, is left
    out of the entries, and the section ends with
    one line naming each left-out skill, so a partial list never reads as complete. `docs validate` reports the same
    skills.
  - When the task-prompt budget cuts the section, the section says it is partial and names the skills it left out,
    through the budget's existing left-out reporting.
  - The section opens with one instruction: these are Kyber-Weave skills, not harness skills; load one by calling
    kw-docs `load_skill` with its name when its description fits the work, and never through the harness's own skill
    tool.
  - An agent with no assigned skills gets no section.
- **`load_skill` on the kw-docs MCP server.**
  - Takes a skill name and an optional `projectPath`, the same way the other kw-docs tools do. A name that is not a
    skill name (see the pool rule: no `/`, `\`, `..` or rooted path) is refused before any file is read.
  - Reads only inside the pool: the resolved skill folder, after following any link, must lie inside the repository
    pool or the shipped pool, or the skill is refused as outside the pool.
  - Returns the skill's `SKILL.md` body, the folder the skill was read from, and the absolute path of each reference
    file in the skill folder. The agent reads reference files with its own read tool; the server adds no read tool for
    them. Reference paths are every regular file under the skill folder except `SKILL.md`, in ordinal path order,
    at most 100 paths; past 100 the reply says how many more there are. Enumeration never enters a linked directory,
    and a file is listed only when its resolved path lies inside the skill folder.
  - The `SKILL.md` body is capped at 64 KiB; past the cap the reply says it is truncated and gives the body's full
    size.
  - Resolves the repository copy before the shipped copy.
  - Answers an unknown name with the skill names that exist (sorted, at most 100, with a count of the rest), and a malformed skill (no `name` or
    `description`) with what is missing; neither is a tool failure.
  - Every reply has a status line a caller can match, directly after the provenance line every kw-docs response opens
    with: `STATUS: SKILL_LOADED` on success, otherwise `STATUS: SKILL_NOT_FOUND`, `STATUS: SKILL_MALFORMED`,
    `STATUS: SKILL_NAME_INVALID` or `STATUS: SKILL_OUTSIDE_POOL`.
  - Starts with the provenance line every kw-docs response carries.
- **Validation.** `docs validate` reports:
  - a `.skills.yml` that is not a list of names, or that lists a name that is not a skill name;
  - a listed skill with no folder in the pool;
  - a `SKILL.md` with no `name` or no `description`, or whose `name` is not its folder's name;
  - a skill folder whose name is not a skill name;
  - a `<scope>.skills.md` left in a standards folder after migration;
  - a shipped role folder with no row in the role table, or a table row whose role folder does not exist.
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
- The role folders the templates ship today are `conductor`, `architect` and `reviewer`; the role table above maps
  the role agents onto them.
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
- `load_skill` with `../x`, a rooted path, or a name whose folder links outside the pool reads no file and answers
  `STATUS: SKILL_NAME_INVALID` or `STATUS: SKILL_OUTSIDE_POOL`.
- A skill listed by both a parent and its child technology appears once, in the parent's position.
- A description containing `</kyber-weave-skills>` is escaped and does not end the section.
- `code-reviewer` and `plan-reviewer` get the `reviewer` folder's skills; an agent with no row in the role table gets
  no role section.
- A listed skill with no folder is named on the section's left-out line, not silently dropped.
- A repository copy that is malformed is reported as malformed by `load_skill` and the section; the shipped copy of
  the same name is not used.
- `load_skill` on a skill folder holding a linked directory that points outside it lists no path from that directory.
- A `SKILL.md` larger than 64 KiB comes back cut at the cap with the truncation and full size stated.
- Every `load_skill` reply has the provenance line first and a `STATUS:` line second, `STATUS: SKILL_LOADED` on
  success.
- `docs validate` reports each case in its list once: a `.skills.yml` listing an invalid name, a skill folder with an
  invalid name, a leftover `.skills.md`, and a role folder and role table that disagree.
- `docs validate` reports a listed skill that has no folder, a `SKILL.md` missing `name` or `description`, a `name`
  that differs from its folder, and a `.skills.yml` that is not a list of names.
- After `squad update`, no harness skill folder contains a KW skill.
- After an agent calls `load_skill`, KyberDash shows the load in a KW skills section, separate from harness skills,
  with the skill, agent and session.
- No shipped standards folder contains a `.skills.md`; each folder that assigns skills has a `.skills.yml`, a folder
  that assigns none needs no file, and `docs init` writes `.skills.yml`.

## Suggested tests

- Build a task prompt over an in-memory standards tree with a parent and a child technology, and assert the skills
  section lists both technologies' skills in a single XML block with the instruction line.
- Build the prompt for a role dispatch with no task id and no technology, and assert it carries the role's skills.
- Call `load_skill` against a tree holding a repository copy and a shipped copy of one name, and assert the
  repository body and folder come back, with reference paths and no reference contents.
- Call `load_skill` with an unknown name and assert the reply lists the existing names and is not a tool failure.
- Run `docs validate` over fixtures for each validation case (including an invalid listed name, an invalid folder
  name, a leftover `.skills.md`, and a role folder with no table row) and assert one finding each.
- Call `load_skill` on fixtures for each refusal (invalid name, linked folder outside the pool, malformed repository
  copy over a valid shipped copy) and assert the status line and that no file outside the pool was read.
- Run `squad update` into a scratch home and assert no harness skill folder holds a KW skill.
- Feed KyberDash a session containing a `load_skill` call and assert it appears in the KW skills section and not
  among harness skills.
