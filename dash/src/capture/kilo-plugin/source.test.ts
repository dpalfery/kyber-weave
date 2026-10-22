import { chmodSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { KILO_PLUGIN_MARKER, KILO_PLUGIN_SOURCE } from './source.js'

type Hooks = Record<string, (input: unknown, output: unknown) => Promise<void>>

let dir: string
let store: string
let counter = 0

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'kilo-plugin-'))
  store = join(dir, 'store')
  process.env.KYBERDASH_KILO_CAPTURE_DIR = store
})

afterEach(() => {
  delete process.env.KYBERDASH_KILO_CAPTURE_DIR
  rmSync(dir, { recursive: true, force: true })
})

/** Load the installed text exactly as Kilo would: a module with a default `{ id, server }`. */
async function loadPlugin(): Promise<{ id: string; hooks: Hooks }> {
  const file = join(dir, `plugin-${counter++}.mjs`)
  writeFileSync(file, KILO_PLUGIN_SOURCE)
  const mod = (await import(`${pathToFileURL(file).href}?n=${counter}`)) as {
    default: { id: string; server: () => Promise<Hooks> }
  }
  return { id: mod.default.id, hooks: await mod.default.server() }
}

const index = (): Record<string, unknown>[] =>
  readFileSync(join(store, 'index.jsonl'), 'utf8')
    .trim()
    .split('\n')
    .map((line) => JSON.parse(line) as Record<string, unknown>)

async function oneCall(hooks: Hooks, sessionID: string, system: string[]) {
  await hooks['experimental.chat.system.transform']!({ sessionID, model: { id: 'm1' } }, { system })
  await hooks['chat.params']!(
    { sessionID, agent: 'code', model: { id: 'm1' }, provider: { info: { id: 'p1' } }, message: { id: 'msg_1' } },
    {},
  )
}

