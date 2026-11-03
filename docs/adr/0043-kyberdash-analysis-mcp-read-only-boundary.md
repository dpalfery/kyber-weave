---
id: adr/0043-kyberdash-analysis-mcp-read-only-boundary
title: "KyberDash Conversational Analytics over MCP: Local Read-Only Boundary with an Attach-Only Adapter"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-25
---

# ADR 0043: KyberDash Conversational Analytics over MCP: Local Read-Only Boundary with an Attach-Only Adapter

## Status

Accepted, 2026-10-25. Records the decision to expose KyberDash evidence through a
local, read-only MCP surface (`kyberdash mcp`, eight `dash_*` tools) computed by the
shared engine behind additive `/api/kyber/analytics/*` routes, with an attach-only
stdio adapter that never opens the store, never starts or stops the engine, and never
performs a write.

## Context

KyberDash holds evidence about agent sessions, context composition, cache behavior,
tool and skill usage, delegation, timing, costs, and observable outcomes. An assistant
could not interrogate that evidence conversationally: the existing documentation MCP
answers a different question, and the context-review endpoint reviews supplied content
rather than providing analytics. The delivery is recorded in
[the archived analysis-MCP plan](../archive/plans/2026-10-24-kyberdash-analysis-mcp.md).

Two constraints shaped the answer. The tray and web dashboard are display layers over
one engine, one `canon.db`, and one `/api/kyber/*` API
([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md)), so a second derivation of
domain figures or a second store would violate the architecture. And starting `web`
can start jobs and a receiver, so anything that launches the engine as a side effect
cannot be read-only.

## Decision

Expose eight tools — `dash_status`, `dash_catalog`, `dash_query`,
`dash_search_sessions`, `dash_inspect`, `dash_findings`, `dash_compare`,
`dash_outcomes` — over stdio (`kyberdash mcp`), each mapping one-to-one onto an
additive route under `/api/kyber/analytics/` (`GET status`, `GET catalog`,
`POST query`, `POST search-sessions`, `POST inspect`, `POST findings`,
`POST compare`, `POST outcomes`). Computation stays in the shared engine service
(`dash/src/analysis/query/`); the adapter (`dash/src/mcp/`) never imports the
canonical store and never derives a domain figure.

The boundary terms, all enforced by schema and engine rather than by annotation:

- **Attach-only lifecycle.** The adapter discovers the running engine through the
  existing `server.json` receipt (or explicit `--server-url` / `--state-dir`), and
  never launches, stops, migrates, or reprojects it. There is no `--db` flag because
  the running engine owns the datastore choice.
- **Catalog-driven, typed queries.** No SQL, expressions, arbitrary columns,
  filesystem paths, or script execution in tool inputs. Unsupported combinations fail
  before any store read; unsupported measurements return availability reasons.
- **Metadata-only search by default.** Captured text needs `includeContent: true`
  plus an explicit session/turn/block identity, one page at a time.
- **Observed outcomes only.** Missing outcomes and conflicting signals stay explicit;
  comparisons are descriptive, never causal; lower cost or fewer tokens alone never
  establish better work.
- **Local loopback only.** Only literal `127.0.0.1` / `[::1]` origins, no redirects;
  no remote HTTP MCP listener or authentication in this surface.

## Alternatives considered

- **Direct-SQL analytics in the adapter.** Rejected: it would duplicate derivation
  outside the shared projection, recreating the two-derivation divergence the
  projection seam exists to prevent, and bypass the materialized-reader and budget
  contracts.
- **Auto-starting the engine on MCP connect.** Rejected: starting `web` can start
  jobs and a receiver, violating the read-only expectation. A missing or stale
  receipt yields actionable `engine_unavailable` instead.
- **Unrestricted query language (SQL / expressions / arbitrary columns).**
  Rejected: the projection defines the figures; an open language turns the adapter
  into a second reporting path with no measurability contract.

## Consequences

- Identical scoped requests through MCP and HTTP return identical domain data and
  measurement/coverage/evidence, apart from transport timing metadata. The MCP tool
  surface names (`groups`, `text`, inferred inspect `kind`) are mapped to the
  engine body (`groupBy`, `query`, explicit `kind`) inside the adapter before any
  engine call; the mapping is adapter behavior, not a second contract.
- Every response uses the versioned common envelope (`contractVersion: 1`) with
  scope, data, measurement, coverage, freshness, evidence, pagination, and error;
  cursors bind contract/catalog version, query, engine instance, and projection
  generation.
- Host-client compatibility is explicitly not claimed: the release SEA smoke proves
  protocol, tool list, and the attach-only boundary, not that every host client
  renders the tools (recorded `[NOT LIVE-VERIFIED]` in the plan closeout).
