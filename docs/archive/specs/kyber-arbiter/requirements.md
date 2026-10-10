---
id: archive/specs/kyber-arbiter/requirements
title: Kyber Arbiter requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-10-10
component: KyberSquad
keywords:
  - arbiter
  - decision engine
  - rule engine
  - escalation
  - harness hooks
  - model evaluation
---

# Requirements Document

**Phase status:** Approved

Owner approved 2026-10-03 ('ARBITER; yes to LENS/REFUTE; approve' plus three-phase split), relayed by the main session. Amended 2026-10-03 per owner direction D29 (build it right first). Amended 2026-10-03 with owner decisions D30–D33 (answers to Q12–Q14 and the 80/20 direction), given with the design approval and relayed by the main session. Amended 2026-10-04 per owner direction ("we said we would implement in 3 phases not only design for phase 1. create a spec for all 3 phases please"): D32 is corrected to the owner's own words, and D34 is added. Relayed by the main session.

## Introduction

The Squad's in-flight decisions are made by the model being governed. Delegation scope is enforced only by the conductor's own instructions. The ready queue is held only by the conductor's attention. Delegation rosters are recorded as `permission-not-expressible` degradations. Review fan-out spawns one seat per lens even when the lens is not applicable. This specification defines a rule engine that answers these questions in up to three steps: plain code for exact facts, a decision model for judgements about meaning, and a reasoning agent for what the model flags or is unsure about. The Arbiter is invoked by harness hooks at the Squad's decision points and by the MCP fallback where hooks are unavailable.

## Requirements

### Requirement 1: Three-step decision gates

**User Story:** As a system architect, I want decisions to be escalated through a three-step pattern—plain code, a decision model, and a reasoning agent—so that exact facts are never subject to model judgment, and the model answers only what it is confident about.

#### Acceptance Criteria

1.1. WHEN a rule is evaluated THEN step 0 runs first, answering only from exact facts (paths, task ids, dependencies, rosters, string matches) through predicates: `all`, `any`, `not`, `exists`, `equals`, `in`, `matches` (glob), `subset-of`, `intersects`, `count`.

1.2. WHEN step 0 does not escalate THEN step 1 runs one batched call with all questions bound to that trigger, asking a decision model (TypeSafe JEV or local Ollama) to judge meaning.

1.3. WHEN step 1 is unsure or flags a red flag THEN step 2 runs: on conductor triggers, the harness blocks the dispatch and returns an escalation envelope; the conductor (not the Arbiter) dispatches architect. On review triggers, the refutation lens (or the lens itself) runs, spawned by code-reviewer's own dispatch.

1.4. WHEN any step escalates THEN the trigger escalates, and one confident red flag is enough—signals are not averaged.

1.5. THEN within step 0's `decide` clauses, first-match wins: the first matching clause answers; no match is `undecidable`.

*Provenance: Owner decision D1, conversation 2026-10-01, revised 2026-10-02 after reviewing TypeSafe's skill and escalation cookbook.*

### Requirement 2: Separate binary and CLI surfaces

**User Story:** As a release engineer, I want the Arbiter's hook and MCP surfaces in a separate binary, and the human CLI surface in the existing `kyber-weave` CLI, so that the hook host has no Spectre.Console dependency and the CLI can grow independently.

#### Acceptance Criteria

2.1. WHEN the Arbiter is released THEN a separate binary named `kyber-weave-arbiter` ships the `hook` and `serve` surfaces.

2.2. WHEN a user runs the CLI THEN `kyber-weave arbiter <validate|rules|plan|eval|audit|setup|status|doctor>` is available in the existing `kyber-weave` CLI.

2.3. THEN the engine itself (rules, facts, providers, configuration) lives in Core and is used by both binaries.

*Provenance: Owner decision D2, conversation 2026-10-01.*

### Requirement 3: Harness hooks enforce; agents do not trigger

**User Story:** As a security engineer, I want enforcement to come from the harness, not from the agent being gated, so that the agent cannot disable a gate.

