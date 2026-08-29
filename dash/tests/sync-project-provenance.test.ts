import { mkdtemp, mkdir, rm, stat, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { parseProviderSources } from '../src/parser.js'
import { CACHE_VERSION, computeEnvFingerprint, type CachedFile, type SessionCache } from '../src/session-cache.js'
import { collectUnsentCalls } from '../src/sync/push.js'
import { buildOtlpPayload } from '../src/sync/otlp.js'
import type { SessionSource } from '../src/providers/types.js'

let root: string
const originalCacheDir = process.env.CODEBURN_CACHE_DIR

beforeEach(async () => {
  root = await mkdtemp(join(tmpdir(), 'codeburn-sync-provenance-'))
  process.env.CODEBURN_CACHE_DIR = join(root, 'cache')
})

afterEach(async () => {
  if (originalCacheDir === undefined) delete process.env.CODEBURN_CACHE_DIR
  else process.env.CODEBURN_CACHE_DIR = originalCacheDir
  await rm(root, { recursive: true, force: true })
})

async function parseOne(provider: string, source: SessionSource) {
  const cache: SessionCache = { version: CACHE_VERSION, providers: {} }
  return parseProviderSources(provider, [source], new Set(), cache, undefined, undefined, false)
}

function wire(projects: Awaited<ReturnType<typeof parseOne>>): string {
  return JSON.stringify(buildOtlpPayload(collectUnsentCalls(projects).allCalls))
}

async function cachedFileFor(path: string, workingDirectory: string, trusted: boolean): Promise<CachedFile> {
  const info = await stat(path)
  return {
    fingerprint: {
      dev: info.dev,
      ino: info.ino,
      mtimeMs: info.mtimeMs,
      sizeBytes: info.size,
    },
    mcpInventory: [],
    turns: [{
      timestamp: '2026-08-23T10:00:00Z',
      sessionId: trusted ? 'trusted-cache' : 'legacy-cache',
      userMessage: '',
      calls: [{
        provider: 'lingtai-tui',
        model: 'gpt-5.5',
        usage: {
          inputTokens: 10,
          outputTokens: 5,
          cacheCreationInputTokens: 0,
          cacheReadInputTokens: 0,
          cachedInputTokens: 0,
          reasoningTokens: 0,
          webSearchRequests: 0,
          cacheCreationOneHourTokens: 0,
        },
        speed: 'standard',
        timestamp: '2026-08-23T10:00:00Z',
        tools: [],
        bashCommands: [],
        skills: [],
        subagentTypes: [],
        deduplicationKey: trusted ? 'trusted-cache-call' : 'legacy-cache-call',
        project: 'local-only-project',
        projectPath: workingDirectory,
        workingDirectory,
        ...(trusted ? { workingDirectoryProvenance: 'provider-field' as const } : {}),
      }],
    }],
  }
}

describe('sync cwd provenance', () => {
  it('does not turn a LingTai agent storage directory into ai.project', async () => {
    const agentDir = join(root, 'Users', 'alice', 'secret-client-agent')
    const ledger = join(agentDir, 'logs', 'token_ledger.jsonl')
    await mkdir(dirname(ledger), { recursive: true })
    await writeFile(ledger, `${JSON.stringify({
      source: 'main', ts: '2026-08-23T10:00:00Z', input: 10, output: 5, model: 'gpt-5.5',
    })}\n`)
    const projects = await parseOne('lingtai-tui', { path: ledger, project: 'Private Agent', provider: 'lingtai-tui' })

    expect(projects[0]!.sessions[0]!.workingDirectory).toBeUndefined()
    expect(wire(projects)).not.toContain('ai.project')
    expect(wire(projects)).not.toContain('secret-client-agent')
  })

  it('does not turn a QuickDesk profile data path into ai.project', async () => {
    const profile = join(root, 'Users', 'alice', 'secret-quickdesk-profile')
    const metrics = join(profile, 'metrics', 'metrics-2026-08-23.jsonl')
    await mkdir(dirname(metrics), { recursive: true })
    await writeFile(metrics, `${JSON.stringify({ Model: 'gpt-5.5', InputTokens: 10, OutputTokens: 5, CostUSD: 0.01 })}\n`)
    const projects = await parseOne('quickdesk', {
      path: metrics,
      project: 'private-profile',
      provider: 'quickdesk',
      sourceId: 'metrics',
      sourcePath: profile,
    })

    expect(projects[0]!.sessions[0]!.workingDirectory).toBeUndefined()
    expect(wire(projects)).not.toContain('ai.project')
    expect(wire(projects)).not.toContain('secret-quickdesk-profile')
  })

  it('does not trust a pre-fix warm-cache workingDirectory without provenance', async () => {
    const sourcePath = join(root, 'legacy-source.jsonl')
    await writeFile(sourcePath, '{}\n')
    const secretPath = '/tmp/secret-client'
    const cache: SessionCache = {
      version: CACHE_VERSION,
      providers: {
        'lingtai-tui': {
          envFingerprint: computeEnvFingerprint('lingtai-tui'),
          files: { [sourcePath]: await cachedFileFor(sourcePath, secretPath, false) },
        },
      },
    }

    const projects = await parseProviderSources(
      'lingtai-tui',
      [{ path: sourcePath, project: 'local-only-project', provider: 'lingtai-tui' }],
      new Set(),
      cache,
      undefined,
      undefined,
      false,
    )

    expect(projects[0]!.sessions[0]!.workingDirectory).toBeUndefined()
    expect(wire(projects)).not.toContain('ai.project')
    expect(wire(projects)).not.toContain('secret-client')
  })

  it('restores a warm-cache cwd only when marked as provider-field provenance', async () => {
    const sourcePath = join(root, 'trusted-source.jsonl')
    await writeFile(sourcePath, '{}\n')
    const trustedPath = '/workspace/trusted-widget'
    const cache: SessionCache = {
      version: CACHE_VERSION,
      providers: {
        'lingtai-tui': {
          envFingerprint: computeEnvFingerprint('lingtai-tui'),
          files: { [sourcePath]: await cachedFileFor(sourcePath, trustedPath, true) },
        },
      },
    }

    const projects = await parseProviderSources(
      'lingtai-tui',
      [{ path: sourcePath, project: 'local-only-project', provider: 'lingtai-tui' }],
      new Set(),
      cache,
      undefined,
      undefined,
      false,
    )

    expect(projects[0]!.sessions[0]!.workingDirectory).toBe(trustedPath)
    expect(wire(projects)).toContain('trusted-widget')
  })

  it('rejects a provenance marker on a relative workingDirectory', async () => {
    const sourcePath = join(root, 'relative-source.jsonl')
    await writeFile(sourcePath, '{}\n')
    const cache: SessionCache = {
      version: CACHE_VERSION,
      providers: {
        'lingtai-tui': {
          envFingerprint: computeEnvFingerprint('lingtai-tui'),
          files: { [sourcePath]: await cachedFileFor(sourcePath, '.', true) },
        },
      },
    }

    const projects = await parseProviderSources(
      'lingtai-tui',
      [{ path: sourcePath, project: 'local-only-project', provider: 'lingtai-tui' }],
      new Set(), cache, undefined, undefined, false,
    )

    expect(projects[0]!.sessions[0]!.workingDirectory).toBeUndefined()
    expect(wire(projects)).not.toContain('ai.project')
  })
})
