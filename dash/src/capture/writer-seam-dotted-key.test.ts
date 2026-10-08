import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { parse } from 'jsonc-parser'
import { afterEach, describe, expect, it } from 'vitest'

import { readJsonKeys } from './edit-json.js'
import { runCapture } from './index.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'

/** VS Code–style literal segment: one property name contains dots, then child fields. */
const LITERAL_ROOT = 'vendor.tool.otel'
const ENABLED_KEY = `${LITERAL_ROOT}.enabled`
const PROTOCOL_KEY = `${LITERAL_ROOT}.protocol`
const PROTOCOL_VALUE = 'http/protobuf'

const FIXTURE_COMMENT = '// dotted literal root fixture'
const HARNESS_ID = 'synthetic-dotted-root'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-writer-seam-dotted-'))
  temporaryHomes.push(home)
  return home
}

function configPath(home: string): string {
  return join(home, '.synthetic-capture', 'dotted-root.jsonc')
}

function seedDottedRootJsonc(home: string): string {
  const initial = `{
  ${FIXTURE_COMMENT}
  "${LITERAL_ROOT}": {
    "enabled": false,
    "protocol": "${PROTOCOL_VALUE}"
  },
}
`
  mkdirSync(dirname(configPath(home)), { recursive: true })
  writeFileSync(configPath(home), initial)
  return initial
}

function dottedRootHarnessWriter(): CaptureHarnessWriter {
  return {
    kind: 'managed',
    id: HARNESS_ID,
    displayName: 'Synthetic dotted-root harness',
    resolvePath: configPath,
    format: 'jsonc',
    desiredKeys: () => ({
      [ENABLED_KEY]: true,
    }),
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

describe('capture writer seam — dotted literal root property (P2.W)', () => {
  it('enable sets enabled on the literal root object without a parallel nested tree', async () => {
    const home = makeHome()
    seedDottedRootJsonc(home)
    const writer = dottedRootHarnessWriter()

    const result = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      harnessIds: [HARNESS_ID],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(result.exitCode).toBe(0)

    const written = readFileSync(configPath(home), 'utf8')
    expect(written).toContain(FIXTURE_COMMENT)

    const parsed: unknown = parse(written)
    expect(isRecord(parsed)).toBe(true)
    expect(parsed).toHaveProperty(LITERAL_ROOT)
    expect(Object.prototype.hasOwnProperty.call(parsed, 'vendor')).toBe(false)

    const literalRoot = readJsonKeys(written, [LITERAL_ROOT])[LITERAL_ROOT]
    expect(literalRoot.present).toBe(true)
    expect(isRecord(literalRoot.value)).toBe(true)
    expect(literalRoot.value.enabled).toBe(true)
    expect(literalRoot.value.protocol).toBe(PROTOCOL_VALUE)

    const enabled = readJsonKeys(written, [ENABLED_KEY])[ENABLED_KEY]
    expect(enabled.present).toBe(true)
    expect(enabled.value).toBe(true)
    expect(typeof enabled.value).toBe('boolean')

    const protocol = readJsonKeys(written, [PROTOCOL_KEY])[PROTOCOL_KEY]
    expect(protocol.present).toBe(true)
    expect(protocol.value).toBe(PROTOCOL_VALUE)
    expect(typeof protocol.value).toBe('string')
  })
})
