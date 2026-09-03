// Service runner for KyberDash OTLP trace receiver
import { homedir } from 'os'
import { join } from 'path'
import { mkdirSync } from 'fs'
import chalk from 'chalk'
import { OtlpReceiver, PortConflictError, type OtlpSpan } from './receiver.js'
import { IngestWriter, type SpanBatchSink } from './writer.js'
import { CanonStore } from '../canon/store.js'
import { readUsageCounters, exclusiveConvention, canonicalContent } from '../canon/adapters/copilot.js'
import { ingestBatch } from '../canon/ingest.js'
import type { CanonicalRecord } from '../canon/types.js'

/**
 * @deprecated Superseded by `ingestBatch`, which puts a span through the
 * documented pipeline: the fingerprint vote decides the harness (R6.2), the
 * winning adapter applies ITS OWN token convention (R4.2), unclaimed spans
 * are quarantined (R6.1) and rejected decompositions persist a problem
 * instead of being stored (R4.3).
 *
 * This function did none of that. It read the harness from `service.name`,
 * set `source` to that same value, stamped `op: 'llm.invoke'` on every span
 * whether or not it was a model call, and applied the cache-EXCLUSIVE
 * conversion to every harness. Measured against the corpus that last choice
 * was wrong for the majority of it: gemini's counters are cache-INCLUSIVE
 * (0 of 9,871 spans go negative under the inclusive conversion, and cache
 * read is a median 92.5% of the reported input), so reassembling the total
 * as `input + cacheRead + cacheCreation` counted the cached input twice and
 * inflated reported input by roughly 1.97x. The sum identity still held, so
 * `validateTokens` could not see it — only the per-adapter convention can.
 *
 * Retained for the existing tests that pin its behaviour; nothing in the
 * ingest path calls it.
 */
export function otlpSpanToRecord(span: OtlpSpan): CanonicalRecord {
  const attrs = span.attributes || {}
  const counters = readUsageCounters(attrs)
  const costUsd = typeof attrs['codeburn.cost_usd'] === 'number' ? attrs['codeburn.cost_usd'] : undefined

  let harness = 'unattributed'
  if (typeof attrs['service.name'] === 'string' && attrs['service.name']) {
    harness = attrs['service.name']
  } else if (typeof attrs['gen_ai.system'] === 'string' && attrs['gen_ai.system']) {
    harness = attrs['gen_ai.system']
  } else if (typeof attrs['llm.system'] === 'string' && attrs['llm.system']) {
    harness = attrs['llm.system']
  }

  return {
    spanId: span.spanId,
    traceId: span.traceId,
    parentSpanId: span.parentSpanId,
    source: harness,
    harness,
    name: span.name,
    op: 'llm.invoke',
    kind: span.kind,
    timestamp: span.timestamp,
    durationMs: span.durationMs,
    status: span.status.code,
    tokens: exclusiveConvention({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: counters.cacheCreation,
      output: counters.output,
      ...(counters.reasoning > 0 ? { reasoning: counters.reasoning } : {}),
    }),
    content: canonicalContent(attrs),
    cost:
      typeof costUsd === 'number' && costUsd !== 0
        ? { basis: 'harness', status: 'priced', value: costUsd, currency: 'USD' }
        : { basis: 'unknown', status: 'no_rate' },
    raw: attrs,
  }
}

export type CollectorOptions = {
  port?: number
  host?: string
  dbPath?: string
}

export async function startOtlpCollectorService(opts: CollectorOptions = {}): Promise<{
  receiver: OtlpReceiver
  writer: IngestWriter
  canon: CanonStore
  close: () => Promise<void>
}> {
  const port = opts.port ?? 4318
  const host = opts.host ?? '127.0.0.1'
  const defaultDir = join(homedir(), '.kyberdash')
  mkdirSync(defaultDir, { recursive: true })
  const dbPath = opts.dbPath ?? join(defaultDir, 'canon.db')

  const canon = new CanonStore(dbPath)

  const toCanon: SpanBatchSink = {
    upsertMany: (spans) => {
      // One arriving request is one unit of attribution, which is exactly
      // what one stored export batch is — so live ingest and a rebuild from
      // exports travel identical code and land identical rows.
      const outcome = ingestBatch(spans, canon)
      const detail = [
        `${outcome.accepted} accepted`,
        outcome.quarantined > 0 ? `${outcome.quarantined} quarantined` : null,
        outcome.rejected > 0 ? `${outcome.rejected} rejected` : null,
      ]
        .filter(Boolean)
        .join(', ')
      console.log(
        chalk.dim(
          `[${new Date().toLocaleTimeString()}] Ingested ${spans.length} spans ` +
            `(${detail}) · total records: ${canon.count()}`,
        ),
      )
    },
  }

  const writer = new IngestWriter(toCanon, { batchSize: 64, flushIntervalMs: 2000 })
  await writer.start()

  const receiver = new OtlpReceiver({ port, host, store: writer })

  try {
    await receiver.start()
  } catch (err) {
    await writer.stop().catch(() => {})
    canon.close()
    if (err instanceof PortConflictError) {
      console.error(
        chalk.red(`\n  Port conflict on ${host}:${port} (${err.code}):\n`) +
          chalk.yellow(`  Another process is already listening on port ${port}.\n`),
      )
      if (err.occupants && err.occupants.length > 0) {
        for (const occ of err.occupants) {
          console.error(chalk.dim(`  Occupant: PID ${occ.pid ?? 'unknown'} (${occ.name ?? 'unknown'}) - ${occ.detail}`))
        }
      }
      process.exitCode = 1
      throw err
    }
    throw err
  }

  const close = async () => {
    await receiver.stop().catch(() => {})
    await writer.stop().catch(() => {})
    canon.close()
  }

  console.log(chalk.bold.cyan('\n  KyberDash OTLP Trace Collector'))
  console.log(chalk.dim(`  Listening on: `) + chalk.green(`http://${host}:${receiver.port}`))
  console.log(chalk.dim(`  Endpoint:     `) + chalk.white(`POST /v1/traces (Protobuf & JSON)`))
  console.log(chalk.dim(`  Store:        `) + chalk.white(dbPath))
  console.log(chalk.dim(`  Records:      `) + chalk.white(`${canon.count()} existing records`))
  console.log(chalk.dim('\n  Ready to collect OTel telemetry from all harnesses.'))
  console.log(chalk.dim('  Press Ctrl+C to stop.\n'))

  process.on('SIGINT', () => {
    void close().finally(() => process.exit(0))
  })
  process.on('SIGTERM', () => {
    void close().finally(() => process.exit(0))
  })

  return { receiver, writer, canon, close }
}
