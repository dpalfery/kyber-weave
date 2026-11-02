// RED tests for the Factory (Droid) capture registry row.
//
// Factory (Droid) is captured by a plugin, not by configuration keys: KyberDash owns a
// whole plugin DIRECTORY in the Factory root (`<factory root>/plugins/kyberdash-factory`)
// and declares no key at all. `enable` installs the plugin and `disable` removes it, and
// neither reads, writes nor receipts the owner's `settings.json`.
//
// The row is reached through the capture registry — the seam the CLI, the API route and the
// Settings drawer all use — so this file states what the row must DO, not how the module
// that implements it is shaped. The tree it installs is the one the installer contract pins
// (`./factory-plugin/install.test.ts`).
//
// The Settings switch starts the capture; it does not opt the prompt text in. The content
// consent is the owner's own decision, made through
// `kyberdash kyber capture plugin consent --harness factory --on` or by editing the consent
// file by hand. `enable` installs the plugin and leaves that decision exactly as it was;
// `disable` removes the plugin and turns the content off.
//
// Every path is under a temporary home. The Factory root is that home's `.factory`, and
// `$FACTORY_DIR` points at the same directory, so the test never depends on WHICH of the
// two ways the writer resolves the root, and the developer's own `~/.factory` and
// `~/.kyberdash` are never read or written.

import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { DEFAULT_CAPTURE_ENDPOINT, isPluginOnly, runCapture, type PluginOnlyWriter } from '../index.js'
import type { PathEnv } from '../paths.js'
import { harnessWriterFor, type CaptureHarnessWriter } from './registry.js'

/** The plugin directory Kyber owns inside the Factory root. */
const PLUGIN_DIR_NAME = 'kyberdash-factory'
/** The files Kyber owns in that directory, relative to it: the tree shape. */
const OWNED_FILES = ['.factory-plugin/plugin.json', 'hooks/hooks.json', 'hooks/kyberdash-factory.mjs']

const HOST = process.platform
const pathEnv = (): PathEnv => ({ platform: HOST })

/** The receiver is never reached: the health probe is injected and always refuses. */
const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
}

type Fixture = {
  /** The injected home: KyberDash's own state lives in `<home>/.kyberdash`. */
  home: string
  /** The Factory root: the home's `.factory`, which `$FACTORY_DIR` also points at. */
  root: string
  /** The plugin directory Kyber owns inside the Factory root. */
  pluginDir: string
  /** The owner's Factory settings. KyberDash never writes it. */
  settingsFile: string
}

const tempDirs: string[] = []
afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(dir)
  return dir
}

function fixture(prefix: string): Fixture {
  const home = tempDir(prefix)
  const root = join(home, '.factory')
  mkdirSync(root, { recursive: true })
  // `$FACTORY_DIR` points at the home's `.factory`, so the two ways a writer may resolve
  // the Factory root name the same directory.
  vi.stubEnv('FACTORY_DIR', root)
  return {
    home,
    root,
    pluginDir: join(root, 'plugins', PLUGIN_DIR_NAME),
    settingsFile: join(root, 'settings.json'),
  }
}

/** Every file under a directory, as sorted relative posix-style paths. */
function listFiles(root: string): string[] {
  if (!existsSync(root)) return []
  const found: string[] = []
  const walk = (dir: string, prefix: string): void => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const relative = prefix === '' ? entry.name : `${prefix}/${entry.name}`
      if (entry.isDirectory()) walk(join(dir, entry.name), relative)
      else found.push(relative)
    }
  }
  walk(root, '')
  return found.sort()
}

/** The writer the registry holds for Factory. */
function factoryWriter(): CaptureHarnessWriter {
  const writer = harnessWriterFor('factory')
  if (writer === undefined) throw new Error('no capture writer is registered for factory')
  return writer
}

/** The Factory writer, which must be a managed writer that owns a plugin and no key. */
function factoryPluginWriter(): PluginOnlyWriter {
  const writer = factoryWriter()
  if (!isPluginOnly(writer)) throw new Error('the factory writer is not a plugin-only managed writer')
  return writer
}

