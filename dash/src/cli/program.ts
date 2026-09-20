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

// Wrapped in a factory because commander option state is sticky across
// parses: a long-lived process that builds many programs must not leak one
// parse's flags into the next.
export function buildProgram(): Command {
  const program = new Command()
    .name(resolveCliName())
    .description('Context health reports and dashboards for AI coding sessions')
    .version(version)
    .option('--verbose', 'print warnings to stderr on read failures and skipped files')
    .option('--timezone <zone>', 'IANA timezone for date grouping (e.g. Asia/Tokyo, America/New_York)')

  program.hook('preAction', async (thisCommand) => {
    const tz = thisCommand.opts<{ timezone?: string }>().timezone ?? process.env['KYBERDASH_TZ']
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
    if (thisCommand.opts<{ verbose?: boolean }>().verbose) {
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
    .action(() => {
      // The inherited command installed the upstream CodeBurn menubar app, which shows spend,
      // not context, and is not built from this repository. It refuses until the KyberDash
      // tray is released from here (spec task 9.1), rather than install the wrong app.
      console.error('\n  The KyberDash tray is not released yet; this command will install it once it is.\n')
      process.exit(1)
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

  return program
}
