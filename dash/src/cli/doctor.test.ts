import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm, chmod } from 'fs/promises'
import { chmodSync, copyFileSync, existsSync, mkdirSync, rmSync, statSync, writeFileSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'
import { createRequire } from 'node:module'

import { collectDoctorReport, renderDoctorTable, renderDoctorJson } from './doctor.js'
import { createCodexProvider } from '../providers/codex.js'
import { createOpenCodeProvider } from '../providers/opencode.js'
import { createDevinProvider } from '../providers/devin.js'
import { createWarpProvider, drainWarpDbAccessDenials } from '../providers/warp.js'
import { isSqliteAvailable, openDatabase } from '../ingest/sqlite.js'
import { emptyCache, type SessionCache } from '../ingest/session-cache.js'
import type { Provider, ProbeRoot, SessionSource } from '../providers/types.js'

const requireForTest = createRequire(import.meta.url)

type NativeDatabase = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}

// ── Helpers ──────────────────────────────────────────────────────────────

// A valid single-line Codex rollout so real discovery + probeRoots + parse run
// end to end against a fixture directory.
function sessionMeta(): string {
  return JSON.stringify({
    type: 'session_meta',
    timestamp: '2026-04-14T10:00:00Z',
    payload: {
      cwd: '/Users/test/proj',
      originator: 'codex-cli',
      session_id: 'sess-001',
      model: 'gpt-5.3-codex',
    },
  })
}

function tokenCount(): string {
  return JSON.stringify({
    type: 'event_msg',
    timestamp: '2026-04-14T10:01:00Z',
    payload: {
      type: 'token_count',
      info: {
        total_token_usage: {
          input_tokens: 100,
          cached_input_tokens: 0,
          output_tokens: 50,
          reasoning_output_tokens: 0,
          total_tokens: 150,
        },
      },
    },
  })
}

async function writeCodexSession(codexDir: string): Promise<void> {
  const dayDir = join(codexDir, 'sessions', '2026', '04', '14')
  await mkdir(dayDir, { recursive: true })
  await writeFile(join(dayDir, 'rollout-sess-001.jsonl'), `${sessionMeta()}\n${tokenCount()}\n`)
}

// Fully controllable synthetic provider for the edge cases (network, throwing,
// parse failure) that on-disk fixtures cannot force deterministically.
function fakeProvider(over: Partial<Provider> & { name: string }): Provider {
  return {
    displayName: over.name,
    modelDisplayName: (m: string) => m,
    toolDisplayName: (t: string) => t,
    discoverSessions: async () => [],
    createSessionParser: () => ({ async *parse() {} }),
    ...over,
  }
}

function only(report: Awaited<ReturnType<typeof collectDoctorReport>>, name: string) {
  const r = report.providers.find(p => p.provider === name)
  if (!r) throw new Error(`no report row for ${name}`)
  return r
}

const deniedProbeFs = {
  existsSync: () => false,
  statSync: () => {
    const err = new Error('EACCES: permission denied') as NodeJS.ErrnoException
    err.code = 'EACCES'
    throw err
  },
}

function restoreProcessPlatform(original: PropertyDescriptor): void {
  Object.defineProperty(process, 'platform', original)
}

let tmpDir: string
beforeEach(async () => { tmpDir = await mkdtemp(join(tmpdir(), 'doctor-test-')) })
afterEach(async () => { await rm(tmpDir, { recursive: true, force: true }) })

// ── Real-provider fixture-dir cases: found / empty / missing ───────────────