#### Acceptance Criteria

3.1. WHEN a dispatch occurs THEN the harness (not the agent's instructions) invokes the Arbiter hook.

3.2. WHEN an agent is dispatched THEN the agent's own instructions never cause the Arbiter to be called.

3.3. THEN the harness decides when the Arbiter runs; the evaluation itself may include steps 0, 1 and 2 as defined in Requirement 1.

*Provenance: Owner decision D3, conversation 2026-10-01.*

### Requirement 4: MCP fallback for harnesses without hooks

**User Story:** As a harness integrator, I want a fallback to the MCP server when a harness cannot fire hooks, so that enforcement does not disappear on unsupported targets.

#### Acceptance Criteria

4.1. WHEN a harness cannot hook a decision point THEN the caller (conductor on delegation, code-reviewer on review) calls the Arbiter MCP server before taking action.

4.2. WHEN code-reviewer cannot hook lens spawns (because sub-agents are not hooked on that harness) THEN code-reviewer calls the Arbiter once before fan-out.

4.3. THEN each harness's support matrix (hook capability, hook location, fallback route) is documented in the design phase.

*Provenance: Owner decision D4, conversation 2026-10-01.*

### Requirement 5: Fail-closed command hooks

**User Story:** As a deployment safety engineer, I want any internal hook error to block the dispatch with an explicit reason, never a silent pass, so that failures are visible.

#### Acceptance Criteria

5.1. WHEN a hook encounters an internal error THEN it produces an explicit block with a reason, never allowing the dispatch.

5.2. WHEN a hook error occurs on a conductor trigger THEN the block carries an escalation envelope to architect, not a pass.

5.3. THEN timeouts are covered by a per-hook latency budget and an audit that flags delegations with no matching Arbiter decision.

*Provenance: Owner decision D5, conversation 2026-10-01.*

### Requirement 6: Hooks on every harness that supports them, sequenced by phase

**User Story:** As a product manager, I want hooks on every harness that documents hook support, sequenced in three delivery phases by harness, so that the Squad's gates reach every surface where hook enforcement is available.

#### Acceptance Criteria

6.1. **Phase 1:** WHEN the first delivery phase is completed THEN hooks are rendered on Claude, Copilot in VS Code (Local harness), Copilot CLI, and OpenCode.

6.2. **Phase 2:** WHEN the second delivery phase is completed THEN hooks are added for Pi, Codex, and Cursor.

6.3. **Phase 3:** WHEN the third delivery phase is completed THEN hooks are added for Kilo, Antigravity, Factory, Devin, Warp, and ZCode.

6.4. WHEN a harness has project-wide hooks with no caller identity THEN the hook gates only dispatches carrying the `KYBER-ARBITER: true` marker (D24). Any other dispatch passes and is logged with `caller: unidentified`.

6.5. THEN harnesses without hook support use the MCP fallback (Requirement 4).

*Provenance: Owner decision D6 (2026-10-01) as the end state; D28 (2026-10-03) sequences the delivery by phase.*

### Requirement 7: Support claimed from vendor documentation

**User Story:** As a maintainer, I want to trust vendor documentation for harness capabilities rather than live-probing them, so that support decisions are reproducible and documented.

#### Acceptance Criteria

7.1. WHEN a hook misbehaves THEN the cause is investigated as a defect against the vendor's stated behavior, not pre-verified with probes.

7.2. THEN cells marked `?` (not documented) in the harness matrix are treated as supported until a defect says otherwise.

7.3. THEN each defect is recorded with its harness, version and the documented cell it contradicts.

*Provenance: Owner decision D7, conversation 2026-10-01.*

### Requirement 8: Owned blocks in shared hook files (ADR required)

**User Story:** As a Squad maintainer, I want to mark my owned entries in shared hook files with a receipt-tracked block, so that the hook file can coexist with the host's own hooks.

#### Acceptance Criteria

8.1. WHEN a harness keeps hooks in a shared file (Cursor `.cursor/hooks.json`, Antigravity `.agents/hooks.json`, etc.) THEN Squad writes a marked, receipt-tracked block into that file.

8.2. WHEN the block is written THEN Squad's entries are identified by their command (`kyber-weave-arbiter hook --harness <h> --caller <a>`). On Antigravity, Squad owns a hook group named `kyber-arbiter`. The receipt records each entry's location and digest. Only documented fields are written (D25).

8.3. THEN an ADR is written that records this exception to the "Squad does not own settings files" boundary in the architecture.

8.4. THEN hand-edited entries inside the Squad block are reported by `squad doctor` as drift.

*Provenance: Owner decision D8, conversation 2026-10-01. Defers the ADR to later phases.*

### Requirement 9: Plans parsed at evaluation time

**User Story:** As an operator, I want plans parsed at evaluation time rather than generating intermediate artifacts, so that a plan change is immediately reflected in the next delegation.

#### Acceptance Criteria

9.1. WHEN the Arbiter evaluates a delegation fact THEN it reads and parses the plan file using only C#, Markdig and regex.

9.2. THEN no intermediate artifacts (JSON, YAML, or binary) are written to disk.

9.3. THEN the parser accepts the three label families found in archived plans: `### T<n>[a-z]`, `**Files:**` / `**scope:**` / `Scope:`, and `**Depends on:**` / `**depends-on:**` / `Depends on:`.

*Provenance: Owner decision D9, conversation 2026-10-01.*

### Requirement 10: Undecidable never allows on conductor triggers

**User Story:** As a governance engineer, I want any undecidable outcome on a conductor trigger to escalate, so that a gated dispatch is never silently released.

#### Acceptance Criteria

10.1. WHEN a conductor trigger (delegation or planner dispatch) returns `undecidable` from any step THEN the outcome is escalated to architect.

10.2. WHEN a plan has no parseable tasks THEN it is escalated (KW-ARB-PLAN-001) because tasks are what tell the conductor which agents to dispatch.

10.3. WHEN a review trigger (lens spawn or refutation) returns `undecidable` THEN the outcome is allowed and logged, because lenses and refutations run as they do today.

*Provenance: Owner decision D10, conversation 2026-10-01.*

### Requirement 11: Code review uses hooks with refutation as step 2

**User Story:** As a review lead, I want to gate lens applicability and high-confidence claim verification with the Arbiter, so that unnecessary refutation spawns are avoided.

#### Acceptance Criteria

11.1. WHEN a code-reviewer spawns a lens THEN the Arbiter evaluates whether the lens applies to the changed paths (step 0) and to the lens's own Applicability text (step 1, `noul`).

11.2. WHEN P(applies) < 0.1 THEN the lens is skipped with a reason recorded as `SKIPPED`.

11.3. WHEN a lens returns a finding THEN the Arbiter checks whether the quoted code is at `file:line` (step 0, plain code) and inside a changed hunk (step 0).

11.4. WHEN a refutation is spawned THEN the Arbiter checks the finding's claim against the quoted code and its surroundings using a `choice` question (step 1, `supports` / `contradicts` / `says_nothing`).

11.5. WHEN the claim is verified at confidence ≥ 0.9 or a gate already corroborates it THEN the refutation is skipped. Otherwise, the refutation lens runs (step 2).

11.6. THEN the Arbiter never drops a finding on a model's answer alone.

*Provenance: Owner decision D11, conversation 2026-10-01.*

### Requirement 12: Gates may declare applies-when paths

**User Story:** As a release engineer, I want gates to declare path conditions under which they apply, so that a gate like `ts-typecheck` does not run on a .NET-only change.

#### Acceptance Criteria

12.1. WHEN a gate declares `applies-when: { paths: ["dash/**"] }` in the configuration THEN the Arbiter evaluates the path predicate.

12.2. WHEN the predicate does not match THEN the gate is not executed and is reported as not applicable (KW-REVIEW-026).

12.3. THEN `review verdict` reports not-applicable gates separately from passed and failed gates.

12.4. THEN older `review-gates/v1` reports still read without requiring the `applies-when` field.

*Provenance: Owner decision D12, conversation 2026-10-01.*

### Requirement 13: Agent review council runs on reserved paths

**User Story:** As a governance officer, I want the agent review council (code-reviewer) to always run on reserved paths like agent files, so that the human reviewer receives its findings.

#### Acceptance Criteria

13.1. WHEN a change touches an `always-human` path (`products/kyber-squad/agents/**`, `capabilities.yml`, etc.) THEN the review council runs as it does today, and `code-reviewer` returns `NEEDS_HUMAN`.

13.2. THEN the Arbiter's delegation rules still apply to dispatches to reserved paths; the council runs after gating decisions are made.

*Provenance: Owner decision D13, conversation 2026-10-01.*

### Requirement 14: Shipped rules with permanent ids

**User Story:** As a rule maintainer, I want to ship default rules with permanent `KW-ARB-*` ids, so that hosts can tune or disable them by id.

#### Acceptance Criteria

14.1. WHEN the Arbiter ships THEN it includes 18 default rules with ids in the `KW-ARB-*` namespace.

14.2. WHEN a host wants to customize a rule THEN it tuned by id, setting only the overridable fields: `enabled`, `confidence-at-least` / `probability-below`, and `effects`.

14.3. THEN hosts may add their own rules with ids that do not use the reserved `KW-ARB-` prefix.

14.4. THEN each rule id is permanent and never reused or renumbered.

*Provenance: Owner decision D14, conversation 2026-10-01.*

### Requirement 15: Non-allow outcomes on conductor escalate to architect

**User Story:** As an orchestration engineer, I want every non-allow outcome on a conductor trigger to escalate to architect, so that the conductor never retries unchanged.

#### Acceptance Criteria

15.1. WHEN a conductor trigger (delegation or planner dispatch) results in any non-allow outcome—`escalate`, an internal error (`ANSWER: error`), or `undecidable`—THEN the harness blocks the dispatch with an escalation envelope (per Requirement 5.2).

15.2. WHEN the conductor receives the envelope THEN it dispatches architect (not the user) with that envelope.

15.3. WHEN architect receives the envelope THEN architect decides whether to resolve it or return `NEEDS_DECISION` for the conductor to relay to the user.

15.4. THEN there is no effect that asks the user directly—the Arbiter does not prompt.

15.5. THEN the envelope format matches the style of the Squad's existing status handoffs.

15.6. THEN review triggers have effects: lens and refutation spawns have `allow`, `skip` or `verify`; gate applicability has `applies` or `not-applicable` (D12). Undecidable maps to `allow` on review triggers (per Requirement 11).

*Provenance: Owner decision D15, conversation 2026-10-01.*

### Requirement 16: Feature named Kyber Arbiter

**User Story:** As a product manager, I want the feature named "Kyber Arbiter" and not "JEV", so that the name reflects the product's decision gates, not a single provider.

#### Acceptance Criteria

16.1. WHEN the product is released THEN the CLI commands are `kyber-weave arbiter …`, the binary is `kyber-weave-arbiter`, rule ids are `KW-ARB-*`, and the configuration section is `arbiter:`.

16.2. THEN JEV is identified as one provider behind the Arbiter, not the feature's name.

*Provenance: Owner decision D16, conversation 2026-10-01.*

### Requirement 17: One systemone client for TypeSafe or Ollama

**User Story:** As an operator, I want to choose between TypeSafe's cloud JEV service and a local Ollama instance running decision models, so that I can keep sensitive data on-premises.

#### Acceptance Criteria

17.1. WHEN a provider is configured THEN the client calls either:
- TypeSafe's cloud: `https://api.typesafe.ai/v1` running `jev-*`, or
- Local Ollama 0.35+: `http://localhost:11434/v1` running `nimble` or `tev1`.

17.2. THEN both endpoints implement the same `/v1/systemone` API, accepting the same request shape and returning answers, probabilities and confidence.

17.3. THEN one HTTP client, hand-written on the BCL `HttpClient`, serves both endpoints.

17.4. THEN the model is pinned in configuration (not a `-latest` drift), and the decision log records the model version that answered each question.

*Provenance: Owner decision D17, conversation 2026-10-01, revised 2026-10-02 to include Ollama decision models.*

### Requirement 18: API key from environment variable or credential store

**User Story:** As a security engineer, I want the API key to come from the environment or a secure credential store, never from a configuration file, so that the key is never at rest in plaintext.

#### Acceptance Criteria

18.1. WHEN the Arbiter resolves a key THEN it checks, in order:
- the `TYPESAFE_API_KEY` environment variable;
- the OS credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service), where `kyber-weave arbiter setup` wrote it.

