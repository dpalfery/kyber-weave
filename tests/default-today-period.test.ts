import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm, utimes } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { assembleDashboardData, renderDashboard } from '../src/dashboard.js'
import { clearSessionCache } from '../src/parser.js'
import { clearLoadCacheMemo } from '../src/session-cache.js'

const DAY_MS = 24 * 60 * 60 * 1000

let tmpDir: string

beforeEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  tmpDir = await mkdtemp(join(tmpdir(), 'default-today-'))
  process.env['CLAUDE_CONFIG_DIR'] = tmpDir
  process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache')
  process.env['CODEBURN_DESKTOP_SESSIONS_DIR'] = join(tmpDir, 'desktop-sessions')
})

afterEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  await rm(tmpDir, { recursive: true, force: true })
})

/** One Claude session whose only turn landed `hoursAgo` ago. */
async function writeSession(name: string, hoursAgo: number): Promise<void> {
  const dir = join(tmpDir, 'projects', 'proj')
  await mkdir(dir, { recursive: true })
  const at = new Date(Date.now() - hoursAgo * 60 * 60 * 1000)
  const path = join(dir, `${name}.jsonl`)
  await writeFile(path, JSON.stringify({
    type: 'assistant',
    sessionId: name,
    timestamp: at.toISOString(),
    cwd: '/tmp/proj',
    message: {
      id: `msg-${name}`, type: 'message', role: 'assistant', model: 'claude-sonnet-4-5',
      content: [], usage: { input_tokens: 100, output_tokens: 50 },
    },
  }) + '\n')
  await utimes(path, at, at)
}

/** The interactive first paint with no explicit period: opens on `today`, with
 *  the fallback armed. */
function assembleAuto(period: 'today' | 'week', autoFallback: boolean) {
  return assembleDashboardData(period, 'all', undefined, undefined, null, null, true, autoFallback)
}

describe('unset default period (#1111)', () => {
  it('opens on today when today has sessions', async () => {
    await writeSession('now', 0)
    await writeSession('older', 3 * 24)
    const { period, filteredProjects } = await assembleAuto('today', true)
    expect(period).toBe('today')
    expect(filteredProjects.length).toBe(1)
  })

  it('falls back to 7 days when today is empty', async () => {
    await writeSession('older', 3 * 24)
    const { period, filteredProjects } = await assembleAuto('today', true)
    expect(period).toBe('week')
    expect(filteredProjects.length).toBe(1)
  })

  it('resolves to 7 days when there is no data at all', async () => {
    const { period } = await assembleAuto('today', true)
    // Both windows are empty, so the fallback still fires — the point is that it
    // resolves to one period deterministically instead of throwing.
    expect(period).toBe('week')
  })

  it('an explicit period is never moved, even when its window is empty', async () => {
    await writeSession('older', 3 * 24)
    const today = await assembleAuto('today', false)
    expect(today.period).toBe('today')
    expect(today.filteredProjects.length).toBe(0)
    const week = await assembleAuto('week', false)
    expect(week.period).toBe('week')
  })
})

describe('non-interactive render (#1111)', () => {
  it('keeps the 7-day default when stdout is not a TTY', async () => {
    await writeSession('now', 0)
    const chunks: string[] = []
    const write = vi.spyOn(process.stdout, 'write').mockImplementation((chunk: unknown) => {
      chunks.push(String(chunk))
      return true
    })
    try {
      // isTTY is false under vitest, so this is the piped `codeburn` path even
      // with the auto flag set by the CLI.
      await renderDashboard('week', 'all', 0, undefined, undefined, null, undefined, undefined, true)
    } finally {
      write.mockRestore()
    }
    const out = chunks.join('')
    expect(out).toContain('[ 7 Days ]')
    expect(out).not.toContain('[ Today ]')
  })
})
