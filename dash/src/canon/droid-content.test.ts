// The Factory content gate's own leaf: the consent decision, Factory's identity, and
// the bounded memory of Factory traces.
//
// The gate is what keeps Factory's content out of the store while KyberDash's consent
// is off. It reads the owner's decision (`~/.kyberdash/factory-plugin.json`, with the
// `KYBERDASH_FACTORY_CONTEXT` override), it knows what a Factory span looks like, and it
// remembers the Factory traces it has seen so a child that arrives in a later batch is
// still identified.
//
// The module under test does not exist yet: this file is the RED proof. It is loaded
// through a dynamic import with a computed specifier so Vitest still COLLECTS the file
// and every test fails with the one reason that matters — the module under test is
// missing — instead of the whole file failing to collect.
//
// Every fixture lives in a temporary directory that the suite removes afterwards, so no
// test ever reads or writes the real `~/.kyberdash`.

import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { readConsentState, writeContentConsent } from '../capture/consent-file.js'
import {
  FACTORY_CONSENT_FILE_NAME as PLUGIN_CONSENT_FILE_NAME,
  KYBERDASH_FACTORY_CONTEXT_ENV as PLUGIN_CONTEXT_ENV,
  readContentConsent as readPluginContentConsent,
  resolveContentConsent as resolvePluginContentConsent,
} from '../capture/harnesses/factory-plugin/consent.js'

/** The module under test. It does not exist yet; every test fails on this import. */
type DroidContentModule = typeof import('./droid-content.js')

const DROID_CONTENT_MODULE_SPECIFIER = './droid-content.js'

async function loadDroidContent(): Promise<DroidContentModule> {
  return (await import(/* @vite-ignore */ DROID_CONTENT_MODULE_SPECIFIER)) as DroidContentModule
}

/** The one file body that turns content on. */
const ON_BODY = '{"version":1,"content":true}'

/** The documented size bound: the same bound the capture-side consent reader uses. */
const MAX_CONSENT_BYTES = 4096

const dirs: string[] = []
const temp = (): string => {
  const dir = mkdtempSync(join(tmpdir(), 'kd-droid-content-'))
  dirs.push(dir)
  return dir
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

/** A consent file under a throwaway home. */
async function consentFixture(body: string): Promise<string> {
  const content = await loadDroidContent()
  const home = temp()
  mkdirSync(join(home, '.kyberdash'), { recursive: true })
  const path = content.resolveConsentPath(home)
  writeFileSync(path, body)
  return path
}

/** A consent path under a throwaway home where no file was ever written. */
async function missingConsentPath(): Promise<string> {
  const content = await loadDroidContent()
  return content.resolveConsentPath(temp())
}

describe('the Factory consent decision', () => {
  it('is named factory-plugin.json inside KyberDash\'s own state directory', async () => {
    const content = await loadDroidContent()
    expect(content.FACTORY_CONSENT_FILE_NAME).toBe('factory-plugin.json')
    expect(content.resolveConsentPath('/h/u')).toBe(
      join('/h/u', '.kyberdash', 'factory-plugin.json'),
    )
  })

  it('is on for exactly { "version": 1, "content": true }', async () => {
    const content = await loadDroidContent()
    expect(content.readContentConsent(await consentFixture(ON_BODY))).toBe(true)
  })

  it('is off for version 1 with content false', async () => {
    const content = await loadDroidContent()
    expect(content.readContentConsent(await consentFixture('{"version":1,"content":false}'))).toBe(
      false,
    )
  })

  it('is off for a malformed file, an unknown version and a wrong shape', async () => {
    const content = await loadDroidContent()
    for (const body of [
      'not json at all',
      '{"version":2,"content":true}',
      '{"content":true}',
      '{"version":1,"content":"true"}',
      '[]',
    ]) {
      expect(
        content.readContentConsent(await consentFixture(body)),
        `${body} must read as off`,
      ).toBe(false)
    }
  })

  it('is off for a missing file', async () => {
    const content = await loadDroidContent()
    expect(content.readContentConsent(await missingConsentPath())).toBe(false)
  })

  it('is off for a file larger than the size bound', async () => {
    const content = await loadDroidContent()
    const oversized = JSON.stringify({ version: 1, content: true, padding: 'x'.repeat(MAX_CONSENT_BYTES) })
    expect(content.readContentConsent(await consentFixture(oversized))).toBe(false)
  })

  it('never throws on an unreadable file', async () => {
    const content = await loadDroidContent()
    expect(
      content.readContentConsent('/does/not/exist/factory-plugin.json'),
      'a missing file is a decision, not a failure',
    ).toBe(false)
  })

  it('lets the env override win over the file', async () => {
    const content = await loadDroidContent()
    const path = await consentFixture(ON_BODY)
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '0' }, path)).toBe(false)
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '1' }, path)).toBe(true)

    const off = await consentFixture('{"version":1,"content":false}')
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '1' }, off)).toBe(true)
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '0' }, off)).toBe(false)
  })

  it('defers to the file for any other env value', async () => {
    const content = await loadDroidContent()
    const path = await consentFixture(ON_BODY)
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: 'yes' }, path)).toBe(true)
    expect(content.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '' }, path)).toBe(true)
  })
})

