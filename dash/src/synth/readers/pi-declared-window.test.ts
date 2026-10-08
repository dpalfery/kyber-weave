// Pi turns carry the harness-declared context window for the turn's model,
// read from the user's `~/.pi/agent/models-store.json` (T7).
//
// The store path resolves from HOME, so these tests redirect HOME at a
// temporary directory holding a synthetic store. A missing or unparseable
// store means no window, never a default — and never an error.
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import modelsStoreFixture from '../fixtures/pi-models-store.json' with { type: 'json' }
import { piReader } from './pi.js'
import type { ReaderTurn } from './types.js'

// The transcript names the qualified `provider/model` form (as pi session
// files do); the store carries the bare model id.
const QUALIFIED_MODEL = 'synthetic-provider/synthetic-pi-test-model'
const DECLARED_WINDOW = 250_000

const tempRoots: string[] = []
let previousHome: string | undefined

beforeEach(() => {
  previousHome = process.env['HOME']
})

afterEach(() => {
  if (previousHome === undefined) delete process.env['HOME']
  else process.env['HOME'] = previousHome
  for (const root of tempRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function useTempHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-pi-home-'))
  tempRoots.push(home)
  process.env['HOME'] = home
  return home
}

function installStore(home: string, content: string): void {
  const dir = join(home, '.pi', 'agent')
  mkdirSync(dir, { recursive: true })
  writeFileSync(join(dir, 'models-store.json'), content)
}

function writePiSession(entries: unknown[]): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-pi-window-'))
  tempRoots.push(root)
  const path = join(root, 'synthetic-pi-session.jsonl')
  writeFileSync(path, `${entries.map((entry) => JSON.stringify(entry)).join('\n')}\n`)
  return path
}

function sessionEntries(model: string): unknown[] {
  return [
    { type: 'session', id: 'synthetic-pi-session', timestamp: '2026-10-01T00:00:00.000Z' },
    { type: 'model_change', model },
    {
      type: 'message',
      timestamp: '2026-10-01T00:00:01.000Z',
      message: { role: 'user', content: [{ type: 'text', text: 'Synthetic pi request.' }] },
    },
    {
      type: 'message',
      timestamp: '2026-10-01T00:00:02.000Z',
      message: {
        role: 'assistant',
        model,
        content: [{ type: 'text', text: 'Synthetic pi reply.' }],
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0 },
      },
    },
  ]
}

async function readTurns(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of piReader.read(path)) turns.push(turn)
  return turns
}

describe('pi declared context window', () => {
  it('carries declaredContextWindow from the models-store entry for the turn model', async () => {
    const home = useTempHome()
    installStore(home, JSON.stringify(modelsStoreFixture))
    const [turn] = await readTurns(writePiSession(sessionEntries(QUALIFIED_MODEL)))
    expect(turn?.declaredContextWindow).toBe(DECLARED_WINDOW)
  })

  it('carries no window when no store entry matches the turn model', async () => {
    const home = useTempHome()
    installStore(home, JSON.stringify(modelsStoreFixture))
    const [turn] = await readTurns(writePiSession(sessionEntries('synthetic-provider/synthetic-pi-unknown-model')))
    expect(turn?.declaredContextWindow).toBeUndefined()
  })

  it('carries no window when the store is missing, without error', async () => {
    useTempHome()
    const [turn] = await readTurns(writePiSession(sessionEntries(QUALIFIED_MODEL)))
    expect(turn?.declaredContextWindow).toBeUndefined()
  })

  it('carries no window when the store is unparseable, without error', async () => {
    const home = useTempHome()
    installStore(home, 'this is not JSON {')
    const [turn] = await readTurns(writePiSession(sessionEntries(QUALIFIED_MODEL)))
    expect(turn?.declaredContextWindow).toBeUndefined()
  })
})
