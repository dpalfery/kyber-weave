// Synthetic fixtures only: tool names, descriptions and schemas below are
// invented. They exercise the on-disk shape (`<root>/mcp/<server>/<tool>.json`
// beside `<root>/conversations/<cascade>.db`), not any real user's content.
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { contentFromParts } from '../../canon/types.js'
import { measurabilityFor } from '../../canon/measurability.js'
import { PROVIDER_READERS, ingestProviders } from '../provider.js'
import {
  antigravityReader,
  antigravityRootFromConversationPath,
  readAntigravityMcpCatalogue,
} from './antigravity.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import type { ReaderTurn } from './types.js'

const roots: string[] = []

afterAll(() => {
  for (const root of roots) rmSync(root, { recursive: true, force: true })
})

function makeRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-agy-reader-'))
  roots.push(root)
  mkdirSync(join(root, 'conversations'), { recursive: true })
  writeFileSync(join(root, 'conversations', 'cascade-1.db'), 'not-inspected')
  return root
}

function writeTool(root: string, server: string, file: string, body: unknown): void {
  mkdirSync(join(root, 'mcp', server), { recursive: true })
  writeFileSync(join(root, 'mcp', server, file), typeof body === 'string' ? body : JSON.stringify(body))
}

async function collect(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of antigravityReader.read(path)) turns.push(turn)
  return turns
}

describe('antigravityReader', () => {
  it('emits one tool_definitions part per MCP server with the schema files verbatim', async () => {
    const root = makeRoot()
    const alpha = { name: 'alpha_lookup', description: 'Looks up alpha.', parameters: { properties: { q: { type: 'string' } } } }
    writeTool(root, 'srv-b', 'beta_run.json', { name: 'beta_run', description: 'Runs beta.' })
    writeTool(root, 'srv-a', 'alpha_lookup.json', alpha)
    writeTool(root, 'srv-a', 'instructions.md', '# not json')

    const turns = await collect(join(root, 'conversations', 'cascade-1.db'))

    expect(turns).toHaveLength(1)
    const parts = turns[0]!.parts
    expect(parts.map((p) => [p.part, p.server])).toEqual([
      ['tool_definitions', 'srv-a'],
      ['tool_definitions', 'srv-b'],
    ])
    expect(JSON.parse(parts[0]!.text)).toEqual([alpha])
    expect(parts[0]!.tokens).toBeUndefined()
    expect(Object.keys(contentFromParts(parts))).toEqual(['tool_definitions'])
  })

  it('skips malformed, nameless and oversized schema files without inventing tools', async () => {
    const root = makeRoot()
    writeTool(root, 'srv', 'broken.json', '{ not json')
    writeTool(root, 'srv', 'nameless.json', { description: 'no name' })
    writeTool(root, 'srv', 'blank.json', { name: '  ' })
    writeTool(root, 'srv', 'big.json', { name: 'big', description: 'x'.repeat(1_100_000) })
    writeTool(root, 'srv', 'ok.json', { name: 'ok_tool' })

    const parts = await readAntigravityMcpCatalogue(root)

    expect(parts).toHaveLength(1)
    expect((JSON.parse(parts[0]!.text) as Array<{ name: string }>).map((t) => t.name)).toEqual(['ok_tool'])
  })

  it('yields nothing when there is no MCP directory, no tools, or the path is not a conversation store', async () => {
    const empty = makeRoot()
    expect(await collect(join(empty, 'conversations', 'cascade-1.db'))).toEqual([])

    mkdirSync(join(empty, 'mcp', 'srv-empty'), { recursive: true })
    expect(await collect(join(empty, 'conversations', 'cascade-1.db'))).toEqual([])

    const root = makeRoot()
    writeTool(root, 'srv', 'a.json', { name: 'a' })
    expect(await collect(join(root, 'conversations', 'missing.db'))).toEqual([])
    expect(await collect(join(root, 'antigravity-statusline.jsonl'))).toEqual([])
  })

  it('resolves the app-data root only for conversation databases', () => {
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity-cli/conversations/x.db')).toBe(
      '/h/.gemini/antigravity-cli',
    )
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity/conversations/x.pb')).toBeUndefined()
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity/other/x.db')).toBeUndefined()
  })

  it('is registered for every Antigravity harness id and keeps built-in content unmeasurable', () => {
    for (const id of ['antigravity', 'antigravity-cli', 'antigravity-ide']) {
      expect(PROVIDER_READERS.get(id)).toBe(antigravityReader)
      const m = measurabilityFor(id)
      expect(m['tool_definitions']).toBeUndefined()
      expect(m['system_prompt']).toMatchObject({ availability: 'not_measurable' })
    }
  })

  it('lands the catalogue on the first synthesized turn, naming each tool and its server', async () => {
    const root = makeRoot()
    writeTool(root, 'srv-a', 'alpha_lookup.json', { name: 'alpha_lookup', description: 'Looks up alpha.' })
    const mk = (n: number): ParsedProviderCall => ({
      provider: 'antigravity',
      model: 'synthetic-model',
      inputTokens: 100,
      outputTokens: 10,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens: 0,
      webSearchRequests: 0,
      costUSD: 0,
      tools: [],
      bashCommands: [],
      timestamp: `2026-08-29T12:00:0${n}.000Z`,
      speed: 'standard',
      deduplicationKey: `antigravity:cascade-1:${n}`,
      userMessage: '',
      sessionId: 'cascade-1',
    })

    const { records, problems } = await ingestProviders(['antigravity-cli'], () => ({
      calls: [mk(0), mk(1)],
      filePath: join(root, 'conversations', 'cascade-1.db'),
      harnessId: 'antigravity-cli',
      sourceKey: 'antigravity-cli:cascade-1',
    }))

    expect(problems).toEqual([])
    const withDefs = records.filter((r) => r.parts?.some((p) => p.part === 'tool_definitions'))
    expect(withDefs).toHaveLength(1)
    expect(withDefs[0]!.parts![0]).toMatchObject({ part: 'tool_definitions', server: 'srv-a' })
    expect(records.filter((r) => r.parts === undefined || r.parts.length === 0).length).toBe(records.length - 1)
  })
})
