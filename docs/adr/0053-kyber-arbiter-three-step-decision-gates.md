---
id: adr/0053-kyber-arbiter-three-step-decision-gates
title: Kyber Arbiter three-step decision gates
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-09
---

# ADR 0053: Kyber Arbiter three-step decision gates

## Status

Accepted, 2026-10-03. Records the decision to gate Squad delegations and review fan-out
through a rule engine that answers in up to three steps — plain code, a decision model, a
reasoning agent — enforced by harness hooks, delivered in three phases by harness group:
Phase 1 Claude, Copilot in VS Code, Copilot CLI, and OpenCode; Phase 2 Pi, Codex, and Cursor;
Phase 3 Kilo, Antigravity, Factory, Devin, Warp, and ZCode.

Amended 2026-11-08 by the conductor run-speed programme: decision 9's changed-path basis
becomes committed plus dirty (D44), and decision 23 adds `KW-ARB-PLANREAD-001`.

## Context

The Squad's in-flight decisions were made by the model being governed. Delegation scope was
enforced only by the conductor's own instructions. The ready queue was held only by the
conductor's attention. Delegation rosters were recorded as `permission-not-expressible`
degradations. Review fan-out spawned one seat per lens even when the lens did not apply.
Each of these is a place where an exact fact exists but no plain code reads it, or where a
judgement about meaning is made without a confidence attached.

## Decision

1. **Three steps, in order (D1).** Step 0 answers only from exact facts through a closed
   predicate set (`all`, `any`, `not`, `exists`, `equals`, `in`, `matches`, `subset-of`,
   `intersects`, `count`). Step 1 asks a decision model one batched call per trigger. Step 2
   is a reasoning agent dispatched after the block, never inside the hook. First match wins
   inside step 0; one confident red flag escalates the trigger without averaging.
2. **Separation (D2, D16).** The feature is named Kyber Arbiter — JEV names one provider, not
   the feature. Hook and MCP surfaces ship in a separate `kyber-weave-arbiter` binary with no
   Spectre.Console dependency; human commands live in the existing CLI as
   `kyber-weave arbiter …`; the engine lives in Core and serves both.
3. **Enforcement comes from the harness (D3–D5).** Hooks fire from harness machinery, never
   from the gated agent's instructions. Harnesses without hooks use the MCP fallback, called
   by the conductor and code-reviewer. Any internal hook error blocks with an explicit
   reason — fail closed — inside a per-hook latency budget, with an audit for delegations
   that have no matching decision.
4. **Phased by harness (D6, D28, D32).** Phase 1 covers Claude, Copilot in VS Code, Copilot
   CLI, and OpenCode — targets that need no shared hook file. Phase 2 adds Pi, Codex, and
   Cursor with owned blocks in shared hook files. Phase 3 adds Kilo, Antigravity, Factory,
   Devin, Warp, and ZCode with the MCP fallback. Each phase ships as its own PR.
   Project-wide hooks gate only dispatches carrying the `KYBER-ARBITER: true` marker (D24);
   anything else passes and is logged with `caller: unidentified`.
5. **Vendor documentation is the support claim (D7).** Hook capability is taken from vendor
   pages, not live probes. Undocumented cells count as supported until a defect proves
   otherwise, and each defect is recorded with its harness, version, and the documented cell
   it contradicts.
6. **Owned blocks are the one settings-file exception (D8, D25).** Squad writes
   receipt-tracked blocks into shared hook files, identified by the hook command signature
   (`kyber-weave-arbiter hook --harness <harness> --caller <agent>`).
   [ADR 0054](0054-squad-owned-blocks-in-shared-hook-files.md) records the exception to the
   owned-files-not-settings boundary.
7. **Plans are parsed at evaluation time (D9, D30).** The parser reads plan and spec-task
   artifacts with C#, Markdig, and regex, writes nothing, and resolves `TASK:` ids against
   both grammars. A task that lists no files skips its file-scope checks with a logged skip
   rather than escalating.
8. **Undecidable maps by trigger family (D10).** Conductor and investigate triggers escalate
   on `undecidable`; review triggers allow and log, because lenses and refutations run as
   they do today. A plan with no parseable tasks escalates.
9. **Review uses hooks with refutation as step 2 (D11).** Lens applicability gates the spawn (skip
   below P(applies) 0.1). The changed-path basis is the review base against the working tree —
   **committed plus dirty**, the form `GitFacts.GetChangedPathsSinceBase` already computes — not the
   dirty list alone, because per-task commits are the normal case and a dirty-only basis makes a
   committed change invisible. An empty set is **not** treated as Markdown-only: `correctness` and
   `test-adequacy` apply unless the changed paths positively show there is no code to judge, so a
   tree we cannot read is judged rather than skipped. Quote presence and hunk membership annotate
   the return; a `choice` question verifies claims at confidence 0.9 or a corroborating gate skips
   the refutation. A finding is never dropped on a model answer alone.
