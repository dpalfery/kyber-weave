import { describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'

const row = (label: string) => ({
  sessionId: 'sess-1',
  harness: 'copilot',
  label,
  payload: { id: 'sess-1', label },
})

describe('upsertSession and transcript sync progress', () => {
  it('keeps the sync columns when a rebuild rewrites the derived row', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession(row('first'))
    store
      .getDatabase()
      .prepare(
        `UPDATE session SET transcript_path = ?, transcript_offset = ?, sync_status = ?, synced_at = ?, settled_at = ?
         WHERE session_id = ?`,
      )
      .run('/tmp/t.jsonl', 1024, 'synced', '2026-10-15T00:00:00.000Z', '2026-10-15T01:00:00.000Z', 'sess-1')

    store.upsertSession(row('second'))

    const after = store
      .getDatabase()
      .prepare('SELECT label, transcript_path, transcript_offset, sync_status, synced_at, settled_at FROM session')
      .get() as Record<string, unknown>
    expect(after).toEqual({
      label: 'second',
      transcript_path: '/tmp/t.jsonl',
      transcript_offset: 1024,
      sync_status: 'synced',
      synced_at: '2026-10-15T00:00:00.000Z',
      settled_at: '2026-10-15T01:00:00.000Z',
    })
    store.close()
  })

  it('still defaults a brand-new row to pending with offset 0', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession(row('new'))
    const after = store
      .getDatabase()
      .prepare('SELECT sync_status, transcript_offset FROM session')
      .get() as Record<string, unknown>
    expect(after).toEqual({ sync_status: 'pending', transcript_offset: 0 })
    store.close()
  })
})
