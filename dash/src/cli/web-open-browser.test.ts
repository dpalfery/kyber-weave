// CodeQL alert 273 reproduction and browser launch safety test suite.
//
// Verifies:
// 1. On Windows, `openBrowser` dispatches to `rundll32.exe url.dll,FileProtocolHandler`
//    rather than `cmd /c start`, eliminating the cmd.exe shell interpretation sink and
//    preserving query parameter strings containing `&` (e.g. `/compare?a=...&b=...`).
// 2. On macOS and Linux, `openBrowser` dispatches to `open` and `xdg-open` respectively.
// 3. URLs passed to `openBrowser` and `joinViewUrl` are strictly validated against loopback
//    origin rules (`http://127.0.0.1:<1..65535>`) and the registered route table in
//    `dash/src/server/view-paths.json`. Unsafe, off-route, or non-loopback URLs are refused
//    without spawning any child process, emitting a one-line warning, and invalid view joins throw `UnknownViewError`.
// 4. `validateDashboardUrl` enforces loopback origin, port ranges, rejection of traversal
//    or scheme injections, and canonicalization.

import { execFile } from 'child_process'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { BRAND } from '../brand-overlay.js'
import {
  UnknownViewError,
  joinViewUrl,
  openBrowser,
} from './web.js'
import * as webModule from './web.js'

vi.mock('child_process', () => ({
  execFile: vi.fn(),
}))

const execFileMock = vi.mocked(execFile)

type ValidateDashboardUrlFn = (rawUrl: string) => string | null

// In T2 (GREEN), web.ts exports `validateDashboardUrl`. In T1 (RED), it is not yet exported.
// Accessing it via the module namespace keeps TypeScript compilation clean while producing
// clear assertion failures during the RED phase.
const validateDashboardUrl = (webModule as {
  validateDashboardUrl?: ValidateDashboardUrlFn
}).validateDashboardUrl

let originalPlatformDescriptor: PropertyDescriptor | undefined
let warnSpy: ReturnType<typeof vi.spyOn>

function withPlatform<T>(platform: NodeJS.Platform, run: () => T): T {
  const currentDescriptor = Object.getOwnPropertyDescriptor(process, 'platform')
  Object.defineProperty(process, 'platform', { value: platform, enumerable: true, configurable: true })
  try {
    return run()
  } finally {
    if (currentDescriptor) Object.defineProperty(process, 'platform', currentDescriptor)
    else delete (process as { platform?: NodeJS.Platform }).platform
  }
}

beforeEach(() => {
  originalPlatformDescriptor = Object.getOwnPropertyDescriptor(process, 'platform')
  warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
  vi.clearAllMocks()
})

afterEach(() => {
  if (originalPlatformDescriptor) {
    Object.defineProperty(process, 'platform', originalPlatformDescriptor)
  }
  warnSpy.mockRestore()
  vi.clearAllMocks()
})

