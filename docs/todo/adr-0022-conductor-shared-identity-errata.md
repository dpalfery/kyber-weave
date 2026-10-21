---
id: todo/adr-0022-conductor-shared-identity-errata
title: ADR 0022 calls the conductor a shared identity; it is unoccupied
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-16
---

# ADR 0022 calls the conductor a shared identity; it is unoccupied

[ADR 0022](../adr/0022-antigravity-native-agents.md) decision 1 closes with a clause that no
longer holds:

> Shared conductor identities emit only the canonical skill (suppressing the redundant skill
> projection), while the agent renders natively.

`conductor` is not a shared identity. It has no canonical skill, so the role-skill fallback
lowers it through the *unoccupied*-identity rule to a same-name role skill, and the
`role-skill.shared-identities` list in
[`products/kyber-squad/profiles/fallbacks.yml`](../../products/kyber-squad/profiles/fallbacks.yml)
names only `product-owner` and `bug-crusher`
([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md), which introduces the
primary shared identity). The clause is stale — `conductor` was removed from
`shared-identities` in commit `d184cd7` (2026-09-08), before ADR 0022 was accepted
(2026-09-23).

An accepted ADR is edited in place to state only the decision in force, so the stale clause is
corrected in ADR 0022 itself rather than superseded by a new record. The Antigravity
reclassification decision itself is unaffected:
decision 3 correctly renders `conductor` as a native primary agent. This todo was recorded
during the corpus two-audience remediation, which corrected the same claim in the root
`README.md`.

This todo is complete when ADR 0022 decision 1 no longer names the conductor as a shared
identity and states that it is an unoccupied identity.
