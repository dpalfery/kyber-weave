// T2 (R1) loadable entry, test-first against the 2.1.4 loader contract.
//
// Fixture: fixtures/plugin-module-1x.json. `loader_default_vs_named_exports`
// quotes the 2.1.4 schema: the default export must be { id, setup } (or
// { id, effect }); only `.default` is read. `context_hook_input_2_1_4` pins
// the `ctx.session.hook("context", cb)` input shape and the G6 answer (the
// setup context exposes `ctx.model.list()` with `limit.context`).
//
// Consent (Q1/Q2, plan §3.4): the consent file `{version:1, content}` is read
// per event; env KYBERDASH_OPENCODE_CONTEXT is override only (0 off, 1 on).
// Content (systemPrompt, toolsText, skills) rides only under consent;
// identity and the context window always flow. Never-fail-host throughout.
// Tests inject the receiver (`fetchFn`) so nothing reaches 127.0.0.1:4318 (D10).

import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { resolveConsentPath } from './consent.js'
import entryDefault, { CONTENT_TRUNCATION_MARKER, MAX_CONTENT_CHARS, createSetup, type EntryOptions } from './entry.js'
import { OPENCODE_PLUGIN_ID, createInstalledOpencodePlugin, type OpencodeHookEvent } from './plugin.js'
import type { OtlpFetch } from './transport/otlp-sender.js'

type ContextInput = {
  sessionID: string
  model: { id: string; providerID: string; variant?: string }
  agent: string
  system: Array<{ text: string }>
  tools: Record<string, { description: string; input: unknown }>
  messages: unknown[]
}

const FIXTURE = JSON.parse(
  readFileSync(new URL('./fixtures/plugin-module-1x.json', import.meta.url), 'utf8'),
) as { context_hook_input_2_1_4: { example_input: ContextInput } }

const SESSION_ID = 'opencode-session-7f3a'
const NOW = '2026-10-26T00:00:00.000Z'

/** Fresh deep copy of the pinned 2.1.4 context input. */
const input = (): ContextInput => structuredClone(FIXTURE.context_hook_input_2_1_4.example_input)

const stubReceiver = (): OtlpFetch => vi.fn(() => Promise.resolve(new Response(null, { status: 200 })))

const dirs: string[] = []
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

/** A temp HOME holding an optional consent file; returns the consent path. */
const consentFile = (body?: string): string => {
  const home = mkdtempSync(join(tmpdir(), 'kd-entry-'))
  dirs.push(home)
  const path = resolveConsentPath(home)
  if (body !== undefined) {
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(path, body)
  }
  return path
}

type Hook = (input: never) => void | Promise<void>
type FakeCtx = {
  session: { hook: ReturnType<typeof vi.fn> }
  model?: { list: ReturnType<typeof vi.fn> }
}

const CATALOG = {
  data: [
    { providerID: 'anthropic', id: 'claude-haiku', limit: { context: 100000, output: 8000 } },
    { providerID: 'anthropic', id: 'claude-sonnet-4-5', limit: { context: 200000, output: 64000 } },
  ],
}

/** Run setup on a fake 2.1.4 context and hand back the registered context callback. */
const boot = async (
  options: EntryOptions & { list?: ReturnType<typeof vi.fn> | null } = {},
): Promise<{
  callback: (i: unknown) => Promise<void>
  ctx: FakeCtx
  events: () => OpencodeHookEvent[]
  installed: ReturnType<typeof createInstalledOpencodePlugin>
}> => {
  const { list, ...entryOptions } = options
  const installed = createInstalledOpencodePlugin({ fetchFn: stubReceiver(), now: () => NOW })
  let registered: Hook | undefined
  const ctx: FakeCtx = {
    session: {
      hook: vi.fn((_name: string, cb: Hook) => {
        registered = cb
        return Promise.resolve({ dispose: () => Promise.resolve() })
      }),
    },
  }
  if (list !== null) ctx.model = { list: list ?? vi.fn(() => Promise.resolve(CATALOG)) }
  await createSetup({ installed, env: {}, consentPath: consentFile(), ...entryOptions })(ctx as never)
  if (registered === undefined) throw new Error('setup registered no context callback')
  const callback = registered
  return {
    callback: async (i) => {
      await callback(i as never)
    },
    ctx,
    events: () => installed.plugin.events,
    installed,
  }
}

