// T6 (RED first): the web client for `GET|POST /api/kyber/capture`. The client
// is a display-layer parser: it validates the wire shape into bounded,
// vocabulary-checked values and never computes capture state itself. Fixtures
// are the shared T0 contract files, consumed unmodified; fetch is mocked per
// test so nothing reaches a server.

import { afterEach, describe, expect, it, vi } from 'vitest'

import statusMixed from '../../../src/server/fixtures/capture-api/status-mixed.json'
import statusUnknown from '../../../src/server/fixtures/capture-api/status-unknown.json'
import postPreviewEnable from '../../../src/server/fixtures/capture-api/post-preview-enable.json'
import postWroteEnable from '../../../src/server/fixtures/capture-api/post-wrote-enable.json'
import postRefusedDrift from '../../../src/server/fixtures/capture-api/post-refused-drift.json'
import postDisableLast from '../../../src/server/fixtures/capture-api/post-disable-last.json'

import {
  KyberApiError,
  fetchKyberCaptureStatus,
  postKyberCapture,
  type KyberCaptureResult,
  type KyberCaptureRefusal,
  type KyberCaptureEvent,
} from './kyberApi.js'

type FetchCall = { url: string; init: RequestInit | undefined }

function stubFetch(status: number, body: unknown): FetchCall[] {
  const calls: FetchCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init })
      return new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
  return calls
}

afterEach(() => {
  vi.unstubAllGlobals()
})

/** Deep clone so a test can mutate a fixture without touching the shared module object. */
function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function asResult(value: unknown): KyberCaptureResult {
  const result = value as KyberCaptureResult | KyberCaptureRefusal
  if (result.outcome === 'refused') throw new Error('expected a non-refused result')
  return result
}

