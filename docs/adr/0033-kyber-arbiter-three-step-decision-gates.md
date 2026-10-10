---
id: adr/0033-kyber-arbiter-three-step-decision-gates
title: Kyber Arbiter three-step decision gates
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-09
---

# ADR 0033: Kyber Arbiter three-step decision gates

## Status

Accepted, 2026-10-03. Records the decision to gate Squad delegations and review fan-out
through a rule engine that answers in up to three steps — plain code, a decision model, a
reasoning agent — enforced by harness hooks, delivered in three phases by harness group:
Phase 1 Claude, Copilot in VS Code, Copilot CLI, and OpenCode; Phase 2 Pi, Codex, and Cursor;
Phase 3 Kilo, Antigravity, Factory, Devin, Warp, and ZCode.

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
   [ADR 0034](0034-squad-owned-blocks-in-shared-hook-files.md) records the exception to the
   owned-files-not-settings boundary.
7. **Plans are parsed at evaluation time (D9, D30).** The parser reads plan and spec-task
   artifacts with C#, Markdig, and regex, writes nothing, and resolves `TASK:` ids against
   both grammars. A task that lists no files skips its file-scope checks with a logged skip
   rather than escalating.
8. **Undecidable maps by trigger family (D10).** Conductor and investigate triggers escalate
   on `undecidable`; review triggers allow and log, because lenses and refutations run as
   they do today. A plan with no parseable tasks escalates.
9. **Review uses hooks with refutation as step 2 (D11).** Lens applicability gates the spawn
   (skip below P(applies) 0.1); quote presence and hunk membership annotate the return; a
   `choice` question verifies claims at confidence 0.9 or a corroborating gate skips the
   refutation. A finding is never dropped on a model answer alone.
10. **Gates declare `applies-when` (D12).** A gate that matches no changed path is reported
    as not applicable (`KW-REVIEW-026`), never executed, and never counted as passed or
    failed. Older `review-gates/v1` reports still read.
11. **Reserved paths keep the council (D13).** `always-human` paths still run code-reviewer
    to `NEEDS_HUMAN`; delegation rules still apply first.
12. **Eighteen shipped rules with permanent ids (D14).** Hosts tune shipped rules by id
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
    credential store entry for the endpoint origin, then no key; loopback needs none. Writes
    travel on stdin, never argv; the key appears in no file and no log.
16. **No provider by default (D19).** Provider `none` switches model rules off: step 0 and
    step 2 still run, step 1 reports not evaluated, and `doctor` warns.
17. **TypeSafe's skill is reference only (D20).** The documentation links it; nothing installs,
    vendors, or ships it.
18. **Delegation identity is explicit headers (D21, D27).** The conductor writes `PLAN_FILE:`
    and `TASK:`; code-reviewer writes `LENS:` and `REFUTE:` alongside the marker. Index
    inference is diagnosis only, never a decision input.
19. **Three-part install (D22).** Binary via `install.sh`/`update` with `--no-arbiter`;
    hook wiring via `squad install`/`update` when `arbiter.enabled` is true; provider and key
    via `arbiter setup` into the user override and the credential store. A global install
    renders no hooks and records `arbiter-not-enforced`.
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
- **Unsupported harnesses stay advisory until Phase 3.** Warp and ZCode have no hooks; the
  MCP fallback that covers them ships last, and affected triggers record
  `arbiter-not-enforced` meanwhile.
- **Ledger loss needs attestation.** Completion and RED evidence live in `artifacts/`, which
  is not committed; a fresh clone escalates dependent tasks until architect attests, after
  user confirmation, what is complete (D31).
- **Step-1 thresholds are tuned for one model.** Shipped thresholds start conservative for
  `jev-1.13.0`; any other model warns in `doctor` until its thresholds are tuned from the
  recorded decision log.
- **Phase PRs target an integration branch.** While the specification was open,
  `docs validate --merge-ready` reported `KW-DOC-LIFECYCLE-003` on the Phase 1 and Phase 2
  PRs. Each phase PR targeted the long-lived integration branch `integration/kyber-arbiter`,
  and one merge to `main` follows the closeout, which archived the specification.

## Permanent identifiers

Rule ids `KW-ARB-*` (18 shipped), `KW-REVIEW-026` (gate not applicable), the degradation
code `arbiter-not-enforced`, the record schemas `kyber-arbiter.decision/v1` and
`kyber-arbiter.ledger/v1`, and the routing headers `KYBER-ARBITER:`, `PLAN_FILE:`, `TASK:`,
`LENS:`, and `REFUTE:` are permanent. Renaming one silently un-suppresses host tuning,
receipts, and logs keyed on it.

## Related

- [ADR 0034](0034-squad-owned-blocks-in-shared-hook-files.md) — owned entries in shared hook files, the exception to decision 6
- [Kyber Arbiter architecture](../kyber-arbiter/architecture.md) — engine, rules, configuration
- [Kyber Arbiter runbook](../kyber-arbiter/runbook.md) — hooks, trust, fail-closed operation
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — hook wiring per harness
- [Review council architecture](../code-review/architecture.md) — review triggers and gate applicability
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-ARB-*` id and `KW-REVIEW-026`
