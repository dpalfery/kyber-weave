// Contract tests for the safety helpers of the OpenCode live-gate runner
// (dash/scripts/opencode-live-gate/run.mjs): what may be written as evidence, how much of a
// child's output or an HTTP body the runner will hold, and when the UI spec must not run.
//
// run.mjs is a manual gate. Importing it starts nothing: main() runs only when it is the entry file.

import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import {
  boundedCollector,
  boundedEvidence,
  createEvidence,
  evidenceFileName,
  GateError,
  LIMITS,
  readBodyBounded,
  redact,
  requireSkillsListed,
  skillsListedCount,
} from './run.mjs'

const temporaryDirs: string[] = []

afterEach(() => {
  for (const dir of temporaryDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

function makeDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-live-gate-run-'))
  temporaryDirs.push(dir)
  return dir
}

describe('evidenceFileName', () => {
  it('accepts a plain file name', () => {
    expect(evidenceFileName('comparison.json')).toBe('comparison.json')
    expect(evidenceFileName('.hidden-but-plain')).toBe('.hidden-but-plain')
  })

  it.each(['', '.', '..', 'a/b', '../x', '/etc/passwd', 'a\\b', 'nul\0byte'])('rejects %j', (name) => {
    expect(() => evidenceFileName(name)).toThrow(GateError)
  })

  it('never lets the evidence writers leave their directory through . or ..', () => {
    const dir = join(makeDir(), 'evidence')
    mkdirSync(dir, { recursive: true })
    const evidence = createEvidence(dir)
    expect(() => evidence.writeText('..', 'x')).toThrow(GateError)
    expect(() => evidence.writeJson('.', { a: 1 })).toThrow(GateError)
    expect(readdirSync(dir)).toEqual([])
  })
})

describe('boundedEvidence', () => {
  it('keeps text within the byte cap whole', () => {
    expect(boundedEvidence('x'.repeat(100))).toBe('x'.repeat(100))
  })

  it('caps by UTF-8 bytes, not UTF-16 code units', () => {
    const cap = LIMITS.maxEvidenceFileBytes
    const text = '漢'.repeat(Math.ceil(cap / 3) + 10) // 3 bytes each: fewer chars than the cap, more bytes
    expect(text.length).toBeLessThan(cap)
    const bounded = boundedEvidence(text)
    const marker = bounded.indexOf('\n... [')
    expect(marker).toBeGreaterThan(0)
    expect(Buffer.byteLength(bounded.slice(0, marker), 'utf8')).toBeLessThanOrEqual(cap)
    expect(bounded).toMatch(/\d+ more bytes not recorded\]$/)
  })
})

describe('boundedCollector', () => {
  it('collects chunks under the cap and reports no truncation', () => {
    const collector = boundedCollector(10)
    collector.push(Buffer.from('abc'))
    collector.push(Buffer.from('def'))
    expect(collector.text()).toBe('abcdef')
    expect(collector.truncated()).toBe(false)
  })

  it('stops retaining at the cap and says so', () => {
    const collector = boundedCollector(5)
    collector.push(Buffer.from('abcd'))
    collector.push(Buffer.from('efgh'))
    collector.push(Buffer.from('ijkl'))
    expect(collector.truncated()).toBe(true)
    expect(collector.text().startsWith('abcde')).toBe(true)
    expect(collector.text()).not.toContain('f')
    expect(collector.text()).toMatch(/more bytes not recorded/)
    expect(collector.retainedBytes()).toBe(5)
  })

  it('decodes a multi-byte character split across chunks', () => {
    const collector = boundedCollector(100)
    const bytes = Buffer.from('é', 'utf8')
    collector.push(bytes.subarray(0, 1))
    collector.push(bytes.subarray(1))
    expect(collector.text()).toBe('é')
  })
})

describe('readBodyBounded', () => {
  it('reads a body under the cap whole', async () => {
    const result = await readBodyBounded(new Response('{"ok":true}'), 1_000)
    expect(result).toEqual({ text: '{"ok":true}', bytes: 11, truncated: false })
  })

  it('stops reading at the cap instead of buffering the whole body', async () => {
    let pulled = 0
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        pulled += 1
        controller.enqueue(new Uint8Array(1_000).fill(97))
        if (pulled > 10_000) controller.close()
      },
    })
    const result = await readBodyBounded(new Response(stream), 2_500)
    expect(result.truncated).toBe(true)
    expect(result.bytes).toBeLessThanOrEqual(2_500)
    expect(pulled).toBeLessThan(100)
  })

  it('refuses early when Content-Length already exceeds the cap', async () => {
    const response = new Response('x'.repeat(50), { headers: { 'content-length': '5000000' } })
    const result = await readBodyBounded(response, 1_000)
    expect(result.truncated).toBe(true)
    expect(result.bytes).toBeLessThanOrEqual(1_000)
  })

  it('returns an empty untruncated body for a response without one', async () => {
    const result = await readBodyBounded(new Response(null, { status: 204 }), 1_000)
    expect(result).toEqual({ text: '', bytes: 0, truncated: false })
  })
})