describe('fetchKyberCaptureStatus', () => {
  it('GETs /api/kyber/capture and parses the mixed-status fixture', async () => {
    const calls = stubFetch(200, statusMixed)

    const status = await fetchKyberCaptureStatus()

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/capture')
    expect(calls[0]!.init?.method ?? 'GET').toBe('GET')
    expect(status.platform).toBe('darwin')
    expect(status.receiver).toEqual(statusMixed.receiver)
    expect(status.harnesses.map((h) => h.id)).toEqual(statusMixed.harnesses.map((h) => h.id))

    const claude = status.harnesses.find((h) => h.id === 'claude-code')!
    expect(claude.mode).toBe('managed')
    expect(claude.state).toBe('on')
    expect(claude.receipt).toBe('absent')
    expect(claude.warnings.map((w) => w.code)).toEqual(['endpoint-differs'])
    expect(claude.files[0]!.presentKeys).toHaveLength(12)

    const copilot = status.harnesses.find((h) => h.id === 'copilot')!
    expect(copilot.state).toBe('partial')
    expect(copilot.files[1]!.applicable).toBe(false)

    const opencode = status.harnesses.find((h) => h.id === 'opencode')!
    expect(opencode.manualSteps[0]!.env).toEqual({
      name: 'OTEL_EXPORTER_OTLP_ENDPOINT',
      value: 'http://127.0.0.1:4318',
    })

    const pi = status.harnesses.find((h) => h.id === 'pi')!
    expect(pi.mode).toBe('status-only')
    expect(pi.state).toBe('not-applicable')
    expect(pi.statusLines).toEqual(statusMixed.harnesses.find((h) => h.id === 'pi')!.statusLines)
  })

  it('parses the unknown-status fixture, keeping exists:null and unknown states', async () => {
    stubFetch(200, statusUnknown)

    const status = await fetchKyberCaptureStatus()

    expect(status.receiver.hosted).toBe(false)
    expect(status.receiver.liveness).toBe('unknown')
    const claude = status.harnesses.find((h) => h.id === 'claude-code')!
    expect(claude.installed).toBe('unknown')
    expect(claude.state).toBe('unknown')
    expect(claude.files[0]!.exists).toBeNull()
  })

  it('keeps drift entries including an absent current value as null', async () => {
    const body = clone(statusMixed)
    const file = body.harnesses.find((h) => h.id === 'claude-code')!.files[0]!
    ;(file as { drift: unknown }).drift = [{ key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: null }]
    stubFetch(200, body)

    const status = await fetchKyberCaptureStatus()

    expect(status.harnesses.find((h) => h.id === 'claude-code')!.files[0]!.drift).toEqual([
      { key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: null },
    ])
  })

  it('drops unknown fields at every level', async () => {
    const body = clone(statusMixed) as Record<string, unknown>
    body.surprise = 'x'
    ;(body.receiver as Record<string, unknown>).secret = 'x'
    const harness = (body.harnesses as Array<Record<string, unknown>>)[0]!
    harness.apiKey = 'sk-do-not-keep'
    ;((harness.files as Array<Record<string, unknown>>)[0]!).rawConfig = { env: { ANTHROPIC_API_KEY: 'sk-x' } }
    ;((harness.warnings as unknown[]) as Array<Record<string, unknown>>).push({ code: 'c', text: 't', extra: 1 })
    stubFetch(200, body)

    const status = await fetchKyberCaptureStatus()

    expect(Object.keys(status).sort()).toEqual(['harnesses', 'platform', 'receiver'])
    expect(Object.keys(status.receiver).sort()).toEqual(['detail', 'hosted', 'liveness'])
    expect(JSON.stringify(status)).not.toContain('sk-do-not-keep')
    expect(JSON.stringify(status)).not.toContain('ANTHROPIC_API_KEY')
    expect(JSON.stringify(status)).not.toContain('"extra"')
  })

  it('turns an out-of-vocabulary state into unknown, and other enums into their safe values', async () => {
    const body = clone(statusMixed)
    const [first, second, third] = body.harnesses as unknown as Array<Record<string, unknown>>
    first!.state = 'enabled-ish'
    second!.installed = 'maybe'
    third!.mode = 'writable'
    third!.receipt = 'lost'
    ;(body.receiver as Record<string, unknown>).liveness = 'sort-of'
    stubFetch(200, body)

    const status = await fetchKyberCaptureStatus()

    expect(status.harnesses[0]!.state).toBe('unknown')
    expect(status.harnesses[1]!.installed).toBe('unknown')
    // An unrecognised mode must never become a toggle: it degrades to read-only.
    expect(status.harnesses[2]!.mode).toBe('status-only')
    expect(status.harnesses[2]!.receipt).toBe('absent')
    expect(status.receiver.liveness).toBe('unknown')
  })

  it('caps long strings and bounds arrays', async () => {
    const body = clone(statusMixed)
    const harness = body.harnesses[0] as unknown as Record<string, unknown>
    harness.installEvidence = Array.from({ length: 500 }, (_, i) => `evidence-${i}`)
    harness.statusLines = ['x'.repeat(100_000)]
    ;(body.harnesses as unknown[]).push(
      ...Array.from({ length: 200 }, (_, i) => ({ ...clone(body.harnesses[0]), id: `extra-${i}` })),
    )
    stubFetch(200, body)

    const status = await fetchKyberCaptureStatus()

    expect(status.harnesses.length).toBeLessThanOrEqual(64)
    expect(status.harnesses[0]!.installEvidence.length).toBeLessThanOrEqual(32)
    expect(status.harnesses[0]!.statusLines[0]!.length).toBeLessThanOrEqual(4096)
  })

  it('throws KyberApiError for a non-2xx answer', async () => {
    stubFetch(500, { error: 'Capture failed' })

    await expect(fetchKyberCaptureStatus()).rejects.toMatchObject({
      name: 'KyberApiError',
      status: 500,
      path: '/api/kyber/capture',
    })
    await expect(fetchKyberCaptureStatus()).rejects.toBeInstanceOf(KyberApiError)
  })

  it('throws KyberApiError for a 2xx body that is not a status object', async () => {
    stubFetch(200, ['not', 'an', 'object'])

    await expect(fetchKyberCaptureStatus()).rejects.toBeInstanceOf(KyberApiError)
  })
})