10. **Gates declare `applies-when` (D12).** A gate that matches no changed path is reported
    as not applicable (`KW-REVIEW-026`), never executed, and never counted as passed or
    failed. Older `review-gates/v1` reports still read.
11. **Reserved paths keep the council (D13).** `always-human` paths still run code-reviewer
    to `NEEDS_HUMAN`; delegation rules still apply first.
12. **Nineteen shipped rules with permanent ids (D14).** Hosts tune shipped rules by id
    (`enabled`, thresholds, `effects` only) and add their own under non-`KW-ARB-` ids. Ids
    are never reused or renumbered.
13. **Every non-allow conductor outcome escalates to architect (D15).** The harness blocks
    with an envelope in the style of the existing status handoffs; the conductor dispatches
    architect with it and never retries unchanged or prompts the user. Review outcomes are
    notes (`allow`, `skip`, `verify`, `annotate`), not escalations.
14. **One hand-written provider client (D17, D23).** TypeSafe's cloud JEV service and local
    Ollama decision models share the `systemone` API behind one BCL `HttpClient`, with the
    model pinned in configuration and recorded per decision. `setup` suggests `nimble` when
    Ollama is detected; `tev1` stays selectable, and `setup` warns against it because its
    ~2,000-token input is too small for most shipped rules.
15. **Keys never rest in config (D18).** Resolution order is `TYPESAFE_API_KEY`, then the OS
    credential store entry for the endpoint origin, then no key; loopback needs none. The key
    is stored only in the OS credential store and appears in no config file, log, receipt, or
    git history. Writes travel on stdin (`--key-stdin`) or the `TYPESAFE_API_KEY` env var as the
    safer paths. Explicit exception in force: `kyber-weave arbiter install --arbiter-key <key>`
    accepts the key on argv for the one-command path, and prints a one-line warning that it is
    visible in process argv and shell history, naming the safer alternatives.
16. **No provider by default (D19).** Provider `none` switches model rules off: step 0 and
    step 2 still run, step 1 reports not evaluated, and `doctor` warns.
17. **TypeSafe's skill is reference only (D20).** The documentation links it; nothing installs,
    vendors, or ships it.
18. **Delegation identity is explicit headers (D21, D27).** The conductor writes `PLAN_FILE:`
    and `TASK:`; code-reviewer writes `LENS:` and `REFUTE:` alongside the marker. Index
    inference is diagnosis only, never a decision input.
19. **One-command install (D22).** User scope is the default for every feature that installs or
    configures per-harness artifacts. `kyber-weave arbiter install --target <t>` installs in
    user scope by default, and `--project` is the opt-in project-scope variant. The command
    ensures config at project scope (`.kyber-weave/kyber-weave.yml`) with scaffolding (reusing
    the `docs init` scaffold), while global scope writes user config `~/.kyber-weave/kyber-weave.yml`
    without scaffolding. In both scopes, it sets `arbiter.enabled: true` non-destructively,
    renders the target through the existing Squad install pipeline, applies the provider, stores
    the key in the OS credential store, verifies, and prints the target's trust step. It
    replaces the three-part sequence (`docs init`, hand-edit `arbiter.enabled`, `squad install
    --target <t>`, `arbiter setup`). The binary still ships via `install.sh`/`kyber-weave
    update` with `--no-arbiter`, and `kyber-weave update` re-renders the arbiter hooks/scripts
    for registered projects with `arbiter.enabled` after replacing binaries (`--no-arbiter` or
    an absent arbiter skips it). `arbiter uninstall --target <t>` removes only Squad-owned
    files/blocks; key deletion is opt-in (`--remove-key`). A global install renders user-level
    hooks in each supported harness's global hook location; a harness with no global hook uses
    project scope only when the user chooses `--project`; a harness with no hook support falls
    back to the Arbiter MCP server `kyber-weave-arbiter serve`. Each rendered harness is tagged
    as `enforced-global`, `enforced-project`, `partial`, or `fallback-only`, with reason codes
    `fallback-only`, `project-only`, `dispatch-unconfirmed`, and `hooks-disabled-by-harness` when
    the matrix does not fully enforce it. The install output lists exactly what was written and
    which harnesses are enforced versus fallback-only.
20. **Size is justified, not cut to fit (D26, D29).** Each phase documents estimated changed
    lines by area as information. Correctness comes before line-count targets; the Phase 1
    size escalation past the review ceiling is accepted.
21. **Routing headers are hook metadata, not reading material (Req 25).** Workers treat the
    dispatch packet as their whole context and do not open plan or spec files. Enforcement
    is a Read guard where the caller is known and header stripping where the harness allows
    input rewriting; planners, reviewers, and the docs closeout keep plan access. There is
    no `FILES:` header (D33): no mechanism is added for a problem that has not occurred.
