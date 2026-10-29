// T2 plugin skeleton: lifecycle + failure-injection (test-first RED).
//
// One codebase serves both opencode targets (Desktop + CLI v2) from the
// dash-CLI-managed home `dash/src/capture/harnesses/opencode-plugin/`.
// Every hook is best-effort and never fails the host (CommandCode
// `statusbar.ts` swallow-and-continue precedent): an emitter throw or a
// delivery timeout resolves quietly and the session continues.

import { describe, expect, it } from 'vitest'

import {
  OPENCODE_CONTEXT_HOOK_KIND,
  OPENCODE_PLUGIN_CONFIG_KEYS,
  OPENCODE_PLUGIN_HOOK_KINDS,
  OPENCODE_PLUGIN_ID,
  createInstalledOpencodePlugin,
  createOpencodePlugin,
  registerOpencodePlugin,
  resolveOpencodePluginPaths,
  type OpencodeHookKind,
  type OpencodePluginHost,
} from './plugin.js'

// The default sender would POST to the live receiver at 127.0.0.1:4318 (D10); inject a stub.
const acceptingReceiver = () => Promise.resolve(new Response(null, { status: 200 }))

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
    expect(plugin.failures[0]).toMatchObject({ kind: 'span.start' })
    expect(plugin.failures[0]?.error).toMatchObject({ name: 'Error', message: expect.stringMatching(/receiver down/) })
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

describe('Opencode plugin session.context hook (T2, fixture: fixtures/context-payload.json @opencode/plugin 2.1.4)', () => {
  it('fires session.context with the pinned payload shape', async () => {
    const kind: OpencodeHookKind = 'session.context'
    expect(kind).toBe(OPENCODE_CONTEXT_HOOK_KIND)
    // Lifecycle kinds unchanged: the context kind rides beside them, not among them.
    expect(OPENCODE_PLUGIN_HOOK_KINDS).not.toContain('session.context')
    const plugin = createOpencodePlugin({ now: () => '2026-10-23T00:00:00.000Z' })
    await plugin.context({
      sessionId: SESSION_ID,
      messageId: 'msg-01',
      turnIndex: TURN_INDEX,
      systemPrompt: 'system text',
      toolsText: '[{"name":"read"}]',
      skills: ['skill-a'],
      model: 'model-x',
      agent: 'agent-y',
    })
    expect(plugin.events.at(-1)).toMatchObject({
      kind: 'session.context',
      sessionId: SESSION_ID,
      messageId: 'msg-01',
      systemPrompt: 'system text',
      toolsText: '[{"name":"read"}]',
      skills: ['skill-a'],
      model: 'model-x',
      agent: 'agent-y',
    })
  })

  it('registers session.context on the v2 namespaced and v1 dotted host surfaces', () => {
    const plugin = createOpencodePlugin({ now: () => '2026-10-23T00:00:00.000Z' })
    const desktopRegistered: Record<string, unknown> = {}
    const desktopHost: OpencodePluginHost = {
      hooks: (registered: Record<string, unknown>) => {
        Object.assign(desktopRegistered, registered)
      },
    }
    const cliHost: OpencodePluginHost = {}
    registerOpencodePlugin(desktopHost, plugin)
    registerOpencodePlugin(cliHost, plugin)
    expect(desktopRegistered).toHaveProperty('session.context', expect.any(Function))
    expect(desktopRegistered['session.context']).toBe(plugin.context)
    expect(cliHost.session).toHaveProperty('context', expect.any(Function))
    expect(cliHost.session?.['context']).toBe(plugin.context)
    expect(cliHost.session?.['session.context']).toBe(plugin.context)
  })

  it('never fails the host when the session.context handler throws or times out', async () => {
    const throwing = createOpencodePlugin({
      emit: () => {
        throw new Error('receiver down')
      },
      now: () => '2026-10-23T00:00:00.000Z',
    })
    await expect(
      throwing.context({ sessionId: SESSION_ID, systemPrompt: 's' }),
    ).resolves.toBeUndefined()
    expect(throwing.failures).toHaveLength(1)

    const hanging = createOpencodePlugin({
      emit: () => new Promise<void>(() => {}),
      now: () => '2026-10-23T00:00:00.000Z',
      timeoutMs: 5,
    })
    await expect(
      hanging.context({ sessionId: SESSION_ID }),
    ).resolves.toBeUndefined()
    expect(hanging.failures).toHaveLength(1)
  })
})

describe('Opencode installed pipeline stamper forwards session.context content (T2 missing link)', () => {
  it('stamps context content onto the mapper payload when present', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.context({
      sessionId: SESSION_ID,
      turnIndex: TURN_INDEX,
      systemPrompt: 'system text',
      toolsText: '[{"name":"read"}]',
      skills: ['skill-a'],
      model: 'model-x',
      agent: 'agent-y',
    })
    const span = installed.mappedSpans.at(-1)
    expect(span?.attributes['opencode.context.system']).toBe('system text')
    expect(span?.attributes['opencode.context.tools']).toBe('[{"name":"read"}]')
    expect(span?.attributes['opencode.context.skills']).toBe('["skill-a"]')
    expect(span?.attributes['opencode.model.id']).toBe('model-x')
    expect(span?.attributes['opencode.agent']).toBe('agent-y')
  })

  it('leaves content keys absent when the event names no content (lifecycle stamps byte-unchanged)', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.context({ sessionId: SESSION_ID })
    const contextSpan = installed.mappedSpans.at(-1)
    for (const key of [
      'opencode.context.system',
      'opencode.context.tools',
      'opencode.context.skills',
      'opencode.model.id',
      'opencode.agent',
    ]) {
      expect(key in (contextSpan?.attributes ?? {})).toBe(false)
    }

    await installed.plugin.hooks['turn.start']({
      sessionId: SESSION_ID,
      turnIndex: TURN_INDEX,
    })
    const lifecycleSpan = installed.mappedSpans.at(-1)
    expect(lifecycleSpan).toMatchObject({
      traceId: SESSION_ID,
      name: 'opencode.turn.start',
    })
    for (const key of [
      'opencode.context.system',
      'opencode.context.tools',
      'opencode.context.skills',
      'opencode.model.id',
      'opencode.agent',
    ]) {
      expect(key in (lifecycleSpan?.attributes ?? {})).toBe(false)
    }
  })
})