describe('postKyberCapture', () => {
  it('sends exactly {action, harness, dryRun} with a JSON content type', async () => {
    const calls = stubFetch(200, postPreviewEnable)

    await postKyberCapture({ action: 'enable', harness: 'claude-code', dryRun: true })

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/capture')
    expect(calls[0]!.init?.method).toBe('POST')
    expect(calls[0]!.init?.headers).toEqual({ 'content-type': 'application/json' })
    const sent = JSON.parse(String(calls[0]!.init?.body)) as Record<string, unknown>
    expect(Object.keys(sent).sort()).toEqual(['action', 'dryRun', 'harness'])
    expect(sent).toEqual({ action: 'enable', harness: 'claude-code', dryRun: true })
  })

  it('forwards the Settings-only manual-key opt-in on disable, and only then', async () => {
    const calls = stubFetch(200, postPreviewEnable)

    await postKyberCapture({ action: 'disable', harness: 'claude-code', dryRun: true, removeUnreceiptedKeys: true })

    expect(JSON.parse(String(calls[0]!.init?.body))).toEqual({
      action: 'disable',
      harness: 'claude-code',
      dryRun: true,
      removeUnreceiptedKeys: true,
    })

    const enableCalls = stubFetch(200, postPreviewEnable)
    await postKyberCapture({ action: 'enable', harness: 'claude-code', dryRun: true })

    expect(JSON.parse(String(enableCalls[0]!.init?.body))).toEqual({
      action: 'enable',
      harness: 'claude-code',
      dryRun: true,
    })
  })

  it('sends the exact preview with a confirmed manual-key removal', async () => {
    const calls = stubFetch(200, postPreviewEnable)
    const expectedPreviewEvents: KyberCaptureEvent[] = [
      {
        kind: 'would-restore',
        harness: 'claude-code',
        fileId: 'claude-settings',
        path: '/home/dave/.claude/settings.json',
        changes: [{ key: 'env.OTEL_LOGS_EXPORTER', before: 'otlp' }],
      },
    ]

    await postKyberCapture({
      action: 'disable',
      harness: 'claude-code',
      dryRun: false,
      removeUnreceiptedKeys: true,
      expectedPreviewEvents,
    })

    expect(JSON.parse(String(calls[0]!.init?.body))).toEqual({
      action: 'disable',
      harness: 'claude-code',
      dryRun: false,
      removeUnreceiptedKeys: true,
      expectedPreviewEvents,
    })
  })

  it('does not forward extra keys a caller smuggles in', async () => {
    const calls = stubFetch(200, postPreviewEnable)

    await postKyberCapture({
      action: 'enable',
      harness: 'claude-code',
      dryRun: false,
      receiverHosted: true,
    } as never)

    expect(JSON.parse(String(calls[0]!.init?.body))).toEqual({
      action: 'enable',
      harness: 'claude-code',
      dryRun: false,
    })
  })

  it('parses the preview fixture', async () => {
    stubFetch(200, postPreviewEnable)

    const result = asResult(await postKyberCapture({ action: 'enable', harness: 'claude-code', dryRun: true }))

    expect(result.outcome).toBe('previewed')
    expect(result.dryRun).toBe(true)
    expect(result.events[0]!.kind).toBe('would-write')
    expect(result.events[0]!.changes![0]).toEqual({ key: 'env.OTEL_LOGS_EXPORTER', before: 'console', after: 'otlp' })
    // `before` is omitted when the key was absent, and stays omitted.
    expect(result.events[0]!.changes![1]).toEqual({ key: 'env.OTEL_LOG_USER_PROMPTS', after: '1' })
    expect('before' in result.events[0]!.changes![1]!).toBe(false)
    expect(result.harnessState.id).toBe('claude-code')
    expect(result.receiver).toEqual(postPreviewEnable.receiver)
  })

  it('parses the wrote fixture', async () => {
    stubFetch(200, postWroteEnable)

    const result = asResult(await postKyberCapture({ action: 'enable', harness: 'copilot', dryRun: false }))

    expect(result.outcome).toBe('wrote')
    expect(result.events[0]!.kind).toBe('wrote')
    expect(result.harnessState.state).toBe('on')
    expect(result.harnessState.receipt).toBe('recorded')
    expect(result.events[0]!.changes!.find((c) => c.key === 'github.copilot.chat.otel.maxAttributeSizeChars')!.after).toBe(0)
    expect(result.events[0]!.changes!.find((c) => c.key === 'github.copilot.chat.otel.enabled')!.before).toBe(false)
  })

  it('parses the disable-last fixture including the receiver note flags', async () => {
    stubFetch(200, postDisableLast)

    const result = asResult(await postKyberCapture({ action: 'disable', harness: 'opencode', dryRun: false }))

    expect(result.action).toBe('disable')
    expect(result.events[0]!.kind).toBe('restored')
    expect(result.receiver.lastManagedHarnessDisabled).toBe(true)
    expect(result.receiver.hosted).toBe(true)
    expect(result.receiver.enabled).toBe(false)
  })

  it('returns a bounded refusal object for a 409 instead of throwing', async () => {
    stubFetch(409, postRefusedDrift)

    const outcome = await postKyberCapture({ action: 'disable', harness: 'claude-code', dryRun: false })

    expect(outcome.outcome).toBe('refused')
    const refusal = outcome as KyberCaptureRefusal
    expect(refusal.reason).toBe('drift')
    expect(refusal.error).toBe(postRefusedDrift.error)
    expect(refusal.events[0]!.kind).toBe('refused')
    expect(refusal.events[0]!.drift).toEqual([
      { key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: '0' },
      { key: 'env.OTEL_LOG_RAW_API_BODIES', written: '1', current: null },
    ])
  })

  it('degrades an out-of-vocabulary refusal reason and caps the refusal text', async () => {
    const body = { ...clone(postRefusedDrift), reason: 'cosmic-rays', error: 'e'.repeat(50_000) }
    stubFetch(409, body)

    const refusal = (await postKyberCapture({ action: 'disable', harness: 'claude-code', dryRun: false })) as KyberCaptureRefusal

    expect(refusal.outcome).toBe('refused')
    expect(refusal.reason).toBe('unknown')
    expect(refusal.error.length).toBeLessThanOrEqual(4096)
  })

  it('treats a 409 without a parseable refusal body as a refusal with no events', async () => {
    stubFetch(409, 'not json at all')

    const refusal = (await postKyberCapture({ action: 'enable', harness: 'opencode', dryRun: false })) as KyberCaptureRefusal

    expect(refusal.outcome).toBe('refused')
    expect(refusal.reason).toBe('unknown')
    expect(refusal.events).toEqual([])
  })

  it.each([400, 403, 404, 405, 413, 415, 500])('throws KyberApiError for status %i', async (status) => {
    stubFetch(status, { error: 'nope' })

    await expect(postKyberCapture({ action: 'enable', harness: 'claude-code', dryRun: true })).rejects.toMatchObject({
      name: 'KyberApiError',
      status,
      path: '/api/kyber/capture',
    })
  })

  it('drops unknown fields and unknown event kinds from a result', async () => {
    const body = clone(postWroteEnable) as Record<string, unknown>
    body.debug = 'x'
    ;(body.events as Array<Record<string, unknown>>).push({ kind: 'teleported', harness: 'copilot', fileId: 'f', path: '/p' })
    ;(body.events as Array<Record<string, unknown>>)[0]!.rawBody = { token: 'sk-leak' }
    stubFetch(200, body)

    const result = asResult(await postKyberCapture({ action: 'enable', harness: 'copilot', dryRun: false }))

    expect(result.events).toHaveLength(1)
    expect(JSON.stringify(result)).not.toContain('sk-leak')
    expect('debug' in result).toBe(false)
  })

  it('throws KyberApiError for a 2xx body with an unrecognised outcome', async () => {
    stubFetch(200, { ...clone(postWroteEnable), outcome: 'exploded' })

    await expect(postKyberCapture({ action: 'enable', harness: 'copilot', dryRun: false })).rejects.toBeInstanceOf(KyberApiError)
  })
})
