// One resolver owns "which canon.db".
//
// WHY: three call sites used to compute the path independently — the CLI
// (`dash refresh`/`dash clean`), the OTLP receiver, and the dashboard
// server's `KyberBridge`. Each applied a different subset of the precedence
// explicit > KYBER_CANON_DB > ~/.kyberdash/canon.db, so with
// KYBER_CANON_DB set the CLI wrote one file while the dashboard server read
// another. The user-visible symptom was a wipe from the tray that appeared to
// do nothing: the clean went to the env-var file, the server was still
// serving the default one. Every resolver now routes through this function,
// so "which file" has exactly one answer and cannot drift per surface again.
//
// The home directory is read at call time, not at module load, so a process
// that redirects HOME (tests, and an operator overriding the state location)
// is answered from the environment it is actually running in.

import { homedir } from 'node:os'
import { join } from 'node:path'

/**
 * Resolve the canonical store path: an explicit `--db`/option value wins,
 * then a non-empty `KYBER_CANON_DB`, then `~/.kyberdash/canon.db`.
 *
 * Empty strings are treated as absent at every level — an unset flag that
 * commander's parser filled in with '' must not win over the environment.
 */
export function resolveCanonDbPath(explicit?: string): string {
  if (explicit) return explicit
  const fromEnv = process.env.KYBER_CANON_DB
  if (fromEnv) return fromEnv
  return join(homedir(), '.kyberdash', 'canon.db')
}
