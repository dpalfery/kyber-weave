---
id: plans/2026-11-07-otlp-metrics-protobuf
title: Support OTLP/HTTP Protobuf on POST /v1/metrics in KyberDash receiver
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-07
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash]
---

# Support OTLP/HTTP Protobuf on POST /v1/metrics in KyberDash receiver

**Status: Archived.** Closed 2026-11-07; see the Closeout mapping section at the end. Approved for execution on the conductor's "approve and execute" gate (decision D5).
**Development mode:** `test-first` (default; strictly test-first with RED/GREEN gates).
**Autonomy:** `autonomous` (every open item decided by recommendation and recorded below).
**Scale:** `extends-component`, component `KyberDash`.

## Problem and goal

**Problem.** Standard agent harnesses (such as Kilo and OpenCode) export OpenTelemetry metrics over `application/x-protobuf` by default (`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`). However, the KyberDash OTLP receiver at `dash/src/otel/receiver.ts:1642` explicitly restricts `POST /v1/metrics` to `application/json` (`path !== OTLP_METRICS_PATH || encoding === 'json'`) because only `decodeOtlpMetricsJson` was implemented. When an agent harness sends metrics over HTTP/protobuf, the receiver rejects the request with HTTP 415 and records an `OTLP_UNSUPPORTED_MEDIA_TYPE` error in its rolling five-minute error window (`failures5m`), triggering a "Collector down" health indicator in the dashboard.

**Goal.** Support standard OTLP/HTTP Protobuf encoding on `POST /v1/metrics` using the zero-dependency `PbReader` decoder, matching the behavior already present on `POST /v1/traces` (`decodeOtlpProtobuf`) and `POST /v1/logs` (`decodeOtlpLogProtobuf`). The receiver will decode `ExportMetricsServiceRequest` protobuf payloads into the shared `OtlpMetric[]` model and dispatch them to the configured metrics store without error.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. Provenance is `autonomous: recommendation accepted`.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What metric data shapes must protobuf decoding support? | **RECOMMENDED:** Support all five standard OTLP metric data shapes: `gauge` (field 5, `NumberDataPoint`), `sum` (field 7, `NumberDataPoint`), `histogram` (field 9, `HistogramDataPoint`), `exponentialHistogram` (field 10, `ExponentialHistogramDataPoint`), and `summary` (field 11, `SummaryDataPoint`), plus fallback `unknown` for metrics with no points. | Achieves complete behavioral parity with `decodeOtlpMetricsJson`. Emitting agents use various metric shapes (sums for counters, gauges for memory/gauge stats, histograms for latencies). | autonomous: recommendation accepted |
| D2 | How are protobuf content types accepted on `POST /v1/metrics`? | **RECOMMENDED:** Accept both `application/x-protobuf` and `application/protobuf` (tolerating optional parameters like `; charset=utf-8`) via the existing `encodingForContentType` helper. Remove the path check from `encodingAccepted` so `encoding !== undefined` applies uniformly across traces, logs, and metrics. | Standard OTLP HTTP exporters default to `application/x-protobuf` but some libraries send `application/protobuf`. Both are standard across KyberDash trace and log routes. | autonomous: recommendation accepted |
| D3 | Which decoder implementation strategy should be used? | **RECOMMENDED:** Extend the zero-dependency internal `PbReader` and decoding helpers (`pbMessages`, `pbMessage`, `pbKeyValues`, `pbString`, `pbNumber`, `pbBigInt`, `pbFixed64ToDouble`) already in `dash/src/otel/receiver.ts`. Do not add `@opentelemetry/proto` or `protobufjs`. | Keeps KyberDash zero-dependency for wire decoding, avoids supply-chain and bundle bloat, and aligns with ADR 0009. The OTLP wire schema is append-only and stable. | autonomous: recommendation accepted |
| D4 | Is a new ADR needed? | **RECOMMENDED:** No new ADR. State **ADR waiver**: refines existing OTLP receiver decoding patterns under ADR 0009 and ADR 0033 to support standard protobuf encoding on the `/v1/metrics` signal path; introduces no new architecture boundaries, external dependencies, or persistence schemas. | Adheres to established ADR waiver rules for refining existing architectural patterns. | autonomous: recommendation accepted |
| D5 | Is the Test contract approved? | **RECOMMENDED:** Approve `test-first` mode with the Test contract specified below. | Follows repository test-first development standards. | autonomous: recommendation accepted |

