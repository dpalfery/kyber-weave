// Vitest setup file (T1, D10): installs the live-receiver fetch guard for every
// test file, and fails the test that reached the receiver.
//
// The guard refuses the fetch, but a host that swallows delivery errors (the
// plugin never fails its host) would hide the refusal. So every recorded hit
// becomes a failure here, in the test that caused it. Tests that stub fetch
// themselves are unaffected. Tests that inject `fetchFn` never reach the guard.
import { afterAll, afterEach } from 'vitest'

import { installNetworkGuard } from './network-guard.js'

const guard = installNetworkGuard()

function hitsMessage(hits: readonly string[]): string {
  return (
    `network guard: ${hits.length} fetch(es) reached the live KyberDash receiver ` +
    `(127.0.0.1:4318 / localhost:4318): ${hits.join(', ')}. Inject fetchFn instead.`
  )
}

afterEach(() => {
  const hits = [...guard.hits]
  guard.clear()
  if (hits.length > 0) throw new Error(hitsMessage(hits))
})

afterAll(() => {
  const hits = [...guard.hits]
  guard.clear()
  if (hits.length > 0) throw new Error(hitsMessage(hits))
})
