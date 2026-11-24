---
id: adr/0093-kw-skills-load-on-demand-through-kw-docs
title: KW skills load on demand through kw-docs and are never deployed as harness skills
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-24
component: KyberSquad
decided-by: []
code-refs:
  - KwSkillCatalog
  - KwSkillListText
  - DispatchPromptInsertion
  - TaskPromptService
  - DocsQueryService
  - KwSkillRulesValidator
---

# ADR 0093: KW skills load on demand through kw-docs and are never deployed as harness skills

## Status

Accepted, 2026-11-24. Records the design of the plan [Kyber-Weave skills that agents load on
demand](../archive/plans/2026-11-24-kw-skills-load-on-demand.md) (decisions D2 to D19 and D22 to D25),
promoted from a user story the owner settled on 2026-11-22. It refines
[ADR 0049](0049-kyber-squad-ships-the-kyber-weave-skills.md), which ships harness skills, without
changing it: the two kinds of skill stay apart.

## Context

A technology folder's `<scope>.skills.md` was prose that the task prompt inlined whole, in a `<skills>` part and
again for the parent inside `<parent-rules>`, so every worker paid for guidance it might never use, and a role agent
dispatched without a task got nothing. Harness skills solve the on-demand problem, but a harness loads them itself,
per harness and per scope, and a harness skill folder is deployed to every user's home, which is the wrong home for
repository-specific guidance that should travel with the repository's standards.

## Decision

1. **A separate pool, the KW skill.** A KW skill is `<docs-root>/standards/skills/<name>/SKILL.md` (harness
   frontmatter, optional reference files), plus a shipped pool embedded in the binary. A repository skill wins the
   whole name over a shipped one. The corpus does not load the pool; the `KW-DOC-SKILL-001` to `-006` rules do.
2. **Assigned by name, listed by description.** Each technology and role holds `<scope>.skills.yml`, a list of names.
   A dispatch carries one `<kw-skills>` part: the parent's names, the technology's, then the role's, each as
   `- <name>: <description>` under a fixed opening instruction. It is omitted when empty, is one cut unit under the
   existing prompt budget, and a role agent dispatched with no task gets its role's list through the hook.
3. **Loaded through kw-docs.** The kw-docs `load_skill` tool returns the skill's body, folder and absolute reference
   paths, never the references' contents. An unknown or malformed name is an answer, not a failure.
4. **Never deployed.** `squad install` and `squad update` write no KW skill into a harness folder; content tests and a
   CI integration test hold it. KW skill names are disjoint from every Squad and APM skill name.
5. **Recorded separately.** KyberDash shows loads in their own section, recognized from the stored `load_skill` call,
   with the skill read from the reply header.

## Alternatives Considered

- **Keep inlining the prose.** Rejected: it is the cost this removes, and it cannot serve a role agent with no task.
- **Deploy them as harness skills.** Rejected: it ties guidance to each harness's loader and install scope, puts
  repository guidance in the user's home, and makes `squad update` own repository content. A harness skill folder
  also gives the agent no repository-over-shipped resolution.
- **A new `docs` CLI to read them.** Rejected: agents already hold the kw-docs server, and a tool call is what
  KyberDash can see.
- **Select skills by something other than technology or role.** Out of scope; the lists are the selection.

## Consequences

- Prompts shrink to a list of names and descriptions; guidance costs context only when an agent loads it.
- Two meanings of "skill" now exist, kept apart by the name "KW skill" in code, docs and the prompt; the glossary
  defines it against ADR 0049's term.
- A shipped skill is copied once into `~/.kyber-weave/kw-skills/` so its reference files have a path, which makes
  `load_skill` a tool that may write.
- The conductor's own session is never dispatched, so the `conductor` role list reaches no one through the hook.
- Host repositories' legacy `.skills.md` files are reported (`KW-DOC-SKILL-005`), not converted automatically.

The design is in [the Squad architecture](../kyber-squad/architecture.md#13-kw-skills-the-on-demand-pool), the dispatch
part in [the Arbiter architecture](../kyber-arbiter/architecture.md#kw-skills-in-the-dispatch-prompt), the tool in
[the MCP runbook](../docgraph/mcp-runbook.md#load_skillname-projectpath--null), and the recording in
[the KyberDash architecture](../dash/architecture.md#kw-skill-loads).
