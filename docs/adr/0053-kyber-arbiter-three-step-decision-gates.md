---
id: adr/0053-kyber-arbiter-three-step-decision-gates
title: Kyber Arbiter three-step decision gates
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-12
---

# ADR 0053: Kyber Arbiter three-step decision gates

## Status

Accepted, 2026-10-03. Records the decision to gate Squad delegations and review fan-out
through a rule engine that answers in up to three steps — plain code, a decision model, a
reasoning agent — enforced by harness hooks, delivered in three phases by harness group:
Phase 1 Claude, Copilot in VS Code, Copilot CLI, and OpenCode; Phase 2 Pi, Codex, and Cursor;
Phase 3 Kilo, Antigravity, Factory, Devin, Warp, and ZCode.

Amended 2026-11-12: decisions 1, 3, 8 and 23 and one rejected alternative changed when
[ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md) limited
blocking to machine facts and confident model answers; format mismatches in agent-written text
were stalling runs for hours.

Amended 2026-11-08 by the conductor run-speed programme: decision 9's changed-path basis
becomes committed plus dirty (D44), and decision 23 adds `KW-ARB-PLANREAD-001`.

Amended 2026-11-11 by the arbiter decision-model savings work, phase 1: decisions 24 to 29
add secondary triggers, the four new trigger families and the two extended ones, per-rule
shadow with one model-targeting switch, render-time model targets, a per-harness model
override declaration, and the run-store rule. Decision 9 gains the hunk-level lens skip
(`KW-ARB-LENS-002`) in decision 30.

Amended 2026-11-11 by the arbiter decision-model savings work, phase 3: decision 9's rule that a
finding is never dropped on a model answer alone is qualified by the nitpick filter
(`KW-ARB-NITPICK-001`), which drops only a confident nitpick and only once enforced. Decision 29's run
stores are implemented under `<arbiter-dir>/stores/`. The triggers `intake.shortcut`,
`triage.shortcut`, `docs-impact.shortcut`, `delegate.failure`, `sections.select` and `comment.triage`
are derived as the phase 1 catalogue set out. The `answer`, `same-failure`, `advise`, `drop` and
`duplicate` effects are live on their rules once enforced. Nine phase 3 rules ship in shadow.

Amended 2026-11-11 by the arbiter decision-model savings closeout (decision 39 of that
specification): the rule that an unverified capability is unsupported governs the per-dispatch
model override only. The hook-capability rule above, that undocumented cells count as supported
until a defect proves otherwise, is unchanged.

## Context

The Squad's in-flight decisions were made by the model being governed. Delegation scope was
enforced only by the conductor's own instructions. The ready queue was held only by the
conductor's attention. Delegation rosters were recorded as `permission-not-expressible`
degradations. Review fan-out spawned one seat per lens even when the lens did not apply.
Each of these is a place where an exact fact exists but no plain code reads it, or where a
judgement about meaning is made without a confidence attached.

## Decision

1. **Three steps, in order (D1).** Step 0 answers only from facts the machine produces itself
   (git, files on disk, the harness payload, the embedded catalog) through a closed predicate
   set (`all`, `any`, `not`, `exists`, `equals`, `in`, `matches`, `subset-of`, `intersects`,
   `count`), plus the answers that let a dispatch proceed. A judgment that rests on reading
   agent-written prose is a step-1 question ([ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)).
   Step 1 asks a decision model one batched call per trigger. Step 2 is a reasoning agent
   dispatched after the block, never inside the hook. First match wins inside step 0; one
   confident red flag escalates the trigger without averaging.
2. **Separation (D2, D16).** The feature is named Kyber Arbiter — JEV names one provider, not
   the feature. Hook and MCP surfaces ship in a separate `kyber-weave-arbiter` binary with no
   Spectre.Console dependency; human commands live in the existing CLI as
   `kyber-weave arbiter …`; the engine lives in Core and serves both.