describe('the Factory identity the gate reads', () => {
  it('recognises Factory\'s own legacy span names', async () => {
    const content = await loadDroidContent()
    for (const name of [
      'droid.message.user',
      'droid.message.assistant',
      'droid.tool.call',
      'droid.tool.result',
    ]) {
      expect(content.isFactoryLegacySpanName(name), `${name} is Factory's own`).toBe(true)
    }
    for (const name of ['factory.turn', 'gen_ai.chat', 'invoke_agent', 'copilot_chat/gen_ai']) {
      expect(content.isFactoryLegacySpanName(name), `${name} is not Factory's own`).toBe(false)
    }
  })

  it('recognises the native root by the agent it declares', async () => {
    const content = await loadDroidContent()
    expect(content.isFactoryNativeRoot({ 'gen_ai.agent.name': 'droid' })).toBe(true)
    expect(content.isFactoryNativeRoot({ 'gen_ai.agent.name': 'copilot' })).toBe(false)
    expect(content.isFactoryNativeRoot({})).toBe(false)
  })

  it('names the content-bearing attributes it strips', async () => {
    const content = await loadDroidContent()
    expect([...content.FACTORY_CONTENT_ATTRIBUTES]).toEqual([
      'gen_ai.input.messages',
      'gen_ai.output.messages',
      'gen_ai.tool.call.arguments',
      'gen_ai.tool.call.result',
      'message.content',
    ])
  })

  it('strips the content attributes and keeps everything else', async () => {
    const content = await loadDroidContent()
    const attributes = {
      'gen_ai.input.messages': '[{"role":"user"}]',
      'gen_ai.output.messages': '[{"role":"assistant"}]',
      'gen_ai.tool.call.arguments': '{"path":"/tmp/x"}',
      'gen_ai.tool.call.result': 'the result',
      'message.content': 'the message',
      'gen_ai.conversation.id': 'session-1',
      'session.id': 'session-1',
      'gen_ai.request.model': 'claude-sonnet-4-5',
      'gen_ai.usage.input_tokens': 1200,
    }
    const stripped = content.stripFactoryContent(attributes)
    for (const key of content.FACTORY_CONTENT_ATTRIBUTES) {
      expect(stripped, `${key} must be stripped`).not.toHaveProperty(key)
    }
    expect(stripped['gen_ai.conversation.id']).toBe('session-1')
    expect(stripped['session.id']).toBe('session-1')
    expect(stripped['gen_ai.request.model']).toBe('claude-sonnet-4-5')
    expect(stripped['gen_ai.usage.input_tokens']).toBe(1200)
  })

  it('reports whether a span carries content at all', async () => {
    const content = await loadDroidContent()
    expect(content.carriesFactoryContent({ 'gen_ai.input.messages': '[]' })).toBe(true)
    expect(content.carriesFactoryContent({ 'message.content': 'text' })).toBe(true)
    expect(content.carriesFactoryContent({ 'gen_ai.usage.input_tokens': 1 })).toBe(false)
    expect(content.carriesFactoryContent({})).toBe(false)
  })
})