## Investigation findings

**Discovery:** codegraph 10 (`codegraph_explore` over receiver, PbReader, decodeOtlpMetricsJson, OTLP_METRICS_PATH, dispatch, pbFixed64ToDouble, pbAnyValue, OtlpMetric, dataPointFromJson, numericValue), docs 4 (`docs_get_source_repository_and_revision`, `docs_find_documents_citing_code_symbol` [error], `docs_answer_question_from_repo_documentation` [error], `docs_validate_governed_document_schema`, `docs_detect_drift_between_documents_and_code`), direct reads 6 (`dash/src/otel/receiver.ts` lines 790–860 to confirm dataPointFromJson and numericValue handling; `dash/src/otel/receiver.test.ts` lines 150–250 and 251–330 to confirm protobuf test fixture builders; `dash/package.json` to inspect dependencies; `dash/src/otel/receiver.ts` lines 980–1015 to confirm existing PbReader wire documentation; `dash/AGENTS.md` to confirm targeted test commands). Direct reads of indexed code were performed strictly to confirm exact wire field mappings and fixture helper patterns. Read tree: `/Users/dave/git/personal/agy/kyber-weave`.

**Verified facts:**
- In `dash/src/otel/receiver.ts:1642`, `encodingAccepted` evaluates `encoding !== undefined && (path !== OTLP_METRICS_PATH || encoding === 'json')`.
- In `dash/src/otel/receiver.ts:1662`, `POST /v1/metrics` only calls `decodeOtlpMetricsJson(body.toString('utf8'))`.
- Protobuf decoding for spans (`decodeOtlpProtobuf`) and logs (`decodeOtlpLogProtobuf`) already leverages `PbReader`, `readMessage`, `pbMessages`, `pbMessage`, `pbKeyValues`, `pbString`, `pbBigInt`, and `pbFixed64ToDouble`.
- In OpenTelemetry `ExportMetricsServiceRequest` (metrics.proto):
  - Request: `repeated ResourceMetrics resource_metrics = 1`
  - ResourceMetrics: `Resource resource = 1`, `repeated ScopeMetrics scope_metrics = 2`
  - ScopeMetrics: `InstrumentationScope scope = 1`, `repeated Metric metrics = 2`
  - Metric: `string name = 1`, `string description = 2`, `string unit = 3`, `gauge = 5`, `sum = 7`, `histogram = 9`, `exponential_histogram = 10`, `summary = 11`
  - Data points:
    - NumberDataPoint (gauge & sum): `fixed64 start_time_unix_nano = 2`, `fixed64 time_unix_nano = 3`, `double as_double = 4` (fixed64), `sfixed64 as_int = 6` (fixed64/int), `repeated KeyValue attributes = 7`
    - HistogramDataPoint: `fixed64 start_time_unix_nano = 2`, `fixed64 time_unix_nano = 3`, `fixed64 count = 4`, `double sum = 5` (fixed64), `repeated KeyValue attributes = 9`
    - ExponentialHistogramDataPoint: `repeated KeyValue attributes = 1`, `fixed64 start_time_unix_nano = 2`, `fixed64 time_unix_nano = 3`, `fixed64 count = 4`, `double sum = 5` (fixed64)
    - SummaryDataPoint: `fixed64 start_time_unix_nano = 2`, `fixed64 time_unix_nano = 3`, `fixed64 count = 4`, `double sum = 5` (fixed64), `repeated KeyValue attributes = 7`
  - Aggregation temporality: field 2 on `sum`, `histogram`, `exponential_histogram` (varint)
  - Is monotonic: field 3 on `sum` (varint boolean)

## Design