3. **Enforcement comes from the harness (D3–D5).** Hooks fire from harness machinery, never
   from the gated agent's instructions. Harnesses without hooks use the MCP fallback, called
   by the conductor and code-reviewer. An internal hook error allows the dispatch and logs
   `KW-ARB-HOOK-001` (ADR 0072), inside a per-hook latency budget, with an audit for
   delegations that have no matching decision.
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
8. **Undecidable maps by trigger family (D10).** Every family maps `undecidable` to its
   proceeding effect: conductor and investigate triggers allow and log, as review triggers
   always did (ADR 0072). A plan with no parseable tasks is asked about, not escalated.
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
12. **Thirty-two shipped rules with permanent ids (D14).** Phase 1 shipped twenty-one. Phase 2 ships two more (`KW-ARB-DIFFICULTY-001`, `KW-ARB-LADDER-001`), and phase 3 ships nine. `KW-ARB-RUN-001` and `KW-ARB-CHECKOUT-001` were added with [ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md), so the count is now thirty-four. Hosts tune shipped rules by id
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
    a path under `config.planning-dirs`; if it does, the decision model is asked whether the worker
    is told to open it, and only a confident yes escalates (ADR 0072). The routing header is
    removed first because that block names the plan by design, so a rule reading the raw prompt
    would fire on every dispatch. Documentation-profile targets are exempt, because a `docs-dev`
    dispatch naming the plan at closeout is correct. It shares `PlanReadInstruction` with
    `KW-DOC-TASKS-005`, so the prompt-time ban and the authoring-time check cannot drift apart, and
    it may not fire where dispatch hooks are unconfirmed — a harness with no hook runs no rule, and
    claiming otherwise would report a check that never executed.
24. **Secondary triggers ride one primary dispatch (D14, D35).** A secondary trigger is derived
    from the primary trigger of the same event, never classified from its own payload. It is
    evaluated behind the primary: after a primary step 0 that does not short-circuit, its rules
    share the event's single step-1 batch, and its outcome is delivered only when the primary
    outcome is `allow`. Each secondary writes its own decision record, sharing the event's
    ledger id and naming the primary record. Over budget, the secondary questions are removed
    before the primary's, so a new rule never pushes a primary rule over budget. Phase 1 ships
    the mechanism and derives no secondary trigger; its over-budget path re-sends the primary
    alone (see the architecture guide).
25. **Families for the new judgements (D14, D15).** ModelTarget (`keep`, `retarget`; undecidable
    `keep`), Shortcut (`spawn`, `answer`; undecidable `spawn`), FailureCluster (`allow`,
    `same-failure`; undecidable `allow`) and Advice (`keep`, `advise`; undecidable `keep`) are
    new families. LensReturned gains `drop` and RefuteSpawn gains `drop` and `duplicate`. Each
    family fixes its allowed effects and its undecidable mapping, and a host cannot remap either.
    A host rule may not use an effect that only a shipped rule may use (`KW-ARB-CONFIG-015`).
26. **Per-rule shadow and one model-targeting switch (D22, D41).** `ArbiterRule` carries a
    `shadow` flag that a host may set on a shipped rule. A shadowed rule is evaluated and logged
    in full, but contributes the family's neutral effect to the combined outcome. The two
    model-targeting rules take their shadow state from `arbiter.model-targeting.mode` alone, so
    a `shadow` override on either fails `KW-ARB-CONFIG-014`. New step-1 rules ship in shadow
    until thresholds are measured from the decision log. The step-1 questions ADR 0072 added
    to existing conductor rules enforce at once: they can block only on a confident answer, and
    the step-0 escalations they replace enforced before them.
27. **Model profiles resolve at render time (D16, D17).** Squad's render resolves each profile to
    its model token and writes a per-harness model-targets table. The hook host reads that table
    and never references the Squad assemblies, so the layering rule holds and no hook process
    loads the bundled matrix.
28. **The per-dispatch model capability is declared per adapter (D18, D19, D40).** Each adapter
    declares whether one dispatch can name its own model, the field that carries it and the
    accepted value form, from vendor documentation. An undocumented capability is unsupported.
    This is stricter than the general rule in decision 5, because a wrong model claim is silent
    and a wrong hook claim is not; decision 5 governs hook behaviour only (D39). A written
    `delivered: true` means the field was written, not that the model ran.
29. **Text from earlier events lives only in run stores under the Arbiter directory (D24).** The
    ledger and decision log keep digests only. Run stores are phase 3; phase 1 ships their size
    and retention keys and no store.
30. **Hunk-level lens skipping (D21).** `KW-ARB-LENS-002` is a step-0 rule that skips a lens only
    when every changed path is comment-only, rename-only or Markdown and the changed set is
    non-empty. It names six lenses. It never names `security`, `documentation-accuracy` or
    `blast-radius-revertibility`. An unmeasured change answers `undecidable`, which allows.

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
- **Effects on the conductor family** for `retarget` and the skip outcomes. Rejected: they
  would mix two kinds of outcome on one dispatch and let a shortcut answer stand in for a
  conductor decision (decision 24, D15).