describe('kilo capture plugin source', () => {
  it('starts with the ownership marker and exports the shape Kilo requires', async () => {
    expect(KILO_PLUGIN_SOURCE.startsWith(`${KILO_PLUGIN_MARKER}\n`)).toBe(true)
    const { id, hooks } = await loadPlugin()
    expect(id).toBe('kyberdash-capture')
    expect(Object.keys(hooks).sort()).toEqual([
      'chat.params',
      'experimental.chat.system.transform',
      'tool.definition',
    ])
  })

  it('records the system prompt, the tool definitions and the call identity', async () => {
    const { hooks } = await loadPlugin()
    await hooks['tool.definition']!({ toolID: 'bash' }, { description: 'Run a command', parameters: { type: 'object' } })
    await oneCall(hooks, 'ses_1', ['You are Kilo.', '<available_skills>x</available_skills>'])

    const [line] = index()
    expect(line).toMatchObject({ v: 1, sessionID: 'ses_1', messageID: 'msg_1', agent: 'code', model: 'm1', provider: 'p1' })
    const systemBlob = readFileSync(join(store, 'blobs', line!.systemHash as string), 'utf8')
    expect(systemBlob).toBe('You are Kilo.\n<available_skills>x</available_skills>')
    const toolHash = (line!.toolHashes as Record<string, string>).bash!
    expect(JSON.parse(readFileSync(join(store, 'blobs', toolHash), 'utf8'))).toMatchObject({
      id: 'bash',
      description: 'Run a command',
    })
  })

  it('reads the provider id from either shape Kilo passes', async () => {
    const { hooks } = await loadPlugin()
    await hooks['chat.params']!({ sessionID: 's', agent: 'a', model: { id: 'm' }, provider: { id: 'flat' }, message: { id: 'u' } }, {})
    await hooks['chat.params']!({ sessionID: 's', agent: 'a', model: { id: 'm' }, provider: { info: { id: 'nested' } }, message: { id: 'u' } }, {})
    expect(index().map((line) => line.provider)).toEqual(['flat', 'nested'])
  })

  it('stores an identical prompt once however many calls repeat it', async () => {
    const { hooks } = await loadPlugin()
    await oneCall(hooks, 'ses_1', ['same'])
    await oneCall(hooks, 'ses_1', ['same'])
    await oneCall(hooks, 'ses_1', ['same'])
    expect(index()).toHaveLength(3)
    expect(readdirSync(join(store, 'blobs'))).toHaveLength(1)
  })

  it('keeps sessions apart and tells a changed prompt apart by hash', async () => {
    const { hooks } = await loadPlugin()
    await oneCall(hooks, 'ses_a', ['one'])
    await oneCall(hooks, 'ses_b', ['two'])
    await oneCall(hooks, 'ses_a', ['one changed'])
    const lines = index()
    expect(lines[0]!.systemHash).not.toBe(lines[1]!.systemHash)
    expect(lines[0]!.systemHash).not.toBe(lines[2]!.systemHash)
  })

  it('never throws out of a hook on malformed input', async () => {
    const { hooks } = await loadPlugin()
    await expect(hooks['experimental.chat.system.transform']!(undefined, undefined)).resolves.toBeUndefined()
    await expect(hooks['tool.definition']!(null, null)).resolves.toBeUndefined()
    await expect(hooks['chat.params']!(undefined, undefined)).resolves.toBeUndefined()
    const circular: Record<string, unknown> = {}
    circular.self = circular
    await expect(
      hooks['tool.definition']!({ toolID: 'x' }, { description: 'd', parameters: circular }),
    ).resolves.toBeUndefined()
  })

  it('turns capture off after a write error instead of throwing', async () => {
    const { hooks } = await loadPlugin()
    // The store root is a file, so mkdir fails on the first write.
    writeFileSync(store, 'not a directory')
    await expect(oneCall(hooks, 'ses_1', ['x'])).resolves.toBeUndefined()
    rmSync(store)
    await oneCall(hooks, 'ses_1', ['y'])
    expect(() => readFileSync(join(store, 'index.jsonl'))).toThrow()
  })

  // POSIX mode bits: Windows has no equivalent to assert.
  it.skipIf(process.platform === 'win32')('creates the store private to the owner', async () => {
    const { hooks } = await loadPlugin()
    await oneCall(hooks, 'ses_1', ['x'])
    expect(statSync(store).mode & 0o077).toBe(0)
    expect(statSync(join(store, 'index.jsonl')).mode & 0o077).toBe(0)
    chmodSync(store, 0o700)
  })

  it.skipIf(process.platform === 'win32')('tightens the blobs directory even when every blob is already stored', async () => {
    const { hooks } = await loadPlugin()
    await oneCall(hooks, 'ses_1', ['same prompt'])
    chmodSync(store, 0o755)
    chmodSync(join(store, 'blobs'), 0o755)
    // A fresh process, whose second call finds the blob already on disk (a dedup hit).
    const second = await loadPlugin()
    await oneCall(second.hooks, 'ses_1', ['same prompt'])
    expect(statSync(store).mode & 0o077).toBe(0)
    expect(statSync(join(store, 'blobs')).mode & 0o077).toBe(0)
  })

  it.skipIf(process.platform === 'win32')('tightens a store that already exists with wider permissions', async () => {
    mkdirSync(join(store, 'blobs'), { recursive: true, mode: 0o755 })
    chmodSync(store, 0o755)
    chmodSync(join(store, 'blobs'), 0o755)
    writeFileSync(join(store, 'index.jsonl'), '', { mode: 0o644 })
    chmodSync(join(store, 'index.jsonl'), 0o644)
    const { hooks } = await loadPlugin()
    await oneCall(hooks, 'ses_1', ['x'])
    expect(statSync(store).mode & 0o077).toBe(0)
    expect(statSync(join(store, 'blobs')).mode & 0o077).toBe(0)
    expect(statSync(join(store, 'index.jsonl')).mode & 0o077).toBe(0)
    const blob = readdirSync(join(store, 'blobs'))[0]!
    expect(statSync(join(store, 'blobs', blob)).mode & 0o077).toBe(0)
  })
})
