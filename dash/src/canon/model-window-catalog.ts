// Vendor-documented model windows (D15). The bytes are the bundled synthetic
// fixtures, not a live fetch: the contract's vendors are invented, a refresh
// must not accept a caller-supplied URL, and the suite must not use the
// network or a credential. The https URL on each source is the provenance
// stamped onto rows, owned by this registry alone.

import type { Finding } from '../analysis/findings.js'
import { detectCompactionHazard } from '../analysis/findings.js'
import type { SignalResult } from '../analysis/signals.js'
import {
  CONTEXT_LIMIT_KEYS,
  DECLARED_CONTEXT_LIMIT_KEY,
  modelIdentityOf,
  type ContextWindow,
} from './context-window.js'
import { readModelCatalogFixture } from './fixtures/model-window-catalog/fixtures.js'
import type { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'

export type ModelWindowCatalogVendor = 'synth-vendor-a' | 'synth-vendor-b'

export type ModelWindowCatalogSource = {
  documentationUrl: string
  /** Bundled document this source is read from. Not a caller input. */
  fixture: string
}

/**
 * The only documentation sources a refresh may read. Two entries because the
 * contract's vendors are the synthetic pair; adding a real host here would
 * change the key set the registry test pins and would require a network.
 */
export const MODEL_WINDOW_CATALOG_SOURCES: Record<ModelWindowCatalogVendor, ModelWindowCatalogSource> = {
  'synth-vendor-a': {
    documentationUrl: 'https://example.com/kyberdash/synth-vendor-a/model-windows.json',
    fixture: 'vendor-a-initial.json',
  },
  'synth-vendor-b': {
    documentationUrl: 'https://example.com/kyberdash/synth-vendor-b/model-windows.json',
    fixture: 'vendor-b-initial.json',
  },
}

const VENDORS = Object.keys(MODEL_WINDOW_CATALOG_SOURCES) as ModelWindowCatalogVendor[]

/** Cap so a failed vendor cannot echo a remote body back through the API. */
const MAX_VENDOR_ERROR_CHARS = 160

export type VendorCatalogModel = {
  lookupId: string
  canonicalModelId: string
  contextWindow: number
  sourceRevision?: string
}

export type VendorCatalogDocument = {
  models: VendorCatalogModel[]
  sourceRevision?: string
}

export type ModelCatalogRow = {
  lookupId: string
  canonicalModelId: string
  contextWindow: number
  vendor: ModelWindowCatalogVendor
  documentationUrl: string
  retrievedAt: string
  sourceRevision?: string
}

export type ModelCatalogVendorStatus = 'ok' | 'error' | 'unknown'

export type ModelCatalogVendorSnapshot = {
  documentationUrl: string
  status: ModelCatalogVendorStatus
  rowCount: number
  lastRefreshAt: string | null
  error?: string
}

export type ModelCatalogSnapshot = {
  rowCount: number
  lastRefreshAt: string | null
  vendors: Record<ModelWindowCatalogVendor, ModelCatalogVendorSnapshot>
}

export type ModelWindowCatalogRefreshResult = {
  vendorsUpdated: ModelWindowCatalogVendor[]
  vendorsFailed: { vendor: ModelWindowCatalogVendor; error: string }[]
}

export type RefreshModelWindowCatalogOptions = {
  /**
   * Reads one vendor's already-parsed document. The registry chooses which
   * document; this callback cannot supply a URL.
   */
  readVendor: (vendor: ModelWindowCatalogVendor) => VendorCatalogDocument
  now?: () => string
  /**
   * Invoked once after the loop when at least one vendor's rows were
   * replaced. A total failure does not call it: there is nothing new for
   * the derived tables to observe.
   */
  rebuildDerived?: () => void
}

type CatalogDb = {
  exec(sql: string): unknown
  prepare(sql: string): {
    get(...params: unknown[]): unknown
    run(...params: unknown[]): unknown
    all(...params: unknown[]): unknown
  }
}

function catalogDb(store: CanonStore): CatalogDb {
  return store.getDatabase() as unknown as CatalogDb
}

function isVendor(value: string): value is ModelWindowCatalogVendor {
  return Object.prototype.hasOwnProperty.call(MODEL_WINDOW_CATALOG_SOURCES, value)
}

function boundedVendorError(err: unknown): string {
  const message = err instanceof Error ? err.message : ''
  const oneLine = message.replace(/[\r\n\t]+/g, ' ').trim()
  if (oneLine.length === 0 || oneLine.length > MAX_VENDOR_ERROR_CHARS) return 'invalid context window'
  return oneLine
}

function optionalRevision(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const trimmed = value.trim()
  if (trimmed.length === 0 || trimmed.length > 80) return undefined
  return trimmed
}

function requireModelId(value: unknown): string {
  if (typeof value !== 'string' || value.length === 0 || value !== value.trim()) {
    throw new Error('invalid model id')
  }
  return value
}

function requirePositiveWindow(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value <= 0) {
    throw new Error('invalid context window: expected a positive integer')
  }
  return value
}

