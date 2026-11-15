import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { clearCodexMemCaches } from '../ingest/codex-cache.js'
import { iterateNativeUnits } from '../refresh/source-reader.js'
import { createCodexProvider } from './codex.js'

/**
 * T1 diagnostic: Codex originator classification logic
 *
 * Tests originator partitioning: `classifyCodex` (refresh/registry.ts, lines
 * 646-655, module-private) as reached through the exported
 * `classifySessionSource` (refresh/registry.ts:562; the `case 'codex'`
 * dispatch is at lines 581-582). `classifyCodex` is not exported, so the
 * tests call `classifySessionSource` with a `provider: 'codex'` source.
 *
 * The last describe block drives real rollout files through discovery
 * (createCodexProvider -> iterateNativeUnits) and the real parser.
 *
 * The issue #196 reports 647 units discovered but only 31 records. The
 * originator field is the key to distinguishing CLI from Desktop for
 * display-level grouping and harness attribution.
 */

// Import the actual classification function
import { classifySessionSource } from '../refresh/registry.js'
import type { SessionSource } from './types.js'
import type { SessionClassification } from '../refresh/types.js'

let tmpDir: string
let previousCacheDir: string | undefined

beforeEach(async () => {
  tmpDir = await mkdtemp(join(tmpdir(), 'codex-cli-class-test-'))
  // The Codex parser reads/writes a result cache under KYBERDASH_CACHE_DIR; keep it in tmp.
  previousCacheDir = process.env['KYBERDASH_CACHE_DIR']
  process.env['KYBERDASH_CACHE_DIR'] = join(tmpDir, 'cache')
  clearCodexMemCaches()
})

afterEach(async () => {
  if (previousCacheDir === undefined) delete process.env['KYBERDASH_CACHE_DIR']
  else process.env['KYBERDASH_CACHE_DIR'] = previousCacheDir
  await rm(tmpDir, { recursive: true, force: true })
})

function harnessIdOf(classification: SessionClassification): string | undefined {
  return classification.outcome === 'harness' ? classification.harnessId : undefined
}

async function writeSession(filename: string): Promise<SessionSource> {
  const [year, month, day] = '2026-04-14'.split('-')
  const sessionDir = join(tmpDir, 'sessions', year!, month!, day!)
  await mkdir(sessionDir, { recursive: true })
  const filePath = join(sessionDir, filename)
  await writeFile(filePath, '{}')
  return { path: filePath, project: 'Users-test-myproject', provider: 'codex' }
}

describe('codex originator classification (T1 diagnostic)', () => {
  it('classifies codex-cli originator into codex-cli harness', async () => {
    const source = await writeSession('rollout-cli-001.jsonl')

    const classification = classifySessionSource({
      source,
      originator: 'codex-cli',
    })

    expect(classification.outcome).toBe('harness')
    expect(harnessIdOf(classification)).toBe('codex-cli')
  })

  it('classifies Codex Desktop originator into codex-desktop harness', async () => {
    const source = await writeSession('rollout-desktop-001.jsonl')

    const classification = classifySessionSource({
      source,
      originator: 'Codex Desktop',
    })

    expect(classification.outcome).toBe('harness')
    expect(harnessIdOf(classification)).toBe('codex-desktop')
  })

  it('classifies unknown originator into codex-unclassified harness', async () => {
    const source = await writeSession('rollout-unknown.jsonl')

    const classification = classifySessionSource({
      source,
      originator: 'some-other-client',
    })

    expect(classification.outcome).toBe('harness')
    expect(harnessIdOf(classification)).toBe('codex-unclassified')
  })

  it('handles case-insensitive originator matching', () => {
    // codex_cli_rs variant and mixed case
    const testCases = [
      { originator: 'codex_cli_rs', expected: 'codex-cli' },
      { originator: 'CODEX-CLI', expected: 'codex-cli' },
      { originator: 'CoDeX dEsKtOp', expected: 'codex-desktop' },
    ]

    for (const { originator, expected } of testCases) {
      // Create a dummy source (not used for classification)
      const source: SessionSource = { path: '/tmp/test', project: 'test', provider: 'codex' }
      const classification = classifySessionSource({ source, originator })
      expect(harnessIdOf(classification), `originator: ${originator}`).toBe(expected)
    }
  })
})

