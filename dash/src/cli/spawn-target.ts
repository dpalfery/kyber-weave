// Which executable a job child (refresh, import, clean, otel receiver) is spawned from.
//
// The child is always this CLI again — JobHost appends `['dash', 'refresh', ...]`, so the
// target has to be the CLI *root*, not a subcommand and not the entry script's directory.
// The question is only how this process itself was launched, and the three real shapes do
// not want the same answer:
//
//   1. A SEA single-file binary (`kyberdash web`). `process.execPath` is the binary itself
//      and takes no arguments: the CLI is already the whole program. Passing anything - in
//      particular the raw `process.argv[1]`, which in a SEA is the *unexpanded* argv0
//      (`kyberdash` from a PATH lookup, see install/node-deps.ts resolveKyberdashPath) -
//      produces `<binary> kyberdash dash refresh ...`, and the child fails on an unknown
//      command. So: execPath, no args.
//   2. A source checkout under tsx (`tsx src/launcher.ts web`). `process.execPath` is bare
//      node; the TypeScript entry only loads because of the flags tsx put in
//      `process.execArgv` (`--import <loader>`, `--require <preflight>`). Dropping
//      execArgv re-runs the same .ts entry with plain node and dies with
//      `Cannot find module .../src/cli/main.js`. So: execPath + execArgv + the entry.
//   3. A published npm package (`node dist/cli.js`, the `kyberdash` bin). No loader flags,
//      execArgv is empty or holds only interpreter flags; the entry is plain JS. Same
//      answer as (2) with an empty execArgv, which is why one branch covers both.
//
// When there is no entry to re-run (`argv[1]` missing - e.g. `node -e`, or a SEA that
// somehow reported otherwise) there is nothing reliable to spawn, so fall back to the
// `kyberdash` name on PATH: a missing PATH lookup surfaces as ENOENT in the job footer,
// which is honest, whereas re-running the wrong entry is not.

/** The program name a spawn falls back to when this process has no entry script to re-run. */
export const PATH_FALLBACK_PROGRAM = 'kyberdash'

/** A description of the process doing the spawning, injectable so the decision is testable. */
export type SpawnRuntime = {
  /** `process.execPath`: the SEA binary, or the node binary running a checkout. */
  readonly execPath: string
  /** `process.execArgv`: interpreter flags, including any tsx loader flags. */
  readonly execArgv: readonly string[]
  /** `process.argv`: `argv[1]` is the entry script in every non-SEA shape. */
  readonly argv: readonly string[]
  /** True when this is a SEA, where `process.execPath` is the CLI itself. */
  readonly isSea: boolean
}

export type SpawnTarget = {
  readonly program: string
  /** Arguments between `program` and the job's own argv (JobHost appends that). */
  readonly programArgs: readonly string[]
}

/**
 * Inspector flags, which must not reach the child.
 *
 * `node --inspect src/launcher.ts web` puts `--inspect` in `process.execArgv`, so
 * forwarding execArgv verbatim gives the child the same debugger port as the parent: it
 * dies with EADDRINUSE ("address already in use") before it runs a single line. The
 * inspector is an operator tool aimed at THIS process - a parent already holding the port
 * is exactly the process worth inspecting, and a job child wants neither the port nor the
 * break-before-execute. Everything else in execArgv is load-bearing: `--import`,
 * `--require`/`-r`, `--loader` and the `--experimental-*` family are how the TypeScript
 * entry loads at all, so they and their values are kept.
 */
const INSPECTOR_FLAGS_NO_VALUE: ReadonlySet<string> = new Set([
  '--inspect',
  '--inspect-brk',
  '--inspect-wait',
  // The legacy `--debug*` spellings are the same family and the same held port, so they
  // are filtered on the same terms.
  '--debug',
  '--debug-brk',
])

/** Inspector flags whose port may arrive as a separate value rather than `=value`. */
const INSPECTOR_FLAGS_WITH_VALUE: ReadonlySet<string> = new Set([
  '--inspect-port',
  '--debug-port',
])

/** The same family in their `=value` spelling, where the value is inside the token. */
const INSPECTOR_FLAG_PREFIXES: readonly string[] = [
  '--inspect=',
  '--inspect-brk=',
  '--inspect-port=',
  '--inspect-wait=',
  '--debug=',
  '--debug-brk=',
  '--debug-port=',
]

function isInspectorFlagWithInlineValue(arg: string): boolean {
  return INSPECTOR_FLAG_PREFIXES.some((prefix) => arg.startsWith(prefix))
}

/**
 * `execArgv` minus the inspector family. Only the two port flags consume a following bare
 * value, so `skipValue` is armed for those alone - a value belonging to `--import` that
 * happens to follow an `--inspect` would otherwise be silently swallowed with it.
 */
export function filterInspectorFlags(execArgv: readonly string[]): string[] {
  const kept: string[] = []
  let skipNext = false
  for (const arg of execArgv) {
    if (skipNext) {
      skipNext = false
      continue
    }
    if (INSPECTOR_FLAGS_NO_VALUE.has(arg)) continue
    if (INSPECTOR_FLAGS_WITH_VALUE.has(arg)) {
      skipNext = true
      continue
    }
    if (isInspectorFlagWithInlineValue(arg)) continue
    kept.push(arg)
  }
  return kept
}

/** The live process's shape, for callers that have already resolved `isSea` themselves. */
export function currentSpawnRuntime(isSea: boolean): SpawnRuntime {
  return {
    execPath: process.execPath,
    execArgv: process.execArgv,
    argv: process.argv,
    isSea,
  }
}

export function resolveJobSpawnTarget(runtime: SpawnRuntime): SpawnTarget {
  if (runtime.isSea) {
    // The binary is the CLI; it needs no entry script and must not be given the
    // unexpanded argv0 as if it were one.
    return { program: runtime.execPath, programArgs: [] }
  }
  const entry = runtime.argv[1]
  if (entry === undefined || entry === '') {
    return { program: PATH_FALLBACK_PROGRAM, programArgs: [] }
  }
  // execArgv first: a --import/--require/--loader flag only means anything ahead of the
  // entry, and dropping one loses the TypeScript loader this process itself ran under.
  // The inspector family is the exception - see filterInspectorFlags.
  return {
    program: runtime.execPath,
    programArgs: [...filterInspectorFlags(runtime.execArgv), entry],
  }
}