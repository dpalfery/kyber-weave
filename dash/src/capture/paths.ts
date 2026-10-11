// D14 — the platform-aware path seam for capture config resolution.
//
// WHY this module exists: harness paths used to join with the host's own
// `node:path` binding, so a Windows resolution could not be computed (or
// trusted) from a macOS or Linux checkout, and the capture command had to
// refuse win32 outright. Joining with the named platform's separator —
// `\` on win32, `/` anywhere else — makes a Windows path a pure string
// computation that any host can pin in a test (ADR 0031 amendment). XDG_*
// variables are deliberately not consulted here: every harness keeps
// resolving from the HOME it is given on every OS.

import { join as hostJoin, posix, win32 } from 'node:path'

/**
 * The path environment a harness resolves its config path against. The
 * platform picks the joining rules; `appData` is the Windows
 * application-data root the caller injected (tests use a temp directory),
 * ignored on every other platform.
 */
export type PathEnv = {
  platform: NodeJS.Platform
  appData?: string
}

/**
 * Join segments with the separator of `platform`: `\` on win32, `/` on any
 * other platform (darwin and linux are both posix). The first segment is
 * kept verbatim — only trailing separators are normalized away — because it
 * is the caller's root: a synthetic posix HOME under an injected win32
 * platform must survive unchanged for the resolution to stay a pure string
 * computation, and on a real Windows host the root is already win32-style,
 * so the opaqueness is invisible there. `path.win32.join` cannot be used
 * directly for this: it rewrites every `/` in the root to `\`.
 */
export function pjoin(platform: NodeJS.Platform, ...segments: string[]): string {
  const sep = platform === 'win32' ? win32.sep : posix.sep
  const parts = segments
    .filter((segment) => segment !== '')
    .map((segment) => segment.replace(/[\\/]+$/, ''))
  return parts.join(sep)
}

/**
 * Join for a harness `resolvePath`. With an explicit path environment the
 * named platform's rules apply (`pjoin`); without one the host's own
 * `node:path` join applies, exactly as before the platform seam existed, so
 * callers that never inject an environment keep host-native paths.
 */
export function envJoin(env: PathEnv | undefined, ...segments: string[]): string {
  return env ? pjoin(env.platform, ...segments) : hostJoin(...segments)
}