/**
 * Parse and validate one vendor document. Throws before any caller opens a
 * transaction: a negative window or a missing id must not delete the rows
 * already known to be good.
 */
export function parseVendorCatalogDocument(raw: unknown): VendorCatalogDocument {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    throw new Error('invalid context window catalog')
  }
  const body = raw as Record<string, unknown>
  if (!Array.isArray(body.models) || body.models.length === 0) {
    throw new Error('invalid context window catalog')
  }
  const sourceRevision = optionalRevision(body.sourceRevision)
  const seen = new Set<string>()
  const models: VendorCatalogModel[] = []
  for (const entry of body.models) {
    if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new Error('invalid model id')
    }
    const model = entry as Record<string, unknown>
    const lookupId = requireModelId(model.lookupId)
    const canonicalModelId = requireModelId(model.canonicalModelId)
    if (seen.has(lookupId)) throw new Error('invalid model id')
    seen.add(lookupId)
    const contextWindow = requirePositiveWindow(model.contextWindow)
    const rowRevision = optionalRevision(model.sourceRevision) ?? sourceRevision
    models.push({
      lookupId,
      canonicalModelId,
      contextWindow,
      ...(rowRevision !== undefined ? { sourceRevision: rowRevision } : {}),
    })
  }
  return {
    models,
    ...(sourceRevision !== undefined ? { sourceRevision } : {}),
  }
}

/** The registry's bundled document for `vendor`. No URL argument, no credentials. */
export function readBundledVendorCatalog(vendor: ModelWindowCatalogVendor): VendorCatalogDocument {
  const source = MODEL_WINDOW_CATALOG_SOURCES[vendor]
  return parseVendorCatalogDocument(readModelCatalogFixture(source.fixture))
}