18.2. THEN a local Ollama endpoint requires no key.

18.3. THEN both the hook processes and the MCP server resolve the key the same way.

18.4. THEN the key never appears in `kyber-weave.yml`, any MCP or hook configuration file, or a log.

18.5. THEN store writes pass the key on stdin, never in argv.

*Provenance: Owner decision D18, conversation 2026-10-01.*

### Requirement 19: No provider by default

**User Story:** As a privacy officer, I want model rules switched off by default, so that no repository content leaves the machine until a provider is explicitly configured.

#### Acceptance Criteria

19.1. WHEN no provider is configured THEN the default is `none`.

19.2. THEN step 0 (plain code) and step 2 (reasoning agent) still run; step 1 (model) is skipped and reported as not evaluated.

19.3. THEN `doctor` warns when model rules are switched off.

19.4. WHEN a configured provider errors or times out THEN the outcome is `undecidable`, which escalates on conductor triggers.

*Provenance: Owner decision D19, conversation 2026-10-01.*

### Requirement 20: TypeSafe skill reference only

**User Story:** As a documentation maintainer, I want TypeSafe's skill linked as reference material, not installed or vendored, so that the Arbiter documentation is self-contained.

#### Acceptance Criteria

20.1. WHEN the Arbiter documentation is written THEN it links to TypeSafe's skill.

