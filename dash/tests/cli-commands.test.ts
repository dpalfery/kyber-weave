import { spawnSync } from 'node:child_process'
import { describe, expect, it } from 'vitest'

import { buildProgram } from '../src/program.js'

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
  it('refuses rather than install the upstream CodeBurn app', () => {
    const result = spawnSync(process.execPath, ['--import', 'tsx', 'src/cli.ts', 'menubar'], {
      cwd: new URL('..', import.meta.url),
      encoding: 'utf8',
      timeout: 60_000,
    })
    expect(result.status).toBe(1)
    expect(result.stderr).toContain('KyberDash tray is not released yet')
  })
})
