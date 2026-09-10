import { PassThrough } from 'node:stream'

import React from 'react'
import { render } from 'ink'
import stripAnsi from 'strip-ansi'
import { describe, expect, it, onTestFinished, vi } from 'vitest'

import { InteractiveDashboard, type DashboardHistoryIndex } from '../src/dashboard.js'

// #1143: the quit-confirmation path runs only while a cold-start fill is
// active, so the parser mock below holds the fill in flight (parseAllSessions
// never resolves) for the duration of those tests. `filesParsedFromSourceCount`
// stays at zero so the progress interval can't grow `indexedFiles` under the
// test. The other exports are kept stable so the rest of the dashboard
// (provider detection, the budget effect, the q-without-fill handler) still
// works with the mock in place - the existing #1141 tests rely on that.
const { parseAllSessionsMock, filesParsedFromSourceCountMock, resolveNextParse } = vi.hoisted(() => {
  const pending: Array<(projects: unknown[]) => void> = []
  return {
  parseAllSessionsMock: vi.fn<Parameters<typeof import('../src/parser.js').parseAllSessions>, ReturnType<typeof import('../src/parser.js').parseAllSessions>>(
    () => new Promise(resolve => pending.push(resolve as (projects: unknown[]) => void)),
  ),
  filesParsedFromSourceCountMock: vi.fn(() => 0),
  resolveNextParse: (projects: unknown[]) => pending.shift()?.(projects),
  }
})

vi.mock('../src/parser.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/parser.js')>()
  return {
    ...actual,
    parseAllSessions: parseAllSessionsMock,
    filesParsedFromSourceCount: filesParsedFromSourceCountMock,
  }
})

vi.mock('../src/usage-aggregator.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/usage-aggregator.js')>()
  return {
    ...actual,
    buildDurablePeriod: vi.fn(async () => ({
      data: { cost: 99, savingsUSD: 0, calls: 1, sessions: 1, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 },
      carriedCostUSD: 0,
    })),
  }
})

vi.mock('../src/plan-usage.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/plan-usage.js')>()
  return { ...actual, getPlanUsages: vi.fn(async () => []) }
})

