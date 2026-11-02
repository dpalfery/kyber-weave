// The Factory (Droid) capture hook runner: the one-shot process Factory runs for every hook
// event, the delivery it makes to the loopback OTLP receiver, and the bounded file spool it
// parks undelivered events in.
//
// The runner and the spool are written by the next task, so every test here fails until they
// exist. Both modules are loaded inside each test rather than imported at the top of the file, so
// the RED proof is per test: each one fails on the missing module and names the guarantee it was
// pinning, instead of the whole file failing to collect as one error.
//
// The contract this file pins:
//
//   - `runHook(options)` reads the hook payload from stdin and resolves with the process exit
//     code. It resolves 0 on EVERY path - a valid payload, malformed JSON, empty stdin, an
//     unknown hook event, a payload with a missing field, an internal error - and never rejects.
//     A capture plugin must never fail the host that ran it.
//   - stdout carries the hook protocol and nothing else: an ordinary run writes no diagnostic
//     text to stdout at all. Diagnostics go to stderr and to a bounded tail file.
//   - the tail file is capped at 64 KB and drops its oldest content when it grows past the cap.
//   - a hook payload becomes AT MOST one mapped span, through the existing mapper:
//     `mapHookEvent(payload, { contentAllowed })`.
//   - the prompt text travels only when the consent file says so. A consent-off run carries no
//     prompt text at all.
//   - the span is POSTed to the loopback OTLP receiver at http://127.0.0.1:4318/v1/traces as an
//     OTLP trace export body, and the delivery is bounded: it carries an abort signal and its
//     deadline is at most two seconds.
//   - a failed delivery is parked in a file-backed spool, and every run drains that spool first.
//
//   - the spool holds at most 500 events or 4 MB, whichever comes first, and drops the oldest
//     events when it is full. It lives under KyberDash's own state directory
//     (`<stateDir>/capture/factory`), never in the owner's Factory tree, and it survives the
//     process: an event parked by one run is delivered by a later run.
//
// The API the runner must expose:
//
//   runHook(options?): Promise<number>
//     Reads the payload, maps it, delivers it, and resolves with the exit code (always 0).
//     Options: `fetchFn`, `endpoint`, `consentPath`, `stateDir`, `readStdin`, `stdout`, `stderr`,
//     `sleepFn`, `randomFn`, `requestTimeoutMs`. Every option is injected, so a test never
//     reaches the developer's home directory or the live receiver.
//
//   diagnosticTailPath(stateDir): string
//     The bounded diagnostic tail file under a KyberDash state root.
//
//   appendDiagnosticTail(tailPath, text): void
//     Append one diagnostic to the tail file, keeping the file within 64 KB by dropping the
//     oldest content.
//
// The API the spool must expose (all take the KyberDash state root, never the Factory tree):
//
//   factorySpoolDir(stateDir): string        -> `<stateDir>/capture/factory`
//   appendSpoolEvent(stateDir, event): void
//   readSpoolEvents(stateDir): unknown[]
//   clearSpool(stateDir): void

import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, sep } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { FACTORY_KEYS } from './contract.js'

/** The loopback OTLP/HTTP traces endpoint the receiver listens on. */
const LOOPBACK_TRACES_URL = 'http://127.0.0.1:4318/v1/traces'
/** The prompt text that must not travel unless the consent file allows content. */
const PROMPT_TEXT = 'the prompt that must not travel without consent'
/** The cap on the diagnostic tail file. */
const TAIL_CAP_BYTES = 64 * 1024
/** The cap on the spool, in events and in bytes. */
const SPOOL_MAX_EVENTS = 500
const SPOOL_MAX_BYTES = 4 * 1024 * 1024

// ---------------------------------------------------------------------------------------------
// Scratch directories

const made: string[] = []

afterEach(() => {
  for (const dir of made.splice(0)) rmSync(dir, { recursive: true, force: true })
})

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-factory-hook-'))
  made.push(dir)
  return dir
}

