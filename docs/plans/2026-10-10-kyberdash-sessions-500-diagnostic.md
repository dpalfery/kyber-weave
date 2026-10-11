---
id: plans/kyberdash-sessions-500-diagnostic
title: KyberDash Sessions 500 diagnostic and remediation gate
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-10
development-mode: test-first
---

# KyberDash Sessions 500 diagnostic and remediation gate

## Status

**Blocked pending a captured failure.** The reported Sessions 500 is not reproducible in the
current local runtime. The direct engine route, the Vite proxy route, all nine current session
detail routes, and all 162 current turn-content requests returned 2xx during the 2026-10-10
investigation. This plan permits evidence capture and classification only. It must be revised
with an exact RED test before any application-code fix begins.

## Problem

The Sessions page has twice been reported as showing HTTP 500. Commit `eb11c26` fixed one
confirmed startup failure by preserving the `node:sea` import in the bundled CLI, and the
running bundle contains that import. The same visible symptom was reported again after that
fix, but repeated probes now succeed and no failing request, response, or matching engine
diagnostic was preserved.

`runWebDashboard` catches any exception thrown while serving a request, emits the real error
only to engine stderr, and sends the browser a generic `500 {"error":"internal server error"}`.
The tray launches its engine with stderr connected to `Stdio::null()`. A browser screenshot or
the text "500" therefore cannot distinguish an engine exception, a dead or stale engine behind
the Vite proxy, a proxy-generated failure, or a session-specific endpoint failure.

## Goal

Capture one recurrence without restarting or mutating the runtime, correlate the browser
failure with the exact engine and proxy state, and classify the failing boundary. If every
current probe remains 2xx, stop and ask the user to leave the failure visible for evidence
capture. Once a failure is reproduced, revise this plan with the exact regression-test file,
RED assertion, production symbols, and GREEN acceptance before dispatching source changes.

## Scope

In scope:

- the browser request that makes Sessions display 500, including initiator and selected
  session/harness;
- the Vite proxy at `127.0.0.1:5173` and the engine API at the URL recorded in
  `~/.kyberdash/server.json` (currently `127.0.0.1:4747`);
- tray and engine process identity, listener ownership, and the engine stderr sink;
- the list route, the selected session-detail route, and any turn-content request that fails;
- a test-first plan revision after a real failure is captured.

Out of scope until that evidence exists:

- application, test, tray, build, or configuration changes;
- restarting the tray, engine, Vite server, or receiver before the evidence snapshot;
- database writes, wipe, refresh, import, migration, or repair;
- treating a 404 for a genuinely absent content part as the reported 500;
- speculative logging, retry, proxy, SQLite, or lifecycle fixes.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | May implementation begin from the repeated symptom without a captured failing request? | No. Capture and classify a real failure first. | Current probes are all 2xx, and the same visible 500 can originate at several boundaries. Guessing would create a test for a theory rather than the defect. | ANSWERED — conductor direction, 2026-10-10 |
| D2 | Which architecture governs diagnosis and any later repair? | The shared `kyberdash web` engine, one `/api/kyber/*` API, and one canonical store remain authoritative; the web and tray stay display layers. | This is the existing KyberDash architecture and display-layer rule. | ANSWERED — repository architecture, reaffirmed by conductor, 2026-10-10 |
| D3 | What happens when all live probes remain 2xx? | Stop without restarting or editing source, report the successful probes, and ask the user to leave the next failure visible. | A restart destroys the process and proxy state needed to distinguish the failure. | ANSWERED — conductor direction, 2026-10-10 |
| D4 | When may a regression test and implementation task be added? | Only after the captured failure identifies the failing route and boundary; revise and reapprove the exact Test contract first. | Test-first work requires RED evidence against the real defect and the same assertion for GREEN. | ANSWERED — conductor direction and test-first contract, 2026-10-10 |

## Investigation findings

- `GET http://127.0.0.1:4747/api/kyber/sessions` returned 200.
- `GET http://127.0.0.1:5173/api/kyber/sessions` returned 200 on five repeated probes.
- All nine current `GET /api/kyber/session/:id` requests returned 2xx.
- All 162 current turn-content requests returned 2xx.
- The tray-owned engine and OTLP receiver were listening on loopback, and
  `~/.kyberdash/server.json` named the current engine PID and port.
- `dash/src/server/routes.ts::handleKyberRequest` serves `/api/kyber/sessions` from
  `KyberBridge.listSessions` and returns 200 when that call completes.
- `dash/src/cli/web.ts::runWebDashboard` maps an uncaught request exception to a generic 500
  and writes the real error to stderr.
- `dash/tray/src-tauri/src/supervisor.rs::SystemServerSpawner::spawn` pipes stdout for the
  listening handshake but discards stderr. During a tray-owned recurrence, capture and record
  that sink as unavailable rather than claiming an engine error log exists.

## Test contract

