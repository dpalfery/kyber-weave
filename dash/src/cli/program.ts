import { Command } from 'commander'
import { allProviderNames } from '../providers/index.js'
import { DashboardPortRefusedError, runWebDashboard, UnknownViewError } from './web.js'
import { resolveCliName } from '../brand-overlay.js'
import { registerKyberCommands } from './register.js'
import { registerReportCommand } from './report.js'
import { readConfig } from '../config.js'
import { setModelAliases, setPriceOverrides, setLocalModelSavings, setFlatRateModels, setFlatRateRemoved, setProxyPaths } from '../pricing/models.js'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { version } = require('../../package.json')

/**
 * Strict integer for `--port`: digits only. `parseInt` would read `47.9` as `47` and
 * `80abc` as `80`, silently binding a port the operator never asked for — the opposite
 * of this command's contract. A non-numeric value becomes NaN here, which the action's
 * range check refuses with exit 2 before the store opens. This mirrors the engine's
 * `portCodec.decode`, so the surface and the persisted setting agree on what a port is.
 */
function parsePortOption(value: string): number {
  const trimmed = value.trim()
  return /^\d+$/.test(trimmed) ? Number(trimmed) : Number.NaN
}

function assertProvider(value: string, command: string): void {
  const names = allProviderNames()
  if (value === 'all' || names.includes(value)) return
  process.stderr.write(
    `${resolveCliName()} ${command}: unknown provider "${value}". Valid values: all, ${names.join(', ')}.\n`
  )
  process.exit(1)
}

// The options every command accepts, before or after its own name.
function declareSharedOptions(command: Command): Command {
  return command
    .option('--verbose', 'print warnings to stderr on read failures and skipped files')
    .option('--timezone <zone>', 'IANA timezone for date grouping (e.g. Asia/Tokyo, America/New_York)')
}

function descendants(command: Command): Command[] {
  return command.commands.flatMap(child => [child, ...descendants(child)])
}

