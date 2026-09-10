import { afterEach, describe, expect, it, vi } from 'vitest'

import { decodeKimiUsage, fetchKimiQuota } from './kimi'

// Fixtures mirror the menubar's KimiUsageParsingTests: the API has shipped
// numbers as both JSON numbers and strings, and the reset stamp under several
// key spellings, so every shape must decode.
const liveBody = {
  user: { userId: 'x', region: 'REGION_OVERSEA', membership: { level: 'LEVEL_INTERMEDIATE' } },
  usage: { limit: '100', used: '5', remaining: '95', resetTime: '2026-07-30T13:27:17.211Z' },
  limits: [
    {
      window: { duration: 300, timeUnit: 'TIME_UNIT_MINUTE' },
      detail: { limit: '100', remaining: '100', resetTime: '2026-07-23T23:27:17.211Z' },
    },
  ],
  parallel: { limit: '20' },
}

const credential = (expiresAt: number) => JSON.stringify({ access_token: 'eyJhbGciOiJFUzI1NiJ9.payload.sig', expires_at: expiresAt })
const fresh = credential(2_000_000_000)
const okJson = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
const now = () => 1_700_000_000_000

afterEach(() => vi.restoreAllMocks())

describe('Kimi usage decode', () => {
  it('decodes the live response shape: weekly primary, rolled-up rate window, plan, parallel limit', () => {
    const quota = decodeKimiUsage(liveBody)!
    expect(quota.connection).toBe('connected')
    expect(quota.planLabel).toBe('Intermediate')
    expect(quota.primary).toEqual({ label: 'Weekly', percent: 0.05, resetsAt: '2026-07-30T13:27:17.211Z' })
    // 300 minutes rolls up to a 5-hour label; used derives from remaining.
    expect(quota.details.map(row => row.label)).toEqual(['Weekly', '5-hour'])
    expect(quota.details[1]!.percent).toBe(0)
    expect(quota.footerLines).toEqual(['Parallel sessions: 20'])
  })

  it('accepts numeric fields and the snake_case reset spelling', () => {
    const quota = decodeKimiUsage({ usage: { limit: 100, used: 40, remaining: 60, reset_at: '2026-07-30T12:00:00Z' } })!
    expect(quota.primary).toEqual({ label: 'Weekly', percent: 0.4, resetsAt: '2026-07-30T12:00:00.000Z' })
    expect(quota.planLabel).toBeNull()
    expect(quota.footerLines).toEqual([])
  })

  it('reads epoch-second reset stamps as strings and as numbers', () => {
    const asString = decodeKimiUsage({ usage: { limit: 10, used: 5, resetTime: '1784900000' } })!
    const asNumber = decodeKimiUsage({ usage: { limit: 10, used: 5, resetTime: 1_784_900_000 } })!
    expect(asString.primary!.resetsAt).toBe('2026-07-24T13:33:20.000Z')
    expect(asNumber.primary!.resetsAt).toBe(asString.primary!.resetsAt)
  })

  it('labels windows by duration and promotes the first when there is no usage envelope', () => {
    const quota = decodeKimiUsage({ limits: [{ window: { duration: 7, timeUnit: 'day' }, detail: { limit: 1000, used: 250 } }] })!
    expect(quota.details.map(row => row.label)).toEqual(['Weekly'])
    expect(quota.primary).toEqual({ label: 'Weekly', percent: 0.25, resetsAt: null })
  })

  it('clamps over-limit usage to 100% instead of overflowing the bar', () => {
    const quota = decodeKimiUsage({ usage: { limit: 100, used: 143, remaining: -43 } })!
    expect(quota.primary!.percent).toBe(1)
  })

  it('returns null when nothing usable decodes', () => {
    expect(decodeKimiUsage({})).toBeNull()
    expect(decodeKimiUsage({ usage: { limit: 0, used: 0 }, limits: [] })).toBeNull()
    expect(decodeKimiUsage('garbage')).toBeNull()
  })
})

