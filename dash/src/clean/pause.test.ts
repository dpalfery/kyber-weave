// Pause-port tests for a database clean (issue #312, plan T3 RED): probe,
// pause, and resume the loopback OTLP receiver around a wipe. The receiver
// module itself is tested separately (`receiver.test.ts`); these tests pin
// the port's contract — free port or foreign service proceeds without
// pausing, our receiver pauses, and a pause refusal fails closed.
import { afterEach, describe, expect, it } from 'vitest'

import {
  OTLP_ADMIN_PAUSE_PATH,
  OTLP_ADMIN_RESUME_PATH,
  OTLP_HEALTHZ_PATH,
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  OtlpReceiver,
} from '../otel/receiver.js'
import { pauseReceiver, resumeReceiver, CLEAN_PAUSE_LEASE_MS } from './pause.js'

const started: OtlpReceiver[] = []

afterEach(async () => {
  for (const receiver of started.splice(0)) await receiver.stop().catch(() => {})
})

async function startReceiver(): Promise<string> {
  const receiver = new OtlpReceiver({ port: 0 })
  await receiver.start()
  started.push(receiver)
  return `http://127.0.0.1:${receiver.port}`
}

describe('receiver pause port (issue #312)', () => {
  it('pauses and resumes our receiver', async () => {
    const baseUrl = await startReceiver()

    expect(await pauseReceiver(baseUrl)).toEqual({ paused: true })
    expect((await fetch(`${baseUrl}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}',
    })).status).toBe(503)

    await resumeReceiver(baseUrl)
    expect((await fetch(`${baseUrl}${OTLP_HEALTHZ_PATH}`)).status).toBe(200)
  })

  it('proceeds without pausing when the port is free', async () => {
    const free = new OtlpReceiver({ port: 0 })
    await free.start()
    const port = free.port
    await free.stop()

    expect(await pauseReceiver(`http://127.0.0.1:${port}`)).toEqual({ paused: false })
    await resumeReceiver(`http://127.0.0.1:${port}`)
  })

  it('proceeds without pausing when the port holds a foreign service', async () => {
    const { createServer } = await import('node:http')
    const foreign = createServer((req, res) => {
      if (req.url === OTLP_HEALTHZ_PATH) {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ service: 'something-else' }))
        return
      }
      res.writeHead(404, { 'content-type': 'application/json' })
      res.end('{}')
    })
    await new Promise<void>((resolve) => foreign.listen(0, '127.0.0.1', resolve))
    const address = foreign.address()
    const port = typeof address === 'object' && address !== null ? address.port : 0
    try {
      expect(await pauseReceiver(`http://127.0.0.1:${port}`)).toEqual({ paused: false })
    } finally {
      await new Promise<void>((resolve) => foreign.close(() => resolve()))
    }
  })

  it('resume is best-effort: a dead receiver never fails the clean', async () => {
    const free = new OtlpReceiver({ port: 0 })
    await free.start()
    const port = free.port
    await free.stop()

    await resumeReceiver(`http://127.0.0.1:${port}`)
  })

  it('fails closed when our receiver answers but refuses the pause', async () => {
    // A receiver that predates the admin routes identifies as ours but 404s
    // the pause path — the clean must not proceed.
    const { createServer } = await import('node:http')
    const shim = createServer((req, res) => {
      if (req.url === OTLP_HEALTHZ_PATH) {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-test' }))
        return
      }
      if (req.url === OTLP_ADMIN_PAUSE_PATH || req.url === OTLP_ADMIN_RESUME_PATH) {
        res.writeHead(404, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ error: { code: 'OTLP_NOT_FOUND' } }))
        return
      }
      if (req.url === OTLP_TRACES_PATH || req.url === OTLP_LOGS_PATH) {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
        return
      }
      res.writeHead(404, { 'content-type': 'application/json' })
      res.end('{}')
    })
    await new Promise<void>((resolve) => shim.listen(0, '127.0.0.1', resolve))
    const address = shim.address()
    const port = typeof address === 'object' && address !== null ? address.port : 0
    try {
      await expect(pauseReceiver(`http://127.0.0.1:${port}`)).rejects.toThrow(/refused|404|HTTP 404/)
    } finally {
      await new Promise<void>((resolve) => shim.close(() => resolve()))
    }
  })

  it('fails fast when our receiver wedges on the pause path (issue #312 F7)', async () => {
    // A hung receiver accepts the socket but never answers: the pause fetch
    // must time out instead of hanging the clean forever before any row is
    // wiped. The hang is simulated without touching fetch internals — the
    // shim identifies as ours on healthz instantly, then stalls the pause.
    const { createServer } = await import('node:http')
    const shim = createServer((req, res) => {
      if (req.url === OTLP_HEALTHZ_PATH) {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-test' }))
        return
      }
      // Stall: hold the pause connection open without responding.
    })
    await new Promise<void>((resolve) => shim.listen(0, '127.0.0.1', resolve))
    const address = shim.address()
    const port = typeof address === 'object' && address !== null ? address.port : 0
    try {
      const start = Date.now()
      await expect(
        pauseReceiver(`http://127.0.0.1:${port}`, CLEAN_PAUSE_LEASE_MS, 250),
      ).rejects.toThrow(/pause request failed|TimeoutError|aborted/i)
      expect(Date.now() - start).toBeLessThan(10_000)
    } finally {
      await new Promise<void>((resolve) => shim.close(() => resolve()))
    }
  })

  it('fails fast when the healthz probe wedges (issue #312 F7)', async () => {
    // The port accepts connections but never answers healthz: the probe must
    // time out and proceed without pausing rather than hang the clean.
    const { createServer } = await import('node:net')
    const stall = createServer((socket) => {
      socket.resume()
    })
    await new Promise<void>((resolve) => stall.listen(0, '127.0.0.1', resolve))
    const address = stall.address()
    const port = typeof address === 'object' && address !== null ? address.port : 0
    try {
      const start = Date.now()
      expect(await pauseReceiver(`http://127.0.0.1:${port}`, CLEAN_PAUSE_LEASE_MS, 250)).toEqual({
        paused: false,
      })
      expect(Date.now() - start).toBeLessThan(10_000)
    } finally {
      await new Promise<void>((resolve) => stall.close(() => resolve()))
    }
  })
})