20.2. THEN the skill is not installed, vendored, or shipped with the Arbiter.

20.3. THEN whoever implements the provider client or writes model rules installs the skill at that time.

*Provenance: Owner decision D20, conversation 2026-10-01.*

### Requirement 21: Conductor delegation identity from headers

**User Story:** As a conductor engineer, I want each delegation's plan task identified by explicit headers, not inferred from context, so that diagnosis and auditing are reliable.

#### Acceptance Criteria

21.1. WHEN the conductor dispatches a specialist THEN the conductor writes `PLAN_FILE:` and `TASK:` headers in the delegation prompt.

21.2. THEN inferring the plan or task from the plan index is used for diagnosis only, never to decide.

21.3. THEN the audit flags any delegation with no matching Arbiter decision, as part of the latency budget audit in Requirement 5.3.

*Provenance: Owner decision D21, conversation 2026-10-01.*

### Requirement 22: Three-part installation and update

**User Story:** As a deployment engineer, I want installation and setup in three parts—binary, hook wiring, and provider configuration—so that each part is independent and can be updated separately.

#### Acceptance Criteria

22.1. **The binary.** WHEN `kyber-weave update` is run or `install.sh` is invoked THEN the `kyber-weave-arbiter` binary ships like `kyber-weave-mcp`, with a `--no-arbiter` opt-out.