This Draft covers diagnosis only. Every row is explicitly no-test because it mutates no
product behavior. A later application-code task is forbidden until this table is revised with
the exact failing test surface and the revised contract is approved.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 — preserve the live failure | No automated test; local evidence under `artifacts/dash-sessions-500/` | Browser Network inspection plus read-only `curl`, `ps`, `lsof`, and `~/.kyberdash/server.json` reads | One failing browser request is saved with URL, method, initiator, status, response, timestamp, selected scope, process/listener ownership, and available stderr evidence before any restart | N/A — diagnostic capture | Evidence bundle is complete, or every probe is 2xx and execution stops |
| T2 — classify the boundary | No automated test; T1 evidence review | Compare the exact path through the proxy URL and the engine URL, preserving status/body/timing | Evidence identifies engine request failure, proxy/runtime routing failure, stale process discovery, or a session-specific endpoint | N/A — read-only classification | One boundary is supported by matching observations; unsupported hypotheses are rejected |
| T3 — revise the RED/GREEN contract | This plan and `<plan-index>` only | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | The revised plan names the exact production symbols, test file, runner, observable failure, RED evidence, and GREEN acceptance | A focused test fails for the captured defect before production code changes | Same focused test passes after the smallest root-cause fix, with the unchanged assertion |

## Tasks

### T1 — Preserve one live recurrence

- **Objective:** Capture the failing browser request and the matching local runtime state
  before any restart, rebuild, or mutation.
- **Owned paths:** `artifacts/dash-sessions-500/` only; no repository source or database
  writes.
- **Required skills:** none; read-only runtime diagnosis.
- **Procedure:**
  1. In browser Network data, record the exact failing request URL, method, initiator, status,
     response headers/body, timestamp, selected harness, selected session, and the UI action
     that triggered it.
  2. Copy `~/.kyberdash/server.json`; record the tray and engine PIDs and command lines; record
     the owners of the engine and OTLP listeners.
  3. Record the engine stderr destination. If the tray owns the engine and the descriptor is
     `/dev/null`, record that fact as unavailable; do not invent or substitute a tray log.
  4. Replay the exact path once against the proxy origin and once against the engine origin,
     saving status, headers, body, and timing without printing stored session content into the
     chat transcript.
  5. If the list succeeds but the selected detail or content call fails, preserve the exact
     encoded path and session identity needed to reconstruct a sanitized fixture.
- **Acceptance:** The evidence captures a real non-2xx request and all available correlated
  runtime state. If all requests are 2xx, stop immediately and request user evidence while the
  next failure is visible.

### T2 — Classify the failing boundary

- **Objective:** Reduce the captured recurrence to one supported boundary before proposing a
  test or fix.
- **Owned paths:** T1 evidence only.
- **Required skills:** `architect`.
- **Classification rules:**
  - proxy fails while the identical direct-engine request succeeds: proxy/dev-runtime path;
  - proxy and direct engine return the same engine 500: engine route/store path;
  - proxy fails and the recorded engine URL is not reachable or disagrees with process/listener
    state: lifecycle or stale-discovery path;
  - session list succeeds while one detail/content request fails: session-specific path;
  - browser shows an error while every initiated request is 2xx: client state/rendering path.
- **Acceptance:** The report names the failing boundary and cites the request, response, process,
  and timing evidence. Any claim about SQLite, bundling, proxying, or client state must be tied
  to that evidence.
- **Dependency:** T1.

### T3 — Return to architect for test-first revision

- **Objective:** Convert T2's reproduced failure into an implementation-ready test and repair
  plan.
- **Owned paths:** this plan and `<plan-index>` only.
- **Required skills:** `architect`, then `test-dev` and the production-code skill selected from
  the proven boundary.
- **Acceptance:** The revised plan identifies exact files/symbols and adds a RED/GREEN Test
  contract. No application-code task is dispatched before that revision is approved.
- **Dependency:** T2.

## Dependency graph and concurrency

```text
T1 -> T2 -> T3 -> STOP for revised-plan approval
```

`MAX_CONCURRENCY: 1`. Every later task consumes the evidence and conclusion of the previous
task. Parallel probes during T1 are allowed only after the browser failure and process snapshot
have been preserved; they remain one task because they describe one instant of runtime state.

## Risks and controls

- **Failure disappears before capture:** stop and wait for the user to leave the next instance
  visible; do not manufacture load or restart services to chase it.
- **Evidence contains private prompt/session content:** retain it locally under ignored
  `artifacts/`, redact only when reporting, and preserve the original local artifact.
- **Tray-owned stderr is unavailable:** record the `/dev/null` sink. A later controlled
  reproduction may launch an equivalent engine with stderr redirected, but only after T1
  captures the original process state.
- **A healthy list hides a detail failure:** preserve every browser-initiated Sessions request,
  not only `/api/kyber/sessions`.
- **A new test encodes a guess:** T3 blocks test authorship until T2 cites a reproduced failure.

## Verification gates

During the current diagnostic phase:

1. Evidence completeness review against T1 acceptance.
2. Boundary classification review against T2's rules.
3. `docs validate .` and `docs drift .` after every plan revision.

After T3 adds implementation tasks, the revised plan must include the focused RED/GREEN runner,
the four KyberDash gates, and browser acceptance against the local branch. Those commands are
not claimed by this Draft because no source change is authorized yet.

## Review and closeout

- `task-reviewer` checks the evidence contract and rejects any fix unsupported by a reproduced
  failure.
- `code-review` reviews the eventual accumulated change before commit or push.
- `docs-dev` updates `docs/dash/runbook.md` if the repair establishes a lasting troubleshooting
  or runtime-observability contract, records an explicit documentation waiver if it does not,
  archives this plan on completion, and updates `<plan-index>`.
- ADR waiver is expected unless the proven fix changes the established engine/display-layer
  boundary; a lifecycle or implementation repair inside that boundary is not a new
  architectural decision.