function replaceVendorRows(
  db: CatalogDb,
  vendor: ModelWindowCatalogVendor,
  document: VendorCatalogDocument,
  retrievedAt: string,
): void {
  const documentationUrl = MODEL_WINDOW_CATALOG_SOURCES[vendor].documentationUrl
  const deleteRows = db.prepare('DELETE FROM model_context_window_catalog WHERE vendor = ?')
  const insertRow = db.prepare(
    `INSERT INTO model_context_window_catalog (
       lookup_id, canonical_model_id, context_window, vendor, documentation_url, retrieved_at, source_revision
     ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
  )
  const markOk = db.prepare(
    `INSERT INTO model_context_window_catalog_vendor (vendor, status, last_refresh_at, last_error)
     VALUES (?, 'ok', ?, NULL)
     ON CONFLICT(vendor) DO UPDATE SET
       status = 'ok',
       last_refresh_at = excluded.last_refresh_at,
       last_error = NULL`,
  )
  db.exec('BEGIN')
  try {
    deleteRows.run(vendor)
    for (const model of document.models) {
      insertRow.run(
        model.lookupId,
        model.canonicalModelId,
        model.contextWindow,
        vendor,
        documentationUrl,
        retrievedAt,
        model.sourceRevision ?? null,
      )
    }
    markOk.run(vendor, retrievedAt)
    db.exec('COMMIT')
  } catch (err) {
    db.exec('ROLLBACK')
    throw err
  }
}

function markVendorFailed(db: CatalogDb, vendor: ModelWindowCatalogVendor, error: string): void {
  // Status only. The catalog rows stay: a failed document must not empty a
  // vendor that already validated, and it must not invent a window.
  db.exec('BEGIN')
  try {
    db.prepare(
      `INSERT INTO model_context_window_catalog_vendor (vendor, status, last_refresh_at, last_error)
       VALUES (?, 'error', NULL, ?)
       ON CONFLICT(vendor) DO UPDATE SET
         status = 'error',
         last_error = excluded.last_error`,
    ).run(vendor, error)
    db.exec('COMMIT')
  } catch (err) {
    db.exec('ROLLBACK')
    throw err
  }
}

export function refreshModelWindowCatalog(
  store: CanonStore,
  options: RefreshModelWindowCatalogOptions,
): ModelWindowCatalogRefreshResult {
  const db = catalogDb(store)
  const now = options.now ?? (() => new Date().toISOString())
  const vendorsUpdated: ModelWindowCatalogVendor[] = []
  const vendorsFailed: { vendor: ModelWindowCatalogVendor; error: string }[] = []

  for (const vendor of VENDORS) {
    let document: VendorCatalogDocument
    try {
      document = parseVendorCatalogDocument(options.readVendor(vendor))
    } catch (err) {
      const error = boundedVendorError(err)
      markVendorFailed(db, vendor, error)
      vendorsFailed.push({ vendor, error })
      continue
    }
    try {
      replaceVendorRows(db, vendor, document, now())
    } catch (err) {
      const error = boundedVendorError(err)
      markVendorFailed(db, vendor, error)
      vendorsFailed.push({ vendor, error })
      continue
    }
    vendorsUpdated.push(vendor)
  }

  if (vendorsUpdated.length > 0) options.rebuildDerived?.()

  return { vendorsUpdated, vendorsFailed }
}

export function lookupModelCatalogRow(store: CanonStore, lookupId: string): ModelCatalogRow | undefined {
  const row = catalogDb(store)
    .prepare(
      `SELECT lookup_id, canonical_model_id, context_window, vendor, documentation_url, retrieved_at, source_revision
       FROM model_context_window_catalog WHERE lookup_id = ?`,
    )
    .get(lookupId) as
    | {
        lookup_id: string
        canonical_model_id: string
        context_window: number
        vendor: string
        documentation_url: string
        retrieved_at: string
        source_revision: string | null
      }
    | undefined
  if (row == null || !isVendor(row.vendor)) return undefined
  return {
    lookupId: row.lookup_id,
    canonicalModelId: row.canonical_model_id,
    contextWindow: row.context_window,
    vendor: row.vendor,
    documentationUrl: row.documentation_url,
    retrievedAt: row.retrieved_at,
    ...(row.source_revision !== null && row.source_revision !== '' ? { sourceRevision: row.source_revision } : {}),
  }
}

export function getModelCatalogSnapshot(store: CanonStore): ModelCatalogSnapshot {
  const db = catalogDb(store)
  const total = db.prepare('SELECT COUNT(*) AS n FROM model_context_window_catalog').get() as { n: number }
  const counts = db
    .prepare('SELECT vendor, COUNT(*) AS n FROM model_context_window_catalog GROUP BY vendor')
    .all() as { vendor: string; n: number }[]
  const countByVendor = new Map<string, number>()
  for (const count of counts) countByVendor.set(count.vendor, count.n)
  const statuses = db
    .prepare('SELECT vendor, status, last_refresh_at, last_error FROM model_context_window_catalog_vendor')
    .all() as { vendor: string; status: string; last_refresh_at: string | null; last_error: string | null }[]
  const statusByVendor = new Map(statuses.map((status) => [status.vendor, status]))

  const vendors = {} as Record<ModelWindowCatalogVendor, ModelCatalogVendorSnapshot>
  let lastRefreshAt: string | null = null
  for (const vendor of VENDORS) {
    const status = statusByVendor.get(vendor)
    const vendorLast = status?.last_refresh_at ?? null
    if (vendorLast !== null && (lastRefreshAt === null || vendorLast > lastRefreshAt)) lastRefreshAt = vendorLast
    const kind: ModelCatalogVendorStatus = status?.status === 'ok' || status?.status === 'error' ? status.status : 'unknown'
    const error = kind === 'error' && status?.last_error ? status.last_error : undefined
    vendors[vendor] = {
      documentationUrl: MODEL_WINDOW_CATALOG_SOURCES[vendor].documentationUrl,
      status: kind,
      rowCount: countByVendor.get(vendor) ?? 0,
      lastRefreshAt: vendorLast,
      ...(error !== undefined ? { error } : {}),
    }
  }

  return { rowCount: total.n, lastRefreshAt, vendors }
}

export type ResolvedContextWindow =
  | { measurable: true; contextLimit: number; contextLimitSource: 'reported' | 'declared' | 'catalog' }
  | { measurable: false; reason: string }

function attributeLimit(record: CanonicalRecord, keys: readonly string[]): string | undefined {
  const raw = record.raw
  if (raw === null || typeof raw !== 'object') return undefined
  const attributes = raw as Record<string, unknown>
  for (const key of keys) {
    const value = attributes[key]
    if (typeof value === 'string' && value !== '') return value
    if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  }
  return undefined
}

function usableLimit(named: string): number | undefined {
  const limit = Number(named)
  return Number.isFinite(limit) && limit > 0 ? limit : undefined
}

/**
 * Precedence is reported, then declared, then an exact catalog id or alias,
 * then not measurable. The 200K default is not a catalog hit: a miss stays
 * unmeasurable so a guessed denominator cannot be labelled vendor data.
 */
export function resolveContextWindowForModel(
  records: readonly CanonicalRecord[],
  modelId: string | undefined,
  store: CanonStore,
): ResolvedContextWindow {
  let firstDeclared: string | undefined
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const reported = attributeLimit(record, CONTEXT_LIMIT_KEYS)
    if (reported !== undefined) {
      const limit = usableLimit(reported)
      if (limit !== undefined) {
        return { measurable: true, contextLimit: limit, contextLimitSource: 'reported' }
      }
      return {
        measurable: false,
        reason: 'A reported context window was present but not a positive limit, so pressure is not measurable.',
      }
    }
    if (firstDeclared === undefined) {
      const declared = attributeLimit(record, [DECLARED_CONTEXT_LIMIT_KEY])
      if (declared !== undefined) firstDeclared = declared
    }
  }
  if (firstDeclared !== undefined) {
    const limit = usableLimit(firstDeclared)
    if (limit !== undefined) {
      return { measurable: true, contextLimit: limit, contextLimitSource: 'declared' }
    }
    return {
      measurable: false,
      reason: 'A declared context window was present but not a positive limit, so pressure is not measurable.',
    }
  }
  if (modelId === undefined || modelId === '') {
    return { measurable: false, reason: 'No model identity; the context window is not measurable.' }
  }
  const row = lookupModelCatalogRow(store, modelId)
  if (row === undefined) {
    return {
      measurable: false,
      reason: 'No exact model catalog row for this model; the context window is not measurable.',
    }
  }
  return { measurable: true, contextLimit: row.contextWindow, contextLimitSource: 'catalog' }
}

/**
 * Pressure against a catalog window is derived. The ratio is real arithmetic
 * on a documented denominator; it is not a measurement the harness reported.
 */
export function catalogContextPressure(
  peakInputTokens: number,
  window: { contextLimit: number; contextLimitSource: 'reported' | 'declared' | 'catalog' },
): SignalResult<number> {
  // The caller may not have narrowed the source. Anything other than catalog
  // is not this ratio: a reported window is measured elsewhere, and a
  // declared one has its own derived path.
  if (
    window.contextLimitSource !== 'catalog' ||
    !Number.isFinite(peakInputTokens) ||
    peakInputTokens < 0 ||
    !(window.contextLimit > 0)
  ) {
    return {
      status: 'not_measurable',
      reason: 'Catalog pressure needs a finite peak and a positive catalog window.',
    }
  }
  return {
    status: 'derived',
    value: peakInputTokens / window.contextLimit,
    measurementClass: 'inferred',
    confidence: 'medium',
    confidenceBasis:
      'Peak input divided by a vendor-catalog context window. The ratio is derived, never a harness-reported measurement.',
    numerator: peakInputTokens,
    denominator: window.contextLimit,
  }
}

export function analyzeCompactionWithModelCatalog(input: {
  records: readonly CanonicalRecord[]
  modelId?: string
  store: CanonStore
}): { findings: Finding[] } {
  const findings = detectCompactionHazard({
    records: input.records,
    resolveWindow: (groupRecords) => {
      const resolved = resolveContextWindowForModel(groupRecords, input.modelId ?? modelIdentityOf(groupRecords), input.store)
      if (!resolved.measurable || resolved.contextLimitSource !== 'catalog') return undefined
      return { contextLimit: resolved.contextLimit, contextLimitSource: 'catalog' }
    },
  })
  return { findings }
}

export function catalogWindowForRecords(
  records: readonly CanonicalRecord[],
  store: CanonStore,
): ContextWindow | undefined {
  const resolved = resolveContextWindowForModel(records, modelIdentityOf(records), store)
  if (!resolved.measurable || resolved.contextLimitSource !== 'catalog') return undefined
  return { contextLimit: resolved.contextLimit, contextLimitSource: 'catalog' }
}

export function formatUnknownWindowCoverageHint(): string {
  return 'A context window can be reported by session telemetry, declared by harness configuration, or taken from the vendor catalog. Until one of those covers the model, pressure is not measurable.'
}