/** The consent file of the Factory plugin, inside the injected state root. */
function consentPathIn(dir: string): string {
  return join(dir, '.kyberdash', 'factory-plugin.json')
}

function writeConsent(path: string, content: boolean): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify({ version: 1, content }))
}

/** Every file under a directory tree, in bytes. */
function sizeOfTree(dir: string): number {
  let total = 0
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name)
    total += entry.isDirectory() ? sizeOfTree(full) : statSync(full).size
  }
  return total
}

// ---------------------------------------------------------------------------------------------
// The receiver: a recording fetch that never touches the network

type Attempt = {
  url: string
  contentType: string | null
  raw: string
  body: unknown
  signal: AbortSignal | null
  abortedAt: number | null
}

type Receiver = {
  fetch: (url: string, init?: RequestInit) => Promise<Response>
  attempts: Attempt[]
  accepted: unknown[]
}

function receiver(policy: (attempt: number) => number | 'throw' | 'hang' = () => 200): Receiver {
  const attempts: Attempt[] = []
  const accepted: unknown[] = []
  const fetch = async (url: string, init?: RequestInit): Promise<Response> => {
    const raw = String(init?.body ?? '')
    let body: unknown = raw
    try {
      body = JSON.parse(raw)
    } catch {
      // A body that is not JSON is kept as text, so the test can say so.
    }
    const attempt: Attempt = {
      url,
      contentType: new Headers(init?.headers).get('content-type'),
      raw,
      body,
      signal: init?.signal ?? null,
      abortedAt: null,
    }
    attempts.push(attempt)
    const answer = policy(attempts.length)
    if (answer === 'throw') throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
    if (answer === 'hang') {
      return new Promise<Response>((_, reject) => {
        attempt.signal?.addEventListener(
          'abort',
          () => {
            attempt.abortedAt = Date.now()
            reject(new Error('aborted (synthetic)'))
          },
          { once: true },
        )
      })
    }
    if (answer >= 200 && answer < 300) accepted.push(body)
    return new Response('{}', { status: answer, headers: { 'content-type': 'application/json' } })
  }
  return { fetch, attempts, accepted }
}

// ---------------------------------------------------------------------------------------------
// The wire shape the runner delivers

type WireSpan = {
  name?: string
  traceId?: string
  spanId?: string
  attributes?: Array<{ key: string; value?: unknown }>
}

/** Every span of an OTLP trace export body, however the body groups them. */
function spansOf(body: unknown): WireSpan[] {
  const record = body as { resourceSpans?: Array<{ scopeSpans?: Array<{ spans?: WireSpan[] }> }> } | null
  const spans: WireSpan[] = []
  for (const resource of record?.resourceSpans ?? []) {
    for (const scope of resource?.scopeSpans ?? []) spans.push(...(scope?.spans ?? []))
  }
  return spans
}

/** The string an attribute carries, or undefined when the span does not carry it. */
function stringAttribute(span: WireSpan, key: string): string | undefined {
  const attribute = (span.attributes ?? []).find((candidate) => candidate.key === key)
  const value = attribute?.value
  if (typeof value !== 'object' || value === null) return undefined
  const text = (value as { stringValue?: unknown }).stringValue
  return typeof text === 'string' ? text : undefined
}

/** The mapped shape of a span: what the mapper decides, without the transport's own fields. */
function mappedShape(span: WireSpan): Record<string, unknown> {
  return {
    name: span.name,
    traceId: span.traceId,
    spanId: span.spanId,
    attributes: span.attributes ?? [],
  }
}

// ---------------------------------------------------------------------------------------------
// The runner

type RunInput = {
  fetch: Receiver['fetch']
  stateDir: string
  consentPath: string
  payload?: unknown
  raw?: string
  readStdin?: () => Promise<string>
}

type RunResult = { code: number; stdout: string; stderr: string }

/** Run the hook runner once, with everything injected. */
async function runHook(input: RunInput): Promise<RunResult> {
  const entry = await import('./entry.js')
  const run = (entry as { runHook?: unknown }).runHook
  if (typeof run !== 'function') {
    throw new Error('entry.ts must export runHook(options): Promise<number>')
  }
  let stdout = ''
  let stderr = ''
  const readStdin =
    input.readStdin ?? (async () => (input.raw ?? JSON.stringify(input.payload ?? {})))
  const code = await (
    run as (options: Record<string, unknown>) => Promise<number>
  )({
    fetchFn: input.fetch,
    consentPath: input.consentPath,
    stateDir: input.stateDir,
    readStdin,
    stdout: (text: string) => {
      stdout += text
    },
    stderr: (text: string) => {
      stderr += text
    },
  })
  return { code, stdout, stderr }
}

/** A hook payload of the event under test. */
function promptPayload(sessionId = 'ses_factory_01', messageId = 'msg_factory_01'): Record<string, unknown> {
  return {
    hook_event_name: 'UserPromptSubmit',
    session_id: sessionId,
    message_id: messageId,
    prompt: PROMPT_TEXT,
    model: 'factory-model-01',
    cwd: '/work/repo',
  }
}

// ---------------------------------------------------------------------------------------------
// The runner

describe('the Factory hook runner never fails its host', () => {
  it('exits 0 and writes no diagnostic text to stdout for a well-formed hook payload', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    const { code, stdout } = await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: promptPayload(),
    })
    expect(code).toBe(0)
    expect(stdout, 'stdout carries the hook protocol, not diagnostics').toBe('')
  })

  const brokenPayloads: Array<{ name: string; raw: string }> = [
    { name: 'malformed JSON', raw: '{"hook_event_name": "UserPromptSubmit"' },
    { name: 'empty stdin', raw: '' },
    {
      name: 'an unknown hook event',
      raw: JSON.stringify({ hook_event_name: 'NotAFactoryHook', session_id: 'ses_factory_01' }),
    },
    {
      name: 'a payload that is not an object',
      raw: JSON.stringify(['not', 'an', 'object']),
    },
  ]

  for (const broken of brokenPayloads) {
    it(`exits 0 and delivers nothing for ${broken.name}`, async () => {
      const stateDir = tempDir()
      const rcv = receiver()
      const { code, stdout } = await runHook({
        fetch: rcv.fetch,
        stateDir,
        consentPath: consentPathIn(stateDir),
        raw: broken.raw,
      })
      expect(code).toBe(0)
      expect(stdout).toBe('')
      expect(spansOf(rcv.attempts[0]?.body)).toEqual([])
    })
  }

  it('exits 0 for a payload whose fields are missing', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    const { code, stdout } = await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: { hook_event_name: 'UserPromptSubmit' },
    })
    expect(code).toBe(0)
    expect(stdout).toBe('')
    const spans = spansOf(rcv.attempts[0]?.body)
    expect(spans).toHaveLength(1)
    expect(spans[0]?.name).toBe('factory.turn')
  })

  it('exits 0 when the runner fails internally, and reports it in the diagnostic tail', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    const { code, stdout } = await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      readStdin: async () => {
        throw new Error('stdin exploded (synthetic)')
      },
    })
    expect(code, 'an internal error must not fail the host').toBe(0)
    expect(stdout).toBe('')
    const entry = await import('./entry.js')
    const tailPath = (entry as { diagnosticTailPath?: unknown }).diagnosticTailPath
    if (typeof tailPath !== 'function') {
      throw new Error('entry.ts must export diagnosticTailPath(stateDir): string')
    }
    const tail = (tailPath as (dir: string) => string)(stateDir)
    expect(readFileSync(tail, 'utf8'), 'the failure is reported in the diagnostic tail').not.toBe('')
  })
})

// ---------------------------------------------------------------------------------------------
// Mapping and delivery

describe('the Factory hook runner maps one payload onto one span', () => {
  it('turns one UserPromptSubmit payload into exactly one factory.turn span', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    const payload = promptPayload()
    await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload,
    })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    const spans = spansOf(rcv.attempts[0]?.body)
    expect(spans).toHaveLength(1)
    expect(spans[0]?.name).toBe('factory.turn')

    const { mapHookEvent } = await import('./mapper.js')
    const expected = mapHookEvent(payload, { contentAllowed: false })
    expect(expected).toHaveLength(1)
    expect(spans.map(mappedShape), 'the span is exactly what the mapper produced').toEqual(
      expected.map((span) => mappedShape(span as WireSpan)),
    )
  })

  it('maps a PreCompact payload onto no span and delivers nothing', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: { hook_event_name: 'PreCompact', session_id: 'ses_factory_01' },
    })
    const delivered = rcv.attempts.flatMap((attempt) => spansOf(attempt.body))
    expect(delivered, 'contract 1 names no span for a compaction').toEqual([])
  })

  it('posts the span to the loopback OTLP traces URL as an OTLP trace export body', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: promptPayload(),
    })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    const attempt = rcv.attempts[0]!
    expect(attempt.url).toBe(LOOPBACK_TRACES_URL)
    expect(attempt.contentType).toBe('application/json')
    expect(() => JSON.parse(attempt.raw)).not.toThrow()
    const body = JSON.parse(attempt.raw) as { resourceSpans?: unknown[] }
    expect(Array.isArray(body.resourceSpans)).toBe(true)
    expect(body.resourceSpans?.length).toBeGreaterThan(0)
  })

  it('bounds the delivery: the POST carries an abort signal and its deadline is at most two seconds', async () => {
    const stateDir = tempDir()
    // The first attempt never answers; every later attempt does, so a runner that retries
    // still finishes and the test never waits on a second two-second deadline.
    const rcv = receiver((attempt) => (attempt === 1 ? 'hang' : 200))
    const started = Date.now()
    const { code } = await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: promptPayload(),
    })
    expect(code).toBe(0)
    expect(rcv.attempts.length).toBeGreaterThan(0)
    expect(rcv.attempts[0]?.signal, 'the delivery must carry an abort signal').toBeInstanceOf(AbortSignal)
    expect(
      typeof rcv.attempts[0]?.abortedAt,
      'a receiver that never answers must be aborted',
    ).toBe('number')
    const deadline = (rcv.attempts[0]?.abortedAt ?? Number.POSITIVE_INFINITY) - started
    expect(deadline, 'the delivery deadline must be at most two seconds').toBeLessThan(2400)
  })
})

// ---------------------------------------------------------------------------------------------
// Consent

describe('the Factory hook runner honours the consent file', () => {
  it('carries no prompt text when the consent file says content off', async () => {
    const stateDir = tempDir()
    const consentPath = consentPathIn(stateDir)
    writeConsent(consentPath, false)
    const rcv = receiver()
    await runHook({ fetch: rcv.fetch, stateDir, consentPath, payload: promptPayload() })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    const attempt = rcv.attempts[0]!
    expect(attempt.raw, 'the prompt text must not travel without consent').not.toContain(PROMPT_TEXT)
    const spans = spansOf(attempt.body)
    expect(stringAttribute(spans[0] ?? {}, FACTORY_KEYS.promptText)).toBeUndefined()
  })

  it('carries the prompt text when the consent file says content on', async () => {
    const stateDir = tempDir()
    const consentPath = consentPathIn(stateDir)
    writeConsent(consentPath, true)
    const rcv = receiver()
    await runHook({ fetch: rcv.fetch, stateDir, consentPath, payload: promptPayload() })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    const spans = spansOf(rcv.attempts[0]?.body)
    expect(stringAttribute(spans[0] ?? {}, FACTORY_KEYS.promptText)).toBe(PROMPT_TEXT)
  })

  it('carries no prompt text when there is no consent file at all', async () => {
    const stateDir = tempDir()
    const rcv = receiver()
    await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: promptPayload(),
    })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    expect(rcv.attempts[0]?.raw).not.toContain(PROMPT_TEXT)
  })
})

