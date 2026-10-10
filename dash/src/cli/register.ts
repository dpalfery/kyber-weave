// CLI command registration for KyberDash (spec: docs/specs/kyberdash; ADR 0006).
// Keeps command wiring out of vendored upstream files (dash/src/**) so upstream
// updates can be pulled cleanly without merge conflicts over command dispatch.

import type { Command } from 'commander'
import { CommanderError, InvalidArgumentError, Option } from 'commander'
import { resolveCanonDbPath } from '../canon/paths.js'
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
import { formatRefreshDiagnostics, formatRefreshReport } from '../refresh/report.js'
import { MAX_CLEAN_REINGEST_WEEKS } from '../clean/clean.js'
import { formatCleanReport } from '../clean/report.js'
import {
  DEFAULT_FOLDER_IMPORT_WEEKS,
  folderSourcesAllowed,
  importFolderHistory,
  runMaintenancePass,
} from '../refresh/folder-import.js'
import { SETTING_KEYS, readSetting, writeSetting, type OnOff, type SettingKey } from '../settings/shared-settings.js'

/**
 * The four shared settings in the order `dash settings show` prints them, keyed by the
 * short name an operator types. Both spellings are accepted on input -- the short form
 * from the tray, the `settings.`-prefixed form pasted from the HTTP API -- but the short
 * name is what this command reports, because that is what the operator typed.
 */
const DISPLAY_SETTINGS: readonly { readonly short: string; readonly key: SettingKey }[] = [
  { short: 'folder_import.scheduled', key: SETTING_KEYS.folderImportScheduled },
  { short: 'jobs.paused', key: SETTING_KEYS.jobsPaused },
  { short: 'jobs.refresh_cadence_minutes', key: SETTING_KEYS.refreshCadenceMinutes },
  { short: 'receiver.hosted', key: SETTING_KEYS.receiverHosted },
]

const SETTING_NAMES: Readonly<Record<string, SettingKey>> = Object.fromEntries(
  DISPLAY_SETTINGS.flatMap(({ short, key }) => [
    [short, key],
    [key, key],
  ]),
)

/**
 * Parse an on/off value. Exact and case-sensitive on purpose: `shared-settings.ts` fails
 * closed on anything it does not recognise, so accepting `ON` here would write a row no
 * later read would honour, and the switch would look set while doing nothing.
 */
function parseOnOffSetting(raw: string): OnOff | undefined {
  return raw === 'on' || raw === 'off' ? raw : undefined
}

/** Digits only, within the one-day ceiling a cadence is useful within (A1). */
function parseCadenceSetting(raw: string): number | undefined {
  if (!/^[0-9]+$/.test(raw)) return undefined
  const minutes = Number(raw)
  return Number.isSafeInteger(minutes) && minutes >= 1 && minutes <= 1440 ? minutes : undefined
}

function unknownSettingMessage(name: string): string {
  return (
    `unknown setting '${name}'. Expected one of: ` +
    DISPLAY_SETTINGS.map((entry) => entry.short).join(', ') +
    ' (each also accepted with the settings. prefix)'
  )
}

/** The short name for a storage key, so `set` echoes what `show` will report next. */
function shortNameFor(key: SettingKey): string {
  return DISPLAY_SETTINGS.find((entry) => entry.key === key)?.short ?? key
}

/**
 * Decode one stderr write into the text it carries. A stream write is either a string or
 * bytes, and the bytes arrive as a Buffer or as a view over one. Node and commander write
 * strings today, but an undecoded chunk would fall out of the tail that `alreadyPrinted`
 * reads, and the error message would print a second time. The second argument is a write
 * callback in the `write(chunk, cb)` and `write(chunk, encoding, cb)` forms and an encoding
 * only in `write(chunk, encoding)`; a callback carries no string value, so reading it as an
 * encoding is safe in every form and falls back to utf-8 when it is not one.
 */
export function decodeStderrChunk(chunk: unknown, encoding: unknown): string {
  if (typeof chunk === 'string') return chunk
  if (ArrayBuffer.isView(chunk)) {
    const view = chunk as ArrayBufferView
    const label = typeof encoding === 'string' ? encoding : 'utf8'
    return Buffer.from(view.buffer, view.byteOffset, view.byteLength).toString(label as BufferEncoding)
  }
  return ''
}

/**
 * Whether stderr already carried this exact message, judged against the *end* of what was
 * written rather than anywhere within it. Commander writes the message itself before
 * `exitOverride` fires, followed by a newline, so the tail ends with it; an earlier warning
 * or log line that merely mentions the message must not suppress the real one, which is why
 * a substring test over the whole tail is not good enough.
 */