22.2. **Hook wiring.** WHEN `squad install` or `squad update` is run THEN hooks are rendered per agent when `arbiter.enabled` is true in the project's `.kyber-weave/kyber-weave.yml`.

22.3. **Provider and key.** WHEN the user runs `kyber-weave arbiter setup | status | doctor` THEN the provider choice is stored in `~/.config/kyber-weave/arbiter.yml` (user-level override, optional), and the API key is written to the OS credential store (Requirement 18).

22.4. THEN a global install (with no project configuration) renders no hooks and records the degradation `arbiter-not-enforced`.

*Provenance: Owner decision D22, conversation 2026-10-02.*

### Requirement 23: Local model defaults to nimble

**User Story:** As a product manager, I want `kyber-weave arbiter setup` to suggest the `nimble` model when Ollama is detected, so that users get a pre-tuned model with good performance for most rules.

#### Acceptance Criteria

23.1. WHEN `kyber-weave arbiter setup` runs and Ollama 0.35+ is detected THEN it suggests `nimble` (9B, 9.3 GB, ~8k tokens per question, 74.8% accuracy) as the local model.

23.2. THEN `tev1` remains selectable but its ~2,000-token usable input is too small for most shipped rules, so it is not recommended.

23.3. THEN the accuracy figures are from Ollama's own 3,880-decision benchmark: `nimble` 74.8%, JEV 1.13.0 76.0%, `tev1` 73.3%.