describe('default export matches the 2.1.4 loader schema { id, setup }', () => {
  it('has exactly the keys id and setup', () => {
    expect(Object.keys(entryDefault).sort()).toEqual(['id', 'setup'])
    expect(entryDefault.id).toBe('kyberdash-opencode')
    expect(entryDefault.id).toBe(OPENCODE_PLUGIN_ID)
    expect(entryDefault.setup).toEqual(expect.any(Function))
  })

  it('does no work at import: no timer, no fetch', async () => {
    vi.resetModules()
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    const timeoutSpy = vi.spyOn(globalThis, 'setTimeout')
    const intervalSpy = vi.spyOn(globalThis, 'setInterval')
    try {
      await import('./entry.js')
      expect(fetchSpy).not.toHaveBeenCalled()
      expect(timeoutSpy).not.toHaveBeenCalled()
      expect(intervalSpy).not.toHaveBeenCalled()
    } finally {
      fetchSpy.mockRestore()
      timeoutSpy.mockRestore()
      intervalSpy.mockRestore()
    }
  })
})

describe('setup registers exactly one context hook', () => {
  it('calls ctx.session.hook("context", fn) once and nothing else on the context', async () => {
    const hook = vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() }))
    const touched: string[] = []
    const ctx = new Proxy(
      { session: { hook } },
      {
        get(target, prop, receiver) {
          touched.push(String(prop))
          return Reflect.get(target, prop, receiver) as unknown
        },
      },
    )
    await expect(
      createSetup({ installed: createInstalledOpencodePlugin({ fetchFn: stubReceiver() }), env: {}, consentPath: consentFile() })(ctx as never),
    ).resolves.toBeUndefined()
    expect(hook).toHaveBeenCalledTimes(1)
    expect(hook).toHaveBeenCalledWith('context', expect.any(Function))
    expect(touched.filter((p) => p !== 'session' && p !== 'model' && p !== 'tool')).toEqual([])
  })

  it('the shipped default setup registers one context hook', async () => {
    const hook = vi.fn((_name: string, _callback: unknown) => Promise.resolve({ dispose: () => Promise.resolve() }))
    await expect(entryDefault.setup({ session: { hook } } as never)).resolves.toBeUndefined()
    expect(hook).toHaveBeenCalledTimes(1)
    expect(hook.mock.calls[0]?.[0]).toBe('context')
  })
})

describe('context callback maps the 2.1.4 input to one session.context event', () => {
  it('emits sessionId, model, provider, agent, and (consent on) system, tools, skills', async () => {
    const { callback, events } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const given = input()
    await callback(given)
    expect(events()).toHaveLength(1)
    const event = events()[0]
    expect(event).toMatchObject({
      kind: 'session.context',
      sessionId: SESSION_ID,
      model: 'claude-sonnet-4-5',
      provider: 'anthropic',
      agent: 'build',
      systemPrompt: given.system.map((p) => p.text).join('\n\n'),
      toolsText: JSON.stringify(given.tools),
      skills: ['review', 'deploy'],
    })
  })

  it('reaches the wire attributes through the mapper', async () => {
    const { callback, installed } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    await callback(input())
    const attributes = installed.mappedSpans.at(-1)?.attributes
    expect(attributes).toMatchObject({
      'opencode.session.id': SESSION_ID,
      'opencode.model.id': 'claude-sonnet-4-5',
      'opencode.model.provider': 'anthropic',
      'opencode.agent': 'build',
      'opencode.model.context_limit': 200000,
      'opencode.contract_version': '2',
      'opencode.context.skills': '["review","deploy"]',
    })
  })

  it('never mutates the host input', async () => {
    const { callback } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const given = input()
    const before = structuredClone(given)
    await callback(given)
    expect(given).toEqual(before)
  })

  it('drops an input that names no session: no event', async () => {
    const { callback, events } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const { sessionID: _dropped, ...withoutSession } = input()
    await callback(withoutSession)
    await callback({ ...input(), sessionID: '' })
    expect(events()).toHaveLength(0)
  })

  it('leaves absent fields absent', async () => {
    const { callback, events } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    await callback({ sessionID: SESSION_ID })
    const event = events()[0]
    expect(event).toBeDefined()
    for (const key of ['model', 'provider', 'agent', 'systemPrompt', 'toolsText', 'skills', 'contextLimit']) {
      expect(event).not.toHaveProperty(key)
    }
  })

  it('names a malformed skills block by omitting skills, keeping the other content', async () => {
    const { callback, events } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const given = input()
    given.system = [{ text: '<available_skills>\n<skill>\n<id>a</id>\n</skill>' }]
    await callback(given)
    const event = events()[0]
    expect(event).not.toHaveProperty('skills')
    expect(event).toHaveProperty('systemPrompt')
    expect(event).toHaveProperty('toolsText')
  })
})