1. **Protobuf Decoder (`decodeOtlpMetricsProtobuf`):**
   - Exported function in `dash/src/otel/receiver.ts`: `export function decodeOtlpMetricsProtobuf(body: Uint8Array): OtlpMetric[]`.
   - Reads request message with `readMessage(body)`.
   - Iterates `resource_metrics` (field 1), extracting resource attributes via `stripIdentityAttributes(pbKeyValues(resourceMessage, 1))`.
   - Iterates `scope_metrics` (field 2), extracting scope `name` (field 1) and `version` (field 2).
   - Iterates `metrics` (field 2), reading `name` (field 1), `description` (field 2), and `unit` (field 3).
   - Resolves metric kind and data points from oneof submessages:
     - Field 5 (`gauge`): kind `'gauge'`, data points from submessage field 1 (`NumberDataPoint`).
     - Field 7 (`sum`): kind `'sum'`, temporality from field 2, `isMonotonic` from field 3, data points from field 1 (`NumberDataPoint`).
     - Field 9 (`histogram`): kind `'histogram'`, temporality from field 2, data points from field 1 (`HistogramDataPoint`).
     - Field 10 (`exponentialHistogram`): kind `'exponentialHistogram'`, temporality from field 2, data points from field 1 (`ExponentialHistogramDataPoint`).
     - Field 11 (`summary`): kind `'summary'`, data points from field 1 (`SummaryDataPoint`).
     - Default: kind `'unknown'`, empty `dataPoints`.
   - NumberDataPoint extracts `attributes` (field 7, stripped with `stripIdentityAttributes`), timestamps `start_time_unix_nano` (field 2) and `time_unix_nano` (field 3) as decimal strings or null, and numeric value from `as_int` (field 6, `BigInt.asIntN(64, intBits)`) or `as_double` (field 4, `pbFixed64ToDouble(doubleBits)`).
   - Histogram/Summary/ExponentialHistogram data points extract `count` (field 4, number or null) and `sum` (field 5, double or null).

2. **Receiver Dispatch Integration:**
   - In `OtlpReceiver.prototype.dispatch`:
     - Update `encodingAccepted`: `const encodingAccepted = encoding !== undefined`.
     - Update 415 rejection message to `content type ... is not supported: use application/json or application/x-protobuf` uniformly.
     - On `path === OTLP_METRICS_PATH`:
       ```typescript
       const metrics =
         encoding === 'json'
           ? decodeOtlpMetricsJson(body.toString('utf8'))
           : decodeOtlpMetricsProtobuf(body)
       ```
     - Rest of admission and sink dispatch remains unchanged.

3. **Documentation:**
   - Update doc comments on `OTLP_METRICS_PATH` in `dash/src/otel/receiver.ts`.
   - Update `docs/dash/telemetry-inventory.md` § "Collector and canonical-record contract" to document that `POST /v1/metrics` accepts both JSON and Protobuf.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | OTLP Metrics Protobuf decoding contract | KyberDash | reference | `docs/dash/telemetry-inventory.md` (§ Collector and canonical-record contract) |
| H2 | OTLP Metrics endpoint media-type acceptance | KyberDash | code-comment | `dash/src/otel/receiver.ts` (`OTLP_METRICS_PATH` doc comment) |

## Test contract (test-first)

| Task ID | Target test file / suite | RED test assertion / command | GREEN condition / assertion |
|---|---|---|---|
| T1 | `dash/src/otel/receiver.test.ts` | Command: `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/otel/receiver.ts src/otel/receiver.test.ts`<br>RED assertion: (1) `decodeOtlpMetricsProtobuf` is not exported / undefined; (2) `POST /v1/metrics` with `Content-Type: application/x-protobuf` is rejected with 415 `OTLP_UNSUPPORTED_MEDIA_TYPE` and increments `failures5m`. | All tests pass: `decodeOtlpMetricsProtobuf` decodes gauge, sum, histogram, exponentialHistogram, summary protobuf bytes to `OtlpMetric[]`; `POST /v1/metrics` responds 200 with `{ acceptedMetrics: N }`; `failures5m` remains 0; receiver health is true. |

## Implementation tasks

### T1 (test-dev): Author failing unit and HTTP integration tests for OTLP metrics protobuf decoding — Complete
- Target: `dash/src/otel/receiver.test.ts`.
- Add protobuf wire fixture generator for metrics payloads (`metricsProtobufFixture`) using existing test helpers (`pbSubmessage`, `pbLengthDelimited`, `pbStringField`, `pbVarintField`, `pbFixed64Field`, `pbKeyValue`).
- Add unit tests for `decodeOtlpMetricsProtobuf`:
  - Decodes gauge with `as_int` and `as_double` NumberDataPoints, attributes stripped, nanosecond timestamps as strings.
  - Decodes sum with `aggregation_temporality` and `is_monotonic`.
  - Decodes histogram with `count` and `sum`.
  - Decodes exponentialHistogram and summary.
  - Decodes metric with no data points as kind `'unknown'` and empty array.
  - Throws `OtlpDecodeError` on truncated payload.