22. **One specification covers all three phases (D34).** Phase PRs land in order; a single
    closeout after Phase 3 archives the specification.
23. **The plan-read ban is enforced at dispatch (fix 10).** `KW-ARB-PLANREAD-001` runs on the
    `delegate` trigger. After the routing header block is removed, it asks whether the prompt names
    a path under `config.planning-dirs`; if it does, the dispatch escalates. The routing header is
    removed first because that block names the plan by design, so a rule reading the raw prompt
    would fire on every dispatch. Documentation-profile targets are exempt, because a `docs-dev`
    dispatch naming the plan at closeout is correct. It shares `PlanReadInstruction` with
    `KW-DOC-TASKS-005`, so the prompt-time ban and the authoring-time check cannot drift apart, and
    it may not fire where dispatch hooks are unconfirmed — a harness with no hook runs no rule, and
    claiming otherwise would report a check that never executed.

Egress follows one rule (R9): a step-1 question declares its state as named facts, and no
fact outside those declared states leaves the machine.

## Alternatives considered

- **Model-first gating.** Letting the decision model answer everything was rejected: exact
  facts must never be subject to model judgement, and a confidently wrong model answer must
  never overturn plain code.
- **Agent-invoked enforcement.** Having dispatchers call the Arbiter from their instructions
  was rejected: the gated agent could disable its own gate.
- **One push for all harnesses.** Shipping every harness in a single change was rejected in
  favour of three reviewable phases sequenced by hook-file ownership.
- **Probing harnesses live.** Pre-verifying hook behaviour with probes was rejected: vendor
  documentation is reproducible, and defects are recorded against it as they appear.
- **A `FILES:` routing header.** Having the conductor list editable files per dispatch was
  rejected as over-engineering that takes decisions away from the sub-agent.

## Consequences

- **Phase 1 exceeds the review ceiling.** At roughly 17,900 lines of code and tests it is
  over `review.policy.max-reviewable-lines`, so the Phase 1 verdict is `NEEDS_HUMAN` on size
  as well as on reserved paths. That escalation is accepted, not worked around.
- **Unsupported harnesses stay fallback-only until the matrix is verified.** Warp and ZCode have
  no hooks, and the MCP fallback records the harness as `fallback-only` with the reason code
  `hooks-disabled-by-harness` or `dispatch-unconfirmed` until the hook matrix is verified.
- **Ledger loss needs attestation.** Completion and RED evidence live in `artifacts/`, which
  is not committed; a fresh clone escalates dependent tasks until architect attests, after
  user confirmation, what is complete (D31).
- **Step-1 thresholds are measured for Jev 1.13 and Jev 1.14.** Shipped thresholds started
  conservative for `jev-1.13.0`. A measurement on 2026-11-09 with 120 labelled cases left
  them unchanged and moved the default model to `jev-1.14.0`; `jev-1.13.0` stays supported.
  The cases are synthetic, labelled by two Claude models, and run through OpenRouter; the
  method and limits are in [the architecture guide](../kyber-arbiter/architecture.md). The
  repository treats `jev-1.13-free` as the free-tier id of Jev 1.13 and it shares those
  floors, although TypeSafe's models page, changelog and Jev 1.14 post do not list it. Any
  other model warns in `doctor` until its thresholds are measured. The decision log cannot
  supply that measurement yet, because it records no scores for step-1 answers.
- **Phase PRs target an integration branch.** While the specification was open,
  `docs validate --merge-ready` reported `KW-DOC-LIFECYCLE-003` on the Phase 1 and Phase 2
  PRs. Each phase PR targeted the long-lived integration branch `integration/kyber-arbiter`,
  and one merge to `main` follows the closeout, which archived the specification.

## Permanent identifiers

Rule ids `KW-ARB-*` (19 shipped), `KW-REVIEW-026` (gate not applicable), the reason codes
`fallback-only`, `project-only`, `dispatch-unconfirmed`, and `hooks-disabled-by-harness`, the
record schemas `kyber-arbiter.decision/v1` and `kyber-arbiter.ledger/v1`, and the routing
headers `KYBER-ARBITER:`, `PLAN_FILE:`, `TASK:`, `LENS:`, and `REFUTE:` are permanent.
Renaming one silently un-suppresses host tuning, receipts, and logs keyed on it.

## Related

- [ADR 0054](0054-squad-owned-blocks-in-shared-hook-files.md) — owned entries in shared hook files, the exception to decision 6
- [Kyber Arbiter architecture](../kyber-arbiter/architecture.md) — engine, rules, configuration
- [Kyber Arbiter runbook](../kyber-arbiter/runbook.md) — hooks, trust, fail-closed operation
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — hook wiring per harness
- [Review council architecture](../code-review/architecture.md) — review triggers and gate applicability
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-ARB-*` id and `KW-REVIEW-026`
- [ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the tracked task file, whose prompt-time ban decision 23 enforces with the same detector
