// CLI command registration for KyberDash (spec: docs/specs/kyberdash; ADR 0006).
// Keeps command wiring out of vendored upstream files (dash/src/**) so upstream
// updates can be pulled cleanly without merge conflicts over command dispatch.

import { homedir } from 'node:os'
import { join } from 'node:path'
import type { Command } from 'commander'
import { CanonStore } from '../canon/store.js'

/**
 * Resolve database file path, falling back to the default KyberDash store
 * location in ~/.kyberdash/canon.db when --db is not specified.
 */
function resolveDbPath(dbPath?: string): string {
  return dbPath ?? join(homedir(), '.kyberdash', 'canon.db')
}

/** Parse integer argument for commander options. */
function parseInteger(value: string): number {
  return parseInt(value, 10)
}

/**
 * Handler for the OTLP trace receiver service. Kept shared between the
 * `kyber otel` subcommand and the top-level `otel` alias so both paths run
 * identical service initialization and lifecycle management.
 */
async function runOtel(opts: { port?: number; host?: string; db?: string }): Promise<never> {
  const { startOtlpCollectorService } = await import('../otel/service.js')
  await startOtlpCollectorService({
    port: opts.port,
    host: opts.host,
    dbPath: opts.db,
  })
  // Otlp service blocks forever; the process lifecycle is terminated via
  // signals (SIGINT / SIGTERM) handled by the collector service runner.
  return new Promise<never>(() => {})
}

/**
 * Register the `kyber` command group and backwards-compatible aliases.
 */
export function registerKyberCommands(program: Command): void {
  const kyber = program
    .command('kyber')
    .description('KyberDash canonical telemetry and session commands')

  kyber
    .command('otel')
    .description('Start the KyberDash OTLP trace receiver on port 4318')
    .option('--port <number>', 'Port to listen on (default: 4318)', parseInteger, 4318)
    .option('--host <host>', 'Host to bind to (default: 127.0.0.1)', '127.0.0.1')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .action(runOtel)

  kyber
    .command('backfill')
    .description('Re-derive canonical content for stored records from their raw payloads')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .action(async (opts: { db?: string }) => {
      const dbPath = resolveDbPath(opts.db)
      const store = new CanonStore(dbPath)
      try {
        const { backfillContent } = await import('../tools/backfill.js')
        const report = backfillContent(store)
        console.log(`Scanned:        ${report.scanned}`)
        console.log(`Filled:         ${report.filled}`)
        console.log(`Empty:          ${report.empty}`)
        console.log(`Unreadable:     ${report.unreadable}`)
        console.log(`Sessions named: ${report.sessionsNamed}`)
      } finally {
        store.close()
      }
    })

  kyber
    .command('renormalize')
    .description('Re-derive harness attribution and token conversion from stored raw payloads')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .action(async (opts: { db?: string }) => {
      const { renormalizeRecords } = await import('../tools/backfill.js')
      const store = new CanonStore(resolveDbPath(opts.db))
      try {
        const report = renormalizeRecords(store)
        console.log(`Traces:        ${report.traces}`)
        console.log(`Reattributed:  ${report.reattributed}`)
        console.log(`Unchanged:     ${report.unchanged}`)
        console.log(`Unclaimed:     ${report.unclaimed}`)
      } finally {
        store.close()
      }
    })

  kyber
    .command('build')
    .description('Build or rebuild derived sessions from canonical records')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .action(async (opts: { db?: string }) => {
      const dbPath = resolveDbPath(opts.db)
      const store = new CanonStore(dbPath)
      try {
        const { buildSessions } = await import('../canon/sessions.js')
        const report = await buildSessions(store)
        console.log(`Built:   ${report.built}`)
        console.log(`Skipped: ${report.skipped}`)
        console.log(`Pruned:  ${report.pruned}`)
      } finally {
        store.close()
      }
    })

  // Top-level alias for backwards compatibility: existing invocations targeting
  // `codeburn otel` continue to work without having to know about the kyber group.
  program
    .command('otel')
    .description('Start the KyberDash OTLP trace receiver on port 4318 (alias for `kyber otel`)')
    .option('--port <number>', 'Port to listen on (default: 4318)', parseInteger, 4318)
    .option('--host <host>', 'Host to bind to (default: 127.0.0.1)', '127.0.0.1')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .action(runOtel)
}