describe('consent gate (Q1/Q2, plan §3.4)', () => {
  const CONTENT = ['systemPrompt', 'toolsText', 'skills'] as const
  const IDENTITY = { sessionId: SESSION_ID, model: 'claude-sonnet-4-5', provider: 'anthropic', agent: 'build', contextLimit: 200000 }

  const expectDropped = (event: OpencodeHookEvent | undefined): void => {
    expect(event).toMatchObject(IDENTITY)
    for (const key of CONTENT) expect(event).not.toHaveProperty(key)
  }
  const expectKept = (event: OpencodeHookEvent | undefined): void => {
    expect(event).toMatchObject(IDENTITY)
    for (const key of CONTENT) expect(event).toHaveProperty(key)
  }

  it('consent file content:false drops only the content fields', async () => {
    const { callback, events } = await boot({ consentPath: consentFile('{"version":1,"content":false}') })
    await callback(input())
    expectDropped(events()[0])
  })

  it('consent file content:true lets content ride', async () => {
    const { callback, events } = await boot({ consentPath: consentFile('{"version":1,"content":true}') })
    await callback(input())
    expectKept(events()[0])
  })

  it('a missing or unreadable file means off', async () => {
    const missing = await boot({ consentPath: consentFile() })
    await missing.callback(input())
    expectDropped(missing.events()[0])
    const garbage = await boot({ consentPath: consentFile('{{{') })
    await garbage.callback(input())
    expectDropped(garbage.events()[0])
  })

  it('env 0 forces off over a consenting file; env 1 forces on over a refusing file', async () => {
    const off = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '0' }, consentPath: consentFile('{"version":1,"content":true}') })
    await off.callback(input())
    expectDropped(off.events()[0])
    const on = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' }, consentPath: consentFile('{"version":1,"content":false}') })
    await on.callback(input())
    expectKept(on.events()[0])
  })

  it('any other env value defers to the file', async () => {
    const { callback, events } = await boot({
      env: { KYBERDASH_OPENCODE_CONTEXT: 'true' },
      consentPath: consentFile('{"version":1,"content":true}'),
    })
    await callback(input())
    expectKept(events()[0])
  })

  it('reads the file per event: a change applies without reinstalling', async () => {
    const path = consentFile('{"version":1,"content":false}')
    const { callback, events } = await boot({ consentPath: path })
    await callback(input())
    writeFileSync(path, '{"version":1,"content":true}')
    await callback(input())
    expectDropped(events()[0])
    expectKept(events()[1])
  })
})

describe('context window (G6 answered: ctx.model.list exposes limit.context; D6)', () => {
  it('sends the catalog window as contextLimit', async () => {
    const { callback, events } = await boot()
    await callback(input())
    expect(events()[0]?.contextLimit).toBe(200000)
  })

  it('looks the catalog up once across requests', async () => {
    const list = vi.fn(() => Promise.resolve(CATALOG))
    const { callback } = await boot({ list })
    await callback(input())
    await callback(input())
    expect(list).toHaveBeenCalledTimes(1)
  })

  it('tolerates a bare-array list result', async () => {
    const { callback, events } = await boot({ list: vi.fn(() => Promise.resolve(CATALOG.data)) })
    await callback(input())
    expect(events()[0]?.contextLimit).toBe(200000)
  })

  it('omits contextLimit (never 0) when the model is not in the catalog', async () => {
    const { callback, events } = await boot({ list: vi.fn(() => Promise.resolve({ data: [] })) })
    await callback(input())
    expect(events()).toHaveLength(1)
    expect(events()[0]).not.toHaveProperty('contextLimit')
  })

  it.each([0, -5, Number.NaN, '200000'])('omits contextLimit for the unusable limit %s', async (context) => {
    const catalog = { data: [{ providerID: 'anthropic', id: 'claude-sonnet-4-5', limit: { context } }] }
    const { callback, events } = await boot({ list: vi.fn(() => Promise.resolve(catalog)) })
    await callback(input())
    expect(events()[0]).not.toHaveProperty('contextLimit')
  })

  it('omits contextLimit when the context exposes no model catalog', async () => {
    const { callback, events } = await boot({ list: null })
    await callback(input())
    expect(events()).toHaveLength(1)
    expect(events()[0]).not.toHaveProperty('contextLimit')
  })

  it('still emits when the catalog lookup rejects, and stops asking', async () => {
    const list = vi.fn(() => Promise.reject(new Error('catalog down')))
    const { callback, events } = await boot({ list })
    await callback(input())
    await callback(input())
    expect(events()).toHaveLength(2)
    expect(events()[0]).not.toHaveProperty('contextLimit')
    expect(list).toHaveBeenCalledTimes(1)
  })

  it('still emits when the catalog lookup hangs (bounded)', async () => {
    const { callback, events } = await boot({ list: vi.fn(() => new Promise(() => undefined)), lookupTimeoutMs: 20 })
    await callback(input())
    expect(events()).toHaveLength(1)
    expect(events()[0]).not.toHaveProperty('contextLimit')
  })
})