export function alreadyPrinted(tail: string, message: string): boolean {
  return tail.trimEnd().endsWith(message.trimEnd())
}

function invalidSettingValueMessage(name: string, key: SettingKey): string {
  const expected = key === SETTING_KEYS.refreshCadenceMinutes ? 'a whole number of minutes, 1 to 1440' : 'on or off'
  return `'${name}' expects ${expected}`
}

/**
 * `dash refresh` found another refresh holding the lock (R10.4). Distinct from 1 (the
 * refresh ran and something failed) and 2 (bad arguments), so a caller — the tray above
 * all — can tell "already running" from "broken" without parsing the message.
 */
export const REFRESH_BUSY_EXIT_CODE = 3

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
  // The folder-import gate, the maintenance pass and the explicit import are central
  // engine concerns (dash/src/refresh/folder-import.ts). They are injected here so every
  // surface -- CLI, tray, web -- reaches the one implementation through the same seam.
  runMaintenancePass?: typeof runMaintenancePass
  importFolderHistory?: typeof importFolderHistory
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
 * Parse a whole-week window shared by `dash clean --reingest-weeks` and
 * `dash import-history --weeks`: 1 to MAX_CLEAN_REINGEST_WEEKS (one year). An import
 * scans source logs week by week, so an unbounded window turns a typo into a
 * multi-millennia self-inflicted DoS (F6). Rejected at parse time, before the store
 * opens, so a refused request never leaves a half-created canon.db behind.
 */
function createWeeksParser(flag: string): (value: string) => number {
  return (value: string) => {
    if (!/^[1-9][0-9]*$/.test(value)) {
      throw new InvalidArgumentError(`${flag} must be a positive integer`)
    }
    const weeks = Number(value)
    if (!Number.isSafeInteger(weeks) || weeks > MAX_CLEAN_REINGEST_WEEKS) {
      throw new InvalidArgumentError(`${flag} must be between 1 and ${MAX_CLEAN_REINGEST_WEEKS}`)
    }
    return weeks
  }
}

function createCleanReingestWeeksParser(): (value: string) => number {
  return createWeeksParser('--reingest-weeks')
}

/**
 * Resolve database file path, falling back to the shared resolver
 * (KYBER_CANON_DB, then ~/.kyberdash/canon.db) when --db is not specified.
 * Delegated rather than reimplemented so this command can never disagree
 * with the receiver or the dashboard server about which file is the store;
 * see src/canon/paths.ts.
 */