describe('openBrowser platform dispatch and CodeQL alert 273 remediation', () => {
  it('dispatches to rundll32.exe on win32 without invoking cmd or cmd.exe', () => {
    withPlatform('win32', () => {
      openBrowser('http://127.0.0.1:4747')

      expect(execFileMock).toHaveBeenCalledWith(
        'rundll32.exe',
        ['url.dll,FileProtocolHandler', 'http://127.0.0.1:4747'],
        expect.any(Function),
      )
      expect(execFileMock).not.toHaveBeenCalledWith('cmd', expect.anything(), expect.anything())
      expect(execFileMock).not.toHaveBeenCalledWith('cmd.exe', expect.anything(), expect.anything())
      expect(warnSpy).not.toHaveBeenCalled()
    })
  })

  it('preserves query parameters containing ampersands on win32 without cmd shell truncation', () => {
    withPlatform('win32', () => {
      const compareUrl = 'http://127.0.0.1:4747/compare?a=run-alpha&b=run-beta'
      openBrowser(compareUrl)

      expect(execFileMock).toHaveBeenCalledWith(
        'rundll32.exe',
        ['url.dll,FileProtocolHandler', compareUrl],
        expect.any(Function),
      )
      // The intact URL must be passed directly in argv, never split across command separators
      const call = execFileMock.mock.calls.find((c) => c[0] === 'rundll32.exe')
      expect(call).toBeDefined()
      expect(call?.[1]).toEqual(['url.dll,FileProtocolHandler', compareUrl])
      expect(warnSpy).not.toHaveBeenCalled()
    })
  })

  it('dispatches to open on darwin', () => {
    withPlatform('darwin', () => {
      openBrowser('http://127.0.0.1:4747/run/r1')

      expect(execFileMock).toHaveBeenCalledWith(
        'open',
        ['http://127.0.0.1:4747/run/r1'],
        expect.any(Function),
      )
      expect(warnSpy).not.toHaveBeenCalled()
    })
  })

  it('dispatches to xdg-open on linux', () => {
    withPlatform('linux', () => {
      openBrowser('http://127.0.0.1:4747/problems')

      expect(execFileMock).toHaveBeenCalledWith(
        'xdg-open',
        ['http://127.0.0.1:4747/problems'],
        expect.any(Function),
      )
      expect(warnSpy).not.toHaveBeenCalled()
    })
  })

  it('succeeds and launches browser for all valid routes in view-paths.json', () => {
    withPlatform('darwin', () => {
      const validDashboardRoutes = [
        'http://127.0.0.1:4747',
        'http://127.0.0.1:4747/',
        'http://127.0.0.1:4747/run/abc',
        'http://127.0.0.1:4747/compare?a=1&b=2',
        'http://127.0.0.1:4747/finding/xyz',
        'http://127.0.0.1:4747/session/s1',
        'http://127.0.0.1:4747/session/s1/turn/0',
        'http://127.0.0.1:4747/quarantine',
        'http://127.0.0.1:4747/problems',
        'http://127.0.0.1:4747/harness/codex',
      ]

      for (const route of validDashboardRoutes) {
        vi.clearAllMocks()
        openBrowser(route)
        expect(execFileMock).toHaveBeenCalledTimes(1)
        expect(execFileMock).toHaveBeenCalledWith('open', [expect.stringMatching(/^http:\/\/127\.0\.0\.1:4747/)], expect.any(Function))
        expect(warnSpy).not.toHaveBeenCalled()
      }
    })
  })
})