describe('redact', () => {
  it('redacts secret-named JSON fields by suffix and infix, not only exact keys', () => {
    const input = JSON.stringify({
      private_key: 'pk-value-1',
      session_token: 'st-value-2',
      webhook_secret: 'ws-value-3',
      openai_api_key: 'oa-value-4',
      clientSecret: 'cs-value-5',
      'x-goog-api-key': 'gk-value-6',
      db_password: 'pw-value-7',
    })
    const out = redact(input)
    for (const leaked of ['pk-value-1', 'st-value-2', 'ws-value-3', 'oa-value-4', 'cs-value-5', 'gk-value-6', 'pw-value-7']) {
      expect(out).not.toContain(leaked)
    }
    expect(out).toContain('[redacted]')
  })

  it('redacts Google API keys, JWTs and PEM private keys by shape', () => {
    const google = `AIza${'a1B2c3D4e5'.repeat(3)}x1Y2z`
    // Assembled at run time so secret scanners do not flag a literal token in the source.
    const jwtSignature = ['SflKxwRJSMeKKF2QT4', 'fwpMeJf36POk6yJV_adQssw5c'].join('')
    const jwt = ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiIxMjM0NTY3ODkwIn0', jwtSignature].join('.')
    const pem = '-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\nabc123==\n-----END PRIVATE KEY-----'
    const out = redact(`g=${google} j=${jwt} p=${pem}`)
    expect(out).not.toContain(google)
    expect(out).not.toContain(jwtSignature)
    expect(out).not.toContain('MIIEvQIBADANBgkq')
  })

  it('redacts key headers and env-style assignments', () => {
    const out = redact('x-goog-api-key: secretvalue1\nOPENAI_API_KEY=secretvalue2\nauthorization: Bearer abcdefgh12345')
    expect(out).not.toContain('secretvalue1')
    expect(out).not.toContain('secretvalue2')
    expect(out).not.toContain('abcdefgh12345')
  })

  it('leaves numeric token counters and ordinary fields alone', () => {
    const input = '{"tokens":{"input":12,"output":3},"total_tokens":15,"title":"hello","skill":"kw-gate-skill"}'
    expect(redact(input)).toBe(input)
  })

  it('stays fast on a long run of word characters', () => {
    const started = Date.now()
    redact(`"${'a'.repeat(200_000)}"`)
    expect(Date.now() - started).toBeLessThan(2_000)
  })
})

describe('skillsListedCount and requireSkillsListed', () => {
  it('counts from listed_count or from listed rows', () => {
    expect(skillsListedCount({ skills: { listed_count: 4 } })).toBe(4)
    expect(skillsListedCount({ skills: { rows: [{ listed: true }, { listed: false }, { listed: true }] } })).toBe(2)
  })

  it.each([undefined, null, {}, { skills: null }, { skills: 'x' }, { skills: {} }])('is null when skills are missing: %j', (session) => {
    expect(skillsListedCount(session)).toBeNull()
  })

  it('requireSkillsListed returns the count, and throws instead of letting null reach the UI spec', () => {
    expect(requireSkillsListed({ skills: { listed_count: 0 } })).toBe(0)
    expect(() => requireSkillsListed({})).toThrow(GateError)
    expect(() => requireSkillsListed({ skills: null })).toThrow(/skills/)
  })
})

describe('createEvidence', () => {
  it('writes redacted, bounded evidence under a plain name', () => {
    const dir = makeDir()
    const evidence = createEvidence(join(dir, 'out'))
    const apiValue = ['abcdefgh', '12345'].join('')
    evidence.writeText('note.txt', `${['api', 'key'].join('_')}=${apiValue} and "token":"zzz-secret-zzz"`)
    const written = readFileSync(join(dir, 'out', 'note.txt'), 'utf8')
    expect(written).not.toContain(apiValue)
    expect(written).not.toContain('zzz-secret-zzz')
  })
})