- **Resolving model tokens in the hook host.** Rejected: the hook host may not reference the
  Squad assemblies, and the bundled matrix is not on disk at hook time (decision 27, D16).
- **The in-flight ledger as the text store.** Rejected: the ledger is digest-only by contract
  (decision 29, D24).

## Consequences

- **Phase 1 exceeds the review ceiling.** At roughly 17,900 lines of code and tests it is
  over `review.policy.max-reviewable-lines`, so the Phase 1 verdict is `NEEDS_HUMAN` on size
  as well as on reserved paths. That escalation is accepted, not worked around.
- **Unsupported harnesses stay fallback-only until the matrix is verified.** Warp and ZCode have
  no hooks, and the MCP fallback records the harness as `fallback-only` with the reason code
  `hooks-disabled-by-harness` or `dispatch-unconfirmed` until the hook matrix is verified.
- **Completion is the worker's own report.** A task counts as complete when its worker's
  report reads `RESULT: PASS`, because the separate task reviewer the design named no longer
  exists. A worker can therefore mark its own task complete; the final review still judges
  the whole change.
- **Ledger loss needs attestation.** Completion lives in `artifacts/`, which
  is not committed. On a fresh clone the readiness question sees no completion and no stored
  report, so a confident `waiting` answer escalates dependent tasks until architect attests,
  after user confirmation, what is complete (D31).
- **Step-1 thresholds are measured for Jev 1.13 and Jev 1.14.** Shipped thresholds started
  conservative for `jev-1.13.0`. A measurement on 2026-11-09 with 120 labelled cases left
  them unchanged and moved the default model to `jev-1.14.0`; `jev-1.13.0` stays supported.
  OpenRouter's `typesafe/jev-1.14` and `typesafe/jev-1.13`, the ids measured, are tuned too.
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
- **Phase 1 is a foundation, not a behaviour change.** It ships the trigger families, effects,
  shadow mechanism, configuration keys, the model-targets table and the per-harness capability
  declaration. Only `KW-ARB-LENS-002` changes behaviour. No secondary trigger is derived, no
  rule changes a model, and the hook writes no model field. Each later phase adds its rules
  and derivations under the decisions above.
- **Model targets are a persisted file contract.** The table's schema id
  `kyber-arbiter.model-targets/v1` and its path per scope are permanent once a release
  writes them.

## Permanent identifiers

Rule ids `KW-ARB-*` (34 shipped), `KW-REVIEW-026` (gate not applicable), the reason codes
`fallback-only`, `project-only`, `dispatch-unconfirmed`, and `hooks-disabled-by-harness`, the
record schemas `kyber-arbiter.decision/v1` and `kyber-arbiter.ledger/v1`, and the routing
headers `KYBER-ARBITER:`, `PLAN_FILE:`, `TASK:`, `LENS:`, `REFUTE:`, and `REVIEW_RUN:` are permanent.
Renaming one silently un-suppresses host tuning, receipts, and logs keyed on it.

The savings phase 1 adds `KW-ARB-LENS-002`, `KW-ARB-CONFIG-012` to `KW-ARB-CONFIG-015`, and
`KW-ARB-MODEL-001`. The ids `KW-ARB-DIFFICULTY-001` and `KW-ARB-LADDER-001` shipped in phase 2. The savings phase 3 adds
`KW-ARB-NITPICK-001`, `KW-ARB-NITPICK-002`, `KW-ARB-DUP-001`, `KW-ARB-TRIAGE-001`,
`KW-ARB-INTAKE-001`, `KW-ARB-DOCIMPACT-001`, `KW-ARB-FAILURE-001`, `KW-ARB-TRIM-001` and
`KW-ARB-COMMENT-001`.

## Related

- [ADR 0054](0054-squad-owned-blocks-in-shared-hook-files.md) — owned entries in shared hook files, the exception to decision 6
- [Kyber Arbiter architecture](../kyber-arbiter/architecture.md) — engine, rules, configuration
- [Kyber Arbiter runbook](../kyber-arbiter/runbook.md) — hooks, trust, failure behaviour
- [ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md) — what may block
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — hook wiring per harness
- [Review council architecture](../code-review/architecture.md) — review triggers and gate applicability
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-ARB-*` id and `KW-REVIEW-026`
- [ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the tracked task file, whose prompt-time ban decision 23 enforces with the same detector