describe('collectDoctorReport - codex fixture dirs', () => {
  it('found: a real session file yields an OK verdict and a parsed sample', async () => {
    await writeCodexSession(tmpDir)
    const provider = createCodexProvider(tmpDir)
    const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
    const r = only(report, 'codex')

    expect(r.status).toBe('ok')
    expect(r.candidatesFound).toBeGreaterThanOrEqual(1)
    expect(r.parsedOk).toBeGreaterThanOrEqual(1)
    expect(r.parseFailed).toBe(0)
    expect(r.verdict).toMatch(/^OK \(/)
    const sessions = r.probePaths.find(p => p.label === 'sessions')
    expect(sessions?.exists).toBe(true)
  })

  it('empty: sessions dir exists but holds nothing -> NOTHING FOUND (no sessions)', async () => {
    await mkdir(join(tmpDir, 'sessions'), { recursive: true })
    const provider = createCodexProvider(tmpDir)
    const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
    const r = only(report, 'codex')

    expect(r.status).toBe('empty')
    expect(r.candidatesFound).toBe(0)
    expect(r.verdict).toContain('holds no sessions')
    expect(r.probePaths.find(p => p.label === 'sessions')?.exists).toBe(true)
  })

  it('missing: no sessions dir -> NOTHING FOUND names the missing path', async () => {
    // tmpDir has no `sessions` subdir at all.
    const provider = createCodexProvider(tmpDir)
    const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
    const r = only(report, 'codex')

    expect(r.status).toBe('empty')
    expect(r.candidatesFound).toBe(0)
    // Mutation guard: if the exists-check is broken to always return true, this
    // flips to false and the verdict drops "does not exist".
    expect(r.probePaths.find(p => p.label === 'sessions')?.exists).toBe(false)
    expect(r.verdict).toContain('does not exist')
    expect(r.verdict).toContain(join(tmpDir, 'sessions'))
  })
})

// ── Override case ──────────────────────────────────────────────────────────

describe('collectDoctorReport - env override', () => {
  it('names a set override pointing at a missing dir', async () => {
    const prev = process.env['CODEX_HOME']
    const bogus = join(tmpDir, 'does-not-exist')
    process.env['CODEX_HOME'] = bogus
    try {
      // Construct after setting env so the provider resolves CODEX_HOME.
      const provider = createCodexProvider()
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'codex')

      expect(r.envOverrides).toEqual([{ name: 'CODEX_HOME', value: bogus }])
      expect(r.status).toBe('empty')
      expect(r.verdict).toContain('override CODEX_HOME set')
      expect(r.verdict).toContain('does not exist')
    } finally {
      if (prev === undefined) delete process.env['CODEX_HOME']
      else process.env['CODEX_HOME'] = prev
    }
  })

  it('names a deliberate XDG_DATA_HOME override pointing at a missing dir, blaming the override not the install (opencode)', async () => {
    const prev = process.env['XDG_DATA_HOME']
    const bogus = join(tmpDir, 'xdg-missing')
    process.env['XDG_DATA_HOME'] = bogus
    try {
      // Construct after setting env so the provider resolves XDG_DATA_HOME
      // (src/providers/opencode.ts:38 reads it to resolve the data dir).
      const provider = createOpenCodeProvider()
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'opencode')

      expect(r.envOverrides).toContainEqual({ name: 'XDG_DATA_HOME', value: bogus })
      expect(r.status).toBe('empty')
      // Regression (Ruling 3 of lane 04): with XDG_DATA_HOME treated as an
      // ambient OS var, doctor skipped it and the verdict blamed the install
      // ("tool likely not installed") instead of the override the user set.
      expect(r.verdict).toContain('override XDG_DATA_HOME set')
      expect(r.verdict).toContain('does not exist')
    } finally {
      if (prev === undefined) delete process.env['XDG_DATA_HOME']
      else process.env['XDG_DATA_HOME'] = prev
    }
  })

  // Windows sets APPDATA and LOCALAPPDATA for every process, so neither
  // carries user intent: both are fingerprinted (a change moves the discovery
  // root) but must never be named as a deliberate override (Ruling 3 of lane
  // 04). Table-driven over both so removing either from AMBIENT_ENV_VARS
  // fails a test instead of leaking it into the overrides list.
  for (const varName of ['APPDATA', 'LOCALAPPDATA']) {
    it(`does not name ${varName} as an override for a provider that declares it`, async () => {
      const prev = process.env[varName]
      process.env[varName] = join(tmpDir, varName.toLowerCase())
      try {
        const provider = fakeProvider({ name: 'claude', displayName: 'Claude' })
        const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
        const r = only(report, 'claude')

        expect(r.envOverrides.some(o => o.name === varName)).toBe(false)
      } finally {
        if (prev === undefined) delete process.env[varName]
        else process.env[varName] = prev
      }
    })
  }

  // Every credential in SECRET_ENV_VARS must be redacted at collect time so
  // neither the text render nor the JSON report can leak it (Ruling 2 of lane
  // 04). Table-driven over both, so a credential added to the set without a
  // redaction test fails here instead of leaking into a bug report.
  for (const varName of ['AI_GATEWAY_API_KEY', 'VERCEL_OIDC_TOKEN']) {
    it(`redacts credential values (${varName}) from overrides, the table render, and the JSON report`, async () => {
      const secret = `sk-live-${varName}-value-12345`
      const prev = process.env[varName]
      const sibling = varName === 'AI_GATEWAY_API_KEY' ? 'VERCEL_OIDC_TOKEN' : 'AI_GATEWAY_API_KEY'
      const prevSibling = process.env[sibling]
      process.env[varName] = secret
      // Isolate the case under test: a stray ambient sibling must not change
      // what this case observes.
      delete process.env[sibling]
      try {
        const provider = fakeProvider({ name: 'vercel-gateway', displayName: 'Vercel AI Gateway', network: true })
        const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
        const r = only(report, 'vercel-gateway')

        // The "is this credential set?" diagnostic is useful; the value is a
        // live secret and must never leave doctor (Ruling 2 of lane 04).
        expect(r.envOverrides).toContainEqual({ name: varName, value: '<set>' })
        expect(r.envOverrides.some(o => o.value.includes(secret))).toBe(false)
        const table = renderDoctorTable(report, { color: false })
        expect(table).toContain(`${varName}=<set>`)
        expect(table).not.toContain(secret)
        expect(renderDoctorJson(report)).not.toContain(secret)
      } finally {
        if (prev === undefined) delete process.env[varName]
        else process.env[varName] = prev
        if (prevSibling === undefined) delete process.env[sibling]
        else process.env[sibling] = prevSibling
      }
    })
  }

  // KYBERDASH_CURSOR_MAX_BUBBLES caps how many bubbles Cursor parses
  // (src/providers/cursor.ts:692) and KIMI_MODEL_NAME renames the model
  // attributed to Kimi sessions (src/providers/kimi.ts:155): both are
  // fingerprinted but cannot explain why nothing was discovered, so the
  // verdict must not name them — while Details still lists them, because they
  // ARE overrides in force. Each is asserted through the provider that
  // declares it.
  for (const [varName, providerName, displayName, value] of [
    ['KYBERDASH_CURSOR_MAX_BUBBLES', 'cursor', 'Cursor', '5000'],
    ['KIMI_MODEL_NAME', 'kimi', 'Kimi', 'kimi-latest-920'],
  ] as const) {
    it(`does not blame ${varName} for an empty ${displayName} (not a discovery path)`, async () => {
      const prev = process.env[varName]
      process.env[varName] = value
      try {
        const provider = fakeProvider({ name: providerName, displayName })
        const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
        const r = only(report, providerName)

        expect(r.envOverrides).toContainEqual({ name: varName, value })
        expect(r.verdict).not.toContain(varName)
        const table = renderDoctorTable(report, { color: false })
        expect(table).toContain(`${varName}=${value}`)
      } finally {
        if (prev === undefined) delete process.env[varName]
        else process.env[varName] = prev
      }
    })
  }
})