function resolveDbPath(dbPath?: string): string {
  return resolveCanonDbPath(dbPath)
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
    // distinguishable from a failing person. Not a knob a user needs: the job host passes
    // the trigger of the job it spawns, and the API routes it from the request's `surface`.
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
    const maintenancePass = dependencies.runMaintenancePass ?? runMaintenancePass
    const trigger = opts.trigger ?? 'cli'

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

    // Inside the try, not before it: creating the store can itself throw (EACCES on an
    // unwritable state dir, a corrupt canon.db), and a throw from outside would skip the
    // finally and leak the refresh lock - which is a permanent exit 3 for every later run,
    // with no error left behind to explain it. `store` is optional for the same reason:
    // there is nothing to close when it was never opened.
    let store: CanonStore | undefined
    try {
      store = createStore(resolveDbPath(opts.db))
      // The folder-import gate applies to unattended runs only (D4). A person typing
      // `dash refresh` asked for their own history back, so a bare CLI run always
      // imports; a tick, the tray and the web dashboard read the shared setting.
      if (!folderSourcesAllowed(trigger, store)) {
        // Retention and projection still have to happen on the cadence, or the derived
        // views age forever while the source read is withheld (A4). No refresh_run row
        // is written: that row says "sources were read", and a run that read none would
        // make the audit trail -- and the coverage window read from it -- claim work that
        // never happened (ADR 0018's purge keeps its only production path here).
        await maintenancePass(store, new Date())
        write(
          'Folder sources skipped (scheduled folder import is off). ' +
            'Enable with: kyberdash dash settings set folder_import.scheduled on',
        )
        process.exitCode = 0
        return
      }
      const report = await refreshSources(store, undefined, {
        historyWeeks: opts.historyWeeks ?? DEFAULT_HISTORY_WEEKS,
        trigger,
      })
      write(formatRefreshReport(report).trimEnd())
      const diagnostics = formatRefreshDiagnostics(report.rows)
      if (diagnostics !== '') writeError(diagnostics)
      process.exitCode = report.exitCode
    } finally {
      store?.close()
      await lock.handle.release()
    }
  })

  const clean = dash
    .command('clean')
    .description(
      'Clean the KyberDash database: wipe a harness scope (or all); folder history is re-imported only when --reingest-weeks <n> asks for it (default none)',
    )
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .option('--all', 'Wipe every harness')
    .option('--harness <id...>', 'Wipe one or more harnesses (repeatable value)')
    .option(
      '--reingest-weeks <n>',
      'Opt in to a folder import after the wipe; default none (max: 52)',
      createCleanReingestWeeksParser(),
    )
    // Kept accepted, not removed: scripts written against the old default still pass it,
    // and it now states what the default already is (A6).
    .option('--no-reingest', 'Do not import folder history after the wipe (the default)')
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
      let store: CanonStore | undefined
      try {
        store = createStore(resolveDbPath(opts.db))
        // `reingestWeeks` is stated explicitly, including the explicit null: leaving the
        // key absent would hand the decision back to cleanDatabase's own default and
        // reintroduce the unrequested folder read this command no longer makes.
        const report = await runClean(
          store,
          {
            ...(opts.all === true ? { all: true } : { harnesses }),
            reingestWeeks: opts.reingestWeeks ?? null,
          },
          portsForClean(store),
        )
        write(formatCleanReport(report).trimEnd())
        process.exitCode = 0
      } catch (err) {
        writeError(`kyberdash: clean failed: ${err instanceof Error ? err.message : String(err)}`)
        process.exitCode = 1
      } finally {
        store?.close()
        await lock.handle.release()
      }
    },
  )

  // The one-off, operator-driven history backfill (A6). It is the same ingest a refresh
  // runs, narrowed by --harness and widened by --weeks, kept as its own command rather
  // than a refresh flag so "import this once" is visibly a different decision from
  // "keep importing" -- which is what the shared setting does.
  const importHistory = dash
    .command('import-history')
    .alias('import')
    .description('Import history from local harness folders once')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .option(
      '--weeks <n>',
      `History window in whole weeks (default: ${DEFAULT_FOLDER_IMPORT_WEEKS}, max: ${MAX_CLEAN_REINGEST_WEEKS})`,
      createWeeksParser('--weeks'),
      DEFAULT_FOLDER_IMPORT_WEEKS,
    )
    .option('--harness <id...>', 'Narrow the import to one or more harnesses')
  importHistory.exitOverride((error) => {
    if (error.code === 'commander.invalidArgument') {
      throw new CommanderError(2, error.code, error.message)
    }
    throw error
  })
  importHistory.action(async (opts: { db?: string; weeks?: number; harness?: string[] }) => {
    const createStore = dependencies.createStore ?? ((path: string) => new CanonStore(path))
    const write = dependencies.write ?? ((line: string) => process.stdout.write(`${line}\n`))
    const writeError = dependencies.writeError ?? ((line: string) => process.stderr.write(`${line}\n`))
    const acquireLock = dependencies.acquireStoreRefreshLock ?? acquireStoreRefreshLock
    const runImport = dependencies.importFolderHistory ?? importFolderHistory

    // One writer at a time, same lock and same exit 3 as refresh and clean: a caller
    // cannot tell which command it started, so "already running" means one thing.
    const lock = await acquireLock()
    if (lock.outcome !== 'acquired') {
      const holder = readLockHolder(stateDir())
      const who = holder === null ? 'another process' : `pid ${holder.pid} (since ${holder.since})`
      writeError(`kyberdash: an import is already running — held by ${who}; nothing was written`)
      process.exitCode = REFRESH_BUSY_EXIT_CODE
      return
    }

    // Ownership of the handle follows who opened it: only a store this process created
    // is this process's to close (see the finally below). The handle is opened inside the
    // try, so a createStore that throws releases the lock instead of wedging every later
    // run behind a permanent exit 3.
    const ownsStore = dependencies.createStore === undefined
    let store: CanonStore | undefined
    try {
      store = createStore(resolveDbPath(opts.db))
      const harnesses = opts.harness ?? []
      // trigger 'cli' always: this command *is* the explicit opt-in, so it never consults
      // settings.folder_import.scheduled -- neither to decide whether to run, nor to
      // change it afterwards (D3).
      await runImport(store, {
        weeks: opts.weeks ?? DEFAULT_FOLDER_IMPORT_WEEKS,
        ...(harnesses.length > 0 ? { harnesses } : {}),
        trigger: 'cli',
      })
      write(`Imported folder history for the last ${opts.weeks ?? DEFAULT_FOLDER_IMPORT_WEEKS} week(s)`)
      process.exitCode = 0
    } catch (err) {
      writeError(`kyberdash: import failed: ${err instanceof Error ? err.message : String(err)}`)
      process.exitCode = 1
    } finally {
      // Close only a store this process created. A host or test that injects
      // `createStore` owns the lifetime of the handle it handed over -- cli-commands.test.ts
      // reads the setting back through that same handle after the command returns, and
      // closing it would pull the store out from under the caller. The lock, by contrast,
      // is always ours, so it is released on every path.
      if (ownsStore) store?.close()
      await lock.handle.release()
    }
  })

  // The shared settings (A6). A thin caller over src/settings/shared-settings.ts: it owns
  // argument parsing, exit codes and output shape, and no validation of its own beyond
  // naming what a value must look like -- the codecs in that module are the single
  // definition of a legal value, so this command cannot disagree with the tray or the API.
  const settings = dash
    .command('settings')
    .description('Show or set the shared KyberDash settings')

  const readAllSettings = (store: CanonStore): Record<string, string | number> => {
    const values: Record<string, string | number> = {}
    for (const { short, key } of DISPLAY_SETTINGS) {
      const value = readSetting(store, key)
      values[short] = value
    }
    return values
  }

  const settingsUsageExit = (error: CommanderError): never => {
    // Every usage error on a settings leaf exits 2, including a value commander read as
    // an option (`set jobs.refresh_cadence_minutes -5`): a request the caller cannot
    // express as a positional is a refused request, not a crash, and both the tray and
    // the HTTP layer already read 2 as "the caller asked for something impossible".
    throw new CommanderError(2, error.code, error.message)
  }

  settings
    .command('show', { isDefault: true })
    .description('Print every shared setting and its value')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .option('--json', 'Output a JSON object of setting values')
    .exitOverride(settingsUsageExit)
    .action((opts: { db?: string; json?: boolean }) => {
      const createStore = dependencies.createStore ?? ((path: string) => new CanonStore(path))
      const write = dependencies.write ?? ((line: string) => process.stdout.write(`${line}\n`))
      const store = createStore(resolveDbPath(opts.db))
      try {
        const values = readAllSettings(store)
        if (opts.json === true) {
          write(JSON.stringify(values, null, 2))
          return
        }
        for (const [name, value] of Object.entries(values)) write(`${name} ${value}`)
      } finally {
        store.close()
      }
    })

  settings
    .command('set')
    .description('Set one shared setting')
    .argument('<key>', 'Setting name, with or without the settings. prefix')
    .argument('<value>', 'on or off, or a whole number of minutes for jobs.refresh_cadence_minutes')
    .option('--db <path>', 'Custom path for canon.db SQLite database')
    .exitOverride(settingsUsageExit)
    .action((key: string, value: string, opts: { db?: string }) => {
      const createStore = dependencies.createStore ?? ((path: string) => new CanonStore(path))
      const write = dependencies.write ?? ((line: string) => process.stdout.write(`${line}\n`))
      const settingKey = SETTING_NAMES[key]
      if (settingKey === undefined) {
        throw new CommanderError(2, 'commander.invalidArgument', unknownSettingMessage(key))
      }
      // Parsed and validated before the store is opened, not merely before the write:
      // creating the handle is itself a side effect (it creates ~/.kyberdash/canon.db),
      // so a rejected value must leave the filesystem exactly as it found it.
      const parsed: OnOff | number =
        settingKey === SETTING_KEYS.refreshCadenceMinutes
          ? (() => {
              const minutes = parseCadenceSetting(value)
              if (minutes === undefined) {
                throw new CommanderError(2, 'commander.invalidArgument', invalidSettingValueMessage(key, settingKey))
              }
              return minutes
            })()
          : (() => {
              const onOff = parseOnOffSetting(value)
              if (onOff === undefined) {
                throw new CommanderError(2, 'commander.invalidArgument', invalidSettingValueMessage(key, settingKey))
              }
              return onOff
            })()
      const store = createStore(resolveDbPath(opts.db))
      try {
        writeSetting(store, settingKey, parsed)
        // The canonical value, not the raw argument: `05` and `5` store the same row, and an
        // echo that disagreed with `show` on the next command would read as a failed write.
        write(`${shortNameFor(settingKey)} ${parsed}`)
      } finally {
        store.close()
      }
    })

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