// Wrapped in a factory because commander option state is sticky across
// parses: a long-lived process that builds many programs must not leak one
// parse's flags into the next.
export function buildProgram(): Command {
  // Positional options, set before any subcommand exists because each one
  // copies the setting when it is created. Without them commander matches the
  // root's options anywhere on the line, so the root's `--version` swallowed
  // `menubar --update --version <v>`: it printed the CLI version and exited 0,
  // and `kyber-weave update`, which runs exactly that line, reported a tray
  // update that never ran.
  const program = declareSharedOptions(
    new Command()
      .name(resolveCliName())
      .description('Context health reports and dashboards for AI coding sessions')
      .enablePositionalOptions()
      .version(version),
  )

  program.hook('preAction', async (_thisCommand, actionCommand) => {
    // optsWithGlobals, because the shared options may have been given to the
    // subcommand rather than to the root.
    const shared = actionCommand.optsWithGlobals<{ timezone?: string; verbose?: boolean }>()
    const tz = shared.timezone ?? process.env['KYBERDASH_TZ']
    if (tz) {
      try {
        Intl.DateTimeFormat(undefined, { timeZone: tz })
      } catch {
        console.error(`\n  Invalid timezone: "${tz}". Use an IANA timezone like "America/New_York" or "Asia/Tokyo".\n`)
        process.exit(1)
      }
      process.env.TZ = tz
    }
    const config = await readConfig()
    setModelAliases(config.modelAliases ?? {})
    setPriceOverrides(config.priceOverrides ?? {})
    setLocalModelSavings(config.localModelSavings ?? {})
    setFlatRateModels(config.flatRateModels ?? [])
    setFlatRateRemoved(config.flatRateModelsRemoved ?? [])
    setProxyPaths(config.proxyPaths ?? [])
    if (shared.verbose) {
      process.env['KYBERDASH_VERBOSE'] = '1'
    }
  })

  registerReportCommand(program)

  program
    .command('web')
    .description('Open the local KyberDash web dashboard in your browser')
    // No commander default: an absent --port resolves inside the engine from the
    // persisted settings.dashboard.port (default 4747), so the CLI surface can never
    // disagree with what the Settings drawer saved. An explicit --port wins for this
    // run only and is never persisted. A taken port is a refused start, never a quiet
    // move to a random one: the tray attaches by the configured URL.
    .option('--port <number>', 'Port to listen on for this run (overrides the persisted dashboard port, default 4747)', parsePortOption)
    .option('--no-open', 'Do not open the browser automatically')
    .option('--view <path>', 'Open a dashboard view (for example finding/<id>)')
    .action(async (opts: { port?: number; open: boolean; view?: string }) => {
      // Surface-side input validation: the engine owns persistence and semantic
      // validation, but a value the CLI cannot even express as a port is refused here,
      // before the store opens, so a rejected flag never leaves a half-created state
      // behind. 0 keeps its in-process meaning (bind an ephemeral port).
      if (opts.port !== undefined && (!Number.isInteger(opts.port) || opts.port < 0 || opts.port > 65535)) {
        process.stderr.write(`kyberdash web: --port must be a whole number from 0 to 65535.\n`)
        process.exit(2)
      }
      try {
        await runWebDashboard({
          ...(opts.port === undefined ? {} : { port: opts.port }),
          open: opts.open,
          view: opts.view,
        })
      } catch (err) {
        if (err instanceof UnknownViewError) {
          // Exit 2 is the bad-argument contract; starting the server and then
          // refusing would leave the tray holding a listener it did not ask for.
          process.stderr.write(`kyberdash web: ${err.message}\n`)
          process.exit(2)
        }
        if (err instanceof DashboardPortRefusedError) {
          // A configured port that is taken, or that this process may not bind, is a
          // failure with its remedy in the message: it names the port and what to do
          // next, and a traceback would add nothing it does not already say.
          process.stderr.write(`kyberdash web: ${err.message}\n`)
          process.exit(1)
        }
        throw err
      }
    })

  program
    .command('menubar')
    .description('Install and launch the KyberDash tray on macOS and Windows')
    .option('--force', 'Reinstall even if a copy is already installed')
    .option('--update', 'Update an existing install; do nothing if there is none')
    .option('--version <version>', 'Release version to install (defaults to the running CLI version)')
    .action(async (opts: { force?: boolean; update?: boolean; version?: string }) => {
      const { installTray } = await import('../install/menubar.js')
      const { nodeInstallDeps } = await import('../install/node-deps.js')
      try {
        await installTray(nodeInstallDeps(), { force: opts.force, update: opts.update, version: opts.version })
      } catch (err) {
        // The step is already in the message (R15.6); the exit code is what a
        // caller such as the self-updater branches on.
        process.stderr.write(`kyberdash menubar: ${err instanceof Error ? err.message : String(err)}\n`)
        process.exit(1)
      }
    })

  program
    .command('doctor')
    .description('Per-provider detection status: paths probed, sessions found, parse health (diagnose empty or wrong numbers)')
    .option('--provider <provider>', 'Diagnose a single provider (e.g. claude, codex, opencode)', 'all')
    .option('--json', 'Output machine-readable JSON')
    .option('--no-color', 'Disable ANSI colors')
    .action(async (opts) => {
      assertProvider(opts.provider, 'doctor')
      const { collectDoctorReport, renderDoctorTable, renderDoctorJson } = await import('./doctor.js')
      const report = await collectDoctorReport(opts.provider)
      if (opts.json) {
        process.stdout.write(renderDoctorJson(report) + '\n')
        return
      }
      process.stdout.write(renderDoctorTable(report, { color: opts.color }))
    })

  registerKyberCommands(program)

  // Positional options stop the root reading `--verbose` and `--timezone`
  // after a subcommand name, where they were always accepted. Declaring them
  // on every command keeps `kyberdash report --verbose` working.
  for (const command of descendants(program)) declareSharedOptions(command)

  return program
}
