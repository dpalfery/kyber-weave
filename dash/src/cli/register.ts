// CLI command registration for KyberDash (spec: docs/specs/kyberdash; ADR 0006).
// Keeps command wiring out of vendored upstream files (dash/src/**) so upstream
// updates can be pulled cleanly without merge conflicts over command dispatch.

import { homedir } from 'node:os'
import { join } from 'node:path'
import type { Command } from 'commander'
import { CommanderError, InvalidArgumentError, Option } from 'commander'
import { CanonStore } from '../canon/store.js'
import {
  DEFAULT_CAPTURE_ENDPOINT,
  SUPPORTED_CAPTURE_HARNESS_IDS,
} from '../capture/index.js'
import type { CaptureAction } from '../capture/index.js'
import type { CursorHookStdinOptions } from '../otel/cursor-hook.js'
import type { recordAntigravityStatusLinePayload } from '../providers/antigravity.js'
import { DEFAULT_HISTORY_WEEKS, refreshHarnessSources } from '../refresh/orchestrator.js'
import { REFRESH_TRIGGERS, type RefreshTrigger } from '../canon/refresh-run.js'
import { acquireStoreRefreshLock, readLockHolder, stateDir } from '../refresh/lock.js'

/**
 * `dash refresh` found another refresh holding the lock (R10.4). Distinct from 1 (the
 * refresh ran and something failed) and 2 (bad arguments), so a caller — the tray above
 * all — can tell "already running" from "broken" without parsing the message.
 */
export const REFRESH_BUSY_EXIT_CODE = 3
import { formatRefreshDiagnostics, formatRefreshReport } from '../refresh/report.js'
import { formatCleanReport } from '../clean/report.js'

export type KyberCommandDependencies = {
  readStdin?: () => Promise<string>
  write?: (line: string) => void
  writeError?: (line: string) => void
  postCursorHookOtlp?: CursorHookStdinOptions['post']
  recordAntigravityStatusLine?: typeof recordAntigravityStatusLinePayload
  createStore?: (path: string) => CanonStore
  refreshHarnessSources?: typeof refreshHarnessSources
  cleanDatabase?: typeof import('../clean/clean.js').cleanDatabase
  acquireStoreRefreshLock?: typeof acquireStoreRefreshLock
}

function createHistoryWeeksParser(): (value: string) => number {
  let seen: number | undefined
  return (value: string) => {
    if (!/^[1-9][0-9]*$/.test(value)) {
      throw new InvalidArgumentError('--history-weeks must be a positive integer')
    }
    const weeks = Number(value)
    if (!Number.isSafeInteger(weeks)) {
      throw new InvalidArgumentError('--history-weeks exceeds a safe integer')
    }
    if (seen !== undefined && seen !== weeks) {
      throw new InvalidArgumentError('conflicting --history-weeks values')
    }
    seen = weeks
    return weeks
  }
}

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

