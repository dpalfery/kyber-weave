// Regression: a `dash refresh` whose store cannot be opened must report the
// refusal as one line, not as an uncaught throw.
//
// WHY this shape: the engine runs the scheduled refresh as a child process
// (`dash refresh --trigger scheduled`) and the job footer summarizes that
// child's stderr. A store written by a newer KyberDash than this build is the
// commonest reason the child cannot open its store — the live engine's
// scheduled refresh had been failing on exactly that for hours.
//
// Before the fix the refusal escaped the action as an uncaught throw. In the
// packaged CLI an uncaught throw prints a stack whose every frame names the
// bundle the CLI was loaded from, and that bundle is a base64 blob of
// megabytes: one refusal wrote 40 MB to stderr, and the footer summarized a
// fragment of the blob instead of the reason.
//
// The lock matters as much as the message: the refresh holds it before it
// opens the store, so a failure here that skipped the finally would leave
// every later run reading a permanent exit 3 with nothing to explain it.

import { describe, expect, it } from 'vitest'
import { Command } from 'commander'

import { registerKyberCommands } from './register.js'

const SCHEMA_REFUSAL =
  'canon store at /Users/dave/.kyberdash/canon.db was built with schema version 21, ' +
  'but this build understands version 20; upgrade KyberDash'

describe('dash refresh: a store that cannot be opened', () => {
  it('reports the refusal as one readable line and exits 1', async () => {
    const stderr: string[] = []
    let released = false
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: (line) => stderr.push(line),
      createStore: () => {
        throw new Error(SCHEMA_REFUSAL)
      },
      acquireStoreRefreshLock: async () => ({
        outcome: 'acquired' as const,
        handle: {
          token: 't',
          release: async () => { released = true },
          verifyStillOwner: async () => true,
        },
      }),
    })

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'refresh', '--trigger', 'scheduled'])
    const exitCode = process.exitCode
    process.exitCode = previous

    // The failure is reported, not thrown: the operator gets exit 1 and a line.
    expect(exitCode).toBe(1)
    expect(stderr).toHaveLength(1)
    expect(stderr[0]).toContain('schema version 21')
    expect(stderr[0]).toContain('upgrade KyberDash')
    // One line, and no stack: a stack in the packaged CLI is megabytes of the
    // bundle, and it says nothing the message does not.
    expect(stderr.join('\n')).not.toContain('    at ')
    // The contract that matters: the lock goes back, so the next run is not wedged.
    expect(released).toBe(true)
  })
})
