// The native OpenTelemetry export's EFFECTIVE SHAPE.
//
// Factory's native export sends what its SHAPE says it sends: the format decides which
// metric catalog arrives (`droid.*` under `legacy`, `gen_ai.*` under `genai` — the two
// replace each other and do not stack), the granularity decides whether the legacy format
// produces spans at all, and the message-content switch decides whether content-bearing
// spans are produced. An endpoint that points at this receiver says none of that: it says
// where the traffic goes, not what is in it.
//
// The shape is configured in two places, and the two places do NOT follow one rule:
//
//   - the ENABLED switch follows precedence for the organisation's `telemetry.enabled`:
//     where the organisation sets it at all the machine's `OTEL_CUSTOMER_ENABLED` does not
//     decide, so an organisation can keep the pipeline on (or off) whatever the machine
//     says. Where the organisation sets nothing, the machine's variable decides, and
//     where the machine says nothing either the pipeline is on — Factory's own
//     documentation makes the machine variable an opt-OUT.
//   - the FORMAT follows precedence: the organisation's `telemetry.format` wins, and where
//     the organisation sets it the machine's `OTEL_TELEMETRY_FORMAT` is not read at all.
//     Anything unrecognised resolves to `legacy`.
//   - the GRANULARITY follows most-restrictive-wins: `aggregate` beats `user`, whichever
//     level each of them came from. A value that CANNOT BE READ resolves to `aggregate`,
//     because that is the restrictive one and Factory's own privacy page says so; a value
//     that was never set is a different case and takes the documented default, which is
//     `user`.
//   - the MESSAGE-CONTENT switch follows precedence for the organisation's
//     `telemetry.logMessageContent` — when it is set at all, the machine's
//     `OTEL_LOG_MESSAGE_CONTENT` does not decide — and the granularity overrides both:
//     `aggregate` overrides every content opt-in.
//
// The organisation's block is read from the same two managed-settings views the endpoint is
// read from — the cached organisation settings under the Factory root and the system-managed
// copy — and never from the owner's own settings file.
//
// Every path here is under a temporary home and the environment is injected, so the
// machine's own settings are never reached.

import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { readNativeExport, type NativeExportSources, type NativeExportVerdict } from './native-export.js'

const tempDirs: string[] = []
afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

type Fixture = {
  /** The reader's sources: the Factory root, the home and the platform. */
  sources: NativeExportSources
  /** The Factory root: the organisation-settings cache lives under it. */
  root: string
}

/**
 * The enabled switch of an export that nothing enables or disables: on, because Factory's
 * own documentation makes the machine variable an opt-OUT — a machine that says nothing is
 * a machine that has not opted out.
 */
const DEFAULT_ENABLED = { value: true, source: 'default' }

/** The shape of an export nothing configures: the documented defaults. */
const DEFAULT_SHAPE = {
  enabled: DEFAULT_ENABLED,
  format: { value: 'legacy', source: 'default' },
  granularity: { value: 'user', source: 'default' },
  content: { value: false, source: 'default' },
}

function fixture(prefix: string): Fixture {
  const home = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(home)
  const root = join(home, '.factory')
  mkdirSync(root, { recursive: true })
  return { sources: { factoryRoot: root, home, platform: process.platform }, root }
}

/** The directory the system-managed organisation settings live in, under the given home. */
function systemManagedDir(home: string, platform: NodeJS.Platform): string {
  if (platform === 'win32') return join(home, 'AppData', 'Roaming', 'Factory')
  if (platform === 'linux') return join(home, 'etc', 'factory')
  return join(home, 'Library', 'Application Support', 'Factory')
}

/**
 * The organisation's managed settings, as the machine holds them. The `telemetry` block
 * pins the sink for every member of the organisation, and it reaches the machine both as
 * the cached view under the Factory root and as the system-managed copy. Both are written
 * with the same block, so the fixture does not depend on which of the two the reader
 * consults — and the machine's own organisation settings are never read.
 */
