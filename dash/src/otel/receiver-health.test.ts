// Receiver health contract: the five-minute error window, `lastAcceptedAt` on persistence
// only, and the extended `/healthz` body the main process polls. The approved indicator
// rule lives in `jobs/receiver-host.ts` (`isReceiverHealthy`); this file proves the facts
// it reads are real: rejections and persistence failures enter the window, health probes
// and paused-503s do not, and receipt alone never moves the accepted timestamp.

import { afterAll, describe, expect, it } from 'vitest'

import {
  MAX_RECEIVER_ERROR_SAMPLES,
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  OtlpReceiver,
  healthBodyForPeer,
  RECEIVER_ERROR_WINDOW_MS,
} from './receiver.js'
import { IngestWriter } from './writer.js'

const started: OtlpReceiver[] = []

afterAll(async () => {
  await Promise.all(started.map((receiver) => receiver.stop()))
})

async function startReceiver(opts: ConstructorParameters<typeof OtlpReceiver>[0] = {}): Promise<{
  receiver: OtlpReceiver
  url: string
}> {
  const receiver = new OtlpReceiver({ port: 0, ...opts })
  await receiver.start()
  started.push(receiver)
  return { receiver, url: `http://127.0.0.1:${receiver.port}` }
}

/** One minimal valid span: decodes, so the request exercises the store path, not decode. */
function oneSpanBody(spanId = 'aaaaaaaabbbbcccc'): string {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'health-test' } }] },
        scopeSpans: [
          {
            spans: [
              {
                traceId: '0af7651916cd43dd8448eb211c80319c',
                spanId,
                name: 'health.probe.span',
                startTimeUnixNano: '1756478400123456789',
                endTimeUnixNano: '1756478400223456789',
              },
            ],
          },
        ],
      },
    ],
  })
}

describe('receiver /healthz contract', () => {
  it('preserves service/version/paused and adds endpoint, listening, and window facts', async () => {
    const { receiver, url } = await startReceiver()

    const body = (await (await fetch(`${url}/healthz`)).json()) as Record<string, unknown>

    expect(body).toMatchObject({ service: 'kyberdash-otlp', paused: false })
    expect(typeof body['version']).toBe('string')
    expect(body['endpoint']).toBe(`http://127.0.0.1:${receiver.port}`)
    expect(body['listening']).toBe(true)
    expect(body['lastAcceptedAt']).toBeNull()
    expect(typeof body['checkedAt']).toBe('string')
    expect(body['failures5m']).toBe(0)
    expect(body['lastError']).toBeNull()
    expect(body['lastCode']).toBeNull()
    expect(body['lastErrorAt']).toBeNull()
  })
})

describe('receiver five-minute error window', () => {
  it('counts a malformed OTLP traces request with a useful code and message', async () => {
    const { receiver, url } = await startReceiver()

    const res = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{"resourceSpans": "not-an-array"}',
    })
    expect(res.status).toBe(400)

    const health = receiver.getHealth()
    expect(health.failures5m).toBe(1)
    expect(health.lastCode).toBe('OTLP_MALFORMED_PAYLOAD')
    expect(typeof health.lastError).toBe('string')
    expect(health.lastError!.length).toBeGreaterThan(0)
    expect(typeof health.lastErrorAt).toBe('string')
  })

  it('counts a malformed OTLP logs request too', async () => {
    const { receiver, url } = await startReceiver()

    const res = await fetch(`${url}${OTLP_LOGS_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: 'this is not json',
    })
    expect(res.status).toBe(400)
    expect(receiver.getHealth().failures5m).toBe(1)
    expect(receiver.getHealth().lastCode).toBe('OTLP_MALFORMED_PAYLOAD')
  })

  it('counts wrong-method and wrong-media-type on the signal paths', async () => {
    const { receiver, url } = await startReceiver()

    expect((await fetch(`${url}${OTLP_TRACES_PATH}`)).status).toBe(405)
    expect(
      (
        await fetch(`${url}${OTLP_LOGS_PATH}`, {
          method: 'POST',
          headers: { 'content-type': 'text/plain' },
          body: 'x',
        })
      ).status,
    ).toBe(415)

    const health = receiver.getHealth()
    expect(health.failures5m).toBe(2)
    expect(health.lastCode).toBe('OTLP_UNSUPPORTED_MEDIA_TYPE')
  })

  it('excludes ordinary /healthz reads from the window', async () => {
    const { receiver, url } = await startReceiver()

    for (let i = 0; i < 5; i++) {
      expect((await fetch(`${url}/healthz`)).status).toBe(200)
    }

    expect(receiver.getHealth().failures5m).toBe(0)
    expect(receiver.getHealth().lastErrorAt).toBeNull()
  })

  it('excludes unknown-path 404s: they are not OTLP signal-path evidence', async () => {
    const { receiver, url } = await startReceiver()

    expect((await fetch(`${url}/v1/metrics`)).status).toBe(404)
    expect(receiver.getHealth().failures5m).toBe(0)
  })

  it('excludes intentional paused-503 responses but still reports paused explicitly', async () => {
    const { receiver, url } = await startReceiver()
    receiver.pause(60_000)
    try {
      const shed = await fetch(`${url}${OTLP_TRACES_PATH}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: oneSpanBody(),
      })
      expect(shed.status).toBe(503)

      // The paused state is separately explicit, so the shed load is not failure noise:
      // this is the interpretation the task authorizes (paused is its own red condition).
      const health = receiver.getHealth()
      expect(health.paused).toBe(true)
      expect(health.failures5m).toBe(0)
    } finally {
      receiver.resume()
    }
  })

  it('records a store refusal as a persistence failure while the caller sees the generic code', async () => {
    const { receiver, url } = await startReceiver({
      store: {
        write: () => {
          throw new Error('disk full at /data/canon.db')
        },
      },
    })

    const res = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: oneSpanBody(),
    })

    // The bind interface is configurable, so driver detail must not travel to the caller.
    expect(res.status).toBe(500)
    expect(await res.json()).toMatchObject({ error: { code: 'OTLP_INTERNAL' } })
    const health = receiver.getHealth()
    expect(health.failures5m).toBe(1)
    expect(health.lastCode).toBe('OTLP_PERSISTENCE_FAILED')
  })

  it('expires samples past five minutes and bounds retained bursts', () => {
    let now = 1_700_000_000_000
    const receiver = new OtlpReceiver({ clock: () => now })

    receiver.recordOtlpFailure('OTLP_MALFORMED_PAYLOAD', 'bad batch')
    expect(receiver.recentFailures()).toHaveLength(1)

    now += RECEIVER_ERROR_WINDOW_MS + 1
    expect(receiver.recentFailures()).toHaveLength(0)
    expect(receiver.getHealth().failures5m).toBe(0)
    expect(receiver.getHealth().lastError).toBeNull()

    for (let i = 0; i < MAX_RECEIVER_ERROR_SAMPLES + 25; i++) {
      receiver.recordOtlpFailure('OTLP_MALFORMED_PAYLOAD', `burst ${i}`)
    }
    expect(receiver.recentFailures()).toHaveLength(MAX_RECEIVER_ERROR_SAMPLES)
  })
})