// ---------------------------------------------------------------------------------------------
// The diagnostic tail

describe('the Factory diagnostic tail is bounded', () => {
  it('keeps the tail within 64 KB and drops its oldest content', async () => {
    const entry = await import('./entry.js')
    const append = (entry as { appendDiagnosticTail?: unknown }).appendDiagnosticTail
    const tailPath = (entry as { diagnosticTailPath?: unknown }).diagnosticTailPath
    if (typeof append !== 'function' || typeof tailPath !== 'function') {
      throw new Error('entry.ts must export diagnosticTailPath(stateDir) and appendDiagnosticTail(path, text)')
    }
    const stateDir = tempDir()
    const tail = (tailPath as (dir: string) => string)(stateDir)
    const write = append as (path: string, text: string) => void
    write(tail, 'A'.repeat(40_000))
    write(tail, 'B'.repeat(40_000))
    write(tail, 'C'.repeat(40_000))
    const text = readFileSync(tail, 'utf8')
    expect(Buffer.byteLength(text, 'utf8')).toBeLessThanOrEqual(TAIL_CAP_BYTES)
    expect(text, 'the newest diagnostic must survive').toContain('C')
    expect(text, 'the oldest diagnostic must be dropped').not.toContain('A')
  })

  it('keeps the tail inside the KyberDash state root', async () => {
    const entry = await import('./entry.js')
    const tailPath = (entry as { diagnosticTailPath?: unknown }).diagnosticTailPath
    if (typeof tailPath !== 'function') {
      throw new Error('entry.ts must export diagnosticTailPath(stateDir): string')
    }
    const stateDir = tempDir()
    const tail = (tailPath as (dir: string) => string)(stateDir)
    expect(tail.startsWith(stateDir + sep), 'the tail lives under the KyberDash state root').toBe(true)
  })
})

// ---------------------------------------------------------------------------------------------
// The spool

