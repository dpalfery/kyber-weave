// The Factory (Droid) plugin envelope — the contract-version guard at ingest.
//
// The Kyber-owned Factory plugin emits one span per session, turn, tool call
// and child session, with their identity under the `factory.*` namespace. Every
// envelope span names the contract it was emitted under in
// `factory.contract_version`. Ingest claims an envelope span only when that
// version is the supported one; a span naming any other version, or none,
// scores zero and quarantines under the named unknown-version reason, so it is
// never claimed and never mis-attributed to any harness — the same guard the
// OpenCode envelope carries.
//
// The envelope module stays a LEAF (mirroring
// `src/architecture/opencode-leaves.test.ts`): it may not value-import engine
// or plugin code, so the contract strings below repeat the wire contract's.
// An `import type` is erased at build time and carries no behaviour, so it is
// allowed; a runtime import of any project module or package is a finding.
//
// RED: written before `droid-envelope.ts` and `droid.ts` exist. Against the
// current tree every case below fails with the module under test missing.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import type { OtlpSpan } from '../../otel/receiver.js'
import { ingestBatchWithAdapters } from '../ingest.js'
import { CanonStore } from '../store.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

/** The `dash/src` root the leaf scanner resolves source paths against. */
const SRC_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')

/** The session the plugin's spans belong to. */
const SESSION = 'droid-session-1'
/** The contract version the Kyber-owned plugin emits. */
const CONTRACT_VERSION = '1'
/** A version no contract will ever carry. */
const FUTURE_VERSION = '9999-future'

/**
 * The modules under test, loaded on demand.
 *
 * A static import would fail the whole file before a single case ran, so the
 * report would say nothing about the behaviour each case requires. Loading on
 * demand fails every case with the missing module named, and the case names
 * state what the envelope must do.
 */
let envelopeModule: Promise<typeof import('./droid-envelope.js')> | undefined
let droidModule: Promise<typeof import('./droid.js')> | undefined

function loadEnvelope(): Promise<typeof import('./droid-envelope.js')> {
  envelopeModule ??= import('./droid-envelope.js')
  return envelopeModule
}

function loadDroid(): Promise<typeof import('./droid.js')> {
  droidModule ??= import('./droid.js')
  return droidModule
}

/** The attributes of one plugin envelope span. */
function envelopeAttributes(version: string = CONTRACT_VERSION): Record<string, unknown> {
  return {
    'factory.contract_version': version,
    'factory.session.id': SESSION,
    'factory.session_id': SESSION,
    'factory.sessionId': SESSION,
    'factory.model.id': 'gpt-5-codex',
    'factory.model': 'gpt-5-codex',
  }
}

/** A copy of `attributes` without `key`. */
function without(attributes: Record<string, unknown>, key: string): Record<string, unknown> {
  const copy = { ...attributes }
  delete copy[key]
  return copy
}

/** A decoded OTLP span, built whole so no field is left to a cast. */
function otlpSpan(
  spanId: string,
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    name: 'factory.turn',
    kind: 'internal',
    startTimeUnixNano: '1793337355759000000',
    endTimeUnixNano: '1793337363002000000',
    timestamp: '2026-10-30T12:00:00.000Z',
    durationMs: 1000,
    status: { code: 'unset' },
    resource: { 'service.name': 'kyberdash-droid-plugin' },
    scope: {},
    ...over,
    spanId,
    attributes,
  }
}

/** One import the module makes, and whether it is type-only. */
type ModuleImport = { specifier: string; typeOnly: boolean }

