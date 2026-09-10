import { mkdtemp, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  CACHE_VERSION,
  clearLoadCacheMemo,
  computeEnvFingerprint,
  loadCache,
  markCacheDirty,
  saveCache,
  type CachedFile,
  type ProviderSection,
  type SessionCache,
} from '../src/session-cache.js'

let tmpDir: string

beforeEach(async () => {
  clearLoadCacheMemo()
  tmpDir = await mkdtemp(join(tmpdir(), 'save-cache-yields-'))
  process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache')
})

afterEach(async () => {
  clearLoadCacheMemo()
  delete process.env['CODEBURN_CACHE_DIR']
  await rm(tmpDir, { recursive: true, force: true })
})

function makeFile(timestamp: string): CachedFile {
  return {
    fingerprint: { dev: 0, ino: 0, mtimeMs: 0, sizeBytes: 0 },
    mcpInventory: [],
    turns: [{ timestamp, sessionId: 'fixture', userMessage: '', calls: [] }],
  }
}

function makeCache(providers: string[]): SessionCache {
  const sections: Record<string, ProviderSection> = {}
  for (const provider of providers) {
    sections[provider] = {
      envFingerprint: computeEnvFingerprint(provider),
      files: {
        [`/tmp/${provider}/a.jsonl`]: makeFile('2026-05-15T10:00:00Z'),
        [`/tmp/${provider}/b.jsonl`]: makeFile('2026-05-15T11:00:00Z'),
      },
    }
  }
  return {
    version: CACHE_VERSION,
    providers: sections,
    complete: false,
  }
}

describe('saveCache yields to the event loop between shard writes (#1141)', () => {
  it('surrenders the event loop between every shard write so Ink stdin stays live during a long fill', async () => {
    // Background fill: many dirty providers, each with two files in the same
    // bucket. Every shard in phase one goes through writeShard + yieldToEventLoop;
    // a probe setImmediate scheduled before saveCache must interleave, not block.
    const cache = makeCache(['claude', 'codex', 'copilot', 'kimi', 'grok'])
    for (const provider of Object.keys(cache.providers)) markCacheDirty(cache, provider)

    let probeHits = 0
    let probeAttached = true
    // Attach BEFORE saveCache: any yield inside saveCache lets this handler
    // run. Re-schedule from inside the handler so we count every event-loop
    // turn, not just one.
    const tick = () => {
      if (!probeAttached) return
      probeHits++
      setImmediate(tick)
    }
    setImmediate(tick)

    try {
      await saveCache(cache)
    } finally {
      probeAttached = false
    }

    // Each provider has ONE bucket, so we expect at least as many yields as
    // providers — the yield is between shards, not inside one, so the atomic
    // temp+rename per shard is preserved and every probe pass is a real
    // setImmediate boundary (#1141).
    expect(probeHits).toBeGreaterThanOrEqual(Object.keys(cache.providers).length)

    // The cache is still byte-correct: every provider's file round-trips.
    clearLoadCacheMemo()
    const reloaded = await loadCache()
    for (const provider of Object.keys(cache.providers)) {
      const cachedPaths = Object.keys(reloaded.providers[provider]?.files ?? {}).sort()
      expect(cachedPaths).toEqual([
        `/tmp/${provider}/a.jsonl`,
        `/tmp/${provider}/b.jsonl`,
      ])
    }
  })
})