function writeOrgTelemetry(f: Fixture, telemetry: Record<string, unknown>): void {
  writeFileSync(
    join(f.root, 'org-managed-settings.cache.json'),
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
  const dir = systemManagedDir(f.sources.home, f.sources.platform)
  mkdirSync(dir, { recursive: true })
  writeFileSync(join(dir, 'settings.json'), `${JSON.stringify({ telemetry }, null, 2)}\n`)
}

/** The system-managed copy alone: the machine that holds no cached organisation view. */
function writeSystemTelemetry(f: Fixture, telemetry: Record<string, unknown>): void {
  const dir = systemManagedDir(f.sources.home, f.sources.platform)
  mkdirSync(dir, { recursive: true })
  writeFileSync(join(dir, 'settings.json'), `${JSON.stringify({ telemetry }, null, 2)}\n`)
}

/** The reader, with the machine environment injected rather than read from the shell. */
function read(f: Fixture, processEnv: NodeJS.ProcessEnv = {}) {
  return readNativeExport(f.sources, { processEnv })
}

/** The endpoint a verdict names, when it names one. */
function endpointOf(verdict: NativeExportVerdict): string | undefined {
  return 'endpoint' in verdict ? verdict.endpoint : undefined
}

describe("the native export's effective shape", () => {
  it('reads the format from the machine variable when the organisation pins none', () => {
    const f = fixture('factory-shape-format-machine-')

    expect(read(f, { OTEL_TELEMETRY_FORMAT: 'genai' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'genai', source: 'OTEL_TELEMETRY_FORMAT' },
      granularity: { value: 'user', source: 'default' },
      content: { value: false, source: 'default' },
    })
  })

  it('resolves an unrecognised machine format to legacy', () => {
    const f = fixture('factory-shape-format-unknown-')

    expect(read(f, { OTEL_TELEMETRY_FORMAT: 'otel' }).shape).toEqual(DEFAULT_SHAPE)
  })

  it("resolves the organisation's format over the machine variable", () => {
    const f = fixture('factory-shape-format-organisation-')
    writeOrgTelemetry(f, { format: 'genai' })

    expect(read(f, { OTEL_TELEMETRY_FORMAT: 'legacy' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'genai', source: 'organisation' },
      granularity: { value: 'user', source: 'default' },
      content: { value: false, source: 'default' },
    })
  })

  it("resolves the organisation's format even when it names an unrecognised value", () => {
    const f = fixture('factory-shape-format-organisation-unknown-')
    writeOrgTelemetry(f, { format: 'otel' })

    // The organisation pinned the format, so the machine's variable is not read at all and
    // the unrecognised value resolves to legacy.
    expect(read(f, { OTEL_TELEMETRY_FORMAT: 'genai' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'organisation' },
      granularity: { value: 'user', source: 'default' },
      content: { value: false, source: 'default' },
    })
  })

  it('reads the granularity from the machine variable', () => {
    const f = fixture('factory-shape-granularity-machine-')

    expect(read(f, { OTEL_TELEMETRY_GRANULARITY: 'aggregate' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'default' },
      granularity: { value: 'aggregate', source: 'OTEL_TELEMETRY_GRANULARITY' },
      content: { value: false, source: 'default' },
    })
  })

  it('resolves the granularity most-restrictive-wins: an aggregate machine value beats an organisation that pins user', () => {
    const f = fixture('factory-shape-granularity-machine-aggregate-')
    writeOrgTelemetry(f, { granularity: 'user' })

    expect(read(f, { OTEL_TELEMETRY_GRANULARITY: 'aggregate' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'default' },
      granularity: { value: 'aggregate', source: 'OTEL_TELEMETRY_GRANULARITY' },
      content: { value: false, source: 'default' },
    })
  })

  it('resolves the granularity most-restrictive-wins: an organisation that pins aggregate beats a machine that pins user', () => {
    const f = fixture('factory-shape-granularity-organisation-aggregate-')
    writeOrgTelemetry(f, { granularity: 'aggregate' })

    expect(read(f, { OTEL_TELEMETRY_GRANULARITY: 'user' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'default' },
      granularity: { value: 'aggregate', source: 'organisation' },
      content: { value: false, source: 'default' },
    })
  })

  it('reads the message-content switch from the machine variable', () => {
    const f = fixture('factory-shape-content-machine-')

    expect(read(f, { OTEL_LOG_MESSAGE_CONTENT: 'true' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'default' },
      granularity: { value: 'user', source: 'default' },
      content: { value: true, source: 'OTEL_LOG_MESSAGE_CONTENT' },
    })
  })

  it('reads the message-content switch from the machine variable set to 1', () => {
    const f = fixture('factory-shape-content-machine-one-')

    expect(read(f, { OTEL_LOG_MESSAGE_CONTENT: '1' }).shape.content).toEqual({
      value: true,
      source: 'OTEL_LOG_MESSAGE_CONTENT',
    })
  })

  it("resolves the organisation's message-content switch over the machine variable that turns content on", () => {
    const f = fixture('factory-shape-content-organisation-off-')
    writeOrgTelemetry(f, { logMessageContent: false })

    expect(read(f, { OTEL_LOG_MESSAGE_CONTENT: 'true' }).shape.content).toEqual({
      value: false,
      source: 'organisation',
    })
  })

  it("resolves the organisation's message-content switch over a machine variable that says nothing", () => {
    const f = fixture('factory-shape-content-organisation-on-')
    writeOrgTelemetry(f, { logMessageContent: true })

    expect(read(f, {}).shape.content).toEqual({ value: true, source: 'organisation' })
  })

  it('lets the aggregate granularity override every content opt-in', () => {
    const f = fixture('factory-shape-content-aggregate-')
    writeOrgTelemetry(f, { granularity: 'aggregate', logMessageContent: true })

    expect(read(f, { OTEL_LOG_MESSAGE_CONTENT: 'true' }).shape).toEqual({
      enabled: DEFAULT_ENABLED,
      format: { value: 'legacy', source: 'default' },
      granularity: { value: 'aggregate', source: 'organisation' },
      content: { value: false, source: 'granularity' },
    })
  })

  it("ignores a telemetry block in the owner's own settings file", () => {
    const f = fixture('factory-shape-owner-settings-')
    writeFileSync(
      join(f.root, 'settings.json'),
      `${JSON.stringify({ telemetry: { format: 'genai', granularity: 'aggregate', logMessageContent: true } })}\n`,
    )

    // The block is read from the organisation level only; the owner's file decides nothing.
    expect(read(f, {}).shape).toEqual(DEFAULT_SHAPE)
  })

  it('reads the organisation shape from the system-managed copy when the cache carries none', () => {
    const f = fixture('factory-shape-system-copy-')
    writeSystemTelemetry(f, { format: 'genai' })

    expect(read(f, {}).shape.format).toEqual({ value: 'genai', source: 'organisation' })
  })
})

describe("the export's enabled master switch", () => {
  it("keeps the export on when the organisation enables it, whatever the machine variable says", () => {
    const f = fixture('factory-enabled-organisation-on-')
    writeOrgTelemetry(f, { enabled: true })

    expect(read(f, { OTEL_CUSTOMER_ENABLED: 'false' }).shape.enabled).toEqual({
      value: true,
      source: 'organisation',
    })
  })

  it("keeps the export off when the organisation disables it, whatever the machine variable says", () => {
    const f = fixture('factory-enabled-organisation-off-')
    writeOrgTelemetry(f, { enabled: false })

    expect(read(f, { OTEL_CUSTOMER_ENABLED: 'true' }).shape.enabled).toEqual({
      value: false,
      source: 'organisation',
    })
  })

  it("reads the organisation's enabled switch when the machine variable is absent", () => {
    const f = fixture('factory-enabled-organisation-alone-')
    writeOrgTelemetry(f, { enabled: false })

    expect(read(f, {}).shape.enabled).toEqual({ value: false, source: 'organisation' })
  })

  it('falls back to the machine variable when the organisation pins no enabled switch', () => {
    const f = fixture('factory-enabled-machine-')

    expect(read(f, { OTEL_CUSTOMER_ENABLED: 'false' }).shape.enabled).toEqual({
      value: false,
      source: 'OTEL_CUSTOMER_ENABLED',
    })
  })

  it('keeps the export on when the organisation pins no enabled switch and the machine says nothing', () => {
    const f = fixture('factory-enabled-default-')

    // Factory's own documentation makes the machine variable an opt-out, so a machine that
    // says nothing has not opted out.
    expect(read(f, {}).shape.enabled).toEqual({ value: true, source: 'default' })
  })

  it('switches the export off when the organisation carries an enabled value that cannot be read', () => {
    const f = fixture('factory-enabled-organisation-unreadable-')
    writeOrgTelemetry(f, { enabled: 'yes' })

    expect(read(f, {}).shape.enabled).toEqual({
      value: false,
      source: 'organisation',
      unreadable: true,
    })
  })

  it('switches the export off when the machine carries an enabled value that cannot be read', () => {
    const f = fixture('factory-enabled-machine-unreadable-')

    expect(read(f, { OTEL_CUSTOMER_ENABLED: 'yes' }).shape.enabled).toEqual({
      value: false,
      source: 'OTEL_CUSTOMER_ENABLED',
      unreadable: true,
    })
  })
})

describe('the granularity a value that cannot be read resolves to', () => {
  it('resolves an unrecognised organisation granularity to aggregate', () => {
    const f = fixture('factory-granularity-organisation-unknown-')
    writeOrgTelemetry(f, { granularity: 'otel' })

    expect(read(f, {}).shape.granularity).toEqual({
      value: 'aggregate',
      source: 'organisation',
      unreadable: true,
    })
  })

  it('resolves a granularity of the wrong type to aggregate', () => {
    const f = fixture('factory-granularity-organisation-wrong-type-')
    writeOrgTelemetry(f, { granularity: true })

    expect(read(f, {}).shape.granularity).toEqual({
      value: 'aggregate',
      source: 'organisation',
      unreadable: true,
    })
  })

  it('resolves an unrecognised machine granularity to aggregate', () => {
    const f = fixture('factory-granularity-machine-unknown-')

    expect(read(f, { OTEL_TELEMETRY_GRANULARITY: 'otel' }).shape.granularity).toEqual({
      value: 'aggregate',
      source: 'OTEL_TELEMETRY_GRANULARITY',
      unreadable: true,
    })
  })

  it('resolves to aggregate when the organisation settings file cannot be read at all', () => {
    const f = fixture('factory-granularity-unreadable-file-')
    writeFileSync(join(f.root, 'org-managed-settings.cache.json'), '{ not json\n')

    expect(read(f, {}).shape.granularity).toEqual({
      value: 'aggregate',
      source: 'organisation',
      unreadable: true,
    })
  })

  it('keeps the documented default when nothing pins a granularity at all', () => {
    const f = fixture('factory-granularity-default-')

    // The published default is `user`. The aggregate fallback covers a value that cannot
    // be READ, which is a different case from a value that was never set.
    expect(read(f, {}).shape.granularity).toEqual({ value: 'user', source: 'default' })
  })

  it("never reads the owner's own settings file, so a malformed one cannot change the shape", () => {
    const f = fixture('factory-granularity-owner-file-')
    writeFileSync(join(f.root, 'settings.json'), '{ not json\n')

    // A file the reader never opens cannot make a granularity unreadable.
    expect(read(f, {}).shape.granularity).toEqual({ value: 'user', source: 'default' })
  })
})

describe('the native export verdict the shape travels with', () => {
  it('names the endpoint variable it read, before the documented fallback', () => {
    const f = fixture('factory-verdict-endpoint-variable-')

    expect(
      read(f, {
        OTEL_TELEMETRY_ENDPOINT: 'http://127.0.0.1:4318',
        OTEL_EXPORTER_OTLP_ENDPOINT: 'http://otel.example.com:4318',
      }),
    ).toMatchObject({ state: 'available', source: 'OTEL_TELEMETRY_ENDPOINT' })
  })

  it('falls back to the documented endpoint variable when the first is unset', () => {
    const f = fixture('factory-verdict-endpoint-fallback-')

    expect(read(f, { OTEL_EXPORTER_OTLP_ENDPOINT: 'http://127.0.0.1:4318' })).toMatchObject({
      state: 'available',
      source: 'OTEL_EXPORTER_OTLP_ENDPOINT',
    })
  })

  it('names the organisation as the cause when it pins the endpoint', () => {
    const f = fixture('factory-verdict-organisation-pin-')
    writeOrgTelemetry(f, { endpoint: 'https://otlp.acme.example' })

    expect(read(f, { OTEL_TELEMETRY_ENDPOINT: 'http://127.0.0.1:4318' })).toMatchObject({
      state: 'unavailable',
      source: 'organisation',
      cause: 'organisation-pinned',
    })
  })

  it('reports not configured when nothing configures the export', () => {
    const f = fixture('factory-verdict-unset-')

    expect(read(f, {}).state).toBe('not-configured')
  })
})

describe("the organisation's cached settings live in the harness's own directory", () => {
  /** The endpoint the organisation pins in its cached view. */
  const ORG_ENDPOINT = 'https://otlp.pinned.example.test/v1/traces'
  /** The endpoint the machine variable names: this KyberDash receiver. */
  const MACHINE_ENDPOINT = 'http://127.0.0.1:4318/v1/traces'

  /**
   * A machine whose `FACTORY_DIR` points somewhere else.
   *
   * `FACTORY_DIR` moves the harness's SESSIONS tree and nothing else (D55): the harness
   * still keeps its own settings and its cached organisation settings in its OWN
   * directory, `<home>/.factory`. The two directories are therefore separate here, and
   * the reader is handed the redirected one as its Factory root.
   */
  function redirected(prefix: string): {
    sources: NativeExportSources
    /** The directory `FACTORY_DIR` points at. */
    factoryDir: string
    /** The harness's own directory. */
    harnessDir: string
  } {
    const home = mkdtempSync(join(tmpdir(), prefix))
    tempDirs.push(home)
    const harnessDir = join(home, '.factory')
    mkdirSync(harnessDir, { recursive: true })
    const factoryDir = mkdtempSync(join(tmpdir(), `${prefix}factory-dir-`))
    tempDirs.push(factoryDir)
    return {
      sources: { factoryRoot: factoryDir, home, platform: process.platform },
      factoryDir,
      harnessDir,
    }
  }

  /**
   * The organisation's cached view, as the harness caches it: the `managedSettings.telemetry`
   * block under `org-managed-settings.cache.json` in the directory it was given.
   */
  function writeCachedOrganisationSettings(dir: string, telemetry: Record<string, unknown>): void {
    mkdirSync(dir, { recursive: true })
    writeFileSync(
      join(dir, 'org-managed-settings.cache.json'),
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
  }

  it("reads the organisation's pin from the harness's own directory, not from the directory FACTORY_DIR points at", () => {
    const f = redirected('factory-org-cache-own-dir-')
    // The organisation's cached settings are where the harness writes them: in its own
    // directory. The directory FACTORY_DIR points at holds no such file.
    writeCachedOrganisationSettings(f.harnessDir, {
      endpoint: ORG_ENDPOINT,
      format: 'genai',
      enabled: false,
    })

    const verdict = readNativeExport(f.sources, {
      processEnv: { OTEL_TELEMETRY_ENDPOINT: MACHINE_ENDPOINT },
    })

    // The organisation's pin wins over the machine variable, so the verdict names the
    // organisation's endpoint and never the machine's.
    expect(endpointOf(verdict), 'the organisation pin wins over the machine variable').toBe(
      ORG_ENDPOINT,
    )
    expect(endpointOf(verdict)).not.toBe(MACHINE_ENDPOINT)
    expect(verdict.state).toBe('unavailable')
    expect(verdict).toMatchObject({ source: 'organisation', cause: 'organisation-pinned' })
    // The shape travels from the same cached block.
    expect(verdict.shape.format).toEqual({ value: 'genai', source: 'organisation' })
    expect(verdict.shape.enabled).toEqual({ value: false, source: 'organisation' })
  })

  it('does not read the organisation pin from the directory FACTORY_DIR points at', () => {
    const f = redirected('factory-org-cache-factory-dir-')
    // A file with that name under FACTORY_DIR is not where the harness writes its cached
    // organisation settings, so it is not the organisation's pin.
    writeCachedOrganisationSettings(f.factoryDir, { endpoint: ORG_ENDPOINT })

    const verdict = readNativeExport(f.sources, {
      processEnv: { OTEL_TELEMETRY_ENDPOINT: MACHINE_ENDPOINT },
    })

    expect(endpointOf(verdict), 'the file under FACTORY_DIR is not the organisation pin').not.toBe(
      ORG_ENDPOINT,
    )
    expect(endpointOf(verdict)).toBe(MACHINE_ENDPOINT)
    expect(verdict).toMatchObject({ state: 'available', source: 'OTEL_TELEMETRY_ENDPOINT' })
  })
})
