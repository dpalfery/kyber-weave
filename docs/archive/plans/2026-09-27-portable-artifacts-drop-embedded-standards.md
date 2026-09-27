---
id: archive/plans/2026-09-27-portable-artifacts-drop-embedded-standards
title: Portable artifacts drop embedded coding standards
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-27
development-mode: standard
---

# Portable artifacts drop embedded coding standards

## Status

Complete and archived on 2026-09-27. Approved by the user on 2026-09-27 for
[issue 126](https://github.com/dpalfery/kyber-weave/issues/126); evidence in Closeout.

## Problem and goal

Canonical agents and the code-review technology references ship coding standards inside a
portable artifact, so a host repository cannot state its own. The registry mechanism
(`<technology-coding-standard>`) and the templates under `products/kyber-squad/standards/`
already exist. The goal is to move the remaining reversible rules into those templates and
leave the agents and references resolving the standard by registry name.

## Development mode

`standard` — the change is Markdown content only; no code path changes. The existing
contract tests (golden hotshot fixture, agent-spec validation) are the regression net.

## Approved decisions

- **D1 — property absent.** The agent states that no `<technology-coding-standard>` is
  declared and asks the human whether to proceed. A headless agent returns that question to
  its orchestrator. No built-in fallback, no hard refusal. A declared standard still in
  `status: draft` is treated the same way: the agent says it is a draft and asks.
- **D2 — code-review references survive as pointers.** Each of the seven files is reduced
  to "review against `<technology-coding-standard>`". No file is deleted, so skill
  validation and the golden fixture keep their paths.
- **D3 — no new install path.** `squad install` is unchanged. The standards README documents
  the existing `docs init --kyber-standards` seeding and the one-at-a-time manual path.

## Scope

In:

- Agents that write technology code: `csharp-dev`, `maui-dev`, `dal-dev`, `pulumi-dev`,
  `python-dev`, `test-dev`, `react-dev`, `github-devops`, `sql-database-architect` — each
  names its standard and carries the D1 behaviour.
- `github-devops` and `sql-database-architect`: reversible defaults move into the
  `github-actions` and `sql` templates. Platform facts and security floors stay.
- The seven references under `products/kyber-squad/skills/code-review/references/`. The C#
  analyzer-promotion guidance, which exists nowhere else, moves into the `csharp` template.
- Template "Authority & status" paragraphs and the matching stub in `DocsScaffolder`, the
  standards README, the code-review `SKILL.md` checklist paragraph, and
  `docs/code-review/architecture.md`.
- Hotshot golden body hashes for the changed agents and skill.

Out:

- `tauri-dev` — no Rust/Tauri template exists to receive its rules.
- Automated template installation (D3).

## Tasks

1. Add D1 behaviour and the standard lookup to the nine agents.
2. Migrate `github-devops` and `sql-database-architect` defaults into their templates.
3. Reduce the seven references; move the C# analyzer tier into the `csharp` template.
4. Update template authority text, standards README, code-review `SKILL.md`, and docs.
5. Run the AGENTS.md gates and a code review.
6. Archive this plan and add its inventory row.

## Verification gates

- `dotnet build`, `dotnet test`, and `dotnet format` verify commands from AGENTS.md.
- `skill validate`, `skill lint`, `skill scan` on `code-review`.
- `docs validate . --merge-ready` and `docs drift .`.

## Closeout (2026-09-27)

- Build 0 warnings; tests 2,273 / 2,273 passed; `dotnet format` whitespace and style clean.
- `skill validate`, `skill lint`, `skill scan` on `code-review`: no errors or warnings.
- `docs drift .`: zero findings.
- Hotshot golden and migration-report body hashes updated for the nine changed agents and
  the `code-review` `SKILL.md`, following the convention the earlier standard migrations used.
- Open: `tauri-dev` still embeds Rust/Tauri rules; it needs a template before they can move.