async function writeRollout(
  name: string,
  originator: string,
  sessionId: string,
): Promise<string> {
  const dir = join(tmpDir, 'codex-home', 'sessions', '2026', '09', '30')
  await mkdir(dir, { recursive: true })
  const usage = { input_tokens: 1000, cached_input_tokens: 0, output_tokens: 200, reasoning_output_tokens: 0, total_tokens: 1200 }
  const lines = [
    { type: 'session_meta', timestamp: '2026-09-30T10:00:00Z', payload: { session_id: sessionId, cwd: '/Users/test/proj', originator } },
    { type: 'turn_context', timestamp: '2026-09-30T10:00:05Z', payload: { model: 'gpt-5.3-codex' } },
    // Identical timestamp and usage in both sessions: the worst case for a key collision.
    { type: 'event_msg', timestamp: '2026-09-30T10:01:00Z', payload: { type: 'token_count', info: { last_token_usage: usage, total_token_usage: usage } } },
  ]
  const path = join(dir, name)
  await writeFile(path, lines.map(line => JSON.stringify(line)).join('\n') + '\n')
  return path
}

describe('codex-cli real discovery and parse (T1 diagnostic)', () => {
  const window = { start: new Date('2026-09-17T12:00:00Z'), end: new Date('2026-10-01T12:00:00Z') }

  function provider() {
    const home = join(tmpDir, 'codex-home')
    return createCodexProvider(home, { primaryDir: home, launcherRoots: [] })
  }

  it('discovers a codex-cli rollout under provider codex and parses it into ParsedProviderCall (no structural rejection)', async () => {
    const cliPath = await writeRollout('rollout-cli.jsonl', 'codex-cli', 'sess-cli')

    const units = await iterateNativeUnits('codex-cli', {
      providers: [provider()],
      dateRange: window,
      parseAllSessions: async () => undefined,
    })

    expect(units).toHaveLength(1)
    const unit = units[0]!
    expect(unit.source).toMatchObject({ path: cliPath, provider: 'codex' })
    expect(unit.harnessId).toBe('codex-cli')
    expect(unit.envelopes).toHaveLength(1)
    const call = unit.envelopes[0]!.call
    expect(call).toMatchObject({
      provider: 'codex',
      model: 'gpt-5.3-codex',
      inputTokens: 1000,
      outputTokens: 200,
      sessionId: 'sess-cli',
      timestamp: '2026-09-30T10:01:00Z',
    })
  })

  it('splits a mixed tree by originator: codex-cli harness sees only the CLI rollout, codex-desktop only the Desktop one', async () => {
    const cliPath = await writeRollout('rollout-cli.jsonl', 'codex-cli', 'sess-cli')
    const desktopPath = await writeRollout('rollout-desktop.jsonl', 'Codex Desktop', 'sess-desktop')
    const deps = { providers: [provider()], dateRange: window, parseAllSessions: async () => undefined }

    const cli = await iterateNativeUnits('codex-cli', deps)
    const desktop = await iterateNativeUnits('codex-desktop', deps)

    expect(cli.map(unit => unit.source.path)).toEqual([cliPath])
    expect(desktop.map(unit => unit.source.path)).toEqual([desktopPath])
  })

  it('CLI and Desktop sessions never collide on deduplicationKey, even with identical timestamps and usage', async () => {
    await writeRollout('rollout-cli.jsonl', 'codex-cli', 'sess-cli')
    await writeRollout('rollout-desktop.jsonl', 'Codex Desktop', 'sess-desktop')
    const deps = { providers: [provider()], dateRange: window, parseAllSessions: async () => undefined }

    const calls = [
      ...(await iterateNativeUnits('codex-cli', deps)),
      ...(await iterateNativeUnits('codex-desktop', deps)),
    ].flatMap(unit => unit.envelopes.map(envelope => envelope.call))

    expect(calls).toHaveLength(2)
    const keys = calls.map(call => call.deduplicationKey)
    expect(new Set(keys).size).toBe(2)
  })
})
