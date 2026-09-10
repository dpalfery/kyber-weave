import { afterEach, describe, expect, it, vi } from 'vitest'

import { classifyProcessLine, decodeAntigravityStatus, decodeAntigravitySummary, fetchAntigravityQuota } from './antigravity'

afterEach(() => vi.restoreAllMocks())

const appLine = '1234 /Applications/Antigravity.app/Contents/Resources/app/extensions/antigravity/bin/language_server_macos_arm --app_data_dir antigravity --csrf_token tok-123 --extension_server_port 54321'
const ideLine = '1235 /Applications/Antigravity IDE.app/.../extensions/antigravity/bin/language_server --app_data_dir antigravity-ide --csrf_token ide-tok'
const tokenlessAppLine = '1236 /usr/local/lib/language_server_macos --app_data_dir antigravity'
const cliLine = '1237 /opt/homebrew/bin/agy serve'

describe('process classification', () => {
  it('classifies the app language server with its CSRF token and fallback port', () => {
    expect(classifyProcessLine(appLine)).toEqual({ pid: '1234', cli: false, csrf: 'tok-123', extPort: 54321 })
  })

  it('skips tokenless app servers and IDE servers, accepts the agy CLI without a token', () => {
    expect(classifyProcessLine(tokenlessAppLine)).toBeNull()
    expect(classifyProcessLine(ideLine)).toBeNull()
    const candidate = classifyProcessLine(cliLine)
    expect(candidate).toMatchObject({ pid: '1237', cli: true })
    expect(candidate?.csrf).toBeUndefined()
  })

  it('ignores unrelated language servers and non-matching lines', () => {
    expect(classifyProcessLine('999 /usr/bin/codeium_language_server --csrf_token x')).toBeNull()
    expect(classifyProcessLine('998 vim /tmp/agy-notes.md')).toBeNull()
    expect(classifyProcessLine('garbage line')).toBeNull()
  })
})

describe('payload decoding', () => {
  it('decodes summary groups into joined windows', () => {
    const windows = decodeAntigravitySummary({
      groups: [
        {
          displayName: 'Gemini Models',
          buckets: [
            { displayName: 'Weekly limit', remaining: { remainingFraction: 0.8 } },
            { displayName: 'Five hour limit', remaining: { remainingFraction: 0.25 } },
            { displayName: 'No fraction row' },
          ],
        },
        { displayName: 'Claude and GPT models', buckets: [{ bucketId: 'claude_weekly', remaining: { remainingFraction: 1 } }] },
      ],
    })
    expect(windows.map(row => row.label)).toEqual(['Gemini Models · Weekly limit', 'Gemini Models · Five hour limit', 'Claude and GPT models · claude_weekly'])
    expect(windows[1]!.percent).toBeCloseTo(0.75)
    expect(windows[2]!.percent).toBe(0)
  })

  it('decodes the legacy GetUserStatus quota rows with reset times', () => {
    const windows = decodeAntigravityStatus({
      userStatus: {
        cascadeModelConfigData: {
          clientModelConfigs: [
            { modelName: 'gemini-2.5-pro', quotaInfo: { remainingFraction: 0.5, resetTime: 1_800_000_000 } },
            { modelName: 'claude-sonnet-4', quotaInfo: { remainingFraction: 0.9, resetTime: '2026-07-12T00:00:00Z' } },
            { modelName: 'no-quota-model' },
          ],
        },
      },
    })
    expect(windows.map(row => row.label)).toEqual(['gemini-2.5-pro', 'claude-sonnet-4'])
    expect(windows[0]!.resetsAt).toBe(new Date(1_800_000_000 * 1000).toISOString())
    expect(windows[1]!.resetsAt).toBe('2026-07-12T00:00:00.000Z')
  })

  it('returns no windows for garbage payloads', () => {
    expect(decodeAntigravitySummary(null)).toEqual([])
    expect(decodeAntigravityStatus({ userStatus: {} })).toEqual([])
  })
})