- Add HTTP integration tests:
  - `POST /v1/metrics` with `Content-Type: application/x-protobuf` returns 200 and `{ acceptedMetrics: 1 }`.
  - `POST /v1/metrics` with `Content-Type: application/protobuf; charset=utf-8` returns 200.
  - Health check verifies `failures5m` is 0 and `healthy` is true.
- Run targeted test suite to confirm RED failure.
- **Evidence:** Verified RED initially, now passing in `dash/src/otel/receiver.test.ts`.

### T2 (typescript-dev): Implement `decodeOtlpMetricsProtobuf` and wire dispatch — Complete
- Target: `dash/src/otel/receiver.ts`.
- Implement `decodeOtlpMetricsProtobuf(body: Uint8Array): OtlpMetric[]` per the design above.
- In `OtlpReceiver.prototype.dispatch`:
  - Permit `encoding === 'protobuf'` on `OTLP_METRICS_PATH`.
  - Call `decodeOtlpMetricsProtobuf(body)` when `encoding !== 'json'`.
  - Unify 415 error diagnostics.
- Update doc comments on `OTLP_METRICS_PATH`.
- Run targeted tests via `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/otel/receiver.ts src/otel/receiver.test.ts` to confirm GREEN.
- Run typecheck and lint via `scripts/proc-guard.sh run -- npm --prefix dash run typecheck` and `scripts/proc-guard.sh run -- npm --prefix dash run lint`.
- **Evidence:** Implemented in `dash/src/otel/receiver.ts`; targeted tests pass GREEN; typecheck and lint clean.

### T3 (docs-dev): Update documentation and run documentation validation — Complete
- Target: `docs/dash/telemetry-inventory.md`.
- Update line 33 to state that the local receiver accepts JSON and Protobuf across `POST /v1/traces`, `POST /v1/logs`, and `POST /v1/metrics`.
- Run `docs_validate_governed_document_schema` and `docs_detect_drift_between_documents_and_code` to ensure 0 findings.
- **Evidence:** Updated `docs/dash/telemetry-inventory.md` with verified behavior and `last-reviewed: 2026-11-07`. Plan archived to `docs/archive/plans/2026-11-07-otlp-metrics-protobuf.md`. `docs validate . --merge-ready` and `docs drift .` report 0 findings.

## Closeout mapping

Every decision D1 to D5 and every Design harvest item H1 to H2 maps to a destination below. Documentation-impact for every row: `extends-component`. Last reviewed: 2026-11-07 for mapped live documents. Status: archived.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — metric data shapes supported by protobuf decoding (gauge, sum, histogram, exponentialHistogram, summary). | extends-component | KyberDash | 2026-11-07 | archived |
| D2 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — protobuf content types (`application/x-protobuf`, `application/protobuf`) accepted on `POST /v1/metrics`. | extends-component | KyberDash | 2026-11-07 | archived |
| D3 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — internal zero-dependency `PbReader` decoding strategy. | extends-component | KyberDash | 2026-11-07 | archived |
| D4 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — no ADR — ADR waiver: refines existing OTLP receiver decoding patterns under ADR 0009 and ADR 0033 to support standard protobuf encoding on the `/v1/metrics` signal path; introduces no new architecture boundaries, external dependencies, or persistence schemas. | extends-component | KyberDash | 2026-11-07 | archived |
| D5 | Documentation waiver: execution-approval provenance and test contract, with no surviving architectural, operational, or behavioral invariant. | extends-component | KyberDash | 2026-11-07 | archived |
| H1 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — local receiver accepts JSON and protobuf across `POST /v1/traces`, `POST /v1/logs`, and `POST /v1/metrics`. | extends-component | KyberDash | 2026-11-07 | archived |
| H2 | [docs/dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — media-type acceptance contract across signal routes documented in canonical inventory and code comments. | extends-component | KyberDash | 2026-11-07 | archived |