describe('Kimi quota fetch', () => {
  it('returns disconnected without credentials and never fetches', async () => {
    const fetchMock = vi.fn()
    const result = await fetchKimiQuota({ fetch: fetchMock, readFile: vi.fn(async () => null), now })
    expect(result.quota.connection).toBe('disconnected')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('sends the CLI platform headers with the bearer token and the device id', async () => {
    const fetchMock = vi.fn(async () => okJson(liveBody))
    const result = await fetchKimiQuota({
      fetch: fetchMock,
      readFile: vi.fn(async (path: string) => path.endsWith('device_id') ? 'device-abc\n' : fresh),
      now,
    })
    expect(result.quota.connection).toBe('connected')
    const [url, init] = fetchMock.mock.calls[0]! as unknown as [string, RequestInit]
    expect(url).toBe('https://api.kimi.com/coding/v1/usages')
    expect(init.method).toBe('GET')
    expect(init.headers).toEqual({
      Authorization: 'Bearer eyJhbGciOiJFUzI1NiJ9.payload.sig',
      Accept: 'application/json',
      'User-Agent': 'CodeBurn',
      'X-Msh-Platform': 'kimi_code_cli',
      'X-Msh-Device-Id': 'device-abc',
    })
  })

  it('omits the device header when the device file is unreadable', async () => {
    const fetchMock = vi.fn(async () => okJson(liveBody))
    await fetchKimiQuota({
      fetch: fetchMock,
      readFile: vi.fn(async (path: string) => {
        if (path.endsWith('device_id')) throw new Error('Credential file permissions are too broad')
        return fresh
      }),
      now,
    })
    expect((fetchMock.mock.calls[0]! as unknown as [string, RequestInit])[1].headers).not.toHaveProperty('X-Msh-Device-Id')
  })

  // Only the Kimi CLI can mint a new token, so an expired one is terminal and
  // never triggers a refresh or a write of our own.
  it('treats an expired token as terminal without fetching', async () => {
    const fetchMock = vi.fn()
    const result = await fetchKimiQuota({ fetch: fetchMock, readFile: vi.fn(async () => credential(1_699_999_999)), now })
    expect(result.quota.connection).toBe('terminalFailure')
    expect(result.quota.footerLines[0]).toMatch(/run the Kimi CLI/i)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('treats a rejected token as terminal with the same guidance', async () => {
    for (const status of [401, 403]) {
      const result = await fetchKimiQuota({ fetch: vi.fn(async () => new Response('', { status })), readFile: vi.fn(async () => fresh), now })
      expect(result.quota.connection).toBe('terminalFailure')
      expect(result.quota.footerLines[0]).toMatch(/run the Kimi CLI/i)
    }
  })

  it('uses the Retry-After header for 429 backoff', async () => {
    const fetchMock = vi.fn(async () => new Response('', { status: 429, headers: { 'Retry-After': '75' } }))
    const result = await fetchKimiQuota({ fetch: fetchMock, readFile: vi.fn(async () => fresh), now })
    expect(result.retryAfterSeconds).toBe(75)
    expect(result.quota.connection).toBe('transientFailure')
  })

  it('maps 5xx to transientFailure and other 4xx to terminalFailure', async () => {
    const bad = await fetchKimiQuota({ fetch: vi.fn(async () => new Response('', { status: 503 })), readFile: vi.fn(async () => fresh), now })
    expect(bad.quota.connection).toBe('transientFailure')
    const worse = await fetchKimiQuota({ fetch: vi.fn(async () => new Response('', { status: 404 })), readFile: vi.fn(async () => fresh), now })
    expect(worse.quota.connection).toBe('terminalFailure')
  })

  it('degrades a malformed success body instead of crashing the panel', async () => {
    const fetchMock = vi.fn(async () => new Response('not json {', { status: 200 }))
    const result = await fetchKimiQuota({ fetch: fetchMock, readFile: vi.fn(async () => fresh), now })
    expect(result.quota.connection).toBe('transientFailure')
    expect(result.quota.primary).toBeNull()
  })

  it('redacts tokens and NUL from diagnostics without surfacing them', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const fetchMock = vi.fn(async () => { throw new Error('Bearer eyJhbGciOiJFUzI1NiJ9.leak.sig\0tail') })
    const result = await fetchKimiQuota({ fetch: fetchMock, readFile: vi.fn(async () => fresh), now })
    const logged = warn.mock.calls.flat().join(' ')
    expect(result.quota).not.toHaveProperty('error')
    expect(logged).not.toMatch(/eyJhbGciOiJFUzI1NiJ9|leak|\0/)
    expect(logged).toContain('[REDACTED]')
  })
})