const EMPTY_CATEGORY_BREAKDOWN = {
  coding: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  debugging: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  feature: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  refactoring: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  testing: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  exploration: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  planning: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  delegation: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  git: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  'build/deploy': { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  conversation: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  brainstorming: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
  general: { turns: 0, costUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
} as const

function makeSession(id: string) {
  return {
    sessionId: id,
    project: 'p',
    firstTimestamp: '2026-04-14T10:00:00Z',
    lastTimestamp: '2026-04-14T10:00:00Z',
    totalCostUSD: 1,
    totalSavingsUSD: 0,
    totalInputTokens: 0,
    totalOutputTokens: 0,
    totalCacheReadTokens: 0,
    totalCacheWriteTokens: 0,
    apiCalls: 1,
    turns: [],
    modelBreakdown: {},
    toolBreakdown: {},
    mcpBreakdown: {},
    bashBreakdown: {},
    categoryBreakdown: { ...EMPTY_CATEGORY_BREAKDOWN },
    skillBreakdown: {},
    subagentBreakdown: {},
  }
}

function makeSessionWithCall(id: string, timestamp: string, cost: number) {
  const call = {
    provider: 'claude', model: 'claude-test',
    usage: { inputTokens: 10, outputTokens: 5, cacheCreationInputTokens: 0, cacheReadInputTokens: 0, cachedInputTokens: 0, reasoningTokens: 0, webSearchRequests: 0 },
    costUSD: cost, tools: [], mcpTools: [], skills: [], subagentTypes: [], hasAgentSpawn: false,
    hasPlanMode: false, speed: 'standard' as const, timestamp, bashCommands: [], deduplicationKey: id,
  }
  return {
    ...makeSession(id), firstTimestamp: timestamp, lastTimestamp: timestamp,
    totalCostUSD: cost, totalInputTokens: 10, totalOutputTokens: 5,
    turns: [{ userMessage: 'hi', assistantCalls: [call], timestamp, sessionId: id, category: 'coding' as const, retries: 0, hasEdits: false }],
  }
}

function makeProject(name: string, sessions: ReturnType<typeof makeSession>[]) {
  return {
    project: name,
    projectPath: name,
    sessions,
    totalCostUSD: sessions.reduce((s, x) => s + x.totalCostUSD, 0),
    totalApiCalls: sessions.reduce((s, x) => s + x.apiCalls, 0),
  }
}

function makeTui() {
  const stdin = new PassThrough() as PassThrough & NodeJS.ReadStream
  const stdout = new PassThrough() as PassThrough & NodeJS.WriteStream
  stdin.isTTY = true
  stdin.setRawMode = () => stdin
  stdin.ref = () => stdin
  stdin.unref = () => stdin
  stdout.isTTY = true
  stdout.columns = 120
  stdout.rows = 50
  return { stdin, stdout }
}

async function mountDashboard(stdin: PassThrough & NodeJS.ReadStream, stdout: PassThrough & NodeJS.WriteStream, options?: { indexPendingFiles?: number }) {
  const app = render(React.createElement(InteractiveDashboard, {
    initialProjects: [makeProject('proj', [makeSession('s1')])],
    initialPeriod: 'today',
    initialProvider: 'all',
    refreshSeconds: 0,
    windowColumns: 120,
    ...(options?.indexPendingFiles ? { initialIndexPendingFiles: options.indexPendingFiles } : {}),
  }), { stdin, stdout, debug: true, interactive: true, patchConsole: false })
  await app.waitUntilRenderFlush()
  return app
}

describe('InteractiveDashboard exit keystrokes (#1141)', () => {
  it('exits on a bare q keystroke', async () => {
    const { stdin, stdout } = makeTui()
    const app = await mountDashboard(stdin, stdout)
    onTestFinished(() => app.unmount())

    const exited = app.waitUntilExit()
    stdin.write('q')
    await expect(exited).resolves.toBeUndefined()
  })

  it('exits on a raw Ctrl+C keystroke at any moment after paint', async () => {
    const { stdin, stdout } = makeTui()
    const app = await mountDashboard(stdin, stdout)
    onTestFinished(() => app.unmount())

    const exited = app.waitUntilExit()
    // A raw 0x03 from the terminal: the same byte the kernel hands the TTY
    // when the user holds Control and presses 'c' (#1141). Ink's input
    // parser surfaces it as { input: 'c', key: { ctrl: true } }.
    stdin.write('\x03')
    await expect(exited).resolves.toBeUndefined()
  })
})

describe('InteractiveDashboard indexed-period reload races', () => {
  it('does not let an in-flight day reload overwrite a synchronous indexed period', async () => {
    const { stdin, stdout } = makeTui()
    const chunks: string[] = []
    stdout.on('data', chunk => chunks.push(stripAnsi(String(chunk))))
    const now = new Date().toISOString()
    const indexed = makeProject('indexed-week', [makeSessionWithCall('indexed', now, 7)])
    const history: DashboardHistoryIndex = {
      provider: 'all', normalizedProjects: [indexed],
      cache: { version: 29, savingsConfigHash: '', lastComputedDate: null, days: [], complete: true },
      planUsages: [], readyThrough: 'lifetime',
    }
    const app = render(React.createElement(InteractiveDashboard, {
      initialProjects: [indexed], initialPeriod: 'today', initialProvider: 'all',
      refreshSeconds: 0, windowColumns: 120, initialHistoryIndex: history,
    }), { stdin, stdout, debug: true, interactive: true, patchConsole: false })
    onTestFinished(() => app.unmount())
    await app.waitUntilRenderFlush()

    stdin.write('d')
    await app.waitUntilRenderFlush()
    chunks.length = 0
    stdin.write('2')
    await app.waitUntilRenderFlush()
    expect(chunks.join('')).toContain('indexed-week')

    const yesterday = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString()
    resolveNextParse([makeProject('stale-day', [makeSessionWithCall('stale', yesterday, 99)])])
    await new Promise(resolve => setTimeout(resolve, 25))
    await app.waitUntilRenderFlush()

    const rendered = chunks.join('')
    expect(rendered).toContain('indexed-week')
    expect(rendered).not.toContain('stale-day')
  })
})

describe('InteractiveDashboard quit feedback during the cold-start fill (#1143)', () => {
  const QUIT_STATUS = 'Finishing background index so the next launch starts warm - press q or Ctrl+C again to quit now'

  it('first q during an active fill does not exit and renders a status line', async () => {
    const { stdin, stdout } = makeTui()
    const chunks: string[] = []
    stdout.on('data', chunk => chunks.push(stripAnsi(String(chunk))))
    // non-zero deferred files -> `indexing` initializes to true, and the
    // parser mock above holds the fill in flight so the state cannot
    // resolve out from under the test.
    const app = await mountDashboard(stdin, stdout, { indexPendingFiles: 100 })
    onTestFinished(() => app.unmount())

    stdin.write('q')
    await app.waitUntilRenderFlush()

    const frame = chunks.join('')
    expect(frame).toContain(QUIT_STATUS)
    // Not yet exited: the test would hang forever on waitUntilExit() if it
    // had, so just make sure the app is still mounted by sending a no-op
    // keystroke and seeing it land without throwing.
    stdin.write('j')
    await app.waitUntilRenderFlush()
  })

  it('a second q during the drain exits through the same abrupt path', async () => {
    const { stdin, stdout } = makeTui()
    const app = await mountDashboard(stdin, stdout, { indexPendingFiles: 100 })
    onTestFinished(() => app.unmount())

    stdin.write('q')
    await app.waitUntilRenderFlush()

    const exited = app.waitUntilExit()
    stdin.write('q')
    await expect(exited).resolves.toBeUndefined()
  })

  it('Ctrl+C during the drain exits immediately, bypassing the confirmation', async () => {
    const { stdin, stdout } = makeTui()
    const app = await mountDashboard(stdin, stdout, { indexPendingFiles: 100 })
    onTestFinished(() => app.unmount())

    stdin.write('q')
    await app.waitUntilRenderFlush()
    // Second keystroke is the abrupt path. A raw 0x03 must work whether or
    // not the first q armed the confirmation, matching the #1109 kill-safe
    // exit.
    const exited = app.waitUntilExit()
    stdin.write('\x03')
    await expect(exited).resolves.toBeUndefined()
  })

  it('q with no fill active exits immediately and never renders the status line', async () => {
    const { stdin, stdout } = makeTui()
    const chunks: string[] = []
    stdout.on('data', chunk => chunks.push(stripAnsi(String(chunk))))
    const app = await mountDashboard(stdin, stdout)
    onTestFinished(() => app.unmount())

    const exited = app.waitUntilExit()
    stdin.write('q')
    await expect(exited).resolves.toBeUndefined()
    // Flush the unmount's final frame into the captured buffer first - a
    // teardown-frame render of the line would otherwise be invisible here.
    await new Promise(resolve => setTimeout(resolve, 50))
    // Belt-and-braces: nothing in the rendered history should mention the
    // "Finishing background index" line - no flicker on the no-fill path.
    expect(chunks.join('')).not.toContain(QUIT_STATUS)
  })
})
