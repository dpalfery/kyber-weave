---
id: adr/0041-kyberdash-retire-legacy-context-schema-routes
title: Retire the Uncalled Context and Schema Routes; Supersede ADR 0007 D3 in Part
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-20
supersedes:
  - adr/0007-kyberdash-agent-session-analysis-integration
---

# ADR 0041: Retire the Uncalled Context and Schema Routes; Supersede ADR 0007 D3 in Part

## Status

Accepted, 2026-10-20.

This record supersedes the "Backward-compatible endpoints" clause of
[ADR 0007](0007-kyberdash-agent-session-analysis-integration.md) decision D3
insofar as it names `GET /api/kyber/context` and `GET /api/kyber/schema`. Both
routes are deleted. D3's `/api/kyber/timeline` clause and everything else in
ADR 0007 stand, and ADR 0007 is not edited beyond a status note — the same
shape as [ADR 0008](0008-kyberdash-single-canonical-store.md) superseding one
decision of ADR 0007 and [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md)
superseding one decision of ADR 0023.

## Context

ADR 0007 D3 kept three backward-compatible endpoints that default to the first
active session when no id is given: `/api/kyber/context`, `/schema`, and
`/timeline`. The sessions-list payload-independence work measured what those
legacy siblings actually cost: each one reads a session's entire payload blob
(up to 15.3 MB) to serve a small slice — 10,782 B for `/context`, 468 B for
`/schema` — at 64 ms and 65 ms warm respectively. They are unbounded reads with
no consumer: the only references anywhere in the repository were two cases in
`dash/src/cli/web-dashboard.test.ts`, and no live caller — web, tray, CLI, or
report builder — requests either path. Repairing them as bounded reads would
preserve compatibility for nobody, while the landed bounded pattern
(`GET /api/kyber/session/:id/summary`) already serves the one legitimate need
(identity fields without the payload) that an older caller could have had.

## Decision

1. **`GET /api/kyber/context` and `GET /api/kyber/schema` are deleted, not
   repaired.** The routes are gone from the route table in
   `dash/src/server/routes.ts`, and the two test cases that referenced them
   were updated with the deletion. Deletion is the decision: there is no
   consumer to serve, so there is nothing to keep compatible.
2. **Those paths now answer the generic unmatched-path 404.**
   `GET /api/kyber/context`, `GET /api/kyber/schema` — and any non-GET method on
   them — return 404 `{ "error": "Not found" }` from the method-agnostic
   precedence-guard branch, which replaced the routes' own method guards. A
   non-GET on a deleted path therefore answers 404 rather than 405.
3. **Everything else in the route table is untouched.** `/api/kyber/timeline`
   survives; the `GET /api/kyber/session/:id` passthrough and its
   `GET /api/kyber/session/:id/summary` sibling stand; the content routes, the
   list route, and all remaining endpoints stand. No surviving route's matching
   changed — the deletion removed two branches and nothing else.

## Alternatives Considered

- **Bounding the two routes instead of deleting them.** Rejected. A bounded
  `/context` or `/schema` would spend implementation and review on
  compatibility with zero callers, and would keep two more branches in the
  route table that every future sweep must re-disposition.
- **Leaving the routes for external callers.** Rejected. No in-repository
  caller exists, and an external caller cannot be proven absent — but an
  uncalled 15.3 MB read kept alive on speculation is exactly the hazard the
  payload-independence work exists to remove. An external caller that surfaces
  gets the same explicit 404 every other unknown `/api/kyber/*` path gets.

## Consequences

- Any caller — in this repository there is none — requesting `/api/kyber/context`
  or `/api/kyber/schema` receives 404 `{ "error": "Not found" }` for every HTTP
  method, instead of a payload-derived slice (GET) or 405 (other methods).
- The route table has two fewer unbounded payload reads; future payload sweeps
  no longer need to disposition these paths.
- `/api/kyber/timeline` keeps its ADR 0007 D3 standing unchanged, including its
  own unbounded-read follow-up, which is tracked separately.

## Related

- [ADR 0007: KyberDash Agent Session Analysis Integration, Dual-Database Architecture, and Navigation Topology](0007-kyberdash-agent-session-analysis-integration.md)
- [ADR 0008: Single Canonical Store; Supersede ADR 0007 D4](0008-kyberdash-single-canonical-store.md)
- [KyberDash architecture](../dash/architecture.md)
