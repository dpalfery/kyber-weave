---
id: rules/global-scope-by-default
title: User scope is the default scope for install and config
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-11-05
decided-by:
  - adr/0037-squad-state-in-user-home
---

# User scope is the default scope for install and config

> **USER SCOPE IS THE DEFAULT. INSTALLABLE PER-HARNESS ARTIFACTS WRITE TO THE USER STATE
> ROOT UNDER `~/.kyber-weave/` unless the user chooses project scope explicitly with
> `--project`. WHERE A HARNESS HAS A GLOBAL HOOK LOCATION, THE GLOBAL HOOK IS THE DEFAULT
> INSTALL TARGET; WHERE ONLY A PROJECT LOCATION EXISTS, PROJECT SCOPE IS USED; WHERE NO HOOKS
> ARE SUPPORTED, THE ARBITER MCP `kyber-weave arbiter serve` FALLBACK CARRIES THE
> ENFORCEMENT PATH. THE ARBITER MCP IS A SEPARATE SERVER FROM THE DOCS MCP.**

This rule governs every feature that installs or configures per-harness state. It is current
policy for the repository and carries the same standing as the other [rules](README.md).

## Motivation

A user-scoped install is the default because the user's home directory is the stable place for
state that is not tied to one checkout. A feature that writes harness files must choose a scope
without silently reading or rewriting the wrong location. The same rule avoids a drift between a
project checkout and the operator's own machine state: if a feature writes per-harness artifacts,
its first choice is the user-level location, and project scope is a deliberate opt-in.

## What the rule requires

- **User scope is default.** Any feature that installs or configures per-harness artifacts must
  support `--global`, and the default scope is the user-level state path under
  `~/.kyber-weave/`.
- **Project scope is opt-in.** `--project` is the explicit request to place the artifact beside a
  repository instead of in the user-level state root.
- **Global hook locations win when available.** When a harness has a global hook location, a
  global install writes there by default. If a harness exposes only a project-scoped location,
  project scope is used there even when a global install command is run and the user has not
  chosen a different explicit scope.
- **Unsupported hooks use the Arbiter MCP fallback.** When a harness has no supported hook path,
  the install still records the feature as enforceable through the Arbiter MCP server
  `kyber-weave arbiter serve` rather than silently claiming a hook was rendered.
- **The Arbiter MCP remains separate from the docs MCP.** There is no Arbiter feature added to
  the docs MCP. The docs MCP rule continues to govern repository-scope documentation wiring only.

## What this rule does not allow

- A feature may not silently reject `--global` or require project scope to be used by default.
- A feature may not write to user-scope configuration as a side effect of a repository-scoped docs
  repair or a different component's install path.
- A feature may not claim a harness hook is enforced when the harness has no supported hook file,
  except by recording the fallback path explicitly.

## Enforcement

A feature must document its scope matrix in its install or configuration output: what it writes,
which harnesses are enforced in user scope, which are enforced in project scope, and which fall
back to the Arbiter MCP server. The truth is the install output, not a hidden or implicit default.

## Related

- [ADR 0037](../adr/0037-squad-state-in-user-home.md) — the decision that makes user scope the
  default and keeps user-level state under `~/.kyber-weave/`
- [ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md) — the Arbiter decision
  matrix for enforced hooks, project scope and MCP fallback
- [ADR 0052](../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) — the docs
  MCP boundary that remains repository-scope only
- [Rules index](README.md)