/** Every import, re-export and dynamic import the source makes. */
function importsOf(source: string): ModuleImport[] {
  const file = ts.createSourceFile('leaf.ts', source, ts.ScriptTarget.Latest, true)
  const found: ModuleImport[] = []
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const clause = node.importClause
      const typeOnly =
        clause !== undefined &&
        (clause.isTypeOnly ||
          (clause.name === undefined &&
            clause.namedBindings !== undefined &&
            ts.isNamedImports(clause.namedBindings) &&
            clause.namedBindings.elements.length > 0 &&
            clause.namedBindings.elements.every((element) => element.isTypeOnly)))
      found.push({ specifier: node.moduleSpecifier.text, typeOnly })
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      found.push({ specifier: node.moduleSpecifier.text, typeOnly: node.isTypeOnly })
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      found.push({ specifier: node.arguments[0].text, typeOnly: false })
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}

describe('the droid envelope contract version', () => {
  it('names the version under the factory namespace', async () => {
    const { DROID_CONTRACT_VERSION_KEY } = await loadEnvelope()
    expect(DROID_CONTRACT_VERSION_KEY).toBe('factory.contract_version')
  })

  it('supports contract "1" and no other version', async () => {
    const { DROID_CONTRACT_VERSION_KEY, isSupportedDroidEnvelope } = await loadEnvelope()
    expect(
      isSupportedDroidEnvelope(
        rawSpan({ spanId: 'supported', attributes: envelopeAttributes() }),
      ),
    ).toBe(true)
    for (const version of ['0', '2', FUTURE_VERSION]) {
      expect(
        isSupportedDroidEnvelope(
          rawSpan({ spanId: `unsupported-${version}`, attributes: envelopeAttributes(version) }),
        ),
        version,
      ).toBe(false)
    }
    expect(
      isSupportedDroidEnvelope(
        rawSpan({
          spanId: 'unsupported-missing',
          attributes: without(envelopeAttributes(), DROID_CONTRACT_VERSION_KEY),
        }),
      ),
    ).toBe(false)
  })

  it('recognises a factory-marked span as the envelope, whatever version it names', async () => {
    const { isDroidEnvelopeSpan } = await loadEnvelope()
    expect(
      isDroidEnvelopeSpan(
        rawSpan({ spanId: 'envelope-versioned', attributes: envelopeAttributes(FUTURE_VERSION) }),
      ),
    ).toBe(true)
    expect(
      isDroidEnvelopeSpan(
        rawSpan({ spanId: 'envelope-marked', attributes: { 'factory.session.id': SESSION } }),
      ),
    ).toBe(true)
    // The native export's vocabulary is not the envelope.
    expect(
      isDroidEnvelopeSpan(
        rawSpan({
          spanId: 'native',
          attributes: { 'gen_ai.agent.name': 'droid', 'gen_ai.operation.name': 'chat' },
        }),
      ),
    ).toBe(false)
    expect(isDroidEnvelopeSpan(rawSpan({ spanId: 'empty', attributes: {} }))).toBe(false)
  })

  it('treats a factory span with its version removed as the envelope, so it quarantines by version', async () => {
    const { DROID_CONTRACT_VERSION_KEY, isDroidEnvelopeSpan, isSupportedDroidEnvelope } =
      await loadEnvelope()
    const attributes = without(envelopeAttributes(), DROID_CONTRACT_VERSION_KEY)
    expect(isDroidEnvelopeSpan(rawSpan({ spanId: 'envelope-no-version', attributes }))).toBe(true)
    expect(
      isSupportedDroidEnvelope(rawSpan({ spanId: 'envelope-no-version', attributes })),
    ).toBe(false)
  })

  it('does not mistake a lookalike key for a factory marker', async () => {
    const { isDroidEnvelopeSpan } = await loadEnvelope()
    expect(
      isDroidEnvelopeSpan(
        rawSpan({ spanId: 'lookalike', attributes: { 'factoryx.session.id': SESSION } }),
      ),
    ).toBe(false)
  })
})

