// @vitest-environment jsdom

/**
 * The Maintenance panel: pause/resume of scheduled jobs and the manual folder
 * history import.
 *
 * Architecture rule R1 - the tray is a display layer. This component renders
 * `ViewState.jobs` and calls the handlers it is given; it holds no schedule and
 * no job logic. Pause means ONLY scheduled jobs pause, so every manual action
 * stays usable while paused. Failure it prevents: a "paused" tray that greys
 * out Refresh now, Import or Clean and strands a user with stale data.
 */

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { Maintenance } from './Maintenance'
import type { ViewState } from '../viewState'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

type Jobs = NonNullable<ViewState['jobs']>

const JOBS: Jobs = {
  refresh: {
    state: 'idle',
    lastSuccessAt: '2026-09-19T11:00:00.000Z',
    lastFailure: null,
    nextDueAt: '2026-09-19T12:05:00.000Z',
  },
  paused: false,
  storeGeneration: 3,
}

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.appendChild(container)
})

afterEach(async () => {
  await act(async () => {
    root.unmount()
  })
  container.remove()
})

async function mount(props: {
  jobs: Jobs | null
  harness?: string
  onSetSharedSettings?: (patch: Record<string, unknown>) => void
  onImportFolderHistory?: (request: { weeks: number; harness?: string }) => void
}): Promise<void> {
  await act(async () => {
    root = createRoot(container)
    root.render(
      <Maintenance
        jobs={props.jobs}
        harness={props.harness ?? 'all'}
        onSetSharedSettings={props.onSetSharedSettings ?? (() => {})}
        onImportFolderHistory={props.onImportFolderHistory ?? (() => {})}
      />,
    )
  })
}

function byId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
  return container.querySelector<T>(`[data-testid="${testId}"]`)
}

async function click(testId: string): Promise<void> {
  await act(async () => {
    byId(testId)?.click()
  })
}

/** React tracks the native value setter, so a plain `.value =` is swallowed. */
async function typeInto(testId: string, value: string): Promise<void> {
  const input = byId<HTMLInputElement>(testId)!
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

describe('Maintenance pause control', () => {
  it('pauses scheduled jobs by patching jobsPaused true', async () => {
    const onSetSharedSettings = vi.fn()
    await mount({ jobs: JOBS, onSetSharedSettings })

    expect(byId('pause-toggle')?.textContent).toMatch(/pause/i)
    await click('pause-toggle')

    expect(onSetSharedSettings).toHaveBeenCalledTimes(1)
    expect(onSetSharedSettings).toHaveBeenCalledWith({ jobsPaused: true })
  })

  it('resumes by patching jobsPaused false when the server says paused', async () => {
    const onSetSharedSettings = vi.fn()
    await mount({ jobs: { ...JOBS, paused: true }, onSetSharedSettings })

    expect(byId('pause-toggle')?.textContent).toMatch(/resume/i)
    await click('pause-toggle')

    expect(onSetSharedSettings).toHaveBeenCalledWith({ jobsPaused: false })
  })

  it('sends only the jobsPaused field so it cannot overwrite other shared settings', async () => {
    const onSetSharedSettings = vi.fn()
    await mount({ jobs: JOBS, onSetSharedSettings })
    await click('pause-toggle')

    expect(Object.keys(onSetSharedSettings.mock.calls[0]![0] as object)).toEqual(['jobsPaused'])
  })

  it('states that only scheduled jobs stop and manual actions, OTLP ingest and retention continue', async () => {
    await mount({ jobs: { ...JOBS, paused: true } })

    const text = (byId('jobs-paused-state')?.textContent ?? '').toLowerCase()
    expect(text).toContain('only scheduled jobs')
    expect(text).toContain('manual')
    expect(text).toContain('otlp')
    expect(text).toContain('retention')
  })

  it('does not show the paused explanation while jobs are running', async () => {
    await mount({ jobs: JOBS })

    expect(byId('jobs-paused-state')).toBeNull()
    expect(byId('jobs-state')?.getAttribute('data-paused')).toBe('false')
  })

  it('keeps Import folder history enabled while paused', async () => {
    await mount({ jobs: { ...JOBS, paused: true } })

    expect(byId<HTMLButtonElement>('import-folder-history')?.disabled).toBe(false)
  })

  it('does not claim a pause state when the core reports no jobs', async () => {
    await mount({ jobs: null })

    expect(byId('jobs-state')?.getAttribute('data-paused')).toBe('unknown')
  })
})

describe('Maintenance import folder history', () => {
  it('imports one week by default for all harnesses, without a harness field', async () => {
    const onImportFolderHistory = vi.fn()
    await mount({ jobs: JOBS, harness: 'all', onImportFolderHistory })

    expect(byId<HTMLInputElement>('import-weeks')?.value).toBe('1')
    await click('import-folder-history')

    expect(onImportFolderHistory).toHaveBeenCalledTimes(1)
    const request = onImportFolderHistory.mock.calls[0]![0] as Record<string, unknown>
    expect(request).toEqual({ weeks: 1 })
    expect('harness' in request).toBe(false)
  })

  it('scopes the import to the selected harness', async () => {
    const onImportFolderHistory = vi.fn()
    await mount({ jobs: JOBS, harness: 'cursor', onImportFolderHistory })
    await typeInto('import-weeks', '4')
    await click('import-folder-history')

    expect(onImportFolderHistory).toHaveBeenCalledWith({ weeks: 4, harness: 'cursor' })
  })

  it.each(['1', '52'])('accepts the boundary of %s weeks', async (weeks) => {
    const onImportFolderHistory = vi.fn()
    await mount({ jobs: JOBS, onImportFolderHistory })
    await typeInto('import-weeks', weeks)
    await click('import-folder-history')

    expect(onImportFolderHistory).toHaveBeenCalledWith({ weeks: Number(weeks) })
  })

  it.each(['0', '53', '-1', '1.5', ''])(
    'blocks %j weeks in the UI and never invokes the import',
    async (weeks) => {
      const onImportFolderHistory = vi.fn()
      await mount({ jobs: JOBS, onImportFolderHistory })
      await typeInto('import-weeks', weeks)
      await click('import-folder-history')

      expect(onImportFolderHistory).not.toHaveBeenCalled()
      expect(byId('import-weeks-error')?.textContent ?? '').toMatch(/1.{1,6}52/)
    },
  )

  it('declares the 1..52 bounds on the weeks input', async () => {
    await mount({ jobs: JOBS })

    const input = byId<HTMLInputElement>('import-weeks')!
    expect(input.min).toBe('1')
    expect(input.max).toBe('52')
  })
})
