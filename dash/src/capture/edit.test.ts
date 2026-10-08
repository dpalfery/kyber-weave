import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { applyBlockEdits, findBlockConflict, revertBlockEdits } from './edit-block.js'
import { applyJsonEdits, revertJsonEdits } from './edit-json.js'
import { healthzUrlForEndpoint, probeReceiver, RECEIVER_DOWN_REMEDY } from './probe.js'
import { loadCaptureReceipt, saveCaptureReceipt, sha256Hex } from './receipt.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-units-'))
  temporaryRoots.push(home)
  return home
}

describe('edit-json (jsonc-parser)', () => {
  it('preserves comments and formatting while recording absent priors', () => {
    // Synthetic JSONC fixture: real key names and structure, invented content.
    const initial = '{\n  // synthetic fixture comment\n  "theme": "dark"\n}\n'
    const result = applyJsonEdits(initial, { 'otel.endpoint': 'http://127.0.0.1:4318' })

    expect(result.changed).toBe(true)
    expect(result.content).toContain('// synthetic fixture comment')
    expect(result.content).toContain('http://127.0.0.1:4318')
    expect(result.prior['otel.endpoint']).toEqual({ present: false })
  })

  it('records present priors and reverts to the prior value', () => {
    const initial = '{\n  "otel.endpoint": "http://192.0.2.9:4318"\n}\n'
    const applied = applyJsonEdits(initial, { 'otel.endpoint': 'http://127.0.0.1:4318' })
    expect(applied.prior['otel.endpoint']).toEqual({
      present: true,
      value: 'http://192.0.2.9:4318',
    })

    const reverted = revertJsonEdits(applied.content, {
      'otel.endpoint': { written: 'http://127.0.0.1:4318', prior: { present: true, value: 'http://192.0.2.9:4318' } },
    })
    expect(reverted.drift).toEqual([])
    expect(reverted.content).toContain('http://192.0.2.9:4318')
  })

  it('reports drift instead of reverting a changed key', () => {
    const applied = applyJsonEdits('{\n  "theme": "dark"\n}\n', {
      'otel.endpoint': 'http://127.0.0.1:4318',
    })
    const drifted = applied.content.replace('http://127.0.0.1:4318', 'http://192.0.2.9:4318')

    const reverted = revertJsonEdits(drifted, {
      'otel.endpoint': { written: 'http://127.0.0.1:4318', prior: { present: false } },
    })
    expect(reverted.changed).toBe(false)
    expect(reverted.drift).toEqual([
      {
        key: 'otel.endpoint',
        written: 'http://127.0.0.1:4318',
        current: 'http://192.0.2.9:4318',
      },
    ])
  })
})

describe('edit-block (TOML and YAML managed block)', () => {
  it('appends a marker-delimited block and reads it back', () => {
    const applied = applyBlockEdits('theme = "dark"\n', { 'otel.endpoint': 'http://127.0.0.1:4318' }, 'toml')

    expect(applied.changed).toBe(true)
    expect(applied.content).toContain('BEGIN KYBERDASH MANAGED BLOCK')
    expect(applied.content).toContain('END KYBERDASH MANAGED BLOCK')
    expect(applied.content).toContain('http://127.0.0.1:4318')
    expect(applied.prior['otel.endpoint']).toEqual({ present: false })

    const reverted = revertBlockEdits(applied.content, {
      'otel.endpoint': { written: 'http://127.0.0.1:4318', prior: { present: false } },
    })
    expect(reverted.drift).toEqual([])
    expect(reverted.content).toBe('theme = "dark"\n')
  })

  it('refuses with a snippet when the key already exists outside the block', () => {
    const conflict = findBlockConflict('otel.endpoint = "http://192.0.2.9:4318"\n', ['otel.endpoint'])

    expect(conflict).not.toBeNull()
    expect(conflict?.key).toBe('otel.endpoint')
    expect(conflict?.snippet).toContain('otel.endpoint')

    const refused = applyBlockEdits('otel.endpoint = "http://192.0.2.9:4318"\n', {
      'otel.endpoint': 'http://127.0.0.1:4318',
    }, 'toml')
    expect(refused.conflict?.snippet).toContain('otel.endpoint')
  })
})

describe('receipt', () => {
  it('computes stable SHA-256 digests', () => {
    expect(sha256Hex('abc')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
  })

  it('round-trips through the receipt file under a temporary HOME', () => {
    const home = makeHome()
    saveCaptureReceipt(home, {
      version: 1,
      files: [
        {
          path: join(home, '.test-capture', 'synthetic.json'),
          format: 'json',
          harnessId: 'synthetic',
          keys: {
            'otel.endpoint': { written: 'http://127.0.0.1:4318', prior: { present: false } },
          },
          preSha256: '0'.repeat(64),
          postSha256: '1'.repeat(64),
        },
      ],
    })

    const loaded = loadCaptureReceipt(home)
    expect(loaded?.version).toBe(1)
    expect(loaded?.files).toHaveLength(1)
    expect(loaded?.files[0]?.keys['otel.endpoint']?.written).toBe('http://127.0.0.1:4318')
    expect(readFileSync(join(home, '.kyberdash', 'capture-receipt.json'), 'utf8')).toContain('capture-receipt')
  })

  it('loads null when no receipt was recorded', () => {
    const home = makeHome()
    expect(loadCaptureReceipt(home)).toBeNull()
    writeFileSync(join(home, 'unrelated.txt'), 'synthetic')
    expect(loadCaptureReceipt(home)).toBeNull()
  })
})

describe('probe', () => {
  it('derives the healthz URL from the OTLP endpoint', () => {
    expect(healthzUrlForEndpoint('http://127.0.0.1:4318')).toBe('http://127.0.0.1:4318/healthz')
    expect(healthzUrlForEndpoint('http://127.0.0.1:4318/')).toBe('http://127.0.0.1:4318/healthz')
  })

  it('classifies the KyberDash receiver, a foreign listener, and no listener', async () => {
    const own = await probeReceiver('http://127.0.0.1:4318', async () => ({
      status: 200,
      body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
    }))
    expect(own.state).toBe('kyberdash')

    const foreign = await probeReceiver('http://127.0.0.1:4318', async () => ({
      status: 200,
      body: '<html>synthetic foreign console</html>',
    }))
    expect(foreign.state).toBe('foreign')

    const none = await probeReceiver('http://127.0.0.1:4318', async () => {
      throw new Error('connect ECONNREFUSED (synthetic)')
    })
    expect(none.state).toBe('none')
  })

  it('the down remedy names the tray settings and the otel command', () => {
    expect(RECEIVER_DOWN_REMEDY).toContain('host receiver')
    expect(RECEIVER_DOWN_REMEDY).toContain('launch at login')
    expect(RECEIVER_DOWN_REMEDY).toContain('kyberdash kyber otel')
  })
})
