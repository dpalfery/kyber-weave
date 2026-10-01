import { readFileSync } from 'fs'
import { dirname, resolve } from 'path'
import { fileURLToPath } from 'url'
import { describe, it, expect } from 'vitest'

import { pricingDataDir } from '../../../scripts/pricing-artifact-paths.mjs'
import fallback from './pricing-fallback.json' assert { type: 'json' }
import snapshot from './litellm-snapshot.json' assert { type: 'json' }

// The gap-fill fallback is generated from models.dev / OpenRouter. These assert
// the bundler's hygiene guarantees on the committed artifact, so a future
// rebundle that regresses them fails CI rather than shipping bad pricing.
describe('pricing-fallback.json data hygiene', () => {
  const entries = Object.entries(fallback as Record<string, (number | null)[]>)

  // Post 2026-10-01 refresh the primary snapshot (4671 → 5978 keys) absorbed most
  // former gap-fill rows via bareKey seeding of `seen`, collapsing fallback
  // coverage 205 → 52. Pin the committed cardinality so a broken rebundle that
  // empties or silently shrinks the safety net fails CI; bump deliberately when
  // the snapshot changes.
  it('pins the post-refresh gap-fill cardinality (205 → 52 collapse)', () => {
    expect(entries.length).toBe(52)
  })

  it('has no negative rates (OpenRouter -1 "variable price" sentinels)', () => {
    const bad = entries.filter(([, v]) => (v[0] ?? 0) < 0 || (v[1] ?? 0) < 0 || (v[2] ?? 0) < 0 || (v[3] ?? 0) < 0)
    expect(bad.map(([k]) => k)).toEqual([])
  })

  it('has no entry that is free on both input and output', () => {
    const bad = entries.filter(([, v]) => v[0] === 0 && v[1] === 0)
    expect(bad.map(([k]) => k)).toEqual([])
  })

  it('has no unreachable @pin or date-suffixed keys', () => {
    const bad = entries.filter(([k]) => /@/.test(k) || /\d{8}$/.test(k))
    expect(bad.map(([k]) => k)).toEqual([])
  })

  it('stores per-token rates (no per-million values leaked through)', () => {
    // A per-million value would be >= 1; real per-token rates are tiny.
    const bad = entries.filter(([, v]) => (v[0] ?? 0) >= 1 || (v[1] ?? 0) >= 1)
    expect(bad.map(([k]) => k)).toEqual([])
  })
})

