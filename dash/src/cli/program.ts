import { Command } from 'commander'
import { allProviderNames } from '../providers/index.js'
import { runWebDashboard, UnknownViewError } from './web.js'
import { resolveCliName } from '../brand-overlay.js'
import { registerKyberCommands } from './register.js'
import { registerReportCommand } from './report.js'
import { readConfig } from '../config.js'
import { setModelAliases, setPriceOverrides, setLocalModelSavings, setFlatRateModels, setFlatRateRemoved, setProxyPaths } from '../pricing/models.js'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { version } = require('../../package.json')

function parseInteger(value: string): number {
  return parseInt(value, 10)
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
    .option('--port <number>', 'Port to listen on (falls back to a free port if taken)', parseInteger, 4747)
    .option('--no-open', 'Do not open the browser automatically')
    .option('--view <path>', 'Open a dashboard view (for example finding/<id>)')
    .action(async (opts: { port: number; open: boolean; view?: string }) => {
      try {
        await runWebDashboard({ port: opts.port, open: opts.open, view: opts.view })
      } catch (err) {
        if (err instanceof UnknownViewError) {
          // Exit 2 is the bad-argument contract; starting the server and then
          // refusing would leave the tray holding a listener it did not ask for.
          process.stderr.write(`kyberdash web: ${err.message}\n`)
          process.exit(2)
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