describe('openBrowser loopback origin and route validation refusal', () => {
  const nonLoopbackUrls = [
    ['remote https target', 'https://google.com'],
    ['remote http target', 'http://example.com/run/1'],
    ['localhost hostname instead of literal 127.0.0.1', 'http://localhost:4747'],
    ['non-loopback LAN IP', 'http://192.168.1.1:4747'],
    ['file scheme', 'file:///etc/passwd'],
    ['embedded credentials / authority spoofing', 'http://127.0.0.1:4747@evil.example'],
    ['userinfo in loopback authority', 'http://admin:secret@127.0.0.1:4747'],
    ['ephemeral / zero port', 'http://127.0.0.1:0'],
    ['missing port', 'http://127.0.0.1'],
    ['missing port with path', 'http://127.0.0.1/run/1'],
    ['port exceeding 65535', 'http://127.0.0.1:65536'],
    ['javascript uri', 'javascript:alert(1)'],
  ] as const

  for (const [description, url] of nonLoopbackUrls) {
    it(`refuses ${description} (${url}) and does not call execFile`, () => {
      openBrowser(url)
      expect(execFileMock).not.toHaveBeenCalled()
      expect(warnSpy).toHaveBeenCalledWith(
        `${BRAND.cliName}: refusing to open a URL outside the loopback dashboard routes`,
      )
    })
  }

  const offRouteOrTraversalUrls = [
    ['parent traversal in root', 'http://127.0.0.1:4747/../secret'],
    ['parent traversal segment in route', 'http://127.0.0.1:4747/run/..'],
    ['parent traversal within parameter', 'http://127.0.0.1:4747/finding/../run'],
    ['protocol-relative injection attempt', 'http://127.0.0.1:4747//evil.example'],
    ['unregistered admin route', 'http://127.0.0.1:4747/admin'],
    ['random unrouted path', 'http://127.0.0.1:4747/not-a-view'],
    ['scheme injection in path', 'http://127.0.0.1:4747/run/http://evil.com'],
    ['scheme injection with https', 'http://127.0.0.1:4747/run/https://evil.com'],
    ['missing required compare parameters', 'http://127.0.0.1:4747/compare'],
    ['partial compare parameter a only', 'http://127.0.0.1:4747/compare?a=1'],
  ] as const

  for (const [description, url] of offRouteOrTraversalUrls) {
    it(`refuses ${description} (${url}) and does not call execFile`, () => {
      openBrowser(url)
      expect(execFileMock).not.toHaveBeenCalled()
      expect(warnSpy).toHaveBeenCalledWith(
        `${BRAND.cliName}: refusing to open a URL outside the loopback dashboard routes`,
      )
    })
  }

  it('emits a one-line refusal diagnostic and does not call execFile when URL is refused', () => {
    openBrowser('https://evil.com')
    expect(execFileMock).not.toHaveBeenCalled()
    expect(warnSpy).toHaveBeenCalledTimes(1)
    expect(warnSpy).toHaveBeenCalledWith(
      `${BRAND.cliName}: refusing to open a URL outside the loopback dashboard routes`,
    )
  })
})

describe('joinViewUrl view joining and validation', () => {
  it('correctly joins valid views onto loopback dashboard origin', () => {
    const origin = 'http://127.0.0.1:4747'

    expect(joinViewUrl(origin, undefined)).toBe('http://127.0.0.1:4747')
    expect(joinViewUrl(origin, '')).toBe('http://127.0.0.1:4747')
    expect(joinViewUrl(origin, '/')).toBe('http://127.0.0.1:4747')
    expect(joinViewUrl(origin, 'run/abc')).toBe('http://127.0.0.1:4747/run/abc')
    expect(joinViewUrl(origin, '/run/abc')).toBe('http://127.0.0.1:4747/run/abc')
    expect(joinViewUrl(origin, 'compare?a=1&b=2')).toBe('http://127.0.0.1:4747/compare?a=1&b=2')
    expect(joinViewUrl(origin, 'quarantine')).toBe('http://127.0.0.1:4747/quarantine')
    expect(joinViewUrl(origin, 'problems')).toBe('http://127.0.0.1:4747/problems')
    expect(joinViewUrl(origin, 'finding/xyz')).toBe('http://127.0.0.1:4747/finding/xyz')
    expect(joinViewUrl(origin, 'session/s1/turn/0')).toBe('http://127.0.0.1:4747/session/s1/turn/0')
    expect(joinViewUrl(origin, 'harness/codex')).toBe('http://127.0.0.1:4747/harness/codex')
  })

  const invalidViews = [
    ['parent traversal segment', '../secret'],
    ['parent traversal suffix', 'run/..'],
    ['protocol-relative injection', '//evil.example'],
    ['scheme injection http', 'http://evil.com'],
    ['scheme injection https', 'https://evil.com'],
    ['arbitrary unrouted path', 'admin'],
    ['unregistered view name', 'not-a-view'],
    ['incomplete compare parameters', 'compare?a=1'],
  ] as const

  for (const [description, view] of invalidViews) {
    it(`throws UnknownViewError for ${description} ("${view}")`, () => {
      expect(() => joinViewUrl('http://127.0.0.1:4747', view)).toThrow(UnknownViewError)
    })
  }

  it('throws UnknownViewError when origin is not a valid loopback origin', () => {
    expect(() => joinViewUrl('https://evil.com', 'run/abc')).toThrow(UnknownViewError)
    expect(() => joinViewUrl('http://localhost:4747', 'run/abc')).toThrow(UnknownViewError)
    expect(() => joinViewUrl('http://127.0.0.1', 'run/abc')).toThrow(UnknownViewError)
    expect(() => joinViewUrl('http://127.0.0.1:0', 'run/abc')).toThrow(UnknownViewError)
  })
})

