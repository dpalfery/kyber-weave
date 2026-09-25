import { spawnSync } from 'node:child_process'
import { CommanderError } from 'commander'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { buildProgram } from './program.js'

// Parsing is what these tests are about, so the actions stay out of the way:
// nothing installs a tray, probes a provider, or reads this machine's config.
const installTray = vi.hoisted(() => vi.fn(async () => null))
vi.mock('../install/menubar.js', () => ({ installTray }))
vi.mock('../install/node-deps.js', () => ({ nodeInstallDeps: () => ({}) }))
vi.mock('./doctor.js', () => ({
  collectDoctorReport: async () => ({}),
  renderDoctorTable: () => '',
  renderDoctorJson: () => '{}',
}))
vi.mock('../config.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../config.js')>()),
  readConfig: async () => ({}),
}))

async function parse(...args: string[]): Promise<void> {
  await buildProgram().exitOverride().parseAsync(['node', 'kyberdash', ...args])
}

// Requirements 2.1 and 2.2 of the KyberDash context surfaces specification: the CLI registers
// the context-troubleshooting commands and nothing inherited from the spend product. Hidden
// commands count too, which is why this reads the tree rather than the help text.
const RETAINED = ['dash', 'doctor', 'kyber', 'menubar', 'otel', 'report', 'web']

describe('registered top-level commands', () => {
  it('are exactly the retained set', () => {
    const names = buildProgram()
      .commands.map(command => command.name())
      .sort()
    expect(names).toEqual(RETAINED)
  })

  it('keep the KyberDash subcommands', () => {
    const program = buildProgram()
    const sub = (name: string) =>
      program.commands
        .find(command => command.name() === name)!
        .commands.map(command => command.name())
        .sort()
    expect(sub('kyber')).toEqual(['backfill', 'build', 'cursor-hook', 'otel', 'renormalize'])
    expect(sub('dash')).toEqual(['refresh'])
  })

  it('keep report as the default command', () => {
    const program = buildProgram() as unknown as { _defaultCommandName?: string }
    expect(program._defaultCommandName).toBe('report')
  })
})

describe('menubar', () => {
  /**
   * The installer's own behaviour is covered in `src/install/menubar.test.ts`
   * against injected ports. What is worth checking through a real spawn is the
   * wiring: the command reaches the installer, and a failure there exits
   * non-zero with the step named (R15.6).
   *
   * The origin is pointed at a port nothing is listening on, so the run fails
   * at the first fetch without touching the network or this machine's
   * /Applications.
   */
  it('runs the installer and exits non-zero naming the failing step', () => {
    const result = spawnSync(process.execPath, ['--import', 'tsx', 'src/launcher.ts', 'menubar'], {
      cwd: new URL('../..', import.meta.url),
      encoding: 'utf8',
      timeout: 60_000,
      env: { ...process.env, KYBER_WEAVE_RELEASE_ORIGIN: 'http://127.0.0.1:1/release' },
    })

    expect(result.status).toBe(1)
    expect(result.stderr).toContain('kyberdash menubar:')
    // Either the platform refusal (Linux) or the first network step; both name
    // where it stopped, which is the contract.
    expect(result.stderr).toMatch(/checksums:|platform:/)
  })

  it('takes --force, --update, and --version, plus the shared options', () => {
    const menubar = buildProgram().commands.find(command => command.name() === 'menubar')!
    const flags = menubar.options.map(option => option.long).sort()
    expect(flags).toEqual(['--force', '--timezone', '--update', '--verbose', '--version'])
  })

  /**
   * Declaring `--version` is not the same as receiving it: the root's own
   * `--version` used to match first, so this exact line — the one
   * `kyber-weave update` runs — printed the CLI version and exited 0 without
   * reaching the installer.
   */
  it('hands the self-updater line to the installer, not to the root --version', async () => {
    installTray.mockClear()

    await parse('menubar', '--update', '--version', '1.2.3')

    expect(installTray).toHaveBeenCalledOnce()
    expect(installTray).toHaveBeenCalledWith({}, { force: undefined, update: true, version: '1.2.3' })
  })
})

describe('the root --version', () => {
  it('still prints the CLI version', async () => {
    const program = buildProgram().exitOverride()
    let written = ''
    program.configureOutput({ writeOut: text => { written += text } })

    const error = await program.parseAsync(['node', 'kyberdash', '--version']).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(CommanderError)
    expect((error as CommanderError).code).toBe('commander.version')
    expect(written.trim()).toMatch(/^\d+\.\d+\.\d+/)
  })
})

describe('shared options', () => {
  const saved = { tz: process.env.TZ, verbose: process.env['KYBERDASH_VERBOSE'] }

  afterEach(() => {
    for (const [key, value] of [['TZ', saved.tz], ['KYBERDASH_VERBOSE', saved.verbose]] as const) {
      if (value === undefined) delete process.env[key]
      else process.env[key] = value
    }
  })

  it('are declared on every command, so positional parsing does not strand them', () => {
    const walk = (command: ReturnType<typeof buildProgram>): string[] =>
      command.commands.flatMap(child => {
        const flags = child.options.map(option => option.long)
        const missing = ['--verbose', '--timezone'].filter(flag => !flags.includes(flag))
        return [...missing.map(flag => `${child.name()} ${flag}`), ...walk(child)]
      })
    expect(walk(buildProgram())).toEqual([])
  })

  it.each([
    ['after the subcommand', ['doctor', '--verbose', '--timezone', 'Asia/Tokyo']],
    ['before the subcommand', ['--verbose', '--timezone', 'Asia/Tokyo', 'doctor']],
  ])('reach the pre-action hook when given %s', async (_where, args) => {
    delete process.env['KYBERDASH_VERBOSE']

    await parse(...args)

    expect(process.env['KYBERDASH_VERBOSE']).toBe('1')
    expect(process.env.TZ).toBe('Asia/Tokyo')
  })
})
