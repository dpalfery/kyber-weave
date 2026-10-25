// T3 loadable entry + D5 consent gate (test-first).
//
// Fixture: fixtures/context-payload.json @opencode/plugin 2.1.4 — v2
// `Plugin.define({ id, setup })` with `ctx.session.hook("context", …)`;
// v1 fallback: default-export hooks map. D5: content (systemPrompt,
// toolsText, skills) rides only under KYBERDASH_OPENCODE_CONTEXT=1;
// identity/markers always flow. Never-fail-host throughout.

import { describe, expect, it, vi } from 'vitest'

import entryDefault, {
  KYBERDASH_OPENCODE_CONTEXT_ENV,
  KyberdashOpencodePlugin,
  applyContextConsent,
  createEntryHandlers,
  readContextConsent,
  toHookPayload,
} from './entry.js'
import type { OpencodeHookEvent } from './plugin.js'
import { OPENCODE_PLUGIN_ID } from './plugin.js'

const SESSION_ID = 'opencode-session-7f3a'

const FULL_PAYLOAD = {
  sessionId: SESSION_ID,
  messageId: 'msg-01',
  turnIndex: 3,
  systemPrompt: 'secret system',
  toolsText: '[{"name":"read"}]',
  skills: ['skill-a'],
  model: 'model-x',
  agent: 'agent-y',
} as const

describe('readContextConsent (D5)', () => {
  it('is true only for KYBERDASH_OPENCODE_CONTEXT=1', () => {
    expect(KYBERDASH_OPENCODE_CONTEXT_ENV).toBe('KYBERDASH_OPENCODE_CONTEXT')
    expect(readContextConsent({ KYBERDASH_OPENCODE_CONTEXT: '1' })).toBe(true)
    expect(readContextConsent({})).toBe(false)
    expect(readContextConsent({ KYBERDASH_OPENCODE_CONTEXT: '0' })).toBe(false)
    expect(readContextConsent({ KYBERDASH_OPENCODE_CONTEXT: 'true' })).toBe(false)
  })
})

describe('applyContextConsent (D5 gate)', () => {
  it('drops content fields but keeps identity/markers when consent is absent', () => {
    const gated = applyContextConsent({ kind: 'session.context', ...FULL_PAYLOAD, timestamp: 't' }, false)
    expect(gated).toMatchObject({
      kind: 'session.context',
      sessionId: SESSION_ID,
      messageId: 'msg-01',
      turnIndex: 3,
      model: 'model-x',
      agent: 'agent-y',
    })
    expect(gated).not.toHaveProperty('systemPrompt')
    expect(gated).not.toHaveProperty('toolsText')
    expect(gated).not.toHaveProperty('skills')
  })

  it('lets content ride when consent is set', () => {
    const gated = applyContextConsent({ kind: 'session.context', ...FULL_PAYLOAD, timestamp: 't' }, true)
    expect(gated).toMatchObject({
      systemPrompt: 'secret system',
      toolsText: '[{"name":"read"}]',
      skills: ['skill-a'],
    })
  })
})

