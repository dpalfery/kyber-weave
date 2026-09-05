import { Command } from 'commander'
import { describe, expect, it } from 'vitest'

import { registerKyberCommands } from './register.js'

describe('kyber CLI registration', () => {
  it('registers cursor-hook and routes its stdin through the OTLP delivery and output seams', async () => {
    const stdin = [
      JSON.stringify({
        type: 'agent_turn.started',
        sessionId: 'cursor-cli-session',
        turnId: 'cursor-cli-turn',
        timestamp: '2026-09-04T20:12:00.000Z',
      }),
      JSON.stringify({
        type: 'agent_turn.completed',
        sessionId: 'cursor-cli-session',
        turnId: 'cursor-cli-turn',
        timestamp: '2026-09-04T20:12:01.000Z',
      }),
    ].join('\n')
    const written: string[] = []
    const posted: Record<string, unknown>[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      readStdin: async () => stdin,
      write: (line) => written.push(line),
      postCursorHookOtlp: async (payload) => { posted.push(payload) },
    })

    expect(program.commands.find((command) => command.name() === 'kyber')
      ?.commands.find((command) => command.name() === 'cursor-hook')).toBeDefined()

    await program.parseAsync(['node', 'codeburn', 'kyber', 'cursor-hook'])

    expect(posted).toHaveLength(1)
    expect(written).toHaveLength(1)
    expect(posted).toEqual([JSON.parse(written[0])])
  })
})