// Issue #186 U11: two targeted, source-cited additions to the bundled data. Tuple order is
// [input, output, cacheWrite, cacheRead, fast] in USD per token.
describe('issue #186 U11 bundled rate additions', () => {
  const snap = snapshot as unknown as Record<string, (number | null)[]>
  const fb = fallback as unknown as Record<string, (number | null)[]>
  const find = (key: string) => snap[key] ?? fb[key]

  it('carries claude-sonnet-5-5 at 2 / 10 / cache write 2.50 / cache read 0.20 per 1M', () => {
    const v = find('claude-sonnet-5-5')
    expect(v).toBeDefined()
    expect(v![0]).toBeCloseTo(2e-6, 12)
    expect(v![1]).toBeCloseTo(1e-5, 12)
    expect(v![2]).toBeCloseTo(2.5e-6, 12)
    expect(v![3]).toBeCloseTo(2e-7, 12)
  })

  it('carries gpt-6-luna at 0.10 / 0.50 / cache read 0.01 per 1M (base tier)', () => {
    const v = find('gpt-6-luna')
    expect(v).toBeDefined()
    expect(v![0]).toBeCloseTo(1e-7, 13)
    expect(v![1]).toBeCloseTo(5e-7, 13)
    expect(v![3]).toBeCloseTo(1e-8, 13)
  })

  // Assumed sidecar (JSON cannot carry comments and the rate files are arrays keyed by model,
  // so provenance is pinned in pricing-provenance.json: { "<model>": { source, retrieved } }).
  it('cites a source and retrieval date for each hand-added MANUAL rate', () => {
    const prov = JSON.parse(readFileSync(new URL('./pricing-provenance.json', import.meta.url), 'utf8')) as Record<
      string,
      { source: string; retrieved: string }
    >
    expect(prov['claude-sonnet-5-5'].source).toMatch(/^https:\/\/(platform\.claude\.com|docs\.anthropic\.com)\//)
    expect(prov['gpt-6-luna'].source).toMatch(/^https:\/\/(developers\.openai\.com|openai\.com)\//)
    expect(prov['claude-sonnet-5-5'].retrieved).toBe('2026-09-30')
    expect(prov['gpt-6-luna'].retrieved).toBe('2026-09-30')
    expect(prov['claude-opus-4'].source).toMatch(/^https:\/\/(platform\.claude\.com|docs\.anthropic\.com)\//)
    expect(prov['claude-opus-4'].retrieved).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    expect(prov['claude-opus-4-20250514'].source).toMatch(/^https:\/\/(platform\.claude\.com|docs\.anthropic\.com)\//)
    expect(prov['claude-opus-4-20250514'].retrieved).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    expect(prov['grok-latest'].source).toMatch(/^https:\/\//)
    expect(prov['grok-latest'].retrieved).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    expect(prov['kimi-k2-thinking'].source).toBe('rescued prior value, no public source')
    expect(prov['kimi-k2-thinking'].retrieved).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })
})

// Reproducibility guard: the bundler regenerates litellm-snapshot.json from LiteLLM plus
// MANUAL_ENTRIES, so a rate that lives only in the committed snapshot is dropped on the next
// regeneration. The bundler is read as text; it is never executed (it fetches and writes files).
describe('issue #186 bundler MANUAL_ENTRIES reproducibility', () => {
  const source = readFileSync(new URL('../../../scripts/bundle-litellm.mjs', import.meta.url), 'utf8')
  const block = /const MANUAL_ENTRIES = \{([\s\S]*?)\n\}/.exec(source)?.[1] ?? ''

  function parseTupleLiteral(raw: string): (number | null)[] {
    return raw.split(',').map((token) => {
      const trimmed = token.trim()
      if (trimmed === 'null') return null
      return Number(trimmed)
    })
  }

  const manual = new Map<string, (number | null)[]>()
  for (const line of block.split('\n')) {
    const m = /^\s*'([^']+)':\s*\[([^\]]*)\]/.exec(line)
    if (m) manual.set(m[1], parseTupleLiteral(m[2]))
  }
  const snap = snapshot as unknown as Record<string, (number | null)[]>

  it('writes pricing artifacts to the same directory the runtime imports', () => {
    // Import the shared path export (no network) and compare to the directory
    // that actually holds the committed snapshot this suite loads — pins the
    // bundler's write target to the runtime's import directory (#229).
    const runtimeDataDir = dirname(fileURLToPath(new URL('./litellm-snapshot.json', import.meta.url)))
    expect(resolve(pricingDataDir)).toBe(resolve(runtimeDataDir))
    expect(pricingDataDir.replace(/\\/g, '/')).toMatch(/src\/pricing\/data$/)
    expect(pricingDataDir.replace(/\\/g, '/')).not.toMatch(/src\/data$/)
  })

  it('locates and parses the MANUAL_ENTRIES block (sanity: neighbour claude-mythos-5)', () => {
    expect(manual.has('claude-mythos-5')).toBe(true)
  })

  // Drive the guard off every parsed MANUAL_ENTRIES key so a hand-added rate
  // (kimi-k2-thinking, claude-opus-4-20250514, grok-latest, …) cannot skip the
  // reproducibility check. Compare the full tuple, including optional `fast`.
  for (const id of manual.keys()) {
    it(`declares ${id} in MANUAL_ENTRIES with the committed snapshot rates`, () => {
      const declared = manual.get(id)
      expect(declared).toBeDefined()
      const committed = snap[id]
      expect(committed).toBeDefined()
      const len = Math.max(declared!.length, committed!.length)
      for (let i = 0; i < len; i++) {
        const expected = committed![i] ?? null
        const actual = declared![i] ?? null
        if (expected === null) {
          expect(actual, `${id}[${i}] must stay null (absent field), not 0`).toBeNull()
        } else {
          expect(actual).toBeCloseTo(expected, 13)
        }
      }
    })
  }
})