describe('entry handlers (consent observable at the emitted event)', () => {
  const lastEvent = (handlers: ReturnType<typeof createEntryHandlers>): OpencodeHookEvent => {
    const event = handlers.installed.plugin.events.at(-1)
    expect(event).toBeDefined()
    return event as OpencodeHookEvent
  }

  it('consent unset → emitted event carries no content fields', async () => {
    const handlers = createEntryHandlers({
      env: {},
      now: () => '2026-10-23T00:00:00.000Z',
    })
    await handlers['session.context']({ ...FULL_PAYLOAD })
    const emitted = lastEvent(handlers)
    expect(emitted).toMatchObject({ kind: 'session.context', sessionId: SESSION_ID, model: 'model-x' })
    expect(emitted).not.toHaveProperty('systemPrompt')
    expect(emitted).not.toHaveProperty('toolsText')
    expect(emitted).not.toHaveProperty('skills')
  })

  it('consent set → emitted event carries content', async () => {
    const handlers = createEntryHandlers({
      env: { KYBERDASH_OPENCODE_CONTEXT: '1' },
      now: () => '2026-10-23T00:00:00.000Z',
    })
    await handlers['session.context']({ ...FULL_PAYLOAD })
    expect(lastEvent(handlers)).toMatchObject({
      systemPrompt: 'secret system',
      toolsText: '[{"name":"read"}]',
      skills: ['skill-a'],
    })
  })

  it('never fails the host when delivery throws', async () => {
    const handlers = createEntryHandlers({
      env: {},
      now: () => '2026-10-23T00:00:00.000Z',
      onError: () => {
        throw new Error('tap down')
      },
    })
    handlers.installed.outbox.enqueue = () => {
      throw new Error('receiver down')
    }
    await expect(handlers['session.context']({ sessionId: SESSION_ID })).resolves.toBeUndefined()
  })
})

describe('opencode-loadable shape (T0 pin)', () => {
  it('exposes the v2 Plugin.define shape and registers context on setup', async () => {
    expect(KyberdashOpencodePlugin.id).toBe(OPENCODE_PLUGIN_ID)
    const seen: string[] = []
    const ctx = {
      session: {
        hook: (name: string, _cb: unknown) => {
          seen.push(name)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        },
      },
    }
    await expect(KyberdashOpencodePlugin.setup(ctx as never)).resolves.toBeUndefined()
    expect(seen).toContain('context')
  })

  it('tolerates a rejecting setup host', async () => {
    const ctx = {
      session: {
        hook: () => Promise.reject(new Error('host busy')),
      },
    }
    await expect(KyberdashOpencodePlugin.setup(ctx as never)).resolves.toBeUndefined()
  })

  it('default-exports the v1 hooks map including session.context', () => {
    const hooks = entryDefault as unknown as Record<string, unknown>
    expect(hooks['session.context']).toEqual(expect.any(Function))
    expect(hooks['session.start']).toEqual(expect.any(Function))
    expect(hooks['session.end']).toEqual(expect.any(Function))
  })

  it('setup uses the shared createInstalledOpencodePlugin pipeline', async () => {
    const hook = vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() }))
    await KyberdashOpencodePlugin.setup({ session: { hook } } as never)
    expect(hook).toHaveBeenCalledWith('context', expect.any(Function))
  })
})

describe('session.context entry boundary (review PR #425 finding, round 2)', () => {
  it('drops an identity-less payload: no event emitted', async () => {
    const handlers = createEntryHandlers({
      env: {},
      now: () => '2026-10-23T00:00:00.000Z',
    })
    await handlers['session.context']({} as unknown as Parameters<typeof handlers['session.context']>[0])
    expect(handlers.installed.plugin.events).toHaveLength(0)
    expect(handlers.installed.mappedSpans).toHaveLength(0)
  })

  it('drops a toHookPayload identity-less input: no event emitted', async () => {
    const handlers = createEntryHandlers({
      env: {},
      now: () => '2026-10-23T00:00:00.000Z',
    })
    await handlers['session.context'](toHookPayload({ model: 'm' }))
    expect(handlers.installed.plugin.events).toHaveLength(0)
    expect(handlers.installed.mappedSpans).toHaveLength(0)
  })
})

describe('toHookPayload identity (review PR #425 finding 1, RED)', () => {
  it('omits the identity key when the input names no session', () => {
    const payload = toHookPayload({ model: 'm' })
    expect('sessionId' in payload).toBe(false)
    expect('sessionID' in payload).toBe(false)
  })

  it('never emits both sessionID and sessionId spellings', () => {
    const payload = toHookPayload({ sessionID: 'ses-dual' } as Record<string, unknown>)
    expect('sessionID' in payload).toBe(false)
    expect(payload.sessionId).toBe('ses-dual')
  })
})
