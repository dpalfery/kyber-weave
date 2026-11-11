// Where Factory's native OpenTelemetry metrics are stored, and what
// "accepted" means for them.
//
// Factory's native export posts metrics to `POST /v1/metrics`, and
// metrics-only is its DEFAULT export shape, so the metrics destination is the
// path a real deployment hits first. The receiver decodes the body and the
// writer has a metrics port, but the canon store has nowhere to put a metric
// point: the production sink has no way to persist them, the writer refuses
// the batch rather than accepting one it would drop, and the receiver
// answers 500.
//
// This file pins the store side of that contract:
//
//   * ONE ROW PER DECODED METRIC POINT — every point in a batch becomes a
//     row; nothing is dropped.
//   * NOTHING FABRICATED — a point that carries no value keeps it absent,
//     never zero (docs/rules/honest-unobservability.md: an unobserved
//     measurement must never look like a measured one).
//   * THE VALUES SURVIVE A REOPEN.
//   * THE SOURCE, NOT A HARNESS — the exporter's `service.name` is the row's
//     source. A metric point has no span, so it carries no adapter
//     fingerprint and no harness id.
//   * THE TABLE NEEDS NO MIGRATION STEP OF ITS OWN — it is created by the
//     store's schema, which the store applies on every open, so it costs no
//     migration (the `model_context_window_catalog` precedent). The schema
//     version DID move once, for the point's interval column: a column
//     cannot be added by the schema's IF NOT EXISTS, so it costs a migration
//     step of its own (v22 -> v23).
//   * THE WIPE — the full corpus wipe clears the rows; a scoped per-harness
//     wipe does not, because their attribution is unknown.
//
// THE CONTRACT this file pins — the store must offer exactly this:
//
//   upsertMetrics(metrics: readonly OtlpMetric[]): void
//     Persists one row per decoded metric point.
//   getMetricPoints(): MetricPointRow[]
//     Reads the persisted rows back.
//
// A row carries: the metric name, the kind, the unit, the aggregation
// temporality, the point's timestamp, its value — or, for a histogram or a
// summary, its count and sum — the point's attributes, the resource
// attributes, the scope, and the exporter's `service.name` as the row's
// source.
//
// The accessors are reached through a structural cast rather than imported:
// the store does not offer them yet, and importing a name that does not
// exist fails the whole file before a single case runs — which would say
// nothing about the behaviour each case below requires. The same reasoning
// the native metrics cases in receiver.test.ts give for the metrics path.

import { describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { SCHEMA_SQL, SCHEMA_VERSION, CanonStore } from './store.js'
import { decodeOtlpMetricsJson, type OtlpMetric, type OtlpScope } from '../otel/receiver.js'

// ---------------------------------------------------------------------------
// The store contract
// ---------------------------------------------------------------------------

/**
 * One persisted metric point. A field that the point did not carry reads
 * absent (null or missing) — never a zero.
 */
type MetricPointRow = {
  name?: string
  kind?: string
  unit?: string | null
  aggregationTemporality?: number | null
  timestamp?: string | null
  timeUnixNano?: string | null
  /** The interval the point's measurement starts at, in Unix nanoseconds. */
  startTimeUnixNano?: string | null
  /** The same interval under the name a store may keep it in. */
  intervalStartUnixNano?: string | null
  value?: number | null
  count?: number | null
  sum?: number | null
  attributes?: Record<string, unknown>
  resource?: Record<string, unknown>
  scope?: OtlpScope
  source?: string
  harness?: string | null
  adapterFingerprint?: string | null
}

type MetricsCapableStore = {
  upsertMetrics(metrics: readonly OtlpMetric[]): void
  getMetricPoints(): MetricPointRow[]
}

/**
 * The store as a metrics destination, with the capability the contract names.
 * The assertion names what is missing, so a failure says which behaviour the
 * store still lacks rather than that a function was undefined.
 */
function metricsStore(store: CanonStore): MetricsCapableStore {
  const candidate = store as unknown as Partial<MetricsCapableStore>
  expect(
    typeof candidate.upsertMetrics,
    'the canon store must offer upsertMetrics(metrics) to persist decoded OTLP metric points',
  ).toBe('function')
  expect(
    typeof candidate.getMetricPoints,
    'the canon store must offer getMetricPoints() to read the persisted metric points back',
  ).toBe('function')
  return candidate as MetricsCapableStore
}

/**
 * The store's tables that hold metric points. Discovered rather than named,
 * so the contract this file pins is the capability (a table the metric points
 * live in) and not one spelling of it.
 */
function metricTableNames(store: CanonStore): string[] {
  const rows = store
    .getDatabase()
    .prepare(
      "SELECT name FROM sqlite_master WHERE type = 'table' AND (name LIKE '%metric%' OR name LIKE '%point%')",
    )
    .all() as { name: unknown }[]
  return rows
    .map((row) => String(row.name))
    .filter((name) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(name))
}

/**
 * The instant a stored timestamp denotes, whichever spelling the store kept
 * it in: the nanoseconds the point arrived as, or an ISO instant. What the
 * round trip must preserve is the instant, not the representation.
 */
function instantOf(stamp: string | null | undefined): number | null {
  if (stamp === null || stamp === undefined) return null
  if (/^\d+$/.test(stamp)) return Number(BigInt(stamp) / 1_000_000n)
  const parsed = Date.parse(stamp)
  return Number.isNaN(parsed) ? null : parsed
}

/** The point's timestamp, under either name the store may keep it in. */
function pointTimestamp(row: MetricPointRow): string | null {
  return row.timestamp ?? row.timeUnixNano ?? null
}

/**
 * The interval the point's measurement starts at, under either name the store
 * may keep it in.
 *
 * The decoded point's own field is `startTimeUnixNano` — the nanoseconds,
 * verbatim, the point arrived with — and the row's own spelling of the
 * OBSERVATION time is `timeUnixNano`, so `startTimeUnixNano` is the name the
 * row is expected to carry the interval under.
 */
function intervalStartOf(row: MetricPointRow): string | null | undefined {
  return row.startTimeUnixNano ?? row.intervalStartUnixNano
}

// ---------------------------------------------------------------------------
// Fixtures — decoded from the OTLP/JSON the receiver actually accepts
// ---------------------------------------------------------------------------

/** The exporter's `service.name`: the metric rows' source. */
const SERVICE_NAME = 'droid'
/** The instrumentation scope the exporter declared. */
const SCOPE: OtlpScope = { name: 'droid.telemetry', version: '1.0.0' }
/** The point's timestamp as it arrived: Unix nanoseconds. */
const POINT_TIME_NANOS = '1756478400123456789'
/** The interval a point's measurement starts at, as the exporter sent it. */
const FIRST_INTERVAL_START_NANOS = '1756478300000000000'
/** A second interval: the same measurement, over a different window. */
const SECOND_INTERVAL_START_NANOS = '1756478350000000000'
/** A metric name from Factory's native `legacy` namespace. */
const GAUGE_NAME = 'droid.code.files_modified'
/** A metric name from Factory's native `genai` namespace. */
const HISTOGRAM_NAME = 'gen_ai.client.operation.duration'

type PointJson = {
  value?: number
  count?: number
  sum?: number
  attributes?: Record<string, string>
  timeUnixNano?: string
  /** The interval the point's measurement starts at, as Unix nanoseconds. */
  startTimeUnixNano?: string
}

/** One OTLP/JSON data point. A field left out is a field the point lacks. */
function otlpPoint(point: PointJson): unknown {
  return {
    ...(point.value === undefined ? {} : { asDouble: point.value }),
    ...(point.count === undefined ? {} : { count: String(point.count) }),
    ...(point.sum === undefined ? {} : { sum: point.sum }),
    timeUnixNano: point.timeUnixNano ?? POINT_TIME_NANOS,
    ...(point.startTimeUnixNano === undefined ? {} : { startTimeUnixNano: point.startTimeUnixNano }),
    attributes: Object.entries(point.attributes ?? {}).map(([key, value]) => ({
      key,
      value: { stringValue: value },
    })),
  }
}

/** The OTLP/JSON body the receiver decodes, carrying the given metrics. */
function metricsBody(metrics: readonly unknown[]): string {
  return JSON.stringify({
    resourceMetrics: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: SERVICE_NAME } }] },
        scopeMetrics: [{ scope: SCOPE, metrics }],
      },
    ],
  })
}

/** The decoded metrics — exactly what the receiver hands to the sink. */
function decode(metrics: readonly unknown[]): OtlpMetric[] {
  return decodeOtlpMetricsJson(metricsBody(metrics))
}

/** A gauge metric carrying the given points. */
function gauge(points: readonly PointJson[]): unknown {
  return { name: GAUGE_NAME, unit: '1', gauge: { dataPoints: points.map(otlpPoint) } }
}

/** A histogram metric carrying the given points. */
function histogram(points: readonly PointJson[]): unknown {
  return {
    name: HISTOGRAM_NAME,
    unit: 'ms',
    histogram: { aggregationTemporality: 2, dataPoints: points.map(otlpPoint) },
  }
}

// ---------------------------------------------------------------------------
// The store round trip
// ---------------------------------------------------------------------------