describe('content is capped before it is serialized and sent (C6)', () => {
  const ON = { KYBERDASH_OPENCODE_CONTEXT: '1' }

  it('truncates an oversized system prompt with a marker and keeps the skills parsed from the full text', async () => {
    const { callback, events } = await boot({ env: ON })
    const given = input()
    const big = 'x'.repeat(MAX_CONTENT_CHARS * 2)
    given.system = [...given.system, { text: big }]
    await callback(given)
    const prompt = (events()[0] as { systemPrompt?: string }).systemPrompt ?? ''
    expect(prompt.length).toBe(MAX_CONTENT_CHARS + CONTENT_TRUNCATION_MARKER.length)
    expect(prompt.endsWith(CONTENT_TRUNCATION_MARKER)).toBe(true)
    expect(events()[0]).toMatchObject({ skills: ['review', 'deploy'] })
  })

  it('truncates oversized tools JSON with a marker, without serializing every tool past the cap', async () => {
    const { callback, events } = await boot({ env: ON })
    const given = input()
    const tools: typeof given.tools = {}
    for (let i = 0; i < 50; i += 1) tools[`tool${i}`] = { description: 'd'.repeat(MAX_CONTENT_CHARS / 4), input: {} }
    given.tools = tools
    await callback(given)
    const text = (events()[0] as { toolsText?: string }).toolsText ?? ''
    expect(text.length).toBe(MAX_CONTENT_CHARS + CONTENT_TRUNCATION_MARKER.length)
    expect(text.endsWith(CONTENT_TRUNCATION_MARKER)).toBe(true)
    expect(text.startsWith('{"tool0":')).toBe(true)
  })

  it('leaves content at or under the cap exactly as before', async () => {
    const { callback, events } = await boot({ env: ON })
    const given = input()
    await callback(given)
    expect((events()[0] as { toolsText?: string }).toolsText).toBe(JSON.stringify(given.tools))
  })

  it.each([0, -5, Number.NaN, Number.POSITIVE_INFINITY, '20' as unknown as number])(
    'falls back to the default lookup bound for an invalid lookupTimeoutMs (%s)',
    async (bad) => {
      vi.useFakeTimers()
      try {
        // A catalog that answers after 50ms: the default bound (500ms) waits for it, while a
        // timer built from the invalid value would expire at once and drop the window.
        const slow = vi.fn(() => new Promise((resolve) => setTimeout(() => resolve(CATALOG), 50)))
        const { callback, events } = await boot({ list: slow, lookupTimeoutMs: bad })
        const done = callback(input())
        await vi.advanceTimersByTimeAsync(100)
        await done
        expect(events()).toHaveLength(1)
        expect(events()[0]).toMatchObject({ contextLimit: 200000 })
      } finally {
        vi.useRealTimers()
      }
    },
  )
})

describe('never fails the host', () => {
  it('setup resolves when session.hook rejects', async () => {
    const ctx = { session: { hook: () => Promise.reject(new Error('host busy')) } }
    await expect(entryDefault.setup(ctx as never)).resolves.toBeUndefined()
  })

  it('setup resolves when session.hook throws synchronously or the context is empty', async () => {
    await expect(
      entryDefault.setup({
        session: {
          hook: () => {
            throw new Error('boom')
          },
        },
      } as never),
    ).resolves.toBeUndefined()
    await expect(entryDefault.setup({} as never)).resolves.toBeUndefined()
    await expect(entryDefault.setup(undefined as never)).resolves.toBeUndefined()
  })

  it('the callback resolves when reading the input throws', async () => {
    const { callback } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const hostile = {
      get sessionID(): string {
        throw new Error('hostile getter')
      },
    }
    await expect(callback(hostile)).resolves.toBeUndefined()
    await expect(callback(null)).resolves.toBeUndefined()
    await expect(callback(undefined)).resolves.toBeUndefined()
  })

  it('the callback resolves when delivery throws', async () => {
    const { callback, installed } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    installed.outbox.enqueue = () => {
      throw new Error('receiver down')
    }
    await expect(callback(input())).resolves.toBeUndefined()
  })

  it('the callback resolves when the tools cannot be serialized', async () => {
    const { callback, events } = await boot({ env: { KYBERDASH_OPENCODE_CONTEXT: '1' } })
    const given = input()
    const cyclic: Record<string, unknown> = {}
    cyclic['self'] = cyclic
    given.tools = { loop: { description: 'x', input: cyclic } }
    await expect(callback(given)).resolves.toBeUndefined()
    expect(events()[0]).not.toHaveProperty('toolsText')
  })
})