describe('ingestBatch — the droid envelope contract version', () => {
  it('claims a factory envelope span as a droid record', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    const outcome = ingestBatchWithAdapters(
      [otlpSpan('env-turn', envelopeAttributes())],
      store,
      [droidAdapter],
    )

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    expect(store.get('env-turn')?.harness).toBe('droid')
    store.close()
  })

  it.each([
    { label: 'an older contract', version: '0' },
    { label: 'an unknown version', version: FUTURE_VERSION },
  ])(
    'quarantines a span naming $label under the unknown-version reason, and never attributes it',
    async ({ version }) => {
      const { UNKNOWN_DROID_CONTRACT_VERSION_REASON } = await loadEnvelope()
      const { droidAdapter } = await loadDroid()
      const spanId = `env-${version}`
      const attributes = envelopeAttributes(version)
      const store = new CanonStore(':memory:')
      const outcome = ingestBatchWithAdapters([otlpSpan(spanId, attributes)], store, [droidAdapter])

      expect(outcome).toEqual({ accepted: 0, quarantined: 1, rejected: 0 })
      expect(store.get(spanId)).toBeUndefined()
      const reason = store.getQuarantine(spanId)?.reason
      expect(reason).toBe(UNKNOWN_DROID_CONTRACT_VERSION_REASON)
      expect(reason).not.toBe('unclaimed')
      expect(UNKNOWN_DROID_CONTRACT_VERSION_REASON).toMatch(/droid/i)
      store.close()

      const attributed = new AdapterRegistry([droidAdapter]).attribute([
        rawSpan({
          spanId,
          source: 'kyberdash-droid-plugin',
          traceId: `trace-${spanId}`,
          attributes,
        }),
      ])
      expect(attributed.get(spanId)).toBeUndefined()
    },
  )

  it('quarantines a span with no contract version rather than claiming it', async () => {
    const { DROID_CONTRACT_VERSION_KEY, UNKNOWN_DROID_CONTRACT_VERSION_REASON } = await loadEnvelope()
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    const outcome = ingestBatchWithAdapters(
      [
        otlpSpan(
          'env-missing',
          without(envelopeAttributes(), DROID_CONTRACT_VERSION_KEY),
        ),
      ],
      store,
      [droidAdapter],
    )

    expect(outcome).toEqual({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('env-missing')).toBeUndefined()
    expect(store.getQuarantine('env-missing')?.reason).toBe(UNKNOWN_DROID_CONTRACT_VERSION_REASON)
    store.close()
  })

  it('accounts every row of a mixed batch in the outcome', async () => {
    const { DROID_CONTRACT_VERSION_KEY } = await loadEnvelope()
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    const outcome = ingestBatchWithAdapters(
      [
        otlpSpan('mix-turn', envelopeAttributes()),
        otlpSpan('mix-old', envelopeAttributes('0')),
        otlpSpan('mix-unknown', envelopeAttributes(FUTURE_VERSION)),
        otlpSpan('mix-missing', without(envelopeAttributes(), DROID_CONTRACT_VERSION_KEY)),
      ],
      store,
      [droidAdapter],
    )

    expect(outcome).toEqual({ accepted: 1, quarantined: 3, rejected: 0 })
    store.close()
  })
})

describe('the droid envelope module stays a leaf', () => {
  it('imports no project module and no package at run time', () => {
    const source = readFileSync(path.join(SRC_ROOT, 'canon/adapters/droid-envelope.ts'), 'utf8')
    const imports = importsOf(source)

    const runtime = imports.filter(
      (entry) => !entry.typeOnly && !entry.specifier.startsWith('node:'),
    )
    expect(
      runtime.map((entry) => entry.specifier),
      'droid-envelope.ts must stay an import-free leaf; a runtime import would drag engine code into the contract',
    ).toEqual([])

    const packages = imports.filter(
      (entry) => !entry.specifier.startsWith('node:') && !entry.specifier.startsWith('.'),
    )
    expect(
      packages.map((entry) => entry.specifier),
      'droid-envelope.ts must not depend on a third-party package',
    ).toEqual([])
  })
})