describe('the canon store as the native OTLP metrics destination', () => {
  it('persists one row per decoded metric point, dropping nothing', () => {
    const store = new CanonStore(':memory:')
    try {
      const metrics = decode([
        gauge([
          { value: 4200, attributes: { 'session.id': 'session-a' } },
          { value: 7, attributes: { 'session.id': 'session-b' } },
        ]),
        histogram([{ count: 3, sum: 12.5, attributes: { 'session.id': 'session-c' } }]),
      ])
      // Three points across two metrics: three rows, none of them dropped.
      expect(metrics.flatMap((metric) => metric.dataPoints)).toHaveLength(3)

      const capable = metricsStore(store)
      capable.upsertMetrics(metrics)

      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(3)

      const gaugeRows = rows.filter((row) => row.name === GAUGE_NAME)
      expect(gaugeRows).toHaveLength(2)
      expect(gaugeRows.map((row) => row.value).sort((a, b) => (a ?? 0) - (b ?? 0))).toEqual([7, 4200])
      expect(gaugeRows.every((row) => row.kind === 'gauge')).toBe(true)
      expect(gaugeRows.every((row) => row.unit === '1')).toBe(true)

      // A histogram point carries a count and a sum instead of a value.
      const histogramRows = rows.filter((row) => row.name === HISTOGRAM_NAME)
      expect(histogramRows).toHaveLength(1)
      expect(histogramRows[0]).toMatchObject({
        kind: 'histogram',
        unit: 'ms',
        count: 3,
        sum: 12.5,
      })
      expect(histogramRows[0]?.aggregationTemporality).toBe(2)

      // The point's own attributes, the resource's attributes and the scope
      // all travel with the row.
      expect(gaugeRows[0]?.attributes).toMatchObject({ 'session.id': 'session-a' })
      expect(gaugeRows[0]?.resource).toMatchObject({ 'service.name': SERVICE_NAME })
      expect(gaugeRows[0]?.scope).toMatchObject(SCOPE)

      // The point's timestamp survives the round trip.
      expect(instantOf(pointTimestamp(gaugeRows[0]!))).toBe(instantOf(POINT_TIME_NANOS))
    } finally {
      store.close()
    }
  })

  it('stores the point without the identity attributes the decode seam stripped', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics(
        decode([
          gauge([
            {
              value: 4200,
              attributes: {
                'session.id': 'session-a',
                'user.email': 'owner@example.test',
                'user.id': 'synth-user-id',
                'organization.id': 'synth-organization-id',
              },
            },
          ]),
        ]),
      )

      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(1)
      // The row keeps the correlation attributes and loses the identity
      // ones: the receiver strips identity before anything is stored, so
      // what never reaches a decoded point cannot reach the stored row.
      const keys = Object.keys(rows[0]?.attributes ?? {})
      for (const key of ['user.email', 'user.id', 'organization.id']) {
        expect(keys, `stored metric point attribute ${key} must not survive the decode`).not.toContain(
          key,
        )
      }
      expect(rows[0]?.attributes?.['session.id']).toBe('session-a')
    } finally {
      store.close()
    }
  })

  it('keeps a value the point did not carry absent rather than zero', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics([
        // A point that reports no value at all: the absence is the fact, and
        // it must not read as a measured zero.
        ...decode([
          {
            name: GAUGE_NAME,
            unit: '1',
            sum: {
              aggregationTemporality: 2,
              dataPoints: [otlpPoint({ attributes: { 'session.id': 'no-value' } })],
            },
          },
        ]),
        // A point that measured zero: a measurement, and it stays a zero.
        ...decode([gauge([{ value: 0, attributes: { 'session.id': 'measured-zero' } }])]),
      ])

      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(2)

      const absent = rows.find((row) => row.attributes?.['session.id'] === 'no-value')
      expect(absent, 'the point that carried no value must still be a row').toBeDefined()
      expect(absent?.value ?? null).toBeNull()
      expect(absent?.count ?? null).toBeNull()
      expect(absent?.sum ?? null).toBeNull()

      const measured = rows.find((row) => row.attributes?.['session.id'] === 'measured-zero')
      expect(measured?.value).toBe(0)
    } finally {
      store.close()
    }
  })

  it('keeps the values through a reopen of the store', () => {
    const root = mkdtempSync(join(tmpdir(), 'kw-metric-reopen-'))
    try {
      const dbPath = join(root, 'canon.db')
      const first = new CanonStore(dbPath)
      try {
        metricsStore(first).upsertMetrics(decode([gauge([{ value: 4200 }])]))
      } finally {
        first.close()
      }

      const reopened = new CanonStore(dbPath)
      try {
        const rows = metricsStore(reopened).getMetricPoints()
        expect(rows).toHaveLength(1)
        expect(rows[0]?.name).toBe(GAUGE_NAME)
        expect(rows[0]?.value).toBe(4200)
        expect(rows[0]?.source).toBe(SERVICE_NAME)
      } finally {
        reopened.close()
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('carries the exporter service.name as the source and no harness attribution', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics(decode([gauge([{ value: 4200 }])]))

      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(1)
      expect(rows[0]?.source).toBe(SERVICE_NAME)
      // A metric point has no span, so it names no harness and carries no
      // adapter fingerprint: it is never attributed to one.
      expect(rows[0]?.harness ?? null).toBeNull()
      expect(rows[0]?.adapterFingerprint ?? null).toBeNull()
    } finally {
      store.close()
    }
  })

  it('adds the metrics table without a migration step of its own', () => {
    const store = new CanonStore(':memory:')
    try {
      // The table is created by the store's schema, which the store applies on
      // every open with IF NOT EXISTS — the `model_context_window_catalog`
      // precedent. A table that needs no migration step costs no version bump
      // of its own. The version moved once, for the point's interval column:
      // a column cannot be added by the schema's IF NOT EXISTS, so it costs a
      // migration step of its own (v22 -> v23).
      expect(SCHEMA_SQL).toMatch(/CREATE TABLE IF NOT EXISTS\s+\w*metric/i)
      expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
      expect(SCHEMA_VERSION).toBe(24)
    } finally {
      store.close()
    }
  })

  it('opens a store that predates the metrics table unchanged, and still works', () => {
    const root = mkdtempSync(join(tmpdir(), 'kw-metric-schema-'))
    try {
      const dbPath = join(root, 'canon.db')
      const store = new CanonStore(dbPath)
      const version = store.getMetadata('schema_version')
      // The store as it was before the metrics table existed: the table is
      // gone, the schema version is untouched.
      const tables = metricTableNames(store)
      expect(
        tables.length,
        'the canon store must hold a table for the decoded metric points',
      ).toBeGreaterThan(0)
      for (const table of tables) store.getDatabase().exec(`DROP TABLE ${table}`)
      store.close()

      const reopened = new CanonStore(dbPath)
      try {
        // No migration step ran: the schema the store applies on every open
        // recreated the table, and the version did not move.
        expect(reopened.getMetadata('schema_version')).toBe(version)
        expect(reopened.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))

        const capable = metricsStore(reopened)
        capable.upsertMetrics(decode([gauge([{ value: 4200 }])]))
        expect(capable.getMetricPoints()).toHaveLength(1)
      } finally {
        reopened.close()
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('keeps two points that differ only in their interval start as two rows', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics(
        decode([
          gauge([
            {
              value: 4200,
              attributes: { 'session.id': 'session-a' },
              startTimeUnixNano: FIRST_INTERVAL_START_NANOS,
            },
            {
              value: 4200,
              attributes: { 'session.id': 'session-a' },
              startTimeUnixNano: SECOND_INTERVAL_START_NANOS,
            },
          ]),
        ]),
      )

      // The two points differ ONLY in the interval their measurement covers:
      // the same name, kind, unit, temporality, observation time, value,
      // attributes, resource, scope and source. The interval is part of the
      // point, so it is part of the point's identity: the second point is its
      // OWN row, not an update of the first one.
      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(2)
    } finally {
      store.close()
    }
  })

  it('stores the point\'s interval start, so it can be recovered', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics(
        decode([
          gauge([
            {
              value: 4200,
              attributes: { 'session.id': 'session-a' },
              startTimeUnixNano: FIRST_INTERVAL_START_NANOS,
            },
          ]),
        ]),
      )

      const rows = capable.getMetricPoints()
      expect(rows).toHaveLength(1)
      // The interval is STORED, not merely digested: a reader can recover the
      // window the measurement covers.
      expect(
        intervalStartOf(rows[0]!),
        'the stored row must carry the interval the point declared',
      ).toBe(FIRST_INTERVAL_START_NANOS)
    } finally {
      store.close()
    }
  })

  it('clears the metric rows on the full corpus wipe and keeps them through a scoped per-harness wipe', () => {
    const store = new CanonStore(':memory:')
    try {
      const capable = metricsStore(store)
      capable.upsertMetrics(decode([gauge([{ value: 4200 }])]))
      expect(capable.getMetricPoints()).toHaveLength(1)

      // A scoped per-harness wipe cannot claim to have removed a row it
      // cannot attribute: the metric point names no harness, so it stays —
      // the same standing the unclaimed quarantine rows already have.
      store.wipeHarnesses(['claude-code'])
      expect(capable.getMetricPoints()).toHaveLength(1)

      // The full corpus wipe clears everything the store holds.
      store.wipeAll()
      expect(capable.getMetricPoints()).toHaveLength(0)
    } finally {
      store.close()
    }
  })
})