describe('the bounded memory of Factory traces', () => {
  it('remembers a trace and answers for it', async () => {
    const content = await loadDroidContent()
    const traces = new content.FactoryTraceMemory(4)
    expect(traces.has('trace-1')).toBe(false)
    traces.remember('trace-1')
    expect(traces.has('trace-1')).toBe(true)
    expect(traces.size).toBe(1)
  })

  it('forgets the oldest trace at the bound', async () => {
    const content = await loadDroidContent()
    const traces = new content.FactoryTraceMemory(2)
    traces.remember('trace-1')
    traces.remember('trace-2')
    traces.remember('trace-3')
    expect(traces.size, 'the memory is bounded').toBe(2)
    expect(traces.has('trace-1'), 'the oldest trace is the one forgotten').toBe(false)
    expect(traces.has('trace-3')).toBe(true)
  })

  it('keeps the trace that was used most recently', async () => {
    const content = await loadDroidContent()
    const traces = new content.FactoryTraceMemory(2)
    traces.remember('trace-1')
    traces.remember('trace-2')
    // The older trace is used again, so it is the newer of the two.
    expect(traces.has('trace-1')).toBe(true)
    traces.remember('trace-3')
    expect(traces.has('trace-2'), 'the least recently used trace is forgotten').toBe(false)
    expect(traces.has('trace-1')).toBe(true)
  })

  it('forgets everything when it is cleared', async () => {
    const content = await loadDroidContent()
    const traces = new content.FactoryTraceMemory(2)
    traces.remember('trace-1')
    traces.clear()
    expect(traces.has('trace-1')).toBe(false)
    expect(traces.size).toBe(0)
  })
})

