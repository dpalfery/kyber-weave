// Coverage receiver state: the `receiver` object is additive (existing fields keep their
// shape), reports the host's fresh poll when present, and reads as unknown-never-green
// otherwise. Driven through `handleKyberRequest` with a real store-backed bridge.

import type { IncomingMessage, ServerResponse } from 'node:http'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { ReceiverState } from '../jobs/receiver-host.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { KyberBridge, type IngestActivity } from './bridge.js'
import { handleKyberRequest, type KyberRouteDeps } from './routes.js'

const stores: CanonStore[] = []
const bridges: KyberBridge[] = []

afterEach(() => {
  for (const bridge of bridges.splice(0)) bridge.close()
  for (const store of stores.splice(0)) store.close()
})

function setup(): { store: CanonStore; bridge: KyberBridge } {
  const store = new CanonStore(':memory:')
  stores.push(store)
  const bridge = new KyberBridge({ store })
  bridges.push(bridge)
  return { store, bridge }
}

function healthyHosted(): ReceiverState {
  return {
    enabled: true,
    endpoint: 'http://127.0.0.1:4318',
    status: 'hosted',
    listening: true,
    hostedByUs: true,
    paused: false,
    checkedAt: '2026-10-10T20:00:00.000Z',
    lastAcceptedAt: '2026-10-10T19:59:00.000Z',
    failures5m: 0,
    lastError: null,
    lastCode: null,
    lastErrorAt: null,
    healthy: true,
  }
}

function call(
  href: string,
  bridge: KyberBridge,
  deps?: KyberRouteDeps,
  method = 'GET',
): { status: number; body: Record<string, unknown>; handled: boolean } {
  let status = 0
  let body = ''
  const req = { method } as IncomingMessage
  const res = {
    writeHead: (code: number) => {
      status = code
    },
    end: (data: string) => {
      body = data
    },
  } as unknown as ServerResponse
  const handled = handleKyberRequest(req, res, new URL(href, 'http://127.0.0.1:4747'), bridge, deps)
  return { status, body: body === '' ? {} : (JSON.parse(body) as Record<string, unknown>), handled }
}

describe('GET /api/kyber/coverage receiver state', () => {
  it('keeps every existing property and adds the receiver object', () => {
    const { bridge } = setup()
    const { status, body } = call('/api/kyber/coverage', bridge)

    expect(status).toBe(200)
    // Existing contract, preserved.
    expect(body).toHaveProperty('refresh')
    expect(body).toHaveProperty('ingest')
    expect(body).toHaveProperty('quarantineByReason')
    expect(body).toHaveProperty('checkpoints')
    const ingest = body['ingest'] as IngestActivity
    expect(ingest.status).toBe('unknown')
    // Additive receiver state.
    const receiver = body['receiver'] as ReceiverState
    expect(receiver.status).toBe('unknown')
    expect(receiver.healthy).toBe(false)
    expect(receiver.failures5m).toBeNull()
    expect(receiver.lastAcceptedAt).toBeNull()
  })

  it('serves the host poll verbatim when a receiver host is present', () => {
    const { store, bridge } = setup()
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const state = healthyHosted()
    const deps: KyberRouteDeps = {
      store,
      receiverHost: { getStatus: () => 'hosted', getReceiverState: () => state },
    }

    const { status, body } = call('/api/kyber/coverage', bridge, deps)

    expect(status).toBe(200)
    expect(body['receiver']).toEqual(state)
  })

  it('propagates a paused host as explicitly paused and unhealthy', () => {
    const { store, bridge } = setup()
    const deps: KyberRouteDeps = {
      store,
      receiverHost: {
        getStatus: () => 'hosted',
        getReceiverState: () => ({ ...healthyHosted(), paused: true, healthy: false }),
      },
    }

    const { body } = call('/api/kyber/coverage', bridge, deps)
    const receiver = body['receiver'] as ReceiverState

    expect(receiver.paused).toBe(true)
    expect(receiver.healthy).toBe(false)
  })

  it('propagates window failures instead of letting quarantine read as healthy transport', () => {
    const { store, bridge } = setup()
    const deps: KyberRouteDeps = {
      store,
      receiverHost: {
        getStatus: () => 'reachable',
        getReceiverState: () => ({
          ...healthyHosted(),
          status: 'reachable',
          hostedByUs: false,
          failures5m: 3,
          lastCode: 'OTLP_MALFORMED_PAYLOAD',
          lastError: 'resourceSpans must be an array',
          lastErrorAt: '2026-10-10T19:58:00.000Z',
          healthy: false,
        }),
      },
    }

    const { body } = call('/api/kyber/coverage', bridge, deps)
    const receiver = body['receiver'] as ReceiverState

    expect(receiver.failures5m).toBe(3)
    expect(receiver.lastCode).toBe('OTLP_MALFORMED_PAYLOAD')
    expect(receiver.healthy).toBe(false)
  })

  it('reads enabled off the store but stays unknown when no host is present', () => {
    const { store, bridge } = setup()
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')

    const { body } = call('/api/kyber/coverage', bridge, { store })
    const receiver = body['receiver'] as ReceiverState

    expect(receiver.enabled).toBe(true)
    expect(receiver.status).toBe('unknown')
    expect(receiver.endpoint).toBeNull()
    expect(receiver.healthy).toBe(false)
  })
})
