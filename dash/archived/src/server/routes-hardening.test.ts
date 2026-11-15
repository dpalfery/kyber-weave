// Hardening of the shared JSON-body routes (issue #319 review, items 1, 5 and 6).
//
// Four defects share one file because they share one mechanism - the body handler and
// the oversize answer both run off the `req` stream, outside `createServer`'s try/catch:
//
//   1. A settings PUT reaches `store.setMetadata` synchronously, so a sqlite failure
//      (SQLITE_BUSY, a closed handle, a full disk) became an uncaught exception and
//      killed the web server. It must answer 500 with a fixed body and survive.
//   5. `req.destroy()` fired in the same tick as the 413 `res.end()`, so the client saw
//      ECONNRESET instead of the answer. Asserted here over a REAL socket, because that
//      is the only way the reset is observable at all.
//   6. `hostedElsewhere` has to be answered 409 from the status, before the host is asked
//      to run anything, or the route starts a job it knows will be declined.
//   8. The clean route parsed its body OUTSIDE any guard, from an `async` `req.on('end')`
//      listener nobody awaits: a throw there was an unhandled rejection, and the guard
//      that prevents exactly that (`runGuardedBodyHandler`) is where the parse now runs.
import { createServer, request as httpRequest, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { JobOutcome } from '../jobs/host.js'
import type { KyberBridge } from './bridge.js'
import { callRoute, makeDeps } from './testing.js'
import { cleanBodyParser, handleKyberRequest, runGuardedBodyHandler } from './routes.js'

const servers: Server[] = []

afterEach(async () => {
  vi.restoreAllMocks()
  for (const server of servers.splice(0)) {
    if (server.listening) await new Promise<void>((done) => server.close(() => done()))
  }
})

/** A real loopback server over the real route handler, so sockets behave like sockets. */
async function listeningServer(deps: ReturnType<typeof makeDeps>): Promise<string> {
  const server = createServer((req, res) => {
    handleKyberRequest(
      req,
      res,
      new URL(req.url ?? '/', 'http://localhost'),
      {} as KyberBridge,
      deps,
    )
  })
  servers.push(server)
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done))
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`
}

describe('a body handler that throws answers 500 and the server survives', () => {
  it('answers a settings PUT whose store write fails, with a fixed body and no host text', async () => {
    const deps = makeDeps()
    vi.spyOn(deps.store, 'setMetadata').mockImplementation(() => {
      throw new Error('SQLITE_BUSY: database is locked (/Users/secret/canon.db)')
    })
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {})

    const { status, body, raw } = await callRoute('PUT', '/api/kyber/settings', {
      deps,
      payload: { jobsPaused: true },
    })

    expect(status).toBe(500)
    expect(body).toEqual({ error: 'Internal server error' })
    // The sqlite message names a real path on this machine and stays in the operator's
    // terminal, which is where the console.error above proves it went.
    expect(raw).not.toContain('SQLITE_BUSY')
    expect(raw).not.toContain('/Users/secret')
    expect(errors).toHaveBeenCalled()

    // The process is still serving: the next request is answered normally.
    const next = await callRoute('GET', '/api/kyber/settings', { deps })
    expect(next.status).toBe(200)
    expect(next.body).toMatchObject({ jobsPaused: false })
  })

  it('answers 500 for a refresh body handler that rejects, not just throws', async () => {
    const deps = makeDeps()
    vi.spyOn(console, 'error').mockImplementation(() => {})
    deps.jobHost.runNow = () => {
      // `sendJobStarted` owns this promise, so a rejection there is a 500 of its own;
      // what is under test is that the surrounding invoke path does not escape.
      throw new Error('lease store gone (/Users/secret/jobs.lock)')
    }

    const { status, raw } = await callRoute('POST', '/api/kyber/refresh', {
      deps,
      payload: { surface: 'tray' },
    })

    expect(status).toBe(500)
    expect(raw).not.toContain('/Users/secret')
  })
})

describe('an oversized body is answered, not reset', () => {
  it('delivers the 413 body over a real socket instead of resetting the connection', async () => {
    const deps = makeDeps()
    const base = await listeningServer(deps)

    const answer = await new Promise<{ status: number; text: string }>((resolve, reject) => {
      // Never `end()` here: a client that finishes its body lets the server parse it and
      // answer 400 for malformed JSON, which is not what is under test. Kept open and
      // written to until the answer arrives, the upload is still in flight when the 413
      // goes out - the case the old immediate destroy turned into ECONNRESET, and a client
      // that had already finished writing would not show it at all.
      const keepWriting = setInterval(() => {
        if (!req.writableEnded) req.write('x'.repeat(40_000))
      }, 25)
      const settle = (value: { status: number; text: string }): void => {
        clearInterval(keepWriting)
        resolve(value)
      }
      const req = httpRequest(
        `${base}/api/kyber/settings`,
        { method: 'PUT', headers: { 'content-type': 'application/json' } },
        (res) => {
          let text = ''
          res.setEncoding('utf8')
          res.on('data', (chunk: string) => {
            text += chunk
          })
          res.on('end', () => settle({ status: res.statusCode ?? 0, text }))
        },
      )
      // The server answers and closes while the body is still going out, so the client's
      // own write may fail - that is the peer closing, not the assertion under test.
      req.on('error', () => { /* resolved or rejected by the response handler */ })
      req.write('x'.repeat(40_000))
      setTimeout(() => reject(new Error('no response at all: the socket was reset')), 2_000).unref()
    })

    expect(answer.status).toBe(413)
    expect(JSON.parse(answer.text)).toMatchObject({ error: expect.stringContaining('exceeds') })
  })

  it('closes the connection on the oversize answer', async () => {
    const deps = makeDeps()
    const base = await listeningServer(deps)

    const connection = await new Promise<string | undefined>((resolve, reject) => {
      const req = httpRequest(
        `${base}/api/kyber/settings`,
        { method: 'PUT', headers: { 'content-type': 'application/json' } },
        (res) => {
          resolve(res.headers.connection)
          res.resume()
        },
      )
      req.on('error', () => { /* see above */ })
      req.write('x'.repeat(40_000))
      req.write('x'.repeat(40_000))
      req.end()
      setTimeout(() => reject(new Error('no response')), 2_000).unref()
    })

    expect(connection).toBe('close')
  })
})

describe('hosted-elsewhere is answered from the status, before anything is started', () => {
  it.each(['/api/kyber/refresh', '/api/kyber/import-history'] as const)(
    'answers 409 on %s without calling the host',
    async (path) => {
      const deps = makeDeps()
      // The host has not lost a lease attempt yet, so its own answer would come only
      // after acquiring one - which is the whole point of asking the status instead.
      deps.jobHost.status = {
        ...deps.jobHost.status,
        state: 'idle',
        hostedElsewhere: true,
        nextDueAt: null,
      }

      const { status, body } = await callRoute('POST', path, {
        deps,
        payload: path.endsWith('refresh') ? { surface: 'tray' } : { weeks: 2 },
      })

      expect(status).toBe(409)
      expect(body).toHaveProperty('error')
      expect(deps.jobHost.runNowCalls).toEqual([])
      expect(deps.jobHost.runJobCalls).toEqual([])
    },
  )

  it('still answers 202 promptly when the host starts a job and stays pending', async () => {
    const deps = makeDeps() // runNow never settles: the job is still running

    const started = Date.now()
    const { status } = await callRoute('POST', '/api/kyber/refresh', {
      deps,
      payload: { surface: 'web' },
    })

    expect(status).toBe(202)
    // Bounded: the grace window is a local round trip, not a wait for the refresh.
    expect(Date.now() - started).toBeLessThan(200)
  })
})

/** Post/get over a real socket, so a real `ServerResponse` is what the route writes to. */
function requestJson(
  base: string,
  method: 'POST' | 'GET',
  path: string,
  payload?: unknown,
): Promise<{ status: number; text: string }> {
  return new Promise((resolve, reject) => {
    const req = httpRequest(
      `${base}${path}`,
      {
        method,
        headers: payload === undefined ? {} : { 'content-type': 'application/json' },
      },
      (res) => {
        let text = ''
        res.setEncoding('utf8')
        res.on('data', (chunk: string) => {
          text += chunk
        })
        res.on('end', () => resolve({ status: res.statusCode ?? 0, text }))
      },
    )
    req.on('error', reject)
    if (payload !== undefined) req.end(JSON.stringify(payload))
    else req.end()
    setTimeout(() => reject(new Error(`no answer on ${method} ${path}`)), 2_000).unref()
  })
}

/**
 * Collect unhandled rejections for the duration of `body`. Vitest installs its own
 * handler, so one that gets here is recorded rather than swallowed: an escaping rejection
 * is the defect, and the process staying up says nothing about it.
 */
async function withUnhandledRejectionWatch<T>(body: () => Promise<T>): Promise<{ result: T; rejections: unknown[] }> {
  const rejections: unknown[] = []
  const listener = (reason: unknown): void => {
    rejections.push(reason)
  }
  process.on('unhandledRejection', listener)
  try {
    const result = await body()
    // A rejection surfacing in the microtask/macrotask drain after the answer, not during it.
    await new Promise((resolve) => setTimeout(resolve, 50))
    return { result, rejections }
  } finally {
    process.off('unhandledRejection', listener)
  }
}

describe('a clean body whose parser throws is answered, not thrown out of the listener', () => {
  it('answers a fixed 500 and leaves the server serving the next request', async () => {
    const deps = makeDeps()
    const base = await listeningServer(deps)
    vi.spyOn(console, 'error').mockImplementation(() => {})
    // The parser's own JSON guard turns anything a client can send into a 400, so the
    // throw is injected: the route has to survive it becoming reachable.
    vi.spyOn(cleanBodyParser, 'parse').mockImplementation(() => {
      throw new Error('Unexpected token } in JSON at position 4 (/Users/secret/canon.db)')
    })

    const { result: answer, rejections } = await withUnhandledRejectionWatch(async () =>
      requestJson(base, 'POST', '/api/kyber/clean', { all: true, confirm: true }))

    expect(answer.status).toBe(500)
    expect(JSON.parse(answer.text)).toEqual({ error: 'Clean failed' })
    // The parser's message names a value from this machine; it stays in the operator log.
    expect(answer.text).not.toContain('/Users/secret')
    expect(rejections).toEqual([])
    vi.restoreAllMocks()

    // The process is still serving: the next request is answered normally.
    const next = await requestJson(base, 'GET', '/api/kyber/settings')
    expect(next.status).toBe(200)
    expect(JSON.parse(next.text)).toMatchObject({ jobsPaused: false })
  })

  it('still answers 400 for a body the parser rejects, through the same guard', async () => {
    const deps = makeDeps()
    const bridge = { cleanDatabase: async () => ({}) } as unknown as KyberBridge

    const { status, body } = await callRoute('POST', '/api/kyber/clean', {
      bridge,
      deps,
      payload: { all: true, confirm: false },
    })

    expect(status).toBe(400)
    expect(body).toEqual({ error: 'Invalid clean request payload' })
  })
})

describe('a body handler that answers and then rejects does not write twice', () => {
  it('keeps the 202, and the late rejection neither escapes nor writes again', async () => {
    // The exact shape the guard exists for: the client has its answer, and the handler
    // rejects afterwards. A real socket, because the second write only throws against a
    // real finished response.
    const writes: number[] = []
    const server = createServer((_req, res) => {
      runGuardedBodyHandler(
        res,
        () => {
          res.writeHead(202, { 'content-type': 'application/json' })
          res.end(JSON.stringify({ accepted: true }))
          writes.push(202)
          return Promise.reject(new Error('job host vanished (/Users/secret/jobs.lock)'))
        },
        () => {
          writes.push(500)
          res.writeHead(500, { 'content-type': 'application/json' })
          res.end(JSON.stringify({ error: 'Internal server error' }))
        },
      )
    })
    servers.push(server)
    await new Promise<void>((done) => server.listen(0, '127.0.0.1', done))
    const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
    vi.spyOn(console, 'error').mockImplementation(() => {})

    const { result: answer, rejections } = await withUnhandledRejectionWatch(() =>
      requestJson(base, 'POST', '/api/kyber/refresh'))

    // The answer the client already has stands: nothing is owed it, and the failure that
    // produced it belongs in the operator's terminal, not on the wire.
    expect(answer.status).toBe(202)
    expect(JSON.parse(answer.text)).toEqual({ accepted: true })
    expect(writes).toEqual([202])
    expect(rejections).toEqual([])
  })

  it('answers 500 for a rejection that arrives before any answer', async () => {
    const server = createServer((_req, res) => {
      runGuardedBodyHandler(res, () => Promise.reject(new Error('boom')), () => {
        res.writeHead(500, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ error: 'Internal server error' }))
      })
    })
    servers.push(server)
    await new Promise<void>((done) => server.listen(0, '127.0.0.1', done))
    const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
    vi.spyOn(console, 'error').mockImplementation(() => {})

    const { result: answer, rejections } = await withUnhandledRejectionWatch(() =>
      requestJson(base, 'POST', '/api/kyber/refresh'))

    expect(answer.status).toBe(500)
    expect(JSON.parse(answer.text)).toEqual({ error: 'Internal server error' })
    expect(rejections).toEqual([])
  })
})

/**
 * A POST with no timeout guard of its own. `requestJson` arms a 2s rejection timer, and
 * under the fake clock below that timer is one of the handles being counted - the grace
 * timer has to be the only thing on the clock for these assertions to mean anything.
 */
function postWithoutGuard(
  base: string,
  path: string,
  payload: unknown,
): Promise<{ status: number; text: string }> {
  return new Promise((resolve, reject) => {
    const req = httpRequest(
      `${base}${path}`,
      { method: 'POST', headers: { 'content-type': 'application/json' } },
      (res) => {
        let text = ''
        res.setEncoding('utf8')
        res.on('data', (chunk: string) => {
          text += chunk
        })
        res.on('end', () => resolve({ status: res.statusCode ?? 0, text }))
      },
    )
    req.on('error', reject)
    req.end(JSON.stringify(payload))
  })
}

/**
 * Waits, on real event-loop turns and never on the faked clock, for `count` handles to be
 * armed. `setImmediate` is not faked here, so the loop turns while `setTimeout` does not.
 */
async function untilArmed(count: number): Promise<void> {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    if (vi.getTimerCount() === count) return
    await new Promise<void>((resolve) => setImmediate(resolve))
  }
  throw new Error(`never saw ${count} armed timer(s); saw ${vi.getTimerCount()}`)
}

/**
 * The grace timer on a job trigger is a BOUND, not a second answer: it exists so a job
 * that has not reported back within `JOB_OUTCOME_GRACE_MS` still gets the client its 202.
 * When the real answer arrives first the timer has nothing left to do, and `answered`
 * already stops it writing - but leaving it armed keeps a live handle per answered request
 * for the rest of the process.
 */
describe('the job-trigger grace timer lives exactly as long as the request', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('is armed while the request is in flight and cleared once the host answers first', async () => {
    const deps = makeDeps()
    // Held open so the timer is observable while the request is still running; released
    // once it is armed.
    let settle!: (outcome: JobOutcome) => void
    deps.jobHost.nextOutcome = new Promise<JobOutcome>((resolve) => {
      settle = resolve
    })
    const base = await listeningServer(deps)

    // Only these two faked: the socket, the server and this test's own waits are real
    // timers elsewhere, and faking them would hang the request rather than the clock.
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const answer = postWithoutGuard(base, '/api/kyber/refresh', { surface: 'web' })

    await untilArmed(1) // the grace timer, and nothing else
    // 'busy' is a fast decline: the host answers inside the grace window, which is the
    // only ordering in which a stale timer can still be armed when the request is over.
    settle({ outcome: 'busy' })

    const settled = await answer
    expect(settled.status).toBe(409)
    expect(JSON.parse(settled.text)).toEqual({ error: 'A job is already running' })
    // Cleared, not merely guarded: no handle outlives the request it bounded.
    expect(vi.getTimerCount()).toBe(0)

    // And nothing writes later: advancing past the grace window is silent.
    vi.advanceTimersByTime(1_000)
    expect(vi.getTimerCount()).toBe(0)
  })

  it('still answers 202 from the timer when the host stays pending', async () => {
    const deps = makeDeps() // runNow never settles: the job is still running
    const base = await listeningServer(deps)

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const pending = postWithoutGuard(base, '/api/kyber/refresh', { surface: 'web' })
    await untilArmed(1)
    vi.advanceTimersByTime(25)
    // The grace timer fired, and having answered it holds nothing further.
    expect(vi.getTimerCount()).toBe(0)

    // Back to real timers before awaiting the socket: the client needs a real event loop.
    vi.useRealTimers()
    const answer = await pending
    expect(answer.status).toBe(202)
    expect(JSON.parse(answer.text)).toEqual({ accepted: true })
  })
})