// The consent reader exists in TWO production copies: the canon copy in this directory, and
// the plugin-side copy the plugin bundle inlines
// (capture/harnesses/factory-plugin/consent.ts). They stay apart on purpose — the plugin's
// copy MUST stay a leaf, so it cannot import the canon copy — which means nothing but a
// test ties the two together. Without this guard a one-sided shape change (a version bump,
// a renamed field) applied to the shared writer and one reader would leave the other reading
// the old shape, and the suite would not notice: the plugin would keep publishing the
// prompt text while the engine's ingest gate read the consent as off, or the reverse.
//
// This is a DRIFT GUARD, not a RED test: the two copies agree today, and that is exactly
// what the guard records.
describe('the two copies of the consent reader', () => {
  /** One corpus of consent bodies: every shape the file may be found in. */
  const CORPUS: { name: string; body: string | null; expected: boolean }[] = [
    { name: 'the on shape', body: '{"version":1,"content":true}', expected: true },
    { name: 'the off shape', body: '{"version":1,"content":false}', expected: false },
    { name: 'an unknown version', body: '{"version":2,"content":true}', expected: false },
    { name: 'a content field of the wrong type', body: '{"version":1,"content":"true"}', expected: false },
    { name: 'a missing content field', body: '{"version":1}', expected: false },
    { name: 'malformed JSON', body: '{', expected: false },
    { name: 'a JSON string', body: '"hello"', expected: false },
    { name: 'a JSON array', body: '[]', expected: false },
    { name: 'a JSON null', body: 'null', expected: false },
    { name: 'an empty file', body: '', expected: false },
    {
      name: 'a file larger than the size bound',
      body: JSON.stringify({ version: 1, content: true, padding: 'x'.repeat(MAX_CONSENT_BYTES) }),
      expected: false,
    },
    { name: 'a file that is not there', body: null, expected: false },
  ]

  /** A consent path under a throwaway home with nothing written at it. */
  async function consentPath(): Promise<string> {
    const content = await loadDroidContent()
    return content.resolveConsentPath(temp())
  }

  it('names the same file and the same override variable', async () => {
    const content = await loadDroidContent()
    expect(PLUGIN_CONSENT_FILE_NAME).toBe(content.FACTORY_CONSENT_FILE_NAME)
    expect(PLUGIN_CONTEXT_ENV).toBe(content.KYBERDASH_FACTORY_CONTEXT_ENV)
  })

  for (const entry of CORPUS) {
    it(`reads ${entry.name} the same way in both copies`, async () => {
      const content = await loadDroidContent()
      const path = entry.body === null ? await consentPath() : await consentFixture(entry.body)

      const canon = content.readContentConsent(path)
      const plugin = readPluginContentConsent(path)

      expect(plugin, `${entry.name}: the two copies must reach the same decision`).toBe(canon)
      expect(canon, `${entry.name}: the consent decision`).toBe(entry.expected)
    })
  }

  it('reads the shared writer\'s own output the way the writer meant it', async () => {
    // The writer is the one place the shape is decided, so its own output is the corpus
    // that catches a shape change applied to the writer and to only one of the readers.
    const content = await loadDroidContent()

    const onPath = await consentPath()
    writeContentConsent(onPath, true)
    expect(readConsentState(onPath), 'the writer wrote the on shape').toBe('on')
    expect(content.readContentConsent(onPath), 'the canon copy reads the writer\'s on shape').toBe(true)
    expect(readPluginContentConsent(onPath), 'the plugin copy reads the writer\'s on shape').toBe(true)

    const offPath = await consentPath()
    writeContentConsent(offPath, false)
    expect(readConsentState(offPath), 'the writer wrote the off shape').toBe('off')
    expect(content.readContentConsent(offPath), 'the canon copy reads the writer\'s off shape').toBe(false)
    expect(readPluginContentConsent(offPath), 'the plugin copy reads the writer\'s off shape').toBe(false)
  })

  it('resolves the env override the same way in both copies', async () => {
    const content = await loadDroidContent()
    const envName = content.KYBERDASH_FACTORY_CONTEXT_ENV
    const onPath = await consentFixture(ON_BODY)
    const offPath = await consentFixture('{"version":1,"content":false}')
    const missingPath = await consentPath()

    const cases: { name: string; env: NodeJS.ProcessEnv; path: string; expected: boolean }[] = [
      { name: "'1' forces content on over an off file", env: { [envName]: '1' }, path: offPath, expected: true },
      { name: "'1' forces content on over a missing file", env: { [envName]: '1' }, path: missingPath, expected: true },
      { name: "'0' forces content off over an on file", env: { [envName]: '0' }, path: onPath, expected: false },
      { name: "'0' forces content off over a missing file", env: { [envName]: '0' }, path: missingPath, expected: false },
      { name: 'any other value defers to an on file', env: { [envName]: 'yes' }, path: onPath, expected: true },
      { name: 'an empty value defers to an on file', env: { [envName]: '' }, path: onPath, expected: true },
      { name: 'an absent variable defers to an on file', env: {}, path: onPath, expected: true },
      { name: 'an absent variable defers to an off file', env: {}, path: offPath, expected: false },
    ]

    for (const entry of cases) {
      const canon = content.resolveContentConsent(entry.env, entry.path)
      const plugin = resolvePluginContentConsent(entry.env, entry.path)
      expect(plugin, `${entry.name}: the two copies must reach the same decision`).toBe(canon)
      expect(canon, `${entry.name}: the consent decision`).toBe(entry.expected)
    }
  })
})
