import { describe, expect, it } from 'vitest'

import { resolveTurnSessionId } from './TurnDetail'

// The turn surface asked the content route for a run id. Every derived run is
// keyed `derived:<harness>:<session>` — 1,455 of 1,455 on the measured corpus —
// so the route 404'd for all of them and the page rendered "Session or turn
// content not found" with no context bands.
describe('resolveTurnSessionId', () => {
  const run = {
    executions: [
      { executionId: 'exec-1', sessionId: 'sess-1' },
      { executionId: 'exec-2', sessionId: 'sess-2' },
    ],
  }

  it('resolves the named execution to its session', () => {
    expect(resolveTurnSessionId(run, 'exec-2')).toBe('sess-2')
  })

  it('falls back to the run\'s first execution when the link names none', () => {
    // A turn reached from a finding carries no execution.
    expect(resolveTurnSessionId(run, undefined)).toBe('sess-1')
  })

  it('never returns a run id', () => {
    const derived = { executions: [{ executionId: 'exec-1', sessionId: 'sess-1' }] }

    expect(resolveTurnSessionId(derived, undefined)).not.toMatch(/^derived:/)
  })

  it('uses the execution id when the execution recorded no session', () => {
    const noSession = { executions: [{ executionId: 'exec-only' }] }

    expect(resolveTurnSessionId(noSession, 'exec-only')).toBe('exec-only')
  })

  it('keeps the requested execution when it is not in the run yet', () => {
    // The run query may not have resolved; the execution id is still the better
    // guess than the run id, since executions and sessions share their key.
    expect(resolveTurnSessionId(undefined, 'exec-9')).toBe('exec-9')
  })

  it('is undefined when nothing names an execution', () => {
    expect(resolveTurnSessionId(undefined, undefined)).toBeUndefined()
    expect(resolveTurnSessionId({ executions: [] }, undefined)).toBeUndefined()
  })
})
