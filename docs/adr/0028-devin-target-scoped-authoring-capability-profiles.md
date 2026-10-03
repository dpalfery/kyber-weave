---
id: adr/0028-devin-target-scoped-authoring-capability-profiles
title: Devin Target-Scoped Capability Profiles for Headless Authoring Roles, with a Conductor Pre-Creation Fallback
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-03
component: KyberSquad
---

# ADR 0028: Devin Target-Scoped Capability Profiles for Headless Authoring Roles, with a Conductor Pre-Creation Fallback

## Status

Accepted, 2026-10-03. Records the mechanism and the scope boundary for letting the headless
authoring roles persist a *new* governed document on Devin ([issue #161](https://github.com/dpalfery/kyber-weave/issues/161)).
Extends [ADR 0025](0025-devin-native-agents-and-skill-lowering.md), whose decisions 3 and 4 hold
unchanged: the decision below changes which profile Devin resolves for two roles, not how Devin
lowers a capability or whether subagents delegate. Does not change any other target.

## Context

`architect` and `product-owner` persist governed artifacts as *new* files — a plan under
`<plan-index>`, a specification phase file under `<specification-index>`. A conductor intake run on
Devin Desktop failed at exactly that step: `architect` drafted its plan, called `edit` on a path
that did not exist, Devin returned a failure, and the session stopped at `STATUS: PLAN_WRITE_ERROR`
until a human created the file by hand.

The cause is the composition of three things, each already established elsewhere:

- **The shared profile does not grant execution to `architect`.** `architect` holds
  `process.execute: ask` — the grant is `docs validate` and `docs drift`, not a shell — and
  `product-planning` holds `process.execute: deny`.
- **`ask` narrows on Devin.** [ADR 0025](0025-devin-native-agents-and-skill-lowering.md) decision 3
  withholds the tool and records `safety-narrowed`: a subagent profile has no key that forces the
  prompt, and Devin prompts a subagent only in the foreground. A background subagent never prompts,
  so `exec` is omitted from `allowed-tools` for both roles.
- **The remaining write path cannot create a file.** `filesystem.write` lowers to `edit`, `write`,
  `apply_patch`, and `notebook_edit`, and `allowed-tools` grants all four — but on the Devin CLI the
  only tool actually exposed to the model is `edit`, `apply_patch` requires `agent.codex_tools`, and
  Devin's `edit` fails when the target file does not exist. Naming a tool in `allowed-tools` is not
  the same as the harness having one.

So the roles could write a document and could not create one. `architect`'s `PLAN_READY` contract
adds a second loss: it requires running `docs validate` and `docs drift` before the plan is declared
ready, and both were unreachable on this target for the same reason.

## Decision

1. **A Devin-scoped capability profile is resolved when a subagent names one.** A subagent may
   declare `devin-capability-profile`, and `DevinRenderer` resolves `agent.DevinCapabilityProfile ??
   agent.CapabilityProfile` for every permission lookup it performs — granted tools, degradation
   records, MCP entitlement, and the pure-orchestrator exclusion. The field is declared in the agent
   schema, carried on `SquadAgent`, parsed by `SquadSourceLoader`, and checked by
   `SquadSourceValidator`. It is optional: an agent that omits it renders exactly as before, which is
   what every non-authoring role does.

2. **`architect` and `product-owner` are the only roles that name one.** `architect-devin` and
   `product-planning-devin` mirror their shared profiles exactly except for
   `process.execute: allow`, which the shared profiles hold as `ask` and `deny`. Two roles remain
   deliberately unchanged: `docs-dev`, which writes documents through the edit tool and has no
   execution-dependent completion contract, and `task-reviewer`, which is read-only apart from its
   findings artifact. Widen to a role when that role is observed failing the same way, not before.

3. **Validation keeps the envelope target-scoped in both directions.** A `target: devin` profile is
   rejected as an agent's shared `capability-profile` — the shared-profile diagnostic now names the
   target and points at the matching `<target>-capability-profile` field — and a
   `devin-capability-profile` naming a profile without `target: devin` is rejected. An unknown name
   is rejected. A primary agent naming the field is rejected: Devin lowers primary identities to
   skills, which carry no tool allow-list, so the override would validate and then evaporate. The
   result is that granting `process.execute: allow` on Devin cannot leak to Claude, Cursor, Codex, or
   any other target, which is the same guarantee `architect-copilot` carries.

4. **The conductor pre-creates the destination as a fallback, documented rather than implemented.**
   The conductor runs in the main Devin Local session with shell access the subagent does not have,
   so it can create the empty file itself. `conductor`'s `intake-path`, `plan-path`, and `spec-path`
   references record that: pre-create before dispatch, and pre-create again before redispatching a
   role that returned `PLAN_WRITE_ERROR` or `SPEC_WRITE_ERROR` on a nonexistent file. This is a
   harness-neutral degradation protocol, not Devin-specific text, and it is the path for a future
   target that withholds file creation from subagents without a Devin-scoped profile to fall back on.

5. **Authoring guidance is harness-neutral, and only `architect` carries it.** `architect`'s
   `plan-authoring` reference tells the role that where file editing requires an existing
   destination — Devin is the known case — it initialises the file through the shell before editing.
   `product-owner` is granted the shell but has no equivalent instruction: no authoring reference or
   skill tells it to initialise a destination, so its route to a new specification file is the grant
   plus decision 4's pre-creation fallback. That asymmetry is left as delivered rather than closed
   here; adding the instruction would be a change to canonical product source, not to this decision.

## Alternatives rejected

- **Granting `process.execute: allow` on the shared `architect` and `product-planning` profiles.**
  Fixes Devin and widens every other target with it, including the ones where the grant is
  deliberately not a shell. `architect-copilot` already exists to avoid exactly this.
- **Keeping the roles write-only and relying on the conductor alone.** Leaves `architect` unable to
  run `docs validate` and `docs drift`, so its `PLAN_READY` contract still cannot be met
  autonomously on Devin. The pre-creation protocol is kept as the fallback, not as the mechanism.
- **Widening `docs-dev` and `task-reviewer` too.** Neither was observed failing, and a Devin-scoped
  profile for a role that needs no shell is a grant with no reader.
- **Emitting `write` alone.** `write` is recognised in `allowed-tools` from CLI v3000.11.1, but the
  observed failure is that the harness exposes `edit` to this model, not that the name is
  unrecognised. Naming a tool the harness does not hand the model reproduces the original bug.
- **A renderer-level special case keyed on agent name.** Hides a permission decision inside rendering
  code, where no capability profile records it and no validation can see it. The named field keeps
  the decision in canonical source, which is where `architect-copilot` keeps it.

## Consequences

- `architect` and `product-owner` receive `exec` and its three companions on Devin, and no longer
  record `safety-narrowed` for `process.execute` there. `docs-dev` and `task-reviewer` still do.
- `capability-not-isolable` does not arise for these two roles on Devin: with
  `filesystem.write: allow` alongside `process.execute: allow`, there is no withheld write-tool name
  to name. The record is still emitted for Devin roles that grant execution while withholding writes.
- Devin authoring roles hold a shell. That is a real widening of what the deployed subagent can do,
  accepted because the alternative is a harness that cannot create the artifact the role exists to
  write. The shared profile's narrower intent — a grant scoped to two commands rather than a shell —
  is preserved everywhere else.
- The `write`-does-not-create-files finding is a harness observation, not a documented Devin
  contract. It is the one claim here to re-check against a real install if Devin's tool surface
  changes; ADR 0025's real-install confirmations are still open on the same build.
- A future harness that withholds subagent file creation uses decision 4 and, if it needs a grant to
  fix it properly, follows the `architect-copilot` shape: a target-scoped profile plus a
  `<target>-capability-profile` field, with validation that keeps it out of the shared profile.

## Related

- [ADR 0025](0025-devin-native-agents-and-skill-lowering.md) — Devin rendering decisions this extends
- [ADR 0017](0017-copilot-deterministic-tool-order.md) — Copilot tool membership and emission order, the projection the `copilot-capability-profile` field feeds
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §8 rendering, target-scoped profiles and where `architect-copilot` is declared
- [`products/kyber-squad/profiles/capabilities.yml`](../../products/kyber-squad/profiles/capabilities.yml) — the `architect-copilot`, `architect-devin`, and `product-planning-devin` profiles
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — Devin notes for operators
- [Kyber-Squad requirements](../kyber-squad/requirements.md) — KS-002 non-broadening contract
