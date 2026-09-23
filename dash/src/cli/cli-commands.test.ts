import { spawnSync } from 'node:child_process'
import { describe, expect, it } from 'vitest'

import { buildProgram } from './program.js'

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

  it('takes --force and --update', () => {
    const menubar = buildProgram().commands.find(command => command.name() === 'menubar')!
    const flags = menubar.options.map(option => option.long).sort()
    expect(flags).toEqual(['--force', '--update'])
  })
})