// ── Synthetic edge cases ───────────────────────────────────────────────────

describe('collectDoctorReport - isolation and edge cases', () => {
  it('a provider that throws in discovery becomes an ERROR row, others survive', async () => {
    const boom = fakeProvider({
      name: 'boom',
      discoverSessions: async () => { throw new Error('disk on fire') },
    })
    const good = fakeProvider({
      name: 'good',
      discoverSessions: async () => [{ path: '/x/a.json', project: 'p', provider: 'good' }],
    })
    const report = await collectDoctorReport('all', { providers: [boom, good], cache: emptyCache() })

    const b = only(report, 'boom')
    expect(b.status).toBe('error')
    expect(b.error).toContain('disk on fire')
    expect(b.verdict).toMatch(/^ERROR \(/)
    expect(only(report, 'good').status).toBe('ok')
  })

  it('a parser that throws is counted and downgrades the verdict to ERRORS', async () => {
    const src: SessionSource = { path: '/x/a.json', project: 'p', provider: 'flaky' }
    const flaky = fakeProvider({
      name: 'flaky',
      discoverSessions: async () => [src],
      createSessionParser: () => ({
         
        async *parse() { throw new Error('bad json') },
      }),
    })
    const r = only(await collectDoctorReport('all', { providers: [flaky], cache: emptyCache() }), 'flaky')

    expect(r.status).toBe('errors')
    expect(r.parseFailed).toBe(1)
    expect(r.parsedOk).toBe(0)
    expect(r.verdict).toMatch(/^ERRORS \(/)
  })

  it('network providers are never parsed offline', async () => {
    let parserCreated = false
    const net = fakeProvider({
      name: 'net',
      network: true,
      discoverSessions: async () => [{ path: 'net:report', project: 'p', provider: 'net' }],
      createSessionParser: () => { parserCreated = true; return { async *parse() {} } },
    })
    const r = only(await collectDoctorReport('all', { providers: [net], cache: emptyCache() }), 'net')

    expect(parserCreated).toBe(false)
    expect(r.status).toBe('network')
    expect(r.sampled).toBe(0)
    expect(r.verdict).toContain('NETWORK')
  })

  it('bounds the parse sample and flags it', async () => {
    const sources: SessionSource[] = Array.from({ length: 5 }, (_, i) => ({ path: `/x/${i}.json`, project: 'p', provider: 'many' }))
    const many = fakeProvider({
      name: 'many',
      discoverSessions: async () => sources,
      createSessionParser: () => ({ async *parse() { yield undefined as never } }),
    })
    const r = only(await collectDoctorReport('all', { providers: [many], cache: emptyCache(), sampleLimit: 2 }), 'many')

    expect(r.candidatesFound).toBe(5)
    expect(r.sampled).toBe(2)
    expect(r.bounded).toBe(true)
    expect(r.status).toBe('ok')
  })

  it('reports cache state from the injected snapshot', async () => {
    const cache: SessionCache = emptyCache()
    cache.providers['cached'] = {
      envFingerprint: 'x',
      files: {
        '/a.jsonl': { fingerprint: { dev: 1, ino: 1, mtimeMs: 1, sizeBytes: 1 }, mcpInventory: [], turns: [] },
        '/b.jsonl': { fingerprint: { dev: 1, ino: 2, mtimeMs: 1, sizeBytes: 1 }, mcpInventory: [], turns: [], failed: true },
      },
    }
    const provider = fakeProvider({ name: 'cached', discoverSessions: async () => [] })
    const r = only(await collectDoctorReport('all', { providers: [provider], cache }), 'cached')

    expect(r.cachedFiles).toBe(2)
    expect(r.cachedFailed).toBe(1)
  })

  it('filters to a single provider by name', async () => {
    const a = fakeProvider({ name: 'a' })
    const b = fakeProvider({ name: 'b' })
    const report = await collectDoctorReport('b', { providers: [a, b], cache: emptyCache() })
    expect(report.providers.map(p => p.provider)).toEqual(['b'])
  })
})

// ── Rendering ──────────────────────────────────────────────────────────────

describe('doctor rendering', () => {
  it('renders a plain-text table naming the override and missing path', async () => {
    const provider = fakeProvider({
      name: 'claude',
      displayName: 'Claude',
      probeRoots: async (): Promise<ProbeRoot[]> => [{ path: '/nonexistent/projects', label: 'projects' }],
      discoverSessions: async () => [],
    })
    const prev = process.env['CLAUDE_CONFIG_DIR']
    process.env['CLAUDE_CONFIG_DIR'] = '/nonexistent'
    try {
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const out = renderDoctorTable(report, { color: false })
      expect(out).toContain('kyberDash doctor')
      expect(out).toContain('NOTHING FOUND')
      expect(out).toContain('CLAUDE_CONFIG_DIR=/nonexistent')
      expect(out).toContain('/nonexistent/projects')
      expect(out).toContain('missing')
      // no-color mode emits no ANSI escapes
       
      expect(out).not.toMatch(/\[/)
    } finally {
      if (prev === undefined) delete process.env['CLAUDE_CONFIG_DIR']
      else process.env['CLAUDE_CONFIG_DIR'] = prev
    }
  })

  it('emits valid, round-trippable JSON', async () => {
    const provider = fakeProvider({ name: 'a', discoverSessions: async () => [] })
    const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
    const parsed = JSON.parse(renderDoctorJson(report))
    expect(parsed.providers[0].provider).toBe('a')
    expect(typeof parsed.generatedAt).toBe('string')
  })
})

// ── Re-review hardening (#685): inert-diagnostic guarantees ────────────────

describe('doctor is inert', () => {
  it('sets the cache-write suppression flag while collecting and restores it after', async () => {
    let flagDuringParse: string | undefined
    const spy = fakeProvider({
      name: 'spy',
      discoverSessions: async () => [{ path: '/tmp/x', project: 'p', provider: 'spy' }],
      createSessionParser: () => ({
        async *parse() {
          flagDuringParse = process.env['KYBERDASH_SUPPRESS_CACHE_WRITES']
        },
      }),
    })
    delete process.env['KYBERDASH_SUPPRESS_CACHE_WRITES']
    await collectDoctorReport('spy', { providers: [spy], cache: emptyCache() })
    expect(flagDuringParse).toBe('1')
    expect(process.env['KYBERDASH_SUPPRESS_CACHE_WRITES']).toBeUndefined()
  })

  it('never sample-parses a provider whose parse spawns processes (antigravity)', async () => {
    let parsed = false
    const ag = fakeProvider({
      name: 'antigravity',
      discoverSessions: async () => [{ path: '/tmp/cascade-x.pb', project: 'p', provider: 'antigravity' }],
      createSessionParser: () => ({
        async *parse() {
          parsed = true
        },
      }),
    })
    const report = await collectDoctorReport('antigravity', { providers: [ag], cache: emptyCache() })
    expect(parsed).toBe(false)
    const row = only(report, 'antigravity')
    expect(row.status).toBe('ok')
    expect(row.verdict).toContain('parse sample skipped')
    expect(row.candidatesFound).toBe(1)
  })

  it('does not blame KYBERDASH_CACHE_DIR for an empty provider', async () => {
    const prev = process.env['KYBERDASH_CACHE_DIR']
    process.env['KYBERDASH_CACHE_DIR'] = '/tmp/some-cache'
    try {
      const empty = fakeProvider({ name: 'antigravity' })
      const report = await collectDoctorReport('antigravity', { providers: [empty], cache: emptyCache() })
      const row = only(report, 'antigravity')
      expect(row.verdict).not.toContain('KYBERDASH_CACHE_DIR')
    } finally {
      if (prev === undefined) delete process.env['KYBERDASH_CACHE_DIR']
      else process.env['KYBERDASH_CACHE_DIR'] = prev
    }
  })
})

// ── Claude transcript retention note ───────────────────────────────────────
describe('collectDoctorReport - claude retention note', () => {
  async function withClaudeConfig(settings: string | null, fn: () => Promise<void>): Promise<void> {
    const prev = process.env['CLAUDE_CONFIG_DIR']
    const prevMulti = process.env['CLAUDE_CONFIG_DIRS']
    delete process.env['CLAUDE_CONFIG_DIRS']
    const dir = join(tmpDir, 'claude-config')
    await mkdir(dir, { recursive: true })
    if (settings !== null) await writeFile(join(dir, 'settings.json'), settings)
    process.env['CLAUDE_CONFIG_DIR'] = dir
    try {
      await fn()
    } finally {
      if (prev === undefined) delete process.env['CLAUDE_CONFIG_DIR']
      else process.env['CLAUDE_CONFIG_DIR'] = prev
      if (prevMulti !== undefined) process.env['CLAUDE_CONFIG_DIRS'] = prevMulti
    }
  }

  it('reports an explicit cleanupPeriodDays and renders it as preserved when long', async () => {
    await withClaudeConfig(JSON.stringify({ cleanupPeriodDays: 3650 }), async () => {
      const provider = fakeProvider({ name: 'claude' })
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      expect(report.claudeRetention).toMatchObject({ effectiveDays: 3650, configured: true })
      const table = renderDoctorTable(report, { color: false })
      expect(table).toContain('deletes transcripts after 3650 days')
      expect(table).toContain('per-session detail is preserved')
    })
  })

  it('reports the 30-day default and renders a warning with the settings path', async () => {
    await withClaudeConfig(JSON.stringify({ theme: 'dark' }), async () => {
      const provider = fakeProvider({ name: 'claude' })
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      expect(report.claudeRetention).toMatchObject({ effectiveDays: 30, configured: false })
      const table = renderDoctorTable(report, { color: false })
      expect(table).toContain('deletes transcripts after 30 days')
      expect(table).toContain('cleanupPeriodDays not set')
      expect(table).toContain('"cleanupPeriodDays": 3650')
      expect(table).toContain(report.claudeRetention!.settingsPath)
    })
  })

  it('emits no note when claude is not in the report or has no settings file', async () => {
    await withClaudeConfig(null, async () => {
      const claudeless = await collectDoctorReport('all', { providers: [fakeProvider({ name: 'codex' })], cache: emptyCache() })
      expect(claudeless.claudeRetention).toBeUndefined()
      const noSettings = await collectDoctorReport('all', { providers: [fakeProvider({ name: 'claude' })], cache: emptyCache() })
      expect(noSettings.claudeRetention).toBeUndefined()
      expect(renderDoctorTable(noSettings, { color: false })).not.toContain('deletes transcripts')
    })
  })
})

// ── Issue #197: missing-rate transparency + permission-denied probe roots ──

describe('collectDoctorReport - devin rate transparency (#197)', () => {
  it('a missing ACU rate names the rate gap, not "no sessions"', async () => {
    // HOME redirected so no ~/.kyberdash/config.json can supply devin.acuUsdRate.
    const prevHome = process.env['HOME']
    process.env['HOME'] = tmpDir
    try {
      await mkdir(join(tmpDir, 'transcripts'), { recursive: true })
      await writeFile(join(tmpDir, 'transcripts', 's1.json'), JSON.stringify({ steps: [] }))
      const provider = createDevinProvider(tmpDir)
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'devin')

      expect(r.candidatesFound).toBeGreaterThanOrEqual(1)
      expect(r.verdict).toContain('devin.acuUsdRate')
    } finally {
      if (prevHome === undefined) delete process.env['HOME']
      else process.env['HOME'] = prevHome
    }
  })

  it('a non-finite ACU rate (1e400 parses to Infinity) still names the rate gap', async () => {
    const prevHome = process.env['HOME']
    process.env['HOME'] = tmpDir
    try {
      await mkdir(join(tmpDir, '.kyberdash'), { recursive: true })
      // Raw literal on purpose: JSON.stringify would coerce Infinity to null,
      // but JSON.parse maps 1e400 to positive Infinity.
      await writeFile(join(tmpDir, '.kyberdash', 'config.json'), '{"devin":{"acuUsdRate":1e400}}')
      await mkdir(join(tmpDir, 'transcripts'), { recursive: true })
      await writeFile(join(tmpDir, 'transcripts', 's1.json'), JSON.stringify({ steps: [] }))
      const provider = createDevinProvider(tmpDir)
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'devin')

      expect(r.candidatesFound).toBeGreaterThanOrEqual(1)
      expect(r.verdict).toContain('devin.acuUsdRate')
    } finally {
      if (prevHome === undefined) delete process.env['HOME']
      else process.env['HOME'] = prevHome
    }
  })
})

describe('collectDoctorReport - permission-denied probe root (#197)', () => {
  it('an unreadable probe path reports permission denied, not "does not exist"', async () => {
    // Inject a controlled EACCES — chmod(0o000) is a no-op on Windows, so the
    // probe seam must not depend on POSIX permission bits.
    const inner = join(tmpDir, 'locked', 'warp.sqlite')
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [{ path: inner, label: 'db' }],
    })
    const report = await collectDoctorReport('all', {
      providers: [provider],
      cache: emptyCache(),
      probeFs: {
        existsSync: () => false,
        statSync: () => {
          const err = new Error('EACCES: permission denied') as NodeJS.ErrnoException
          err.code = 'EACCES'
          throw err
        },
      },
    })
    const r = only(report, 'warp')

    expect(r.status).toBe('empty')
    expect(r.probePaths[0]).toMatchObject({ exists: false, accessError: 'permission-denied' })
    expect(r.verdict).toMatch(/permission denied|not readable/i)
  })

  it('an existing Warp db that fails open with EPERM reports permission denied, not empty sessions', async () => {
    const dbPath = join(tmpDir, 'warp.sqlite')
    await writeFile(dbPath, 'x')
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [{ path: dbPath, label: 'db' }],
    })
    const report = await collectDoctorReport('all', {
      providers: [provider],
      cache: emptyCache(),
      probeFs: {
        existsSync: () => true,
        statSync: () => ({}),
        checkAccess: () => {
          const err = new Error('EPERM: operation not permitted') as NodeJS.ErrnoException
          err.code = 'EPERM'
          throw err
        },
      },
    })
    const r = only(report, 'warp')

    expect(r.status).toBe('empty')
    expect(r.probePaths[0]).toMatchObject({ exists: true, accessError: 'permission-denied' })
    expect(r.verdict).toMatch(/permission denied|not readable/i)
    expect(r.verdict).not.toMatch(/holds no sessions/i)
  })

  it('folds discoverFromDb access denials into the Warp doctor verdict', async ({ skip }) => {
    // Production path: existsSync true, open fails EACCES, drain → emptyVerdict.
    // chmod bit clearing is ineffective on Windows (covered by checkAccess above).
    if (process.platform === 'win32') skip()
    if (!isSqliteAvailable()) skip()

    drainWarpDbAccessDenials()
    const dbPath = join(tmpDir, 'warp.sqlite')
    await writeFile(dbPath, 'x')
    await chmod(dbPath, 0o000)
    try {
      const provider = createWarpProvider(dbPath)
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'warp')

      expect(r.status).toBe('empty')
      expect(r.probePaths[0]).toMatchObject({ exists: true, accessError: 'permission-denied' })
      expect(r.verdict).toMatch(/permission denied|not readable/i)
      expect(r.verdict).not.toMatch(/holds no sessions/i)
    } finally {
      await chmod(dbPath, 0o644)
      drainWarpDbAccessDenials()
    }
  })

  it('does not prescribe Full Disk Access for an unwritable cache (#197)', async ({ skip }) => {
    // Readable Warp db forced onto the cache-copy path + unwritable
    // KYBERDASH_CACHE_DIR must not surface as a TCC / Full Disk Access denial.
    if (process.platform === 'win32') skip()
    if (!isSqliteAvailable()) skip()

    const sourceDir = join(tmpDir, 'source')
    const cacheDir = join(tmpDir, 'cache')
    mkdirSync(sourceDir, { recursive: true })
    mkdirSync(cacheDir, { recursive: true })

    const originDir = join(sourceDir, 'origin')
    mkdirSync(originDir)
    const originPath = join(originDir, 'warp.sqlite')
    const dbPath = join(sourceDir, 'warp.sqlite')
    const { DatabaseSync: Database } = requireForTest('node:sqlite') as {
      DatabaseSync: new (path: string) => NativeDatabase
    }
    const writer = new Database(originPath)
    writer.exec('PRAGMA journal_mode=WAL')
    writer.exec(`
      CREATE TABLE agent_conversations (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        conversation_id TEXT NOT NULL,
        conversation_data TEXT NOT NULL,
        last_modified_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
      )
    `)
    writer.exec(`
      CREATE TABLE ai_queries (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        exchange_id TEXT NOT NULL,
        conversation_id TEXT NOT NULL,
        start_ts DATETIME NOT NULL,
        input TEXT NOT NULL,
        working_directory TEXT,
        output_status TEXT NOT NULL,
        model_id TEXT NOT NULL DEFAULT '',
        planning_model_id TEXT NOT NULL DEFAULT '',
        coding_model_id TEXT NOT NULL DEFAULT ''
      )
    `)
    writer.exec(`
      CREATE TABLE blocks (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        pane_leaf_uuid BLOB NOT NULL,
        stylized_command BLOB NOT NULL,
        stylized_output BLOB NOT NULL,
        exit_code INTEGER NOT NULL,
        did_execute BOOLEAN NOT NULL,
        completed_ts DATETIME,
        start_ts DATETIME,
        block_id TEXT NOT NULL DEFAULT '',
        ai_metadata TEXT
      )
    `)
    writer.exec('PRAGMA wal_checkpoint(TRUNCATE)')
    writer.exec('PRAGMA wal_autocheckpoint=0')
    writer.prepare(
      'INSERT INTO agent_conversations (conversation_id, conversation_data, last_modified_at) VALUES (?, ?, ?)',
    ).run('conv-ro', '{}', '2026-05-18 10:00:00')
    copyFileSync(originPath, dbPath)
    copyFileSync(originPath + '-wal', dbPath + '-wal')
    writer.close()
    expect(statSync(dbPath + '-wal').size).toBeGreaterThan(0)
    expect(existsSync(dbPath + '-shm')).toBe(false)

    chmodSync(sourceDir, 0o555)
    const probe = join(sourceDir, '.write-probe')
    try {
      writeFileSync(probe, '')
      rmSync(probe)
      chmodSync(sourceDir, 0o755)
      skip(`SKIP: chmod 0555 did not make ${sourceDir} non-writable for this process`)
      return
    } catch {
      // expected — parent is non-writable
    }

    const previousCacheDir = process.env['KYBERDASH_CACHE_DIR']
    process.env['KYBERDASH_CACHE_DIR'] = cacheDir
    chmodSync(cacheDir, 0o000)
    drainWarpDbAccessDenials()
    try {
      // Same misattribution pin as the sqlite / warp cases: cache-dir EACCES must
      // not surface as a permission errno that doctor could fold into FDA.
      let thrown: unknown
      try {
        const db = openDatabase(dbPath)
        try {
          db.query('SELECT 1 AS ok FROM agent_conversations LIMIT 1')
        } catch (err) {
          thrown = err
        }
        try { db.close() } catch { /* query-time fallback may have closed it */ }
      } catch (err) {
        thrown = err
      }
      expect(thrown).toBeDefined()
      const code = (thrown as NodeJS.ErrnoException | undefined)?.code
      expect(code === 'EACCES' || code === 'EPERM').toBe(false)

      const provider = createWarpProvider(dbPath)
      const report = await collectDoctorReport('all', { providers: [provider], cache: emptyCache() })
      const r = only(report, 'warp')

      expect(r.verdict).not.toMatch(/Full Disk Access/i)
    } finally {
      chmodSync(cacheDir, 0o755)
      chmodSync(sourceDir, 0o755)
      if (previousCacheDir === undefined) delete process.env['KYBERDASH_CACHE_DIR']
      else process.env['KYBERDASH_CACHE_DIR'] = previousCacheDir
      drainWarpDbAccessDenials()
    }
  })

  it('propagates unexpected probe errors (ELOOP) as an ERROR row, not absence', async () => {
    const inner = join(tmpDir, 'loop', 'warp.sqlite')
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [{ path: inner, label: 'db' }],
    })
    const report = await collectDoctorReport('all', {
      providers: [provider],
      cache: emptyCache(),
      probeFs: {
        existsSync: () => false,
        statSync: () => {
          const err = new Error('ELOOP: too many symbolic links') as NodeJS.ErrnoException
          err.code = 'ELOOP'
          throw err
        },
      },
    })
    const r = only(report, 'warp')

    expect(r.status).toBe('error')
    expect(r.verdict).toMatch(/^ERROR \(/)
    expect(r.verdict).toMatch(/ELOOP|symbolic links/i)
  })

  it('permission-denied verdict is platform-aware and keeps an override', async () => {
    const prev = process.env['WARP_DB_PATH']
    const inner = join(tmpDir, 'locked', 'warp.sqlite')
    process.env['WARP_DB_PATH'] = inner
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [{ path: inner, label: 'db' }],
    })
    try {
      const report = await collectDoctorReport('all', {
        providers: [provider],
        cache: emptyCache(),
        probeFs: deniedProbeFs,
      })
      const r = only(report, 'warp')
      expect(r.verdict).toContain('permission denied')
      expect(r.verdict).toContain('override WARP_DB_PATH set')
      if (process.platform === 'darwin') {
        expect(r.verdict).toMatch(/Full Disk Access/i)
      } else {
        expect(r.verdict).toMatch(/check owner\/permissions/)
        expect(r.verdict).not.toMatch(/Full Disk Access/i)
      }
    } finally {
      if (prev === undefined) delete process.env['WARP_DB_PATH']
      else process.env['WARP_DB_PATH'] = prev
    }
  })

  it('darwin permission-denied verdict names Full Disk Access; others do not', async () => {
    const inner = join(tmpDir, 'locked', 'warp.sqlite')
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [{ path: inner, label: 'db' }],
    })
    const original = Object.getOwnPropertyDescriptor(process, 'platform')
    if (original === undefined) throw new Error('process.platform is not an own property')
    const hostPlatform = process.platform
    Object.defineProperty(process, 'platform', { configurable: true, value: 'darwin' })
    try {
      const report = await collectDoctorReport('all', {
        providers: [provider],
        cache: emptyCache(),
        probeFs: deniedProbeFs,
      })
      expect(only(report, 'warp').verdict).toMatch(/on macOS grant Full Disk Access/)
    } finally {
      restoreProcessPlatform(original)
    }
    expect(process.platform).toBe(hostPlatform)
    expect(Object.getOwnPropertyDescriptor(process, 'platform')?.configurable).toBe(true)
  })

  it('names the count when more than one probe root is permission-denied', async () => {
    const first = join(tmpDir, 'a', 'warp.sqlite')
    const second = join(tmpDir, 'b', 'warp.sqlite')
    const provider = fakeProvider({
      name: 'warp',
      probeRoots: async () => [
        { path: first, label: 'db' },
        { path: second, label: 'db' },
      ],
    })
    const report = await collectDoctorReport('all', {
      providers: [provider],
      cache: emptyCache(),
      probeFs: deniedProbeFs,
    })
    const r = only(report, 'warp')
    expect(r.verdict).toContain(first)
    expect(r.verdict).toMatch(/and 1 more/)
    expect(r.verdict).toContain('permission denied')
  })
})