describe('Antigravity local probe', () => {
  const psOutput = [appLine, cliLine].join('\n')

  it('probes the app server over loopback TLS then HTTP and renders the most constrained window first', async () => {
    const calls: Array<[number, boolean, string]> = []
    const request = vi.fn(async (port: number, tls: boolean, pathName: string) => {
      calls.push([port, tls, pathName])
      if (!tls) return null
      if (pathName.includes('RetrieveUserQuotaSummary')) return { status: 200, text: JSON.stringify({ groups: [{ displayName: 'Gemini Models', buckets: [{ displayName: 'Weekly limit', remaining: { remainingFraction: 0.7 } }] }] }) }
      return { status: 200, text: '{}' }
    })
    const execFile = vi.fn(async (_file: string, args: string[]) => args[0] === '-ax'
      ? { stdout: psOutput }
      : { stdout: `python 1234 user 12u IPv4 0x1 0t0 TCP 127.0.0.1:60123 (LISTEN)\n` })
    const quota = await fetchAntigravityQuota({ execFile, request })
    expect(quota.connection).toBe('connected')
    expect(quota.primary?.percent).toBeCloseTo(0.3)
    expect(quota.primary?.label).toBe('Gemini Models · Weekly limit')
    expect(calls.every(([port]) => port === 60123)).toBe(true)
    expect(calls[0]).toEqual([60123, true, '/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary'])
  })

  it('sends the CSRF header for app servers and none for the agy CLI', async () => {
    const headers: Array<string | undefined> = []
    let call = 0
    const request = vi.fn(async (_port: number, _tls: boolean, _pathName: string, _body: string, csrf?: string) => {
      headers.push(csrf)
      call += 1
      return call <= 2
        ? null
        : { status: 200, text: JSON.stringify({ groups: [{ displayName: 'Claude + GPT', buckets: [{ bucketId: 'weekly', remaining: { remainingFraction: 0.1 } }] }] }) }
    })
    const execFile = vi.fn(async (_file: string, args: string[]) => args[0] === '-ax'
      ? { stdout: cliLine }
      : { stdout: `agy 1237 user 5u IPv4 0x1 0t0 TCP *:60555 (LISTEN)\n` })
    const quota = await fetchAntigravityQuota({ execFile, request })
    expect(quota.connection).toBe('connected')
    expect(quota.planLabel).toBeNull()
    expect(headers[0]).toBeUndefined()
  })

  it('falls back to GetUserStatus and lifts planName when the summary has no windows', async () => {
    const request = vi.fn(async (_port: number, tls: boolean, pathName: string) => tls && pathName.includes('GetUserStatus')
      ? { status: 200, text: JSON.stringify({ userStatus: { planName: 'AI Pro', cascadeModelConfigData: { clientModelConfigs: [{ modelName: 'gemini-2.5-pro', quotaInfo: { remainingFraction: 0.4 } }] } } }) }
      : null)
    const execFile = vi.fn(async (_file: string, args: string[]) => args[0] === '-ax'
      ? { stdout: cliLine }
      : { stdout: `agy 1237 user 5u IPv4 0x1 0t0 TCP *:60555 (LISTEN)\n` })
    const quota = await fetchAntigravityQuota({ execFile, request })
    expect(quota.planLabel).toBe('AI Pro')
    expect(quota.details.map(row => row.label)).toEqual(['gemini-2.5-pro'])
  })

  it('reports disconnected when nothing local is listening', async () => {
    const request = vi.fn()
    const execFile = vi.fn(async () => ({ stdout: '' }))
    const quota = await fetchAntigravityQuota({ execFile, request })
    expect(quota.connection).toBe('disconnected')
    expect(request).not.toHaveBeenCalled()
  })

  it('reports disconnected when every port probe fails', async () => {
    const request = vi.fn(async () => null)
    const execFile = vi.fn(async (_file: string, args: string[]) => args[0] === '-ax'
      ? { stdout: cliLine }
      : { stdout: `agy 1237 user 5u IPv4 0x1 0t0 TCP *:60555 (LISTEN)\n` })
    const quota = await fetchAntigravityQuota({ execFile, request })
    expect(quota.connection).toBe('disconnected')
  })

  it('degrades an unexpected ps failure to transientFailure with sanitized diagnostics', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const execFile = vi.fn(async () => { throw new Error('ps exploded Bearer sk-secret eyJabc.def\0tail') })
    const quota = await fetchAntigravityQuota({ execFile })
    expect(quota.connection).toBe('transientFailure')
    const logged = warn.mock.calls.flat().join(' ')
    expect(logged).not.toMatch(/sk-secret|eyJabc|\0/)
    expect(logged).toContain('[REDACTED]')
  })
})
