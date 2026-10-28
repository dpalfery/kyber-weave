---
id: adr/0028-devin-target-scoped-authoring-capability-profiles
title: Devin Target-Scoped Capability Profiles for Headless Authoring Roles, with a Conductor Pre-Creation Fallback
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-28
component: KyberSquad
---

# ADR 0028: Devin Target-Scoped Capability Profiles for Headless Authoring Roles, with a Conductor Pre-Creation Fallback

## Status

Accepted, 2026-10-03. Records the mechanism and the scope boundary for letting the headless
authoring roles persist a *new* governed document on Devin ([issue #161](https://github.com/dpalfery/kyber-weave/issues/161)).
Extends [ADR 0025](0025-devin-native-agents-and-skill-lowering.md), whose decisions 3 and 4 hold
unchanged: the decision below changes which profile Devin resolves for two roles, not how Devin
lowers a capability or whether subagents delegate. Does not change any other target.

Amended 2026-10-26, in place: `docs-dev` now holds `process.execute: allow` on every target through
the shared `documentation` profile, for plan and spec archival (`git mv`) and `docs validate` /
`docs drift`. It was observed unable to archive, which is this ADR's own widening trigger ("widen to
a role when that role is observed failing the same way"). The grant is shared across targets and has
exactly one consumer, `docs-dev`, pinned by a test. On Copilot the grant renders as the seven
`execute/*` tools in `docs-dev`'s tool list, which the source validator requires to match the
profile. `task-reviewer` is unchanged.

Amended 2026-10-28, in place: the architect's two documentation gates (`docs validate` and
`docs drift`) no longer depend on `process.execute`. The shared `architect` profile stays
`process.execute: ask`; the architect satisfies both checks through the read-only MCP tools
`docs_validate` and `docs_drift` on the kyber-weave documentation server ([MCP
runbook](../docgraph/mcp-runbook.md)). That is not a new execute grant — any client of that
server may call the tools. `architect-devin` and `product-planning-devin` still grant
`process.execute: allow` on Devin so those roles can initialise a missing destination before
editing; `architect-copilot` is unchanged.

## Context

`architect` and `product-owner` persist governed artifacts as *new* files — a plan under
`<plan-index>`, a specification phase file under `<specification-index>`. A conductor intake run on
Devin Desktop failed at exactly that step: `architect` drafted its plan, called `edit` on a path
that did not exist, Devin returned a failure, and the session stopped at `STATUS: PLAN_WRITE_ERROR`
until a human created the file by hand.

The cause is the composition of three things, each already established elsewhere:

- **The shared profile does not grant execution to `architect` for documentation gates.**
  `architect` holds `process.execute: ask` on every target. Its `PLAN_READY` schema and
  drift checks are satisfied by the read-only MCP tools `docs_validate` and `docs_drift` on
  the kyber-weave documentation server, not by a shell (amended 2026-10-28). On Devin,
  `product-planning` still holds `process.execute: deny` on the shared profile; file creation
  there uses `architect-devin` / `product-planning-devin`, not the MCP tools.
- **`ask` narrows on Devin.** [ADR 0025](0025-devin-native-agents-and-skill-lowering.md) decision 3
  withholds the tool and records `safety-narrowed`: a subagent profile has no key that forces the
  prompt, and Devin prompts a subagent only in the foreground. A background subagent never prompts,
  so `exec` is omitted from `allowed-tools` for both roles.
- **The remaining write path cannot create a file.** `filesystem.write` lowers to `edit`, `write`,
  `apply_patch`, and `notebook_edit`, and `allowed-tools` grants all four — but on the Devin CLI the
  only tool actually exposed to the model is `edit`, `apply_patch` requires `agent.codex_tools`, and
  Devin's `edit` fails when the target file does not exist. Naming a tool in `allowed-tools` is not
  the same as the harness having one.

So the roles could write a document and could not create one. `architect`'s `PLAN_READY`
contract also required schema and drift checks before the plan was declared ready; on Devin,
those were unreachable through `process.execute: ask` until the 2026-10-28 amendment routed
them through read-only MCP tools on every target. The Devin-scoped profiles below remain for
**creating** a new governed file, not for the gates.

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
   `process.execute: allow`, which the shared profiles hold as `ask` and `deny`. That Devin
   grant is for initialising a missing file before edit, not for the documentation gates —
   those are the MCP tools `docs_validate` and `docs_drift` (amended 2026-10-28). `docs-dev`
   needs no Devin-scoped profile: its shared `documentation` profile grants
   `process.execute: allow` on every target (amended 2026-10-26). That profile has one
   consumer, `docs-dev`, pinned by a test, and the grant is instruction-scoped in the
   `docs-dev` body to archival `git mv`, `docs validate` and `docs drift`, and doc-pinning
   tests. `task-reviewer` remains deliberately unchanged, as it is read-only apart from its
   findings artifact. Widen to a role when that role is observed failing the same way, not
   before.

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

5. **Authoring guidance is harness-neutral, and both authoring roles carry it.** `architect`'s
   `plan-authoring` reference tells the role that where file editing requires an existing
   destination — Devin is the known case — it initialises the file through the shell before editing.
   `product-owner` carries the same sentence in its agent body and in the skill's `spec-authoring`
   reference (the role shares its name with a skill, so it cannot own an agent sidecar: ZCode would
   have to project that closure into the skill's directory). The conductor pre-creation protocol
   (decision 4) remains the fallback if a write still fails on a nonexistent file.

## Alternatives rejected

- **Granting `process.execute: allow` on the shared `architect` and `product-planning` profiles.**
  Fixes Devin and widens every other target with it, including the ones where the grant is
  deliberately not a shell. `architect-copilot` already exists to avoid exactly this.
- **Keeping the roles write-only and relying on the conductor alone.** Leaves `architect`
  unable to run its documentation gates autonomously on Devin before the MCP tools existed.
  The pre-creation protocol is kept as the fallback, not as the mechanism. The gates
  themselves are now MCP tools on every target (amended 2026-10-28).
- **Widening `task-reviewer` too; or dropping the `docs-dev` half.** `task-reviewer` was not observed
  failing, and a Devin-scoped profile for a role that needs no shell is a grant with no reader.
  Dropping the `docs-dev` half was rejected on 2026-10-26: `docs-dev` was observed unable to archive,
  so the closeout path stayed blocked.
- **Emitting `write` alone.** `write` is recognised in `allowed-tools` from CLI v3000.11.1, but the
  observed failure is that the harness exposes `edit` to this model, not that the name is
  unrecognised. Naming a tool the harness does not hand the model reproduces the original bug.
- **A renderer-level special case keyed on agent name.** Hides a permission decision inside rendering
  code, where no capability profile records it and no validation can see it. The named field keeps
  the decision in canonical source, which is where `architect-copilot` keeps it.

## Consequences

- `architect` and `product-owner` receive `exec` and its three companions on Devin for file
  creation and editing setup, and no longer record `safety-narrowed` for `process.execute`
  there. The architect's schema and drift gates are MCP tools, not shell commands
  (amended 2026-10-28). `docs-dev` also receives them through its shared `documentation`
  profile (amended 2026-10-26); `task-reviewer` still records `safety-narrowed`.
- `capability-not-isolable` does not arise for these three roles (`architect`, `product-owner`, `docs-dev`) on Devin: with
  `filesystem.write: allow` alongside `process.execute: allow`, there is no withheld write-tool name
  to name. The record is still emitted for Devin roles that grant execution while withholding writes.
- Devin authoring roles hold a shell. That is a real widening of what the deployed subagent can do,
  accepted because the alternative is a harness that cannot create the artifact the role exists to
  write. `architect` and `product-owner` use the shell to initialise a missing destination before
  editing; `docs-dev` uses it to archive and run its checks. The conductor pre-creation protocol stays as the fallback if a write
  still fails on a nonexistent file. The shared profile's narrower intent — `process.execute: ask`
  on `architect` everywhere except Devin file-creation via `architect-devin`, and documentation
  gates through MCP rather than a shell — is preserved on non-Devin targets and in the lattice
  record.
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
- [`products/kyber-squad/profiles/capabilities.yml`](../../products/kyber-squad/profiles/capabilities.yml) — the `architect-copilot`, `architect-devin`, `product-planning-devin`, and shared `documentation` profiles
- [`products/kyber-squad/agents/docs-dev.md`](../../products/kyber-squad/agents/docs-dev.md) — the single consumer of the `documentation` profile and its `## Commands you run` boundary
- [`products/kyber-squad/agents/architect/references/plan-authoring.md`](../../products/kyber-squad/agents/architect/references/plan-authoring.md) — architect initialise-before-edit instruction
- [`products/kyber-squad/skills/product-owner/references/spec-authoring.md`](../../products/kyber-squad/skills/product-owner/references/spec-authoring.md) — product-owner initialise-before-edit instruction
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — Devin notes for operators
- [Kyber-Squad requirements](../kyber-squad/requirements.md) — KS-002 non-broadening contract
