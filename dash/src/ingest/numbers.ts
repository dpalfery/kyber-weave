/// Finite-positive numeric helpers used by parsers and doctor.
/// Kept in a dependency-free leaf so diagnostic CLI code can share the
/// predicate without warming the ingest graph (parser.ts, providers, pricing).

export function safeNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0
}

export function isPositiveNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0
}