describe('the Factory spool is bounded and survives the process', () => {
  it('parks a failed delivery and delivers it in a later run', async () => {
    const stateDir = tempDir()
    const consentPath = consentPathIn(stateDir)
    const failing = receiver(() => 'throw')
    const first = await runHook({
      fetch: failing.fetch,
      stateDir,
      consentPath,
      payload: promptPayload('ses_first'),
    })
    expect(first.code).toBe(0)

    const spool = await import('./spool.js')
    const read = (spool as { readSpoolEvents?: unknown }).readSpoolEvents
    if (typeof read !== 'function') {
      throw new Error('spool.ts must export readSpoolEvents(stateDir): unknown[]')
    }
    const parked = (read as (dir: string) => unknown[])(stateDir)
    expect(parked.length, 'the undelivered event is parked, not lost').toBeGreaterThan(0)

    const accepting = receiver()
    const second = await runHook({
      fetch: accepting.fetch,
      stateDir,
      consentPath,
      payload: promptPayload('ses_second'),
    })
    expect(second.code).toBe(0)
    const delivered = accepting.attempts.flatMap((attempt) => spansOf(attempt.body))
    const sessions = delivered.map((span) => stringAttribute(span, FACTORY_KEYS.sessionId))
    expect(sessions, 'the parked event is delivered by the later run').toContain('ses_first')
    expect(sessions).toContain('ses_second')
    expect((read as (dir: string) => unknown[])(stateDir), 'the drain empties the spool').toEqual([])
  })

  it('drains the spool before it sends the current event', async () => {
    const stateDir = tempDir()
    const spool = await import('./spool.js')
    const append = (spool as { appendSpoolEvent?: unknown }).appendSpoolEvent
    if (typeof append !== 'function') {
      throw new Error('spool.ts must export appendSpoolEvent(stateDir, event): void')
    }
    const { mapHookEvent } = await import('./mapper.js')
    const parked = mapHookEvent(
      { hook_event_name: 'SessionStart', session_id: 'ses_parked' },
      { contentAllowed: false },
    )
    expect(parked).toHaveLength(1)
    ;(append as (dir: string, event: unknown) => void)(stateDir, parked[0])

    const rcv = receiver()
    await runHook({
      fetch: rcv.fetch,
      stateDir,
      consentPath: consentPathIn(stateDir),
      payload: promptPayload('ses_current'),
    })
    expect(rcv.attempts.length).toBeGreaterThan(0)
    expect(
      rcv.attempts[0]?.raw,
      'the spool is drained before the current event is sent',
    ).toContain('ses_parked')
    const delivered = rcv.attempts.flatMap((attempt) => spansOf(attempt.body))
    expect(delivered.map((span) => stringAttribute(span, FACTORY_KEYS.sessionId))).toContain(
      'ses_current',
    )
  })

  it('keeps at most 500 events and drops the oldest first', async () => {
    const stateDir = tempDir()
    const spool = await import('./spool.js')
    const append = (spool as { appendSpoolEvent?: unknown }).appendSpoolEvent
    const read = (spool as { readSpoolEvents?: unknown }).readSpoolEvents
    if (typeof append !== 'function' || typeof read !== 'function') {
      throw new Error('spool.ts must export appendSpoolEvent(stateDir, event) and readSpoolEvents(stateDir)')
    }
    for (let index = 0; index <= SPOOL_MAX_EVENTS; index += 1) {
      ;(append as (dir: string, event: unknown) => void)(stateDir, { index })
    }
    const events = (read as (dir: string) => unknown[])(stateDir)
    expect(events.length).toBe(SPOOL_MAX_EVENTS)
    expect(events[0], 'the oldest event is dropped first').toEqual({ index: 1 })
    expect(events[events.length - 1]).toEqual({ index: SPOOL_MAX_EVENTS })
  })

  it('keeps the spool within 4 MB and drops the oldest events', async () => {
    const stateDir = tempDir()
    const spool = await import('./spool.js')
    const append = (spool as { appendSpoolEvent?: unknown }).appendSpoolEvent
    const read = (spool as { readSpoolEvents?: unknown }).readSpoolEvents
    if (typeof append !== 'function' || typeof read !== 'function') {
      throw new Error('spool.ts must export appendSpoolEvent(stateDir, event) and readSpoolEvents(stateDir)')
    }
    const blob = 'x'.repeat(1_000_000)
    for (let index = 0; index < 6; index += 1) {
      ;(append as (dir: string, event: unknown) => void)(stateDir, { index, blob })
    }
    expect(sizeOfTree(stateDir), 'the spool is capped at 4 MB').toBeLessThanOrEqual(SPOOL_MAX_BYTES)
    const events = (read as (dir: string) => unknown[])(stateDir)
    expect(events.length).toBeGreaterThan(0)
    expect(events[events.length - 1], 'the newest event survives').toMatchObject({ index: 5 })
    expect(events[0], 'the oldest events are dropped').not.toMatchObject({ index: 0 })
  })

  it('lives under the KyberDash state directory, never in the owner tree', async () => {
    const stateDir = tempDir()
    const spool = await import('./spool.js')
    const dir = (spool as { factorySpoolDir?: unknown }).factorySpoolDir
    if (typeof dir !== 'function') {
      throw new Error('spool.ts must export factorySpoolDir(stateDir): string')
    }
    const spoolDir = (dir as (root: string) => string)(stateDir)
    expect(spoolDir.startsWith(stateDir + sep)).toBe(true)
    expect(spoolDir.endsWith(join('capture', 'factory'))).toBe(true)
  })
})
