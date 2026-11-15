// Per-file opt-in for tests that run code which probes the live KyberDash
// receiver (127.0.0.1:4318) as a side effect: the clean pause probe, the web
// dashboard's receiver poll, and the default OTLP sender.
//
// The stub answers "no receiver is listening" (a refused connection). That is
// exactly what those tests observed before the guard existed, when nothing ran
// on the port, so the assertions keep their original meaning. The stub is
// scoped to the file that calls it and restored after each test. Any other
// fetch to the receiver is still refused and recorded by the guard.
import { afterAll, beforeAll } from 'vitest'

import { answerLiveReceiver } from './network-guard.js'

export function useAbsentLiveReceiver(): void {
  // File-scoped: a receiver call can run in beforeAll or at collection time, before any beforeEach.
  let restore: (() => void) | undefined
  beforeAll(() => {
    restore = answerLiveReceiver(() =>
      Promise.reject(new TypeError('fetch failed: connect ECONNREFUSED 127.0.0.1:4318 (test double: no receiver)')),
    )
  })
  afterAll(() => {
    restore?.()
    restore = undefined
  })
}
