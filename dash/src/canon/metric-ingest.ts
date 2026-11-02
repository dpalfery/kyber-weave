// The ingest seam for native OpenTelemetry metrics.
//
// Factory's native export posts metrics to `POST /v1/metrics`, and
// metrics-only is its DEFAULT export shape -- so this is the path a real
// deployment hits first, before any span ever arrives.
//
// The module is the named seam the collector service calls, mirroring
// `ingestLogBatch(logs, canon)`: it takes the decoded batch and the store,
// and reports what the batch carried. The store owns the rows; this owns the
// count the audit row carries.
//
// What this deliberately does NOT do:
//
//   * It attributes nothing. A metric point has no span, so it names no
//     harness and carries no adapter fingerprint. The exporter's
//     `service.name` is the row's source -- a source, not an attribution --
//     and a metric row is never attributed to a harness.
//   * It fabricates nothing. A point that carried no value of its own keeps
//     the value absent; the absence is the fact, and it must never read as a
//     measured zero (docs/rules/honest-unobservability.md).
//   * It aggregates nothing. Every decoded point becomes its own row, so
//     nothing the exporter reported is dropped on the way in.
//
// A store that refuses the batch throws, and the caller sees it: a metrics
// batch is never accepted and dropped. The refusal reaches the exporter as a
// failed request, which is the whole point -- an exporter's 200 means the
// batch was persisted.

import type { OtlpMetric } from '../otel/receiver.js'
import type { CanonStore } from './store.js'

/** What a decoded metrics batch carried. */
export type MetricIngestOutcome = {
  /**
   * The metric points the batch carried -- one row each in the store. A
   * metric that names no data shape carries none.
   */
  points: number
}

/**
 * Persist a decoded metrics batch.
 *
 * The store owns the rows; this owns the count and the seam. An empty batch
 * is a no-op: there is nothing to store and nothing to report.
 */
export function ingestMetricBatch(
  metrics: readonly OtlpMetric[],
  canon: CanonStore,
): MetricIngestOutcome {
  const points = countMetricPoints(metrics)
  if (points === 0) return { points: 0 }
  canon.upsertMetrics(metrics)
  return { points }
}

/** The number of data points a decoded batch carries: one row each. */
function countMetricPoints(metrics: readonly OtlpMetric[]): number {
  let points = 0
  for (const metric of metrics) points += metric.dataPoints.length
  return points
}
