import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { createHash } from 'node:crypto'

import { kiloCaptureDir, readKiloCapture } from './kilo-capture.js'

let dir: string
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'kilo-capture-'))
})
afterEach(() => {
  rmSync(dir, { recursive: true, force: true })
})

function blob(text: string): string {
  const hash = createHash('sha256').update(text).digest('hex')
  mkdirSync(join(dir, 'blobs'), { recursive: true })
  writeFileSync(join(dir, 'blobs', hash), text)
  return hash
}

function index(rows: object[]): void {
  writeFileSync(join(dir, 'index.jsonl'), rows.map((row) => JSON.stringify(row)).join('\n') + '\n')
}

describe('kiloCaptureDir', () => {
  it('honours the override the plugin honours', () => {
    process.env.KYBERDASH_KILO_CAPTURE_DIR = '/x/y'
    expect(kiloCaptureDir()).toBe('/x/y')
    delete process.env.KYBERDASH_KILO_CAPTURE_DIR
    expect(kiloCaptureDir()).toMatch(/\.kyberdash[\\/]capture[\\/]kilo$/)
  })
})

describe('readKiloCapture', () => {
  it('returns an empty capture when nothing was recorded', () => {
    expect(readKiloCapture(dir).forMessage('ses_1', 'msg_1', 1000)).toBeUndefined()
  })

  it('returns the system prompt and tool definitions of the matching call', () => {
    const systemHash = blob('You are Kilo.')
    const bashHash = blob(JSON.stringify({ id: 'bash', description: 'Run a command' }))
    index([{ v: 1, ts: 1000, sessionID: 'ses_1', messageID: 'msg_1', agent: 'code', model: 'm', systemHash, toolHashes: { bash: bashHash } }])
    const call = readKiloCapture(dir).forMessage('ses_1', 'msg_1', 1000)
    expect(call?.systemPrompt).toBe('You are Kilo.')
    expect(call?.tools).toEqual([{ id: 'bash', text: JSON.stringify({ id: 'bash', description: 'Run a command' }) }])
  })

  it('picks the call nearest in time among calls for the same user message', () => {
    const a = blob('prompt A')
    const b = blob('prompt B')
    index([
      { v: 1, ts: 1000, sessionID: 's', messageID: 'u1', systemHash: a, toolHashes: {} },
      { v: 1, ts: 9000, sessionID: 's', messageID: 'u1', systemHash: b, toolHashes: {} },
    ])
    const capture = readKiloCapture(dir)
    expect(capture.forMessage('s', 'u1', 1200)?.systemPrompt).toBe('prompt A')
    expect(capture.forMessage('s', 'u1', 8800)?.systemPrompt).toBe('prompt B')
  })

  it('does not match a different session or user message', () => {
    index([{ v: 1, ts: 1000, sessionID: 's', messageID: 'u1', systemHash: blob('x'), toolHashes: {} }])
    const capture = readKiloCapture(dir)
    expect(capture.forMessage('other', 'u1', 1000)).toBeUndefined()
    expect(capture.forMessage('s', 'u2', 1000)).toBeUndefined()
  })

  it('omits a bucket whose blob is missing rather than reporting it empty', () => {
    index([{ v: 1, ts: 1, sessionID: 's', messageID: 'u', systemHash: 'f'.repeat(64), toolHashes: { bash: 'e'.repeat(64) } }])
    const call = readKiloCapture(dir).forMessage('s', 'u', 1)
    expect(call?.systemPrompt).toBeUndefined()
    expect(call?.tools).toEqual([])
  })

  it('rejects a blob name that is not a sha256, so the index cannot point outside the store', () => {
    writeFileSync(join(dir, 'secret.txt'), 'outside')
    index([{ v: 1, ts: 1, sessionID: 's', messageID: 'u', systemHash: '../secret.txt', toolHashes: {} }])
    expect(readKiloCapture(dir).forMessage('s', 'u', 1)?.systemPrompt).toBeUndefined()
  })

  it('skips corrupt index lines', () => {
    const systemHash = blob('ok')
    writeFileSync(
      join(dir, 'index.jsonl'),
      `not json\n${JSON.stringify({ v: 1, ts: 1, sessionID: 's', messageID: 'u', systemHash, toolHashes: {} })}\n{bad`,
    )
    expect(readKiloCapture(dir).forMessage('s', 'u', 1)?.systemPrompt).toBe('ok')
  })
})
