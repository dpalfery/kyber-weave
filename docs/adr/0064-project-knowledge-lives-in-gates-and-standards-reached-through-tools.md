---
id: adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools
title: Project knowledge lives in gates and standards, reached through tools
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-08
---

# ADR 0064: Project knowledge lives in gates and standards, reached through tools

## Status

Accepted, 2026-11-08.

## Context

Kyber-Weave ships 24 canonical agents and 28 skills to 13 harness targets and into two package
formats. Every one of those files is re-sent on every round trip of every run, in every repository
that deploys it. A fact about one repository written into that tree is therefore paid for by all of
them, and it is wrong in all of them but one.

[ADR 0001](0001-coding-standards-and-configuration-registry.md) already moved coding standards out
of agent prompts: the standard lives in the project, and the derived Config Reg names it. Two kinds
of project knowledge were still missing a home, and both had drifted into the portable tree:

- **Runnable checks.** The build, the targeted test command and the format check a repository
  actually runs. Measured before this decision: `KyberWeave.Tests` and `src/KyberWeave` appeared on
  11 instruction lines across seven portable files — `agents/conductor.md`, `agents/csharp-dev.md`,
  `agents/test-dev.md`, `agents/docs-dev.md` and the `delivery-execution`, `test-dev` and
  `task-gate` skills — plus one roster comment in `toolchain.yml`. A host that deployed an
  unmodified copy was told to run this repository's test project, which it does not have, on the
  line that told it how to finish.
- **How to finish.** Which of those commands to run, in what order, and why. Where nothing carried
  it, a finishing worker improvised, and the improvised answer was the full suite.

## Decision

Project knowledge has exactly three homes, and a portable file is none of them.

1. **Process that holds for any project stays in the portable tree.** The exit-gate procedure, the
   rework rules and the failure-loop caps are the same everywhere, so they live in the `task-gate`
   skill and its references, and an agent body points at them rather than restating them.
2. **Runnable checks live in the host's configuration.** A command is a `review.gates` entry (run
   over the whole change) or a `review.task-gates` entry (run per agent, per change) in the
   repository's `.kyber-weave/kyber-weave.yml`, selected by `include` globs rather than by name.
   No portable file names a build or a test command.
3. **How to work and finish lives in one `## Completion` section in each coding standard:** the
   build, the targeted test command and the format check, what runs when, and why. Every standard
   has exactly one, at its end, and no separate `## Commands` section beside it — two sections
   answering "how do I finish" drift apart the moment one of them is edited.
4. **A standard is read through the tool, by topic, when the work needs it.**
   `docs_get_coding_standard_for_technology(technology, topic)` returns the standard's rule ids and
   then the sections that answer the topic, inside a character budget; a finishing worker asks for
   the `completion` topic, so that knowledge is not sent on the earlier round trips that never
   reach it. Reading the file directly is allowed only when the tool is missing.
5. **A portable file that needs a project fact names the host's property or section, not the fact.**
   "The repository's test standard, its `## Completion` section" survives a host; `dotnet test
   tests/KyberWeave.Tests` does not. The Config Reg property name (`<test-coding-standard>`) stays
   in the instruction, because it is what lets a worker ask the right question when the technology
   is undeclared or declared with no standard yet.
6. **A repository with no gates and no `## Completion` section is a valid state.** Its workers
   report `TASK_GATE: NONE_DECLARED` and run the technology's ordinary build and tests. The digest
   then says the repository has fewer machine checks, which is a fact about that repository and not
   a failure of it.

What this does not decide: which gates a repository chooses, and how a new repository is asked.
That is the project's own answer, written in its configuration.

## Alternatives Considered

- **Keep the content portable and accept the drift.** Rejected on two counts. Every host inherits
  facts about this repository — a path it does not have, a command that fails — and the drift is
  silent, because nothing compares a host's copy against the repository it describes. A stale
  command is found by a worker at the moment it cannot finish, which is the worst time to discover
  it and the moment at which a fallback (the full suite) looks reasonable.
- **Each agent carries its own copy of the repository's commands.** Rejected. 24 agents times 13
  targets is one fact replicated per agent inside a single repository; a change to the test command
  becomes a many-file edit that no gate covers, and the copies diverge the first time one is
  missed. It re-creates the duplication the task file's paste contract removed for prompts.
- **A technology reference file inside each portable skill.** Rejected. It is the same problem one
  level down: a second copy of the project's standard that the tool and the Config Reg already
  resolve, shipped to hosts it does not describe.
- **Read the standard from disk rather than through the tool.** Rejected. It restores the cost the
  tool exists to avoid — a worker opening a whole standard to find one section — and a file read
  cannot answer "this technology is declared but has no standard yet, ask the human", which is the
  answer a host most needs.

## Consequences

- A host deploys a portable tree that describes no repository in particular. Its own commands are
  in its own configuration and its own standards, and changing one changes it in one place.
- A finishing worker's knowledge arrives by topic at the moment it is needed, so the earlier round
  trips carry neither the commands nor the standard they sit in.
- `## Completion` is enforced, not conventional: `StandardsCompletionSectionTests` fails a declared
  standard that has no such section, more than one, a leftover `## Commands` section, or a live link
  to the retired anchor, and `KyberStandardsTemplatesTests` holds the 14 shipped templates to the
  same shape. `PortableContentTests` fails when a portable file names this repository's project
  paths again — the roster comment in `toolchain.yml` included, because a "keep it simple" scan that
  a comment can trip is a scan someone will widen.
- Four agent bodies changed in one repository, so their migration reports carry new body digests.
  The migration checks are what make the removal of a line from an agent body a visible act.
- The cost: a worker whose harness has no docs tool must read the standard from disk, which is the
  documented exception rather than the route.

## Related

- [Coding standards index](../standards/README.md) — what a standard holds, and the `## Completion` rule
- [Review council architecture](../code-review/architecture.md#task-gates) — the configuration a repository's checks live in
- [ADR 0001](0001-coding-standards-and-configuration-registry.md) — decoupled coding standards and the derived Config Reg
