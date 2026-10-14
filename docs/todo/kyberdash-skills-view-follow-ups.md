---
id: todo/kyberdash-skills-view-follow-ups
title: KyberDash skills view follow-ups
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-14
---

# KyberDash skills view follow-ups

These items were raised when the [skills view plan](../archive/plans/2026-10-13-kyberdash-skills-view.md) closed. None of them blocks the delivered feature. The feature's contract is in [dash/architecture.md](../dash/architecture.md#skills-view-contract), and the per-harness status is in [dash/telemetry-inventory.md](../dash/telemetry-inventory.md#skill-availability-by-harness).

## Items

**(a) Inactive-skill finding volume.** `detectInactiveSkillReference` (`dash/src/analysis/findings.ts:1566`) emits one low-confidence finding per listed skill that was never invoked. A 70-skill Claude listing with no `Skill` call therefore yields about 70 findings, all ranked last. Collapse them into one finding per session, or cap the number. Keep the evidence-of-use wording (ADR 0013 D15/D16).

**(b) Payload growth from per-turn listings.** Each turn stores its own `skills_listed` array. The owner's estimate for a 7,000-turn Antigravity session is about 0.85 MB at 25 skills and 3.4 MB at 100 skills, with the whole payload reaching up to 12 MB. The estimate was not re-measured at closeout. Store listing deltas instead of a full array per turn.

**(c) Missing captures.** Pi, OpenCode and Copilot CLI have no captures. Their listing is `not_measurable`, and their wire formats are unverified (see the READMEs under `dash/src/canon/fixtures/skills/`). The Claude Desktop `skill_listing` is also unverified. No Copilot skill invocation record has been observed. Each source needs a sanitized capture before any listing support is added.

**(d) Minor cleanups.**
- `SkillRow.derivedTokens` (`dash/src/canon/skills.ts:63`) is not used by production code. The payload's `derived_tokens` is computed from the first-observed description.
- The line skip in `loadClaudeCalls` (`dash/src/providers/claude.ts:316`) drops any line containing `"skill_listing"`, whether or not a listing is found. It should skip only when a listing is found.
- The inactive-skill description reads "Turn 0 is the last of 0 recorded turns" when no turn is recorded (`dash/src/analysis/findings.ts:1630`). Reword the zero-turn case.

**(e) Live visual check.** The Skills section was verified only by component tests and a build. Check it in a live dashboard session for every state: populated, not measurable with its reason, not yet projected, empty rows, and the skill drawer. This is an owner gate.

**(f) First-observed description on every turn.** A skill's listed description is the first one observed in the session, and every turn shows it, even when a later listing changes it. This is a documented limit ([architecture, Known limits](../dash/architecture.md#skills-view-contract)). Decide whether it is acceptable or whether turns need their own text.

## Acceptance criteria

- (a) One finding per session, or a capped count, with the same evidence-of-use wording.
- (b) Listing deltas are stored, and the per-turn listing is still served.
- (c) A sanitized capture exists for each source before any listing support is added.
- (d) Each cleanup is made, and its test or wording check passes.
- (e) The owner confirms each state in a live session.
- (f) The owner accepts the limit, or per-turn text is stored.
