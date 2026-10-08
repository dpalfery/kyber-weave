// Identity stripping at the receiver decode seam (Seam 3).
//
// OTLP span, resource and log attributes can carry account identity —
// `user.email`, `user.id`, `organization.id`, `enduser.id`, `*.account_id`,
// `*.account_uuid` and their `org`/`account` spelling variants. The receiver
// owns the decode step, so it strips those keys here, before anything is
// stored: what never reaches a decoded span cannot reach the stored `raw`.
//
// The rule is pattern-based rather than a fixed list so Phase 2 harnesses
// stay out of this file: any `email` key, any key ending in `account_id` or
// `account_uuid` (in any separator spelling), and any `id`/`uuid` key under
// an identity principal (`user`, `enduser`, `org`, `organization`,
// `account`) is identity. `session.id`, `conversation.id` and the whole
// `gen_ai.*` namespace are retained — they are correlation and usage
// evidence, not identity.

/** Attribute keys that are correlation, never identity. */
const RETAINED_KEYS = new Set(['session.id', 'conversation.id'])

/** Principal namespaces whose `id`/`uuid` keys identify an account. */
const IDENTITY_PRINCIPALS = new Set([
  'user',
  'users',
  'enduser',
  'endusers',
  'org',
  'orgs',
  'organization',
  'organizations',
  'account',
  'accounts',
])

/** Trailing key segments that identify an account on their own. */
const IDENTITY_SUFFIXES = new Set(['id', 'uuid', 'guid'])

function tokensOf(key: string): string[] {
  return key.toLowerCase().split(/[._-]+/).filter((token) => token.length > 0)
}

/**
 * True when an attribute key carries account identity. Matching is
 * case-insensitive and separator-insensitive (`user.id`, `user_id` and
 * `userId` are the same key shape); `gen_ai.*` and the retained
 * correlation keys never match.
 */
export function isIdentityAttribute(key: string): boolean {
  const lower = key.toLowerCase()
  if (lower.startsWith('gen_ai.')) return false
  if (RETAINED_KEYS.has(lower)) return false

  const tokens = tokensOf(key)
  if (tokens.length === 0) return false
  const last = tokens[tokens.length - 1]!

  // Any email key is identity (`user.email` and its org/account variants).
  if (last === 'email') return true

  const compact = lower.replace(/[._-]+/g, '')
  if (compact === 'email' || compact.endsWith('email')) {
    // CamelCase email shapes (`userEmail`); separator shapes already matched.
    const head = compact.slice(0, -'email'.length)
    if (head.length === 0 || [...IDENTITY_PRINCIPALS].some((p) => head.endsWith(p))) return true
  }

  // `*.account_id` / `*.account_uuid` in any separator or camel spelling.
  if (
    compact.endsWith('accountid') ||
    compact.endsWith('accountuuid') ||
    compact.endsWith('accountguid')
  ) {
    return true
  }

  // `user.id`, `organization.id`, `enduser.id` and their org/account id/uuid
  // variants, in dotted, underscored or camel spelling.
  if (IDENTITY_SUFFIXES.has(last)) {
    if (tokens.some((token) => IDENTITY_PRINCIPALS.has(token))) return true
    for (const principal of IDENTITY_PRINCIPALS) {
      for (const suffix of IDENTITY_SUFFIXES) {
        if (compact === `${principal}${suffix}`) return true
      }
    }
  }

  return false
}

/**
 * Copy an attribute map without its identity keys. The input is never
 * mutated; stripping is idempotent, so calling it twice keeps the same keys.
 */
export function stripIdentityAttributes(
  attributes: Record<string, unknown>,
): Record<string, unknown> {
  const stripped: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(attributes)) {
    if (!isIdentityAttribute(key)) stripped[key] = value
  }
  return stripped
}
