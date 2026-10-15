// Review follow-ups for the subagent tree (PR 367): nesting is recursive, and a rollup never
// counts a child twice when the parent's own counters already include it.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

type Seed = {
  id: string
  parent?: string
  turns?: number
  cost?: number
  includedInParent?: boolean
  started?: string
}

function seed(store: CanonStore, input: Seed): void {
  const summary: Record<string, unknown> = {
    turn_count: input.turns ?? 1,
    request_count: input.turns ?? 1,
    total_input: (input.turns ?? 1) * 100,
    total_output: (input.turns ?? 1) * 10,
    models: ['m'],
    cost:
      input.cost === undefined
        ? { basis: 'unknown', status: 'no_rate' }
        : { basis: 'published', status: 'priced', value: input.cost, currency: 'USD' },
    ...(input.includedInParent ? { included_in_parent: true } : {}),
  }
  store
    .getDatabase()
    .prepare(
      `INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload)
       VALUES (?, 'claude-code', ?, ?, ?, NULL, 'r', 'b', ?, ?, ?)`,
    )
    .run(
      input.id,
      input.id,
      input.parent ? 1 : 0,
      input.parent ?? null,
      input.started ?? '2026-10-14T10:00:00.000Z',
      '2026-10-14T10:30:00.000Z',
      JSON.stringify({ id: input.id, harness: 'claude-code', summary, problems: [] }),
    )
}

describe('subagent tree and rollup', () => {
  let dir: string
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'kyber-bridge-review-'))
    store = new CanonStore(join(dir, 'canon.db'))
    bridge = new KyberBridge({ store, canonPath: join(dir, 'canon.db') })
  })

  afterEach(() => {
    bridge.close?.()
    store.close()
    rmSync(dir, { recursive: true, force: true })
  })

  it('keeps a grandchild: A <- B <- C nests two deep and rolls up leaf-first', () => {
    seed(store, { id: 'A', turns: 2, cost: 1 })
    seed(store, { id: 'B', parent: 'A', turns: 3, cost: 2 })
    seed(store, { id: 'C', parent: 'B', turns: 4, cost: 4 })

    const [root, ...rest] = bridge.listSessions()

    expect(rest).toEqual([])
    expect(root?.session_id).toBe('A')
    const b = root?.subagents?.[0]
    expect(b?.session_id).toBe('B')
    expect(b?.subagents?.map((s) => s.session_id)).toEqual(['C'])
    expect(b?.rollup).toMatchObject({ turn_count: 7, cost_usd: 6 })
    expect(root?.rollup).toMatchObject({ turn_count: 9, cost_usd: 7 })
  })

  it('does not lose sessions to a parent cycle', () => {
    seed(store, { id: 'X', parent: 'Y' })
    seed(store, { id: 'Y', parent: 'X' })

    const ids = bridge.listSessions().map((s) => s.session_id)

    expect(ids.length).toBeGreaterThan(0)
  })

  it('does not count a child twice when the parent already includes it', () => {
    // The parent's 3 turns and $3 already contain the child's 1 turn and $1.
    seed(store, { id: 'P', turns: 3, cost: 3 })
    seed(store, { id: 'child', parent: 'P', turns: 1, cost: 1, includedInParent: true })

    const [root] = bridge.listSessions()

    expect(root?.rollup).toMatchObject({ turn_count: 3, request_count: 3, total_input: 300, cost_usd: 3 })
    // The child still shows its own figures.
    expect(root?.subagents?.[0]?.turn_count).toBe(1)
  })

  it('sums an independent child but not an included one beside it', () => {
    seed(store, { id: 'P', turns: 3, cost: 3 })
    seed(store, { id: 'inc', parent: 'P', turns: 1, cost: 1, includedInParent: true, started: '2026-10-14T10:01:00.000Z' })
    seed(store, { id: 'sep', parent: 'P', turns: 2, cost: 5, started: '2026-10-14T10:02:00.000Z' })

    const [root] = bridge.listSessions()

    expect(root?.rollup).toMatchObject({ turn_count: 5, cost_usd: 8 })
  })

  it('leaves cost unmeasured when neither side has one', () => {
    seed(store, { id: 'P', turns: 1 })
    seed(store, { id: 'child', parent: 'P', turns: 1, includedInParent: false })

    const [root] = bridge.listSessions()

    expect(root?.rollup?.cost_usd).toBeNull()
  })
})