describe('receiver lastAcceptedAt moves only on persistence', () => {
  it('stays null after receipt and moves after the writer commits', async () => {
    let committed = 0
    const writer = new IngestWriter(
      {
        upsertMany: (spans) => {
          committed += spans.length
        },
      },
      { batchSize: 8 },
    )
    const { receiver, url } = await startReceiver({ store: writer })
    writer.setPersistedHook(() => receiver.markPersisted())

    expect(receiver.getHealth().lastAcceptedAt).toBeNull()

    const res = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: oneSpanBody(),
    })
    expect(res.status).toBe(200)
    expect(committed).toBe(0)
    // Received and queued — but nothing persisted, so the accepted time must not move.
    expect(receiver.getHealth().lastAcceptedAt).toBeNull()

    await writer.flush()
    expect(committed).toBe(1)
    expect(typeof receiver.getHealth().lastAcceptedAt).toBe('string')

    await writer.stop()
  })

  it('records an async writer flush failure in the window without moving accepted time', async () => {
    // The writer half of the collector wiring: the sink refuses the batch after the
    // receiver already answered 200, so the failure lands via onError, not the response.
    const holder: { receiver: OtlpReceiver | null } = { receiver: null }
    const writer = new IngestWriter(
      {
        upsertMany: () => {
          throw new Error('store locked')
        },
      },
      {
        batchSize: 1,
        onError: (err) => {
          holder.receiver?.recordPersistenceFailure(err instanceof Error ? err.message : String(err))
        },
      },
    )
    const { receiver, url } = await startReceiver({ store: writer })
    holder.receiver = receiver
    writer.setPersistedHook(() => receiver.markPersisted())

    const res = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: oneSpanBody(),
    })
    // The exporter got its 200: decode succeeded and the batch queued.
    expect(res.status).toBe(200)

    // The size-triggered auto-flush refuses asynchronously; wait for the onError report.
    const deadline = Date.now() + 2_000
    while (receiver.getHealth().failures5m === 0 && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 10))
    }
    const health = receiver.getHealth()
    expect(health.failures5m).toBeGreaterThanOrEqual(1)
    expect(health.lastCode).toBe('OTLP_PERSISTENCE_FAILED')
    // Nothing persisted, so the accepted time must not move.
    expect(health.lastAcceptedAt).toBeNull()

    await writer.stop().catch(() => {})
  })
})

describe('/healthz error text for remote callers', () => {
  it('withholds the store error message from a non-loopback peer but keeps the code', async () => {
    const { receiver } = await startReceiver()
    receiver.recordPersistenceFailure('SQLITE_FULL: /home/dave/.kyberdash/canon.db')
    const health = receiver.getHealth()

    const remote = healthBodyForPeer(health, '203.0.113.9')
    expect(remote.lastError).toBeNull()
    expect(remote.lastCode).toBe('OTLP_PERSISTENCE_FAILED')
    expect(remote.failures5m).toBe(1)

    expect(healthBodyForPeer(health, '127.0.0.1').lastError).toContain('SQLITE_FULL')
    expect(healthBodyForPeer(health, '::1').lastError).toContain('SQLITE_FULL')
  })
})