/**
 * The consent file's `content` value, or undefined when there is no file. The file is
 * `{ "version": 1, "content": <boolean> }`.
 */
function consentContent(f: Fixture): boolean | undefined {
  const path = join(f.home, '.kyberdash', 'factory-plugin.json')
  if (!existsSync(path)) return undefined
  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
  if (typeof parsed !== 'object' || parsed === null) return undefined
  const content = (parsed as Record<string, unknown>)['content']
  return typeof content === 'boolean' ? content : undefined
}

/** The owner's own consent file, written by hand. */
function writeConsent(f: Fixture, content: boolean): void {
  const path = join(f.home, '.kyberdash', 'factory-plugin.json')
  mkdirSync(join(path, '..'), { recursive: true })
  writeFileSync(path, `${JSON.stringify({ version: 1, content }, null, 2)}\n`)
}

/** The receiver's address without its scheme: what the row must name when it is pointed here. */
const RECEIVER_ADDRESS = DEFAULT_CAPTURE_ENDPOINT.replace(/^https?:\/\//, '')

/**
 * The machine environment Factory's native export reads: the endpoint variable and its
 * documented fallback, and the three that carry the export's shape. All of them are
 * stubbed, so the developer's own shell cannot decide the fixture and the fallback cannot
 * answer for the variable.
 */
function setNativeEnv(endpoint: string | undefined): void {
  vi.stubEnv('OTEL_TELEMETRY_ENDPOINT', endpoint)
  vi.stubEnv('OTEL_EXPORTER_OTLP_ENDPOINT', undefined)
  vi.stubEnv('OTEL_TELEMETRY_FORMAT', undefined)
  vi.stubEnv('OTEL_TELEMETRY_GRANULARITY', undefined)
  vi.stubEnv('OTEL_LOG_MESSAGE_CONTENT', undefined)
}

/**
 * The organisation's managed settings, as the machine holds them. The organisation's
 * `telemetry` block pins the sink for every member of the organisation and OVERRIDES the
 * machine environment variable; it reaches the machine both as the cached view under the
 * Factory root and as the system-managed file. Both are written here, under the temporary
 * home, carrying the same block, so the fixture never depends on which of the two the
 * reader consults — and the machine's own organisation settings are never read.
 */
function writeOrgTelemetry(home: string, root: string, telemetry: Record<string, unknown>): void {
  mkdirSync(root, { recursive: true })
  writeFileSync(
    join(root, 'org-managed-settings.cache.json'),
    `${JSON.stringify(
      {
        orgId: 'org-test',
        userId: 'user-test',
        managedSettings: { telemetry },
        factoryTier: 'team_annual',
        revision: 1,
        cachedAt: 0,
      },
      null,
      2,
    )}\n`,
  )
  const systemDir =
    HOST === 'win32'
      ? join(home, 'AppData', 'Roaming', 'Factory')
      : HOST === 'linux'
        ? join(home, 'etc', 'factory')
        : join(home, 'Library', 'Application Support', 'Factory')
  mkdirSync(systemDir, { recursive: true })
  writeFileSync(join(systemDir, 'settings.json'), `${JSON.stringify({ telemetry }, null, 2)}\n`)
}

/**
 * The printed status of one harness: the lines under its harness heading. The row's own
 * lines are what the native export is reported in, so nothing outside the row can answer
 * for it.
 */
function harnessBlock(lines: readonly string[], id: string): string[] {
  const start = lines.findIndex((line) => line.startsWith(`harness ${id} (`))
  if (start === -1) throw new Error(`no ${id} harness block in ${JSON.stringify(lines)}`)
  const block: string[] = []
  for (const line of lines.slice(start + 1)) {
    if (!line.startsWith('  ')) break
    block.push(line)
  }
  return block
}

/**
 * The row's own lines about the native OpenTelemetry export. A row that says nothing about
 * the native path is the defect these tests exist to catch, so the helper fails loudly
 * instead of returning an empty string.
 */
function nativeText(lines: readonly string[]): string {
  const named = lines.filter((line) => /native|otel|telemetry/i.test(line))
  const candidates = named.length > 0 ? named : lines.filter((line) => /export/i.test(line))
  if (candidates.length === 0) {
    throw new Error(`the row says nothing about the native export: ${JSON.stringify(lines)}`)
  }
  return candidates.join('\n')
}

/**
 * The text with its unavailability claims removed. What is left is any claim that the
 * native path is working — the one claim the row must never make wrongly.
 */
function workingClaim(text: string): string {
  return text.replace(/\bunavailable\b/gi, '').replace(/\bnot\s+available\b/gi, '')
}

/**
 * The row's own line about the export's effective shape: the format, the granularity and
 * the message-content switch, each with the source it came from. A row that names the
 * endpoint but not the shape is the defect these tests exist to catch, so the helper fails
 * loudly instead of returning an empty string.
 */
function shapeText(lines: readonly string[]): string {
  const found = lines.filter((line) => /\bshape\b/i.test(line))
  if (found.length === 0) {
    throw new Error(`the row does not name the export's shape: ${JSON.stringify(lines)}`)
  }
  return found.join('\n')
}

/**
 * The row's own line about what KyberDash does with the export: the metrics, the spans and
 * the content, in fixed words.
 */
function consequenceText(lines: readonly string[]): string {
  const found = lines.filter((line) => /persisted/i.test(line))
  if (found.length === 0) {
    throw new Error(`the row does not say what KyberDash does with the export: ${JSON.stringify(lines)}`)
  }
  return found.join('\n')
}

/** The Factory row as `capture status` prints it. */
async function printedStatus(home: string): Promise<string[]> {
  const result = await runCapture('status', {
    homeDir: home,
    platform: HOST,
    harnessIds: ['factory'],
    fetchHealth: NO_LISTENER,
  })
  expect(result.exitCode).toBe(0)
  return harnessBlock(result.stdout.split('\n'), 'factory')
}

describe('the Factory (Droid) capture writer', () => {
  it('is registered as a managed writer named Factory (Droid)', () => {
    const writer = factoryWriter()
    expect(writer.id).toBe('factory')
    expect(writer.kind).toBe('managed')
    expect(writer.displayName).toBe('Factory (Droid)')
  })

  it('declares no configuration key: the plugin is the whole surface', () => {
    const writer = factoryPluginWriter()
    expect(writer.desiredKeys('http://127.0.0.1:4318')).toEqual({})
    expect(writer.configFiles ?? []).toEqual([])
    expect(writer.ensureIfAbsentKeys?.('http://127.0.0.1:4318') ?? {}).toEqual({})
  })

  it('resolves the Factory settings file under the home', () => {
    const f = fixture('factory-writer-home-')
    expect(factoryWriter().resolvePath(f.home, pathEnv())).toBe(f.settingsFile)
  })

  it('resolves the Factory settings file under $FACTORY_DIR, not under the home', () => {
    const f = fixture('factory-writer-env-')
    const elsewhere = tempDir('factory-root-')
    vi.stubEnv('FACTORY_DIR', elsewhere)
    expect(factoryWriter().resolvePath(f.home, pathEnv())).toBe(join(elsewhere, 'settings.json'))
  })

  it('owns a plugin directory inside the Factory root', () => {
    const f = fixture('factory-writer-plugin-')
    expect(factoryPluginWriter().filePlugin.path(f.home, pathEnv())).toBe(f.pluginDir)
  })

  it('installs the plugin tree and leaves the owner settings file byte-identical', () => {
    const f = fixture('factory-writer-install-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)

    const result = factoryPluginWriter().filePlugin.install(f.home, pathEnv(), { dryRun: false })

    expect(result.exitCode).toBe(0)
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('removes the plugin tree again and leaves the owner settings file byte-identical', () => {
    const f = fixture('factory-writer-uninstall-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)
    const writer = factoryPluginWriter()
    writer.filePlugin.install(f.home, pathEnv(), { dryRun: false })

    const removed = writer.filePlugin.uninstall(f.home, pathEnv(), { dryRun: false })

    expect(removed.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('enables and disables through the engine without receipting a configuration key', async () => {
    const f = fixture('factory-writer-engine-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)

    const enabled = await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })

    expect(enabled.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(true)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
    // The plugin keeps its own receipt, so the key receipt holds no Factory entry.
    const keyReceipt = join(f.home, '.kyberdash', 'capture-receipt.json')
    expect(existsSync(keyReceipt) ? readFileSync(keyReceipt, 'utf8') : '').not.toContain('factory')

    const disabled = await runCapture('disable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })

    expect(disabled.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('enables the capture without recording the prompt text: the consent file is created with content off', async () => {
    const f = fixture('factory-writer-consent-absent-')
    await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    // The switch starts the capture; it does not opt the prompt text in.
    expect(existsSync(f.pluginDir)).toBe(true)
    expect(consentContent(f)).toBe(false)
  })

  it('leaves an owner content consent on when enable installs the capture', async () => {
    const f = fixture('factory-writer-consent-on-')
    writeConsent(f, true)
    await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    expect(existsSync(f.pluginDir)).toBe(true)
    expect(consentContent(f)).toBe(true)
  })

  it('leaves an owner content consent off when enable installs the capture', async () => {
    const f = fixture('factory-writer-consent-off-')
    writeConsent(f, false)
    await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    expect(consentContent(f)).toBe(false)
  })

  it('disable uninstalls the plugin and turns the content consent off', async () => {
    const f = fixture('factory-writer-consent-disable-')
    writeConsent(f, true)
    await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    await runCapture('disable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(consentContent(f)).toBe(false)
  })
})

describe('the Factory row and the native OpenTelemetry export', () => {
  it('reports the native export as available when the machine variable points at this receiver', async () => {
    const f = fixture('factory-avail-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)

    const lines = await printedStatus(f.home)
    const text = nativeText(lines)

    expect(text).toMatch(/\bavailable\b/i)
    expect(text).not.toMatch(/\bunavailable\b|\bnot\s+available\b/i)
    // The row says which endpoint the native export is pointed at.
    expect(text).toContain(RECEIVER_ADDRESS)
    // And it names the shape of what the export sends, not only where it sends it.
    const shape = shapeText(lines)
    expect(shape).toMatch(/format\s+legacy/i)
    expect(shape).toMatch(/granularity\s+user/i)
    expect(shape).toMatch(/message content\s+off/i)
  })

  it('reports the native export as unavailable when the machine variable points elsewhere', async () => {
    const f = fixture('factory-elsewhere-')
    setNativeEnv('http://otel.example.com:4318')

    const lines = await printedStatus(f.home)
    const text = nativeText(lines)

    expect(text).toMatch(/\bunavailable\b|\bnot\s+available\b/i)
    // The reason names that the endpoint points somewhere other than this receiver.
    expect(text).toMatch(/\bpoint(s|ed|ing)?\b|\belsewhere\b|\bother\b|\bdifferent\b|\bnot\b/i)
    // The shape is reported even when the endpoint is not this receiver.
    const shape = shapeText(lines)
    expect(shape).toMatch(/format\s+legacy/i)
    expect(shape).toMatch(/granularity\s+user/i)
    expect(shape).toMatch(/message content\s+off/i)
  })

  it('reports the native export as unavailable and names the organisation that pins it', async () => {
    const f = fixture('factory-orgpin-')
    // The machine variable is set correctly, and the organisation's pinned endpoint
    // overrides it: the organisation's endpoint is the one that receives the traffic.
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)
    writeOrgTelemetry(f.home, f.root, { endpoint: 'https://otlp.acme.example' })

    const lines = await printedStatus(f.home)
    const text = nativeText(lines)

    expect(text).toMatch(/\bunavailable\b|\bnot\s+available\b/i)
    // The cause is named: an organisation-pinned endpoint, not the machine variable.
    expect(text).toMatch(/\borgani[sz]ation\b|\borg-managed\b|\bmanaged settings\b|\benterprise\b/i)
    // Never reported as working, whatever the machine variable says.
    expect(workingClaim(text)).not.toMatch(/\bavailable\b/i)
    // The organisation's pin does not decide the shape: it pinned no shape of its own.
    const shape = shapeText(lines)
    expect(shape).toMatch(/format\s+legacy/i)
    expect(shape).toMatch(/granularity\s+user/i)
  })

  it('reports the native export as not configured when nothing configures it', async () => {
    const f = fixture('factory-unset-')
    setNativeEnv(undefined)

    const lines = await printedStatus(f.home)
    const text = nativeText(lines)

    // "Not configured" is its own verdict: neither working, nor unavailable for a reason.
    expect(text).toMatch(/\bnot\s+configured\b/i)
    expect(text).not.toMatch(/\bunavailable\b|\bnot\s+available\b/i)
    expect(workingClaim(text)).not.toMatch(/\bavailable\b/i)
    // The shape is reported even when nothing configures the export: the defaults are what
    // such an export would send, and the row says where each of them came from.
    const shape = shapeText(lines)
    expect(shape).toMatch(/format\s+legacy/i)
    expect(shape).toMatch(/granularity\s+user/i)
    expect(shape).toMatch(/message content\s+off/i)
  })

  it("reports the organisation's format over the machine's format variable", async () => {
    const f = fixture('factory-shape-format-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)
    vi.stubEnv('OTEL_TELEMETRY_FORMAT', 'legacy')
    writeOrgTelemetry(f.home, f.root, { format: 'genai' })

    const shape = shapeText(await printedStatus(f.home))

    // The organisation's block wins, and the row names it as the source.
    expect(shape).toMatch(/format\s+genai/i)
    expect(shape).toMatch(/\borgani[sz]ation\b/i)
  })

  it('reports the granularity most-restrictive-wins: an aggregate machine value beats an organisation that pins user', async () => {
    const f = fixture('factory-shape-granularity-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)
    vi.stubEnv('OTEL_TELEMETRY_GRANULARITY', 'aggregate')
    writeOrgTelemetry(f.home, f.root, { granularity: 'user' })

    const shape = shapeText(await printedStatus(f.home))

    expect(shape).toMatch(/granularity\s+aggregate/i)
    expect(shape).toMatch(/OTEL_TELEMETRY_GRANULARITY/)
  })

  it('reports that the aggregate granularity overrides the message-content opt-in', async () => {
    const f = fixture('factory-shape-content-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)
    vi.stubEnv('OTEL_LOG_MESSAGE_CONTENT', 'true')
    writeOrgTelemetry(f.home, f.root, { granularity: 'aggregate', logMessageContent: true })

    const shape = shapeText(await printedStatus(f.home))

    expect(shape).toMatch(/granularity\s+aggregate/i)
    expect(shape).toMatch(/message content\s+off/i)
  })

  it('states that the native metrics are persisted, that no native span is claimed, and that no Factory content is stored while the consent is off', async () => {
    const f = fixture('factory-consequence-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)

    const consequence = consequenceText(await printedStatus(f.home))

    expect(consequence).toMatch(/native metrics are persisted/i)
    expect(consequence).toMatch(/native spans are not claimed/i)
    expect(consequence).toMatch(/no Factory content is stored while KyberDash's consent is off/i)
  })

  it('states the content consequence from the consent file, not from an inference', async () => {
    const f = fixture('factory-consent-on-')
    setNativeEnv(DEFAULT_CAPTURE_ENDPOINT)
    writeConsent(f, true)

    const consequence = consequenceText(await printedStatus(f.home))

    // The consent is on, so the row does not claim that no content is stored.
    expect(consequence).toMatch(/Factory content is stored while KyberDash's consent is on/i)
    expect(consequence).not.toMatch(/no Factory content is stored/i)
  })
})