async function readStdinText(): Promise<string> {
  const chunks: Buffer[] = []
  for await (const chunk of process.stdin) {
    chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk)
  }
  return Buffer.concat(chunks).toString('utf8')
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
export function registerKyberCommands(program: Command, dependencies: KyberCommandDependencies = {}): void {
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

  // Harness OpenTelemetry capture (T8: D3, D9, D11). The subgroup points the
  // six D6 harnesses' OTLP exporters at the KyberDash receiver: `status`
  // reports per-harness state and receiver liveness, `enable` writes the
  // exporter keys and records the receipt, `disable` restores per key and
  // refuses on drift. macOS and Linux only; Windows reports unsupported.
  const capture = kyber
    .command('capture')
    .description('Point harness OTLP exporters at the KyberDash receiver')
  // Usage errors exit 2. Like `dash refresh`, each leaf remaps commander's
  // `invalidArgument` to exit 2; the override lives on the leaves (not the
  // `capture` parent) because that is where option-argument parsing fails.
  const captureUsageExit = (error: Error): never => {
    if (error instanceof CommanderError && error.code === 'commander.invalidArgument') {
      throw new CommanderError(2, error.code, error.message)
    }
    throw error
  }

  const collectHarness = (value: string, previous: string[]): string[] => {
    if (!SUPPORTED_CAPTURE_HARNESS_IDS.includes(value)) {
      throw new InvalidArgumentError(
        `--harness "${value}" is not a supported harness. Expected one of: ${SUPPORTED_CAPTURE_HARNESS_IDS.join(', ')}.`,
      )
    }
    return [...previous, value]
  }
  const parseEndpoint = (value: string): string => {
    if (!/^https?:\/\//i.test(value)) {
      throw new InvalidArgumentError(
        '--endpoint must be an OTLP/HTTP URL (http:// or https://). Only OTLP/HTTP protocols may be written.',
      )
    }
    return value
  }
  const declareCaptureOptions = (sub: Command): Command =>
    sub
      .option(
        '--harness <id>',
        `Harness to act on (repeatable; default: ${SUPPORTED_CAPTURE_HARNESS_IDS.join(', ')})`,
        collectHarness,
        [] as string[],
      )
      .option('--endpoint <url>', 'OTLP receiver origin (default: http://127.0.0.1:4318)', parseEndpoint, DEFAULT_CAPTURE_ENDPOINT)

  const runCaptureAction =
    (action: CaptureAction) =>
    async (opts: { harness?: string[]; endpoint?: string; dryRun?: boolean }): Promise<void> => {
      const { runCapture } = await import('../capture/index.js')
      const result = await runCapture(action, {
        harnessIds: opts.harness !== undefined && opts.harness.length > 0 ? opts.harness : undefined,
        endpoint: opts.endpoint,
        dryRun: opts.dryRun,
      })
      if (result.stdout !== '') process.stdout.write(result.stdout)
      if (result.stderr !== '') process.stderr.write(result.stderr)
      process.exitCode = result.exitCode
    }

  declareCaptureOptions(
    capture
      .command('status')
      .description('Report per-harness capture state and receiver liveness'),
  )
    .exitOverride(captureUsageExit)
    .action(runCaptureAction('status'))

  declareCaptureOptions(
    capture
      .command('enable')
      .description('Write harness OTLP exporter keys and record the receipt')
      .option('--dry-run', 'Print the exact per-file change and write nothing'),
  )
    .exitOverride(captureUsageExit)
    .action(runCaptureAction('enable'))

  declareCaptureOptions(
    capture
      .command('disable')
      .description('Restore pre-enable values per key; refuse on drift')
      .option('--dry-run', 'Print the exact per-file change and write nothing'),
  )
    .exitOverride(captureUsageExit)
    .action(runCaptureAction('disable'))

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
    .option(
      '--source <source>',
      'Restrict renormalization to traces containing this source (repeatable). Unset means every trace.',
      (value: string, previous: string[]) => [...previous, value],
      [] as string[],
    )
    .action(async (opts: { db?: string; source?: string[] }) => {
      const { renormalizeRecords } = await import('../tools/backfill.js')
      const { buildSessions } = await import('../canon/sessions.js')
      const store = new CanonStore(resolveDbPath(opts.db))
      try {
        const report = renormalizeRecords(
          store,
          opts.source !== undefined && opts.source.length > 0 ? { sources: opts.source } : {},
        )
        await buildSessions(store)
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
        console.log(`Built:    ${report.built}`)
        console.log(`Skipped:  ${report.skipped}`)
        console.log(`Pruned:   ${report.pruned}`)
        console.log(`Rollups:  ${report.rollups}`)
        console.log(`Findings: ${report.findings}`)
      } finally {
        store.close()
      }
    })

  const dash = program
    .command('dash')
    .description('Manage KyberDash canonical telemetry and derived dashboard data')

  const refresh = dash
    .command('refresh')
    .description('Refresh KyberDash from local harness-source history')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .option(
      '--history-weeks <n>',
      'UTC history window in whole weeks (default: 2)',
      createHistoryWeeksParser(),
      DEFAULT_HISTORY_WEEKS,
    )
    // Hidden: how the run was started, recorded in the run log so a failing cadence is
    // distinguishable from a failing person. Not a knob a user needs, and the tray sets it.
    .addOption(
      new Option('--trigger <source>', 'how this refresh was started')
        .choices([...REFRESH_TRIGGERS])
        .default('cli')
        .hideHelp(),
    )
  refresh.exitOverride((error) => {
    if (error.code === 'commander.invalidArgument') {
      throw new CommanderError(2, error.code, error.message)
    }
    throw error
  })
  refresh.action(async (opts: { db?: string; historyWeeks?: number; trigger?: RefreshTrigger }) => {
    const createStore = dependencies.createStore ?? ((path: string) => new CanonStore(path))
    const refreshSources = dependencies.refreshHarnessSources ?? refreshHarnessSources
    const write = dependencies.write ?? ((line: string) => process.stdout.write(`${line}\n`))
    const writeError = dependencies.writeError ?? ((line: string) => process.stderr.write(`${line}\n`))
    const acquireLock = dependencies.acquireStoreRefreshLock ?? acquireStoreRefreshLock

    // One refresh at a time, and the loser reports rather than queues (R10.3, R10.4). The
    // tray refreshes on a cadence; without this, a slow run and the next tick would write
    // the same store concurrently.
    const lock = await acquireLock()
    if (lock.outcome !== 'acquired') {
      const holder = readLockHolder(stateDir())
      const who = holder === null ? 'another process' : `pid ${holder.pid} (since ${holder.since})`
      writeError(`kyberdash: a refresh is already running — held by ${who}; nothing was written`)
      process.exitCode = REFRESH_BUSY_EXIT_CODE
      return
    }

    const store = createStore(resolveDbPath(opts.db))
    try {
      const report = await refreshSources(store, undefined, {
        historyWeeks: opts.historyWeeks ?? DEFAULT_HISTORY_WEEKS,
        trigger: opts.trigger ?? 'cli',
      })
      write(formatRefreshReport(report).trimEnd())
      const diagnostics = formatRefreshDiagnostics(report.rows)
      if (diagnostics !== '') writeError(diagnostics)
      process.exitCode = report.exitCode
    } finally {
      store.close()
      await lock.handle.release()
    }
  })

  const clean = dash
    .command('clean')
    .description('Clean the KyberDash database: wipe a harness scope (or all) and re-ingest it')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .option('--all', 'Wipe every harness')
    .option('--harness <id...>', 'Wipe one or more harnesses (repeatable value)')
    .option(
      '--reingest-weeks <n>',
      'Re-ingest window in whole weeks after the wipe (default: 1)',
      createHistoryWeeksParser(),
    )
    .option('--no-reingest', 'Skip re-ingestion after the wipe')
    .option('--yes', 'Confirm the irreversible wipe')
  clean.exitOverride((error) => {
    if (error.code === 'commander.invalidArgument') {
      throw new CommanderError(2, error.code, error.message)
    }
    throw error
  })
  clean.action(
    async (opts: {
      db?: string
      all?: boolean
      harness?: string[]
      reingestWeeks?: number
      reingest?: boolean
      yes?: boolean
    }) => {
      const createStore = dependencies.createStore ?? ((path: string) => new CanonStore(path))
      const write = dependencies.write ?? ((line: string) => process.stdout.write(`${line}\n`))
      const writeError = dependencies.writeError ?? ((line: string) => process.stderr.write(`${line}\n`))
      const acquireLock = dependencies.acquireStoreRefreshLock ?? acquireStoreRefreshLock

      // The CLI is non-interactive: confirmation is an explicit flag, and its
      // absence is a usage error reported before the store opens.
      if (opts.yes !== true) {
        throw new CommanderError(2, 'commander.missingConfirmation', 'clean requires --yes to confirm the irreversible wipe')
      }
      const harnesses = opts.harness ?? []
      if ((opts.all === true && harnesses.length > 0) || (opts.all !== true && harnesses.length === 0)) {
        throw new CommanderError(2, 'commander.conflictingScope', 'clean requires exactly one of --all or --harness <id>')
      }

      // One writer at a time: a clean holds the same lock as a refresh, and
      // the loser reports rather than queues — the tray already reads exit 3
      // as "already running" (ADR 0023 D4).
      const lock = await acquireLock()
      if (lock.outcome !== 'acquired') {
        const holder = readLockHolder(stateDir())
        const who = holder === null ? 'another process' : `pid ${holder.pid} (since ${holder.since})`
        writeError(`kyberdash: a refresh or clean is already running — held by ${who}; nothing was written`)
        process.exitCode = REFRESH_BUSY_EXIT_CODE
        return
      }

      const { cleanDatabase, portsForClean } = await import('../clean/clean.js')
      const runClean = dependencies.cleanDatabase ?? cleanDatabase
      const store = createStore(resolveDbPath(opts.db))
      try {
        const report = await runClean(
          store,
          opts.all === true
            ? { all: true, ...(opts.reingest === false ? { reingestWeeks: null } : opts.reingestWeeks !== undefined ? { reingestWeeks: opts.reingestWeeks } : {}) }
            : { harnesses, ...(opts.reingest === false ? { reingestWeeks: null } : opts.reingestWeeks !== undefined ? { reingestWeeks: opts.reingestWeeks } : {}) },
          portsForClean(store),
        )
        write(formatCleanReport(report).trimEnd())
        process.exitCode = 0
      } catch (err) {
        writeError(`kyberdash: clean failed: ${err instanceof Error ? err.message : String(err)}`)
        process.exitCode = 1
      } finally {
        store.close()
        await lock.handle.release()
      }
    },
  )

  kyber
    .command('cursor-hook')
    .description('POST Cursor hook JSONL from stdin to the local OTLP/HTTP receiver')
    .action(async () => {
      const { runCursorHookStdin } = await import('../otel/cursor-hook.js')
      const stdin = await (dependencies.readStdin ?? readStdinText)()
      await runCursorHookStdin({
        stdin,
        write: dependencies.write ?? ((line) => process.stdout.write(`${line}\n`)),
        ...(dependencies.postCursorHookOtlp === undefined ? {} : { post: dependencies.postCursorHookOtlp }),
      })
    })

  kyber
    .command('antigravity-statusline')
    .description('Record an agy statusLine payload from stdin into the KyberDash cache')
    .action(async () => {
      // C9: a status-line host renders whatever this command writes to stdout and may
      // disable it outright when it exits non-zero, so stdout stays silent and the exit
      // code stays 0 for every outcome — malformed input, an ignored payload, and a
      // recorder I/O failure alike. The one permitted emission is a single stderr line
      // when the recorder itself fails.
      // exitCode is assigned rather than merely left alone: it is process-global, so a
      // non-zero value set earlier in the same process would otherwise propagate and
      // break C9 just as surely as assigning one here would.
      process.exitCode = 0
      let payload: unknown
      try {
        payload = JSON.parse(await (dependencies.readStdin ?? readStdinText)())
      } catch {
        return // stdin unreadable or not JSON: nothing recordable, nothing to say
      }
      try {
        const record =
          dependencies.recordAntigravityStatusLine ??
          (await import('../providers/antigravity.js')).recordAntigravityStatusLinePayload
        await record(payload)
      } catch (error) {
        const writeError = dependencies.writeError ?? ((line: string) => process.stderr.write(`${line}\n`))
        // Newlines are collapsed so the contract's "at most one stderr line" holds even
        // for a multi-line error message.
        const detail = (error instanceof Error ? error.message : String(error)).replace(/[\r\n]+/g, ' ')
        writeError(`kyberdash: antigravity-statusline could not record the payload: ${detail}`)
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
