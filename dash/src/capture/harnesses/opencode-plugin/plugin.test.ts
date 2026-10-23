// T2 plugin skeleton: lifecycle + failure-injection (test-first RED).
//
// One codebase serves both opencode targets (Desktop + CLI v2) from the
// dash-CLI-managed home `dash/src/capture/harnesses/opencode-plugin/`.
// Every hook is best-effort and never fails the host (CommandCode
// `statusbar.ts` swallow-and-continue precedent): an emitter throw or a
// delivery timeout resolves quietly and the session continues.

import { describe, expect, it } from 'vitest'

import {
  OPENCODE_PLUGIN_CONFIG_KEYS,
  OPENCODE_PLUGIN_HOOK_KINDS,
  OPENCODE_PLUGIN_ID,
  createOpencodePlugin,
  registerOpencodePlugin,
  resolveOpencodePluginPaths,
  type OpencodePluginHost,
} from './plugin.js'

const SESSION_ID = 'opencode-session-7f3a'
const TURN_INDEX = 3
const SPAN_ID = 'span-00c1'

function sessionHooks(plugin: ReturnType<typeof createOpencodePlugin>) {
  return plugin.hooks
}

describe('Opencode plugin skeleton hook lifecycle', () => {
  it('registers one hook set on both Desktop and CLIv2 targets from a single source', () => {
    const plugin = createOpencodePlugin({ now: () => '2026-10-22T00:00:00.000Z' })

    const desktopRegistered: Record<string, unknown> = {}
    const desktopHost: OpencodePluginHost = {
      hooks: (registered: Record<string, unknown>) => {
        Object.assign(desktopRegistered, registered)
      },
    }
    const cliHost: OpencodePluginHost & {
      session?: Record<string, unknown>
      tool?: Record<string, unknown>
    } = {}

    registerOpencodePlugin(desktopHost, plugin)
    registerOpencodePlugin(cliHost, plugin)

    // The single source exposes the full lifecycle once ...
    expect(Object.keys(sessionHooks(plugin)).sort()).toEqual([...OPENCODE_PLUGIN_HOOK_KINDS].sort())

    // ... and both targets observe it: Desktop via hooks(), CLIv2 via namespaced surfaces.
    for (const kind of OPENCODE_PLUGIN_HOOK_KINDS) {
      expect(desktopRegistered).toHaveProperty(kind, expect.any(Function))
    }
    expect(cliHost.session).toMatchObject(
      Object.fromEntries(OPENCODE_PLUGIN_HOOK_KINDS.filter((kind) => kind.startsWith('session.') || kind.startsWith('turn.')).map((kind) => [kind, expect.any(Function)])),
    )
    expect(cliHost.tool).toMatchObject(
      Object.fromEntries(OPENCODE_PLUGIN_HOOK_KINDS.filter((kind) => kind.startsWith('span.')).map((kind) => [kind, expect.any(Function)])),
    )

    // One codebase: both targets share the same function identities.
    for (const kind of OPENCODE_PLUGIN_HOOK_KINDS) {
      expect(desktopRegistered[kind]).toBe(sessionHooks(plugin)[kind])
    }
  })

  it('emits session, turn and span boundaries in lifecycle order', async () => {
    const emitted: string[] = []
    const plugin = createOpencodePlugin({
      emit: (event) => {
        emitted.push(event.kind)
      },
      now: () => '2026-10-22T00:00:00.000Z',
    })
    const hooks = sessionHooks(plugin)

    await hooks['session.start']({ sessionId: SESSION_ID })
    await hooks['turn.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })
    await hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
    await hooks['span.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
    await hooks['turn.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })
    await hooks['session.end']({ sessionId: SESSION_ID })

    expect(emitted).toEqual([
      'session.start',
      'turn.start',
      'span.start',
      'span.end',
      'turn.end',
      'session.end',
    ])

    const last = plugin.events.at(-1)
    expect(last).toMatchObject({ kind: 'session.end', sessionId: SESSION_ID })
    expect(plugin.events.map((event) => event.timestamp)).toEqual(
      plugin.events.map(() => '2026-10-22T00:00:00.000Z'),
    )
  })

  it('swallows an emitter throw and lets the host session continue', async () => {
    const emitted: string[] = []
    const plugin = createOpencodePlugin({
      emit: (event) => {
        if (event.kind === 'span.start') throw new Error('receiver down')
        emitted.push(event.kind)
      },
      now: () => '2026-10-22T00:00:00.000Z',
    })
    const hooks = sessionHooks(plugin)

    // The failing hook resolves quietly — the host never sees the throw ...
    await expect(
      hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID }),
    ).resolves.toBeUndefined()

    // ... and the session continues past the failure.
    await hooks['span.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
    await hooks['turn.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })

    expect(emitted).toEqual(['span.end', 'turn.end'])
    expect(plugin.failures).toHaveLength(1)
    expect(String(plugin.failures[0]?.error)).toMatch(/receiver down/)
  })

  it('swallows an emitter timeout and lets the host session continue', async () => {
    const plugin = createOpencodePlugin({
      emit: () => new Promise<void>(() => {}),
      now: () => '2026-10-22T00:00:00.000Z',
      timeoutMs: 5,
    })
    const hooks = sessionHooks(plugin)

    const startedAt = Date.now()
    await expect(
      hooks['turn.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX }),
    ).resolves.toBeUndefined()
    expect(Date.now() - startedAt).toBeLessThan(2000)
    expect(plugin.failures).toHaveLength(1)
    expect(plugin.failures[0]).toMatchObject({ kind: 'turn.start' })

    // The timed-out hook still let the host continue: the next boundary is
    // attempted (and recorded) instead of wedging the session.
    await expect(
      hooks['turn.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX }),
    ).resolves.toBeUndefined()
    expect(plugin.failures).toHaveLength(2)
    expect(plugin.events.map((event) => event.kind)).toEqual(['turn.start', 'turn.end'])
  })

  it('tolerates a host with no hook surface and still exposes the hook set', () => {
    const plugin = createOpencodePlugin({ now: () => '2026-10-22T00:00:00.000Z' })

    const returned = registerOpencodePlugin({}, plugin)

    expect(returned).toBe(plugin)
    expect(Object.keys(returned.hooks).sort()).toEqual([...OPENCODE_PLUGIN_HOOK_KINDS].sort())
  })

  it('declares the dash-CLI-managed install targets and config keys', () => {
    const paths = resolveOpencodePluginPaths('/home/operator')

    expect(paths).toMatchObject({
      globalPluginDir: '/home/operator/.config/opencode/plugins',
      projectPluginDir: '.opencode/plugins',
    })
    expect(OPENCODE_PLUGIN_ID).toBe('kyberdash-opencode')
    expect(OPENCODE_PLUGIN_CONFIG_KEYS).toMatchObject({ plugins: expect.any(String) })
  })
})