*Provenance: Owner decision D23, conversation 2026-10-02.*

### Requirement 24: Design must justify estimated size per phase

**User Story:** As a governance officer, I want the design phase to justify each harness delivery phase's estimated changed lines by area, so that the reviewer can assess whether the size is necessary.

#### Acceptance Criteria

24.1. WHEN each design phase concludes THEN it documents the estimated changed lines for that phase, broken down by area (Core engine and providers, Binary, CLI, Squad integration, Review, Agent text, Distribution, Documentation, Tests).

*Provenance: Owner decision D26, conversation 2026-10-03, as a design-phase input. Amended 2026-10-03 per owner direction D29: the design justifies size as information, and does not propose cuts whose only purpose is meeting a line count or the review ceiling.*

### Requirement 25: Routing headers are metadata for the hook, not reading material

**User Story:** As an architect, I want routing headers to be purely metadata for the Arbiter hook, so that agents cannot use them to read plans and break the sub-agent context isolation enforced by issue #278.

#### Acceptance Criteria

25.1. WHEN a dispatch is made THEN the dispatch packet (the agent's input) is its whole context. Routing headers are metadata carried alongside that input, never as its content.

25.2. WHEN an implementation specialist (csharp-dev, react-dev, python-dev, test-dev, etc.) is dispatched THEN it does not open plan or spec files. Its context comes from the harness's normal input capability.

25.3. THEN planners (architect, product-owner), reviewers (code-reviewer, review-lens), and the `docs-dev` closeout task keep their access to plans and specs.

25.4. THEN the routing headers (`PLAN_FILE:`, `TASK:`, `KYBER-ARBITER:`, `LENS:`, `REFUTE:`) are available to the hook for gating; design chooses enforcement: a Read-deny rule where the caller is known, and stripping headers where the harness allows input rewriting.

*Provenance: GitHub issue #278 (conductor workers read the whole plan), design-phase input.*

## Approved decisions resolving open questions

### Decision D24: Marker header for project-wide hook harnesses

**Owner answer (2026-10-03):** Harnesses with project-wide hooks and no caller identity gate dispatches by the presence of a marker header.

**D24 constraints:**

- Squad agents that dispatch under governance (conductor; code-reviewer for lens and refutation spawns; architect and product-owner for investigator dispatches) write the line `KYBER-ARBITER: true` in every dispatch prompt.
- On project-wide-hook harnesses, the hook gates only dispatches carrying the marker. Any other dispatch passes and is logged with `caller: unidentified`, so sessions that never use Squad are not gated.
- A marked dispatch is classified from its target agent and the headers D21 requires (`PLAN_FILE:` / `TASK:` for conductor delegations).
- `kyber-weave arbiter audit` flags dispatches to Squad agents that lack the marker.
- On Claude and Copilot in VS Code, where the caller is known from the harness, gating does not depend on the marker.

*Provenance: Owner, conversation 2026-10-03, answer to Q11. Marker renamed by owner 2026-10-03 to match D16 (`KYBER-ARBITER` instead of `KYBER-JEV`).*

### Decision D25: Command signature identification of owned blocks

**Owner answer (2026-10-03):** Squad's entries in shared JSON hook files are identified by their command signature.

**D25 constraints:**

- Squad's entries in a shared JSON hook file are identified by their command: `kyber-weave-arbiter hook --harness <harness> --caller <agent>`.
- On Antigravity, Squad owns a hook group named `kyber-arbiter`.
- The install receipt records each entry's location and digest.
- Only documented fields are written.

*Provenance: Owner, conversation 2026-10-03, answer to Q10.*

### Decision D26: Three delivery phases sequenced by harness (amended)

**Owner answer (2026-10-03):** The feature ships in three delivery phases sequenced by harness group, after initially stating "this will be one push".

**D26 constraints:**

- The specification, design and tasks cover all three phases (D34).
- Each phase's tasks and deliverables are justified by size per area (Requirement 24).
- The human reviewer will see size escalations as phases exceed `review.policy.max-reviewable-lines: 10000`.
- Whether each harness phase ships as its own PR or all phases ship as one PR was left to Q12, which D32 answers.

*Provenance: Owner, conversation 2026-10-03, answer to Q9 (amended after "let's split the work into 3 phases by harnesses").*

### Decision D27: Review routing headers

**Owner answer (2026-10-03):** code-reviewer's lens spawns and refutation spawns carry distinct routing headers alongside the `KYBER-ARBITER: true` marker.

**D27 constraints:**

- code-reviewer's lens spawns carry `LENS: <name>` alongside `KYBER-ARBITER: true`.
- code-reviewer's refutation spawns carry `REFUTE: <finding-id>` alongside `KYBER-ARBITER: true`.
- Both headers are routing metadata with the same status as D25 (Requirement 25): available to the hook for gating, carried outside the dispatch packet's content.
- Both kinds of spawn target `review-lens`, so the target agent alone cannot tell them apart on harnesses without caller identity. The headers resolve that ambiguity.

*Provenance: Owner, conversation 2026-10-03. Resolves the gap from requirements revision 2: the Arbiter distinguishes lens spawns from refutation spawns by header on project-wide-hook harnesses.*

### Decision D28: Three delivery phases by harness group

**Owner answer (2026-10-03):** The feature is delivered in three phases sequenced by harness group to allow reviewable PR sizes.

**D28 constraints:**

- **Phase 1:** Claude, Copilot in VS Code, Copilot CLI, OpenCode. These harnesses use agent frontmatter or own hook files; no shared JSON files are involved.
- **Phase 2:** Pi, Codex, Cursor. These harnesses use owned blocks in shared JSON hook files, so Phase 2 includes the owned-block work (D8, D25, receipt changes, ADR 0029).
- **Phase 3:** Kilo, Antigravity, Factory, Devin, Warp, ZCode. The MCP fallback server is needed for Warp and ZCode (Phase 3), and for review on any harness whose hooks do not fire inside sub-agents.

*Provenance: Owner, conversation 2026-10-03, answer to Q9: "let's split the work into 3 phases by harnesses. phase 1, claude, github copilot extension and cli and opencode, phase 2: PI, codex, cursor, phase 3: all the rest".*

### Decision D29: Build it right first

**Owner direction (2026-10-03):** "don't cut shit to hit an arbitrary number. lets build it right that is our first principal: build it right before anything else".

**D29 constraints:**

- Completeness and correctness come before line-count targets or review-ceiling constraints.
- The design justifies each area's estimated size per phase as information.
- The design does not propose cuts whose only purpose is meeting a line count or the review ceiling (D26 accepts the size escalation).
- State each fact once; prefer tables over prose; cite requirements by number rather than restating them.

*Provenance: Owner, conversation 2026-10-03.*

### Decision D30: Spec task parsing, the 80/20 version

**Owner answer (2026-10-03), to Q13 from the design phase:** The plan parser also reads the checkbox task list in a spec's `tasks.md`.

**D30 constraints:**

- The parser accepts the spec checkbox grammar (`- [ ] 2.1 Title`, `- [x] …`), so that `TASK:` ids resolve against a spec task list.
- Where a task lists no files, the file-scope checks are skipped, and the skip is logged. These are the file part of `KW-ARB-SCOPE-001`, and `KW-ARB-DIFF-001`'s comparison against the task's files. A missing file list is not treated as `undecidable`.
- The product-owner tasks template does not change.

*Provenance: owner, 2026-10-03.*

### Decision D31: Attestation after ledger loss

**Owner answer (2026-10-03), to Q14 from the design phase:** After the ledger is lost, `architect` writes an attestation, and the hook reads it.

**D31 constraints:**

- `architect` attests only after the user confirms it.
- The hook reads the attestation from `architect`'s output and records it in the ledger. No agent writes the ledger (D3).

*Provenance: owner, 2026-10-03.*

### Decision D32: Each delivery phase ships as its own PR

**Owner answer (2026-10-03), to Q12:** "each phase is its on PR yes".

**D32 constraints:**

- Each delivery phase ships as its own PR. That is the whole decision.
- Where each phase's PR merges is open as Q16 (see the task list).

*Provenance: owner, 2026-10-03. Corrected 2026-10-04: an earlier wording added "the tasks cover Phase 1 and Phases 2 and 3 become todos", which the owner did not say.*

### Decision D34: The specification covers all three phases

**Owner direction (2026-10-04):** "we said we would implement in 3 phases not only design for phase 1. create a spec for all 3 phases please".

**D34 constraints:**

- The task list covers Phase 1, Phase 2 and Phase 3.
- No phase is deferred to a todo.
- A single closeout follows Phase 3 and archives the specification.

*Provenance: owner, 2026-10-04.*

### Decision D33: 80/20, no speculative mechanisms

**Owner direction (2026-10-03):** The design covers what is needed now. Sub-agents keep their own implementation decisions.

**D33 constraints:**

- There is no `FILES:` routing header, which would have had the conductor list the files a worker may edit. The owner rejected it as over-engineering: it was "solving issues that don't yet exist", and "taking all decisions away from the sub agent".
- No mechanism is added for a problem that has not yet occurred.

*Provenance: owner, 2026-10-03.*

## Answered questions

### Q12: Delivery sequence per phase

**Question:** Does each harness delivery phase (Phase 1, Phase 2, Phase 3) ship as its own PR, or do all phases ship as a single PR built in phase order?

**Context:** D26 clarifies that the feature ships in three phases by harness group. Whether each phase is a separate push/PR or all phases ship together as one PR affects the requirements for Phase 2 and Phase 3 specifications and the timing of their approval.

**Options:**

- **(a) Each phase is a separate push.** This specification covers Phase 1. Phase 2 and Phase 3 become todos at the end of Phase 1's closeout, are approved separately, and ship as their own PRs. Each PR stays near the review ceiling (`review.policy.max-reviewable-lines: 10000`).
- **(b) One push for all phases.** The specification's three phases land in one PR. The reviewer sees all three phases' changes together.

**Recommendation:** **(a).** Separate pushes allow each phase to be reviewed on its own merits. `docs validate --merge-ready` fails while a spec is open, so Phase 2 and Phase 3 specifications cannot ship with Phase 1.

*Status: ANSWERED. D32 (owner, 2026-10-03): each phase ships as its own PR. Option (a)'s wording, which said the specification covers only Phase 1, was not part of the answer, and D34 supersedes it.*

## Design inputs

**Phase 1 harness work.** No Phase 1 harness needs a shared hook file. Claude and Copilot in VS Code use agent frontmatter; Copilot CLI uses its own `.github/hooks/kyber-arbiter.json`; OpenCode uses its own plugin file. Therefore, the owned-block work for shared JSON files (D8, D25, receipt changes for blocks, ADR 0029) can land in Phase 2 with Cursor and Codex.

**Phase 3 fallback and review.** The MCP fallback server is needed for Warp and ZCode (Phase 3). The server is also needed for review on any harness whose hooks do not fire inside sub-agents (e.g., on harnesses without sub-agent hook support, code-reviewer calls `arbiter_evaluate` before lens fan-out).