describe('validateDashboardUrl safety contract and canonicalization', () => {
  it('is exported as a function from web.ts', () => {
    expect(typeof validateDashboardUrl).toBe('function')
  })

  it('canonicalizes valid root dashboard origins', () => {
    const canonical = validateDashboardUrl?.('http://127.0.0.1:4747')
    expect(canonical).toMatch(/^http:\/\/127\.0\.0\.1:4747\/?$/)

    const trailingCanonical = validateDashboardUrl?.('http://127.0.0.1:4747/')
    expect(trailingCanonical).toMatch(/^http:\/\/127\.0\.0\.1:4747\/?$/)
  })

  it('accepts and canonicalizes valid views from view-paths.json', () => {
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/run/abc123')).toBe('http://127.0.0.1:4747/run/abc123')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/compare?a=run1&b=run2')).toBe('http://127.0.0.1:4747/compare?a=run1&b=run2')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/finding/fid-1')).toBe('http://127.0.0.1:4747/finding/fid-1')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/session/s1')).toBe('http://127.0.0.1:4747/session/s1')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/session/s1/turn/0')).toBe('http://127.0.0.1:4747/session/s1/turn/0')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/quarantine')).toBe('http://127.0.0.1:4747/quarantine')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/problems')).toBe('http://127.0.0.1:4747/problems')
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/harness/codex')).toBe('http://127.0.0.1:4747/harness/codex')
  })

  it('accepts boundary ports between 1 and 65535', () => {
    expect(validateDashboardUrl?.('http://127.0.0.1:1/problems')).toBe('http://127.0.0.1:1/problems')
    expect(validateDashboardUrl?.('http://127.0.0.1:65535/problems')).toBe('http://127.0.0.1:65535/problems')
  })

  it('returns null for origins missing port or with invalid port range', () => {
    expect(validateDashboardUrl?.('http://127.0.0.1')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:0')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:65536')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:-1')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:notaport')).toBeNull()
  })

  it('returns null for non-loopback schemes, hosts and IP addresses', () => {
    expect(validateDashboardUrl?.('https://127.0.0.1:4747')).toBeNull()
    expect(validateDashboardUrl?.('http://localhost:4747')).toBeNull()
    expect(validateDashboardUrl?.('http://192.168.1.1:4747')).toBeNull()
    expect(validateDashboardUrl?.('http://10.0.0.1:4747')).toBeNull()
    expect(validateDashboardUrl?.('file:///etc/passwd')).toBeNull()
    expect(validateDashboardUrl?.('javascript:alert(1)')).toBeNull()
  })

  it('returns null when credentials or userinfo are present', () => {
    expect(validateDashboardUrl?.('http://user:pass@127.0.0.1:4747/')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747@evil.example/')).toBeNull()
  })

  it('returns null for path traversal, protocol-relative, or scheme injection attempts', () => {
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/../secret')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/run/..')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/finding/../run')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747//evil.example')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/run/http://evil.com')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/run/https://evil.com')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/run/://test')).toBeNull()
  })

  it('returns null for off-route paths or malformed URLs', () => {
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/admin')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/not-a-view')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/compare')).toBeNull()
    expect(validateDashboardUrl?.('http://127.0.0.1:4747/compare?a=1')).toBeNull()
    expect(validateDashboardUrl?.('not-a-url')).toBeNull()
    expect(validateDashboardUrl?.('')).toBeNull()
  })
})
