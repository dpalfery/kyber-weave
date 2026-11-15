/**
 * Source-unit checkpoints and record provenance for harness-source refresh.
 *
 * These types are the in-memory shape of the additive v11 relations. Coverage
 * arithmetic lives here so the store only persists; a parser-contract bump
 * invalidates reuse without implying that canonical rows should be deleted.
 */

export type SourceCheckpointStatus =
  | 'ok'
  | 'partial'
  | 'failed'
  | 'invalidated'
  | 'unchanged'
  | 'unavailable'

export type CoverageInterval = {
  fromUtc: string
  throughUtc: string
}

export type CoverageRequest = {
  revisionToken: string
  parserContractVersion: string
}

export type SourceCheckpoint = {
  harnessId: string
  sourceKey: string
  providerId: string
  parserId: string
  parserContractVersion: string
  format: string
  sourceRootLabel: string
  revisionToken: string
  coveredFromUtc: string
  coveredThroughUtc: string
  lastAttemptUtc: string
  lastSuccessUtc: string | null
  lastStatus: SourceCheckpointStatus
  lastErrorCode: string | null
  unitCount: number
  recordCount: number
}

export type RecordProvenance = {
  spanId: string
  harnessId: string
  sourceKey: string
  nativeSessionId: string | null
  nativeRecordId: string | null
  sourceRevision: string
  parserVersion: string
  importedAtUtc: string
  locationToken: string
}

/** Additive relations and indexes created at schema 11. */
export const SOURCE_STATE_SQL = `
CREATE TABLE IF NOT EXISTS source_checkpoint (
  harness_id TEXT NOT NULL,
  source_key TEXT NOT NULL,
  provider_id TEXT NOT NULL,
  parser_id TEXT NOT NULL,
  parser_contract_version TEXT NOT NULL,
  format TEXT NOT NULL,
  source_root_label TEXT NOT NULL,
  revision_token TEXT NOT NULL,
  covered_from_utc TEXT NOT NULL,
  covered_through_utc TEXT NOT NULL,
  last_attempt_utc TEXT NOT NULL,
  last_success_utc TEXT,
  last_status TEXT NOT NULL,
  last_error_code TEXT,
  unit_count INTEGER NOT NULL DEFAULT 0,
  record_count INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (harness_id, source_key)
);
CREATE INDEX IF NOT EXISTS source_checkpoint_by_harness ON source_checkpoint (harness_id);

CREATE TABLE IF NOT EXISTS record_provenance (
  span_id TEXT NOT NULL PRIMARY KEY CHECK (length(span_id) > 0),
  harness_id TEXT NOT NULL,
  source_key TEXT NOT NULL,
  native_session_id TEXT,
  native_record_id TEXT,
  source_revision TEXT NOT NULL,
  parser_version TEXT NOT NULL,
  imported_at_utc TEXT NOT NULL,
  location_token TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS record_provenance_by_source ON record_provenance (harness_id, source_key);
CREATE INDEX IF NOT EXISTS record_provenance_by_native
  ON record_provenance (harness_id, native_session_id, native_record_id);

CREATE INDEX IF NOT EXISTS records_by_harness_session_time
  ON records (harness, session_id, timestamp);
`

export function checkpointIsReusable(
  checkpoint: SourceCheckpoint | undefined,
  request: CoverageRequest,
): boolean {
  if (checkpoint === undefined) return false
  if (checkpoint.lastStatus === 'invalidated') return false
  if (checkpoint.parserContractVersion !== request.parserContractVersion) return false
  if (checkpoint.revisionToken !== request.revisionToken) return false
  return true
}

/**
 * Intervals inside `requested` that still need processing for this native unit.
 * A changed revision or parser contract reopens the whole request; an expanded
 * history window only returns the newly uncovered prefix and/or suffix.
 */
export function uncoveredIntervals(
  checkpoint: SourceCheckpoint | undefined,
  requested: CoverageInterval,
  request: CoverageRequest,
): CoverageInterval[] {
  if (checkpoint === undefined || !checkpointIsReusable(checkpoint, request)) {
    return [requested]
  }
  return subtractClosed(requested, {
    fromUtc: checkpoint.coveredFromUtc,
    throughUtc: checkpoint.coveredThroughUtc,
  })
}

function subtractClosed(requested: CoverageInterval, covered: CoverageInterval): CoverageInterval[] {
  const gaps: CoverageInterval[] = []
  if (requested.fromUtc < covered.fromUtc) {
    gaps.push({
      fromUtc: requested.fromUtc,
      throughUtc: requested.throughUtc < covered.fromUtc ? requested.throughUtc : covered.fromUtc,
    })
  }
  if (requested.throughUtc > covered.throughUtc) {
    gaps.push({
      fromUtc: requested.fromUtc > covered.throughUtc ? requested.fromUtc : covered.throughUtc,
      throughUtc: requested.throughUtc,
    })
  }
  return gaps
}

export type SourceCheckpointRow = {
  harness_id: unknown
  source_key: unknown
  provider_id: unknown
  parser_id: unknown
  parser_contract_version: unknown
  format: unknown
  source_root_label: unknown
  revision_token: unknown
  covered_from_utc: unknown
  covered_through_utc: unknown
  last_attempt_utc: unknown
  last_success_utc: unknown
  last_status: unknown
  last_error_code: unknown
  unit_count: unknown
  record_count: unknown
}

export type RecordProvenanceRow = {
  span_id: unknown
  harness_id: unknown
  source_key: unknown
  native_session_id: unknown
  native_record_id: unknown
  source_revision: unknown
  parser_version: unknown
  imported_at_utc: unknown
  location_token: unknown
}

export function toSourceCheckpoint(row: SourceCheckpointRow): SourceCheckpoint {
  return {
    harnessId: row.harness_id as string,
    sourceKey: row.source_key as string,
    providerId: row.provider_id as string,
    parserId: row.parser_id as string,
    parserContractVersion: row.parser_contract_version as string,
    format: row.format as string,
    sourceRootLabel: row.source_root_label as string,
    revisionToken: row.revision_token as string,
    coveredFromUtc: row.covered_from_utc as string,
    coveredThroughUtc: row.covered_through_utc as string,
    lastAttemptUtc: row.last_attempt_utc as string,
    lastSuccessUtc: (row.last_success_utc as string | null) ?? null,
    lastStatus: row.last_status as SourceCheckpointStatus,
    lastErrorCode: (row.last_error_code as string | null) ?? null,
    unitCount: row.unit_count as number,
    recordCount: row.record_count as number,
  }
}

export function toRecordProvenance(row: RecordProvenanceRow): RecordProvenance {
  return {
    spanId: row.span_id as string,
    harnessId: row.harness_id as string,
    sourceKey: row.source_key as string,
    nativeSessionId: (row.native_session_id as string | null) ?? null,
    nativeRecordId: (row.native_record_id as string | null) ?? null,
    sourceRevision: row.source_revision as string,
    parserVersion: row.parser_version as string,
    importedAtUtc: row.imported_at_utc as string,
    locationToken: row.location_token as string,
  }
}
