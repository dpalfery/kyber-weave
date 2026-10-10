// Architecture rule R1: the tray and the web dashboard are DISPLAY layers.
//
// They render what the engine (dash/src/**) computed and relay the operator's
// requests to it. They hold no feature logic of their own, run no scheduler or
// job, and launch no process except the engine's server. A second copy of engine
// behaviour in either shell is a second place for it to be wrong, so the guard is
// a source scan that fails the build and names the offender.
//
// Rule document: docs/rules/kyberdash-display-layer.md
//
// Three scans:
//   - Rust (dash/tray/src-tauri/src/**): no scheduler, cadence timer, receiver
//     hosting, CLI argv literal, or process spawn outside supervisor.rs.
//   - TypeScript (dash/tray/ui/src/**, dash/web/src/**): no value import from the
//     engine (dash/src/**) unless the module and names are on ALLOWLIST below.
//     `import type` is always fine: a type carries no behaviour.
//   - TypeScript timers (the same roots): a surface may POLL read-only /api/kyber/*
//     state on a timer to repaint, and may never start, schedule or cadence-drive a
//     job. So a recurring timer — a setInterval, a callback that re-arms a timer, a
//     cron-like construct — is a finding unless the file is on TIMER_ALLOWLIST with a
//     reason, and a mutating HTTP call issued from inside a timer callback is a
//     finding with no allowlist at all. A single, non-re-arming setTimeout (clear the
//     copied badge in two seconds) is a deferred repaint, not scheduling, and passes.
//
// The scanner is checked on in-memory fixtures first, so a green repository scan
// means "no violations", never "the scanner found nothing".

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const RULE_DOC = 'docs/rules/kyberdash-display-layer.md'

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url))
const DASH_ROOT = path.resolve(TEST_DIR, '..', '..')
const ENGINE_ROOT = path.join(DASH_ROOT, 'src')
const RUST_ROOT = path.join(DASH_ROOT, 'tray', 'src-tauri', 'src')
const TS_ROOTS = [path.join(DASH_ROOT, 'tray', 'ui', 'src'), path.join(DASH_ROOT, 'web', 'src')]

export interface Violation {
  /** Path relative to dash/. */
  file: string
  line: number
  construct: string
  reason: string
}

const describeViolation = (v: Violation): string =>
  `${v.file}:${v.line} ${v.construct} -- ${v.reason} (rule: ${RULE_DOC})`

// ---------------------------------------------------------------------------
// Rust scan
// ---------------------------------------------------------------------------

/** The only file allowed to launch a process: it launches the engine's server. */
const RUST_LAUNCHER = 'supervisor.rs'

/** Argv literals that mean "the shell is running an engine job itself". */
const ARGV_LITERAL = /"(refresh|clean|import-history|settings|otel)"/

interface RustRule {
  construct: string
  reason: string
  pattern: RegExp
  /** Files the construct is legitimate in. */
  allowedIn?: string[]
}

const RUST_RULES: RustRule[] = [
  {
    construct: 'scheduler module',
    reason: 'cadence and due-ness belong to the engine, not the display shell',
    pattern: /\b(?:mod\s+scheduler|crate::scheduler|Scheduler::new)\b/,
  },
  {
    construct: 'cadence timer',
    reason: 'a shell-side timer is a scheduler by another name',
    pattern: /\b(?:DEFAULT_CADENCE|is_due)\b/,
  },
  {
    construct: 'argv constant',
    reason: 'a *_ARGS constant is the shell hard-coding an engine command',
    pattern: /\b(?!SERVER_ARGS\b)[A-Z][A-Z0-9_]*_ARGS\b/,
  },
  {
    construct: 'clean argv builder',
    reason: 'building the clean command is engine behaviour',
    pattern: /\bclean_argv\b/,
  },
  {
    construct: 'receiver hosting',
    reason: 'the OTLP receiver is hosted by the engine, not the tray',
    pattern: /\b(?:RECEIVER_ARGS|SystemReceiverSpawner|receiver_spawner)\b/,
  },
  {
    construct: 'engine command argv literal',
    reason: 'refresh/clean/import-history/settings/otel are engine jobs',
    pattern: ARGV_LITERAL,
  },
  {
    construct: 'process spawn',
    reason: `only ${RUST_LAUNCHER} may launch a process (the server)`,
    pattern: /\b(?:Command::new|process::Command)\b/,
    allowedIn: [RUST_LAUNCHER],
  },
]

/** Whole files that are a violation by existing, whatever they contain. */
const RUST_FORBIDDEN_FILES: { file: RegExp; construct: string; reason: string }[] = [
  { file: /(^|\/)scheduler\.rs$/, construct: 'scheduler module', reason: 'the display shell must not own a scheduler' },
  { file: /(^|\/)receiver\.rs$/, construct: 'receiver hosting', reason: 'the display shell must not host the receiver' },
]

/** Drop comments and the trailing unit-test module so prose and tests never trip a rule. */
export function stripRustNoise(source: string): string {
  const testStart = source.search(/^\s*#\[cfg\(test\)\]/m)
  const production = testStart === -1 ? source : source.slice(0, testStart)
  return production
    .split('\n')
    .map((line) => line.replace(/\/\/.*$/, ''))
    .join('\n')
}

export function scanRust(relFile: string, source: string): Violation[] {
  const found = new Map<string, Violation>()
  const base = path.posix.basename(relFile)

  for (const forbidden of RUST_FORBIDDEN_FILES) {
    if (forbidden.file.test(relFile)) {
      found.set(forbidden.construct, { file: relFile, line: 1, construct: forbidden.construct, reason: forbidden.reason })
    }
  }

  const lines = stripRustNoise(source).split('\n')
  for (const rule of RUST_RULES) {
    if (rule.allowedIn?.includes(base)) continue
    const index = lines.findIndex((line) => rule.pattern.test(line))
    // Also catches `.spawn()` on a Command built across lines.
    if (index !== -1 && !found.has(rule.construct)) {
      found.set(rule.construct, { file: relFile, line: index + 1, construct: rule.construct, reason: rule.reason })
    }
  }
  return [...found.values()]
}

// ---------------------------------------------------------------------------
// TypeScript scan
// ---------------------------------------------------------------------------

interface AllowEntry {
  /** Engine module, relative to dash/src, extension stripped. */
  module: string
  /** Names that may be imported by value; '*' allows the module's data wholesale. */
  names: string[] | '*'
  reason: string
}

/**
 * The value imports the display layers may take from the engine. Each is a pure
 * formatter or a shared constant/data table, so importing it duplicates no
 * behaviour. Anything else (for example `sumCosts`, which totals costs and refuses
 * mixed bases) is engine logic and must come from the API, not an import.
 */
export const ALLOWLIST: AllowEntry[] = [
  {
    // renderCost only turns an already-computed CostBlock into text.
    // COST_BASIS_MISMATCH is the problem-code string the API reports.
    module: 'canon/cost',
    names: ['renderCost', 'COST_BASIS_MISMATCH'],
    reason: 'pure cost text formatter and a problem-code constant',
  },
  {
    // formatMeasured / NOT_MEASURABLE render a Measured wire value: the honest
    // "not measurable" glyph and unit/label suffixing, with no computation.
    module: 'analysis/report/types',
    names: ['formatMeasured', 'NOT_MEASURABLE'],
    reason: 'pure formatter for the Measured wire value',
  },
  {
    // The route table is data the server and the router must agree on; sharing
    // one JSON file is how they stay in step. It holds no logic.
    module: 'server/view-paths.json',
    names: '*',
    reason: 'shared view-path data table, no logic',
  },
]

interface EngineImport {
  specifier: string
  line: number
  /** Value names imported; ['*'] for default/namespace/side-effect/dynamic/JSON. */
  names: string[]
}

const isTestOrFixture = (relFile: string): boolean =>
  /\.(test|spec)\.[cm]?[jt]sx?$/.test(relFile) || relFile.includes('/__fixtures__/')

const moduleKey = (absResolved: string): string => {
  const rel = path.relative(ENGINE_ROOT, absResolved).split(path.sep).join('/')
  return rel.replace(/\.(?:[cm]?[jt]sx?)$/, '')
}

/** Value (non-type) imports in a source file, whatever syntax carries them. */
export function valueImports(fileName: string, source: string): EngineImport[] {
  const sf = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const out: EngineImport[] = []
  const lineOf = (node: ts.Node): number => sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1

  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const clause = node.importClause
      if (!clause) {
        out.push({ specifier: node.moduleSpecifier.text, line: lineOf(node), names: ['*'] })
      } else if (!clause.isTypeOnly) {
        const names: string[] = []
        if (clause.name) names.push('default')
        const bindings = clause.namedBindings
        if (bindings && ts.isNamespaceImport(bindings)) names.push('*')
        if (bindings && ts.isNamedImports(bindings)) {
          for (const el of bindings.elements) {
            if (!el.isTypeOnly) names.push((el.propertyName ?? el.name).text)
          }
        }
        if (names.length > 0) out.push({ specifier: node.moduleSpecifier.text, line: lineOf(node), names })
      }
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      !node.isTypeOnly
    ) {
      out.push({ specifier: node.moduleSpecifier.text, line: lineOf(node), names: ['*'] })
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] &&
      ts.isStringLiteralLike(node.arguments[0])
    ) {
      out.push({ specifier: node.arguments[0].text, line: lineOf(node), names: ['*'] })
    }
    ts.forEachChild(node, visit)
  }
  visit(sf)
  return out
}

export function scanTypeScript(relFile: string, source: string, absFile: string): Violation[] {
  if (isTestOrFixture(relFile)) return []
  const violations: Violation[] = []
  for (const imp of valueImports(absFile, source)) {
    if (!imp.specifier.startsWith('.')) continue
    const resolved = path.resolve(path.dirname(absFile), imp.specifier)
    if (path.relative(ENGINE_ROOT, resolved).startsWith('..')) continue
    const key = moduleKey(resolved)
    const entry = ALLOWLIST.find((e) => e.module === key)
    const disallowed =
      entry === undefined
        ? imp.names
        : entry.names === '*'
          ? []
          : imp.names.filter((n) => !entry.names.includes(n))
    for (const name of disallowed) {
      violations.push({
        file: relFile,
        line: imp.line,
        construct: `value import of ${name === '*' ? 'module' : name} from engine ${key}`,
        reason: 'display layers import engine types only; compute via the API or add a commented ALLOWLIST entry for a pure formatter',
      })
    }
  }
  return violations
}

// ---------------------------------------------------------------------------
// TypeScript timer scan
// ---------------------------------------------------------------------------

/**
 * The only timers a surface may hold. Each entry is a read-only poll: it re-reads
 * GET /api/kyber/* so an open tab repaints when the engine moved underneath it,
 * and it cannot start, schedule or cadence-drive a job. A new entry has to be
 * argued in these terms or the fix belongs in the engine, not in this list.
 */
export const TIMER_ALLOWLIST: { file: string; reason: string }[] = [
  {
    file: 'web/src/lib/storeGeneration.ts',
    reason:
      'read-only poll: GET /api/kyber/jobs on a 10s interval, invalidating the query cache only when the store generation changes. It never starts, schedules or cadence-drives a job.',
  },
]

const isTimerAllowlisted = (relFile: string): boolean => TIMER_ALLOWLIST.some((e) => e.file === relFile)

/** Browser/OS scheduling calls. A chain of these is a self-perpetuating scheduler. */
const TIMER_NAMES = new Set(['setInterval', 'setTimeout'])

/**
 * <remarks>
 * A single, non-re-arming `setTimeout` is a deferred repaint ("clear the copied
 * badge in two seconds"), not scheduling: it runs once, it decides nothing about
 * cadence, and nothing can come to depend on it firing. What the rule forbids is
 * *recurring* control — a `setInterval`, a callback that re-arms a timer, or a
 * cron-like construct — so those are what the scan reports. The store-generation
 * poll is a `setInterval` and is allowlisted on that basis; a component that
 * wanted to poll on a timeout loop would be flagged as the chain it is.
 */
const RECURRING_ONLY = new Set(['setTimeout'])

/** HTTP verbs that change state; a surface may only ever GET from a timer. */
const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

/** Cron-like scheduling: a library, a macro, or a bare cron expression. */
const CRON_LIKE = /\b(?:croner|node-cron|CronJob|cronSchedule|cron_schedule|@Cron)\b/

type TimerViolation = Violation

/** The function expression a timer hands its callback to, following a named local. */
function callbackOf(call: ts.CallExpression, sf: ts.SourceFile): ts.ArrowFunction | ts.FunctionExpression | undefined {
  const arg = call.arguments[0]
  if (arg && (ts.isArrowFunction(arg) || ts.isFunctionExpression(arg))) return arg
  if (!arg || !ts.isIdentifier(arg)) return undefined
  let found: ts.ArrowFunction | ts.FunctionExpression | undefined
  const visit = (node: ts.Node): void => {
    if (found) return
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.name.text === arg.text) {
      const init = node.initializer
      if (init && (ts.isArrowFunction(init) || ts.isFunctionExpression(init))) found = init
      return
    }
    ts.forEachChild(node, visit)
  }
  visit(sf)
  return found
}

/**
 * Flags scheduling in a display surface.
 *
 * <remarks>
 * The distinction the rule actually draws is between *polling state* and
 * *driving work*, so the scan is built around how a timer recurs rather than what
 * it is named: a recurring timer (`setInterval`, a callback that re-arms a timer, a
 * cron-like construct) is a finding unless the file is allowlisted as a read-only
 * poll, and a timer callback that issues a mutating request is always a finding,
 * allowlist or not — a surface that POSTs on a cadence has re-implemented the
 * engine's scheduler with HTTP instead of argv.
 */
export function scanTypeScriptTimers(relFile: string, source: string, absFile: string): TimerViolation[] {
  // Deliberate, and documented in the rule document: test files import engine code
  // and reach for timers to build fixtures and to drive fake clocks. They are not
  // shipped, so a rule about the surface says nothing about them; the shipped-tree
  // scans and the tests that run the real components are what hold.
  if (isTestOrFixture(relFile)) return []

  const sf = ts.createSourceFile(absFile, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const lineOf = (node: ts.Node): number => sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1
  const violations: TimerViolation[] = []
  const allowlisted = isTimerAllowlisted(relFile)

  const push = (node: ts.Node, construct: string, reason: string): void => {
    violations.push({ file: relFile, line: lineOf(node), construct, reason })
  }

  /** Every timer call in a subtree, and every mutating fetch issued from it. */
  const inspect = (root: ts.Node): { timers: ts.Node[]; mutatingFetches: ts.Node[] } => {
    const timers: ts.Node[] = []
    const mutatingFetches: ts.Node[] = []
    const visit = (node: ts.Node): void => {
      if (ts.isCallExpression(node)) {
        const callee = node.expression
        const name = ts.isIdentifier(callee) ? callee.text : undefined
        if (name !== undefined && TIMER_NAMES.has(name)) timers.push(node)
        if (ts.isIdentifier(callee) && callee.text === 'fetch' && mutatingMethod(node)) mutatingFetches.push(node)
      }
      ts.forEachChild(node, visit)
    }
    visit(root)
    return { timers, mutatingFetches }
  }

  const visitTop = (node: ts.Node): void => {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && TIMER_NAMES.has(node.expression.text)) {
      const name = node.expression.text
      const callback = callbackOf(node, sf)
      const inside = callback ? inspect(callback) : { timers: [], mutatingFetches: [] }
      const chains = inside.timers

      // A timer that re-arms a timer is a scheduler with extra steps, so it is reported
      // even in an allowlisted poll file.
      for (const nested of chains) {
        push(
          nested,
          'timer chain',
          'a callback that re-arms a timer is a scheduler by another name; a poll is one scheduled read, not a self-perpetuating chain',
        )
      }

      const recurring = chains.length > 0 || !RECURRING_ONLY.has(name)
      if (recurring && !allowlisted) {
        push(
          node,
          `${name} in a display surface`,
          `only a read-only poll belongs in a surface; this module is not on TIMER_ALLOWLIST (rule: ${RULE_DOC})`,
        )
      }

      for (const request of inside.mutatingFetches) {
        push(
          request,
          'mutating request inside a timer callback',
          'a surface may ask the engine to act, but never on a cadence; scheduling and running jobs belongs to the engine',
        )
      }
    }
    ts.forEachChild(node, visitTop)
  }
  visitTop(sf)

  // A cron library or macro is a scheduler whatever the surrounding callback looks
  // like, so it is read off the source text rather than the expression tree.
  source.split('\n').forEach((raw, index) => {
    const trimmed = raw.trim()
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return
    if (!CRON_LIKE.test(raw)) return
    violations.push({
      file: relFile,
      line: index + 1,
      construct: 'cron scheduling',
      reason: `cadence belongs to the engine (JobHost), never to a surface (rule: ${RULE_DOC})`,
    })
  })

  return violations
}

/** True when `fetch(url, { method: 'POST' })` or `fetch(url, 'POST')` mutates. */
function mutatingMethod(call: ts.CallExpression): boolean {
  const options = call.arguments[1]
  if (options && ts.isStringLiteral(options)) return MUTATING_METHODS.has(options.text.toUpperCase())
  if (!options || !ts.isObjectLiteralExpression(options)) return false
  for (const prop of options.properties) {
    if (ts.isPropertyAssignment(prop) && prop.name.getText().toLowerCase() === 'method') {
      const value = prop.initializer
      if (ts.isStringLiteral(value)) return MUTATING_METHODS.has(value.text.toUpperCase())
    }
  }
  return false
}

// ---------------------------------------------------------------------------
// Repository walk
// ---------------------------------------------------------------------------

function walk(dir: string, ext: RegExp): string[] {
  const out: string[] = []
  for (const name of readdirSync(dir).sort()) {
    if (name === 'node_modules' || name === 'dist' || name === 'target') continue
    const full = path.join(dir, name)
    if (statSync(full).isDirectory()) out.push(...walk(full, ext))
    else if (ext.test(name)) out.push(full)
  }
  return out
}

const relToDash = (abs: string): string => path.relative(DASH_ROOT, abs).split(path.sep).join('/')

function scanRepository(): Violation[] {
  const violations: Violation[] = []
  for (const file of walk(RUST_ROOT, /\.rs$/)) {
    violations.push(...scanRust(relToDash(file), readFileSync(file, 'utf8')))
  }
  for (const root of TS_ROOTS) {
    for (const file of walk(root, /\.(?:[cm]?[jt]sx?)$/)) {
      const source = readFileSync(file, 'utf8')
      const rel = relToDash(file)
      violations.push(...scanTypeScript(rel, source, file))
      violations.push(...scanTypeScriptTimers(rel, source, file))
    }
  }
  return violations
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('R1 display-layer scanner (self-tests on in-memory fixtures)', () => {
  const rust = (file: string, body: string) => scanRust(`tray/src-tauri/src/${file}`, body)
  const constructs = (vs: Violation[]) => vs.map((v) => v.construct)

  it('flags a scheduler.rs file by its existence', () => {
    expect(constructs(rust('scheduler.rs', 'fn x() {}'))).toContain('scheduler module')
  })

  it('flags a receiver.rs file as receiver hosting', () => {
    expect(constructs(rust('receiver.rs', 'fn x() {}'))).toContain('receiver hosting')
  })

  it('flags a cadence timer and is_due scheduling in an unrelated file', () => {
    const vs = rust('lib.rs', 'const A: u64 = 1;\nlet d = DEFAULT_CADENCE;\nif s.is_due(now) {}')
    expect(constructs(vs)).toContain('cadence timer')
    expect(vs.find((v) => v.construct === 'cadence timer')?.line).toBe(2)
  })

  it('flags engine argv constants and literals but not SERVER_ARGS', () => {
    expect(constructs(rust('x.rs', 'pub const REFRESH_ARGS: [&str; 2] = ["dash", "refresh"];'))).toEqual(
      expect.arrayContaining(['argv constant', 'engine command argv literal'])
    )
    expect(rust('x.rs', 'let a = SERVER_ARGS;')).toEqual([])
  })

  it('flags each engine command literal', () => {
    const literals = ['refresh', 'clean', 'import-history', 'settings', 'otel']
    // A loop over an empty array asserts nothing and still passes, so the count is pinned.
    expect(literals).toHaveLength(5)
    for (const cmd of literals) {
      expect(constructs(rust('x.rs', `let a = ["${cmd}"];`))).toContain('engine command argv literal')
    }
  })

  it('flags a clean_argv builder', () => {
    expect(constructs(rust('x.rs', 'pub fn clean_argv(&self) {}'))).toContain('clean argv builder')
  })

  it('flags process spawn outside supervisor.rs and allows it inside', () => {
    const body = 'let c = Command::new(prog);'
    expect(constructs(rust('cli.rs', body))).toContain('process spawn')
    expect(rust('supervisor.rs', body)).toEqual([])
  })

  it('ignores comments and the unit-test module', () => {
    const body = '// DEFAULT_CADENCE is_due "refresh"\nfn ok() {}\n#[cfg(test)]\nmod tests { fn t() { DEFAULT_CADENCE; } }'
    expect(rust('lib.rs', body)).toEqual([])
  })

  it('reports file, line, construct and the rule document', () => {
    const [v] = rust('x.rs', 'fn a() {}\nlet c = Command::new(p);')
    expect(describeViolation(v)).toBe(
      `tray/src-tauri/src/x.rs:2 process spawn -- only supervisor.rs may launch a process (the server) (rule: ${RULE_DOC})`
    )
  })

  const abs = path.join(DASH_ROOT, 'web', 'src', 'components', 'Fixture.tsx')
  const ts_ = (body: string) => scanTypeScript('web/src/components/Fixture.tsx', body, abs)

  it('flags a value import from the engine that is not allowlisted', () => {
    const vs = ts_("import { sumCosts } from '../../../src/canon/cost.js'")
    expect(vs).toHaveLength(1)
    expect(vs[0].construct).toContain('sumCosts')
  })

  it('allows allowlisted names and flags only the others from the same import', () => {
    const vs = ts_("import { renderCost, sumCosts, COST_BASIS_MISMATCH } from '../../../src/canon/cost.js'")
    expect(vs.map((v) => v.construct)).toEqual([expect.stringContaining('sumCosts')])
  })

  it('allows import type, inline type specifiers and type-only re-exports', () => {
    expect(ts_("import type { CostBlock } from '../../../src/canon/types.js'")).toEqual([])
    expect(ts_("import { type CostBlock } from '../../../src/canon/types.js'")).toEqual([])
    expect(ts_("export type { CostBlock } from '../../../src/canon/types.js'")).toEqual([])
  })

  it('allows the view-paths JSON table but flags other engine JSON', () => {
    expect(ts_("import p from '../../../src/server/view-paths.json' with { type: 'json' }")).toEqual([])
    expect(ts_("import p from '../../../src/server/other.json' with { type: 'json' }")).toHaveLength(1)
  })

  it('flags side-effect, namespace, re-export and dynamic imports of the engine', () => {
    expect(ts_("import '../../../src/server/boot.js'")).toHaveLength(1)
    expect(ts_("import * as e from '../../../src/refresh/run.js'")).toHaveLength(1)
    expect(ts_("export { run } from '../../../src/refresh/run.js'")).toHaveLength(1)
    expect(ts_("const m = await import('../../../src/refresh/run.js')")).toHaveLength(1)
  })

  it('ignores package imports, local imports and type-position import()', () => {
    expect(ts_("import { useState } from 'react'")).toEqual([])
    expect(ts_("import { x } from './local.js'")).toEqual([])
    expect(ts_("type T = import('../../../src/refresh/run.js').Run")).toEqual([])
  })

  it('skips test files and fixtures', () => {
    const t = path.join(DASH_ROOT, 'web', 'src', 'A.test.tsx')
    expect(scanTypeScript('web/src/A.test.tsx', "import { sumCosts } from '../../src/canon/cost.js'", t)).toEqual([])
    // Deliberate, and asserted here so it stays deliberate: a test may import engine
    // code to build a fixture and may use timers to drive a fake clock, because it is
    // not shipped. The rule document says so too.
    expect(scanTypeScriptTimers('web/src/A.test.tsx', 'setInterval(() => poll(), 10)', t)).toEqual([])
    expect(scanTypeScriptTimers('web/src/__fixtures__/a.tsx', 'setInterval(() => poll(), 10)', t)).toEqual([])
  })

  it('keeps every allowlist entry explained', () => {
    for (const entry of ALLOWLIST) expect(entry.reason.length).toBeGreaterThan(10)
  })

  it('keeps every timer allowlist entry explained and pointed at a file that exists', () => {
    for (const entry of TIMER_ALLOWLIST) {
      expect(entry.reason.length).toBeGreaterThan(20)
      expect(existsSync(path.join(DASH_ROOT, entry.file))).toBe(true)
    }
  })
})

describe('R1 the timer scan: a surface may poll, never schedule (self-tests on in-memory fixtures)', () => {
  const pollAbs = path.join(DASH_ROOT, 'web', 'src', 'lib', 'Fixture.ts')
  const at = (rel: string) => path.join(DASH_ROOT, rel)
  const constructs = (rel: string, body: string): string[] => scanTypeScriptTimers(rel, body, at(rel)).map((v) => v.construct)

  it('flags a setInterval in a surface module that is not an allowlisted poll', () => {
    expect(constructs('web/src/lib/Poll.ts', 'const t = setInterval(poll, 1000)')).toEqual([
      'setInterval in a display surface',
    ])
  })

  it('does not flag a single deferred repaint, but flags a setTimeout that chains', () => {
    // The copy-badge reset is a one-shot repaint: nothing recurs and nothing decides
    // cadence, so it is not scheduling.
    expect(constructs('web/src/components/Copy.tsx', 'setTimeout(() => setCopied(false), 2000)')).toEqual([])
    // Re-arming is a scheduler, whichever timer spelling it uses.
    const chain = 'const tick = (): void => { setTimeout(tick, 1000) }\nsetTimeout(tick, 1000)'
    expect(constructs('web/src/components/Copy.tsx', chain)).toContain('timer chain')
  })

  it('allows the allowlisted store-generation poll', () => {
    const body = 'const timer = setInterval(() => void fetchKyberJobs(), STORE_GENERATION_POLL_MS)'
    expect(constructs('web/src/lib/storeGeneration.ts', body)).toEqual([])
    // The entry is about this file only, not about polling in general.
    expect(constructs('web/src/lib/other.ts', body)).toEqual(['setInterval in a display surface'])
  })

  it('flags a callback that re-arms a timer, even in an allowlisted poll file', () => {
    const body = 'const tick = (): void => { setTimeout(tick, 1000) }\nsetTimeout(tick, 1000)'
    expect(constructs('web/src/lib/storeGeneration.ts', body)).toContain('timer chain')
  })
  it('flags a mutating request issued from inside a timer callback', () => {
    const body = "setInterval(() => { void fetch('/api/kyber/clean', { method: 'POST' }) }, 60000)"
    expect(constructs('web/src/lib/anything.ts', body)).toContain('mutating request inside a timer callback')
    // Method shape variants, and a DELETE/PUT spelled as a plain string.
    expect(constructs('web/src/lib/a.ts', "setTimeout(function () { fetch('/api/kyber/x', 'DELETE') }, 5)")).toContain(
      'mutating request inside a timer callback'
    )
  })

  it('does not flag a read-only poll request or a mutating request outside a timer', () => {
    expect(constructs('web/src/lib/storeGeneration.ts', "setInterval(() => { void fetch('/api/kyber/jobs') }, 10000)")).toEqual([])
    expect(constructs('web/src/lib/onClick.ts', "void fetch('/api/kyber/clean', { method: 'POST' })")).toEqual([])
    expect(constructs('web/src/lib/a.ts', "fetch('/api/kyber/x', { method: 'GET' })")).toEqual([])
  })

  it('flags cron-like scheduling in a surface module but not in a comment', () => {
    expect(constructs('web/src/lib/cron.ts', "import { CronJob } from 'croner'")).toContain('cron scheduling')
    expect(constructs('web/src/lib/cron.ts', '// CronJob was rejected here')).toEqual([])
  })

  it('reports the offending line', () => {
    const body = 'export const a = 1\nconst t = setInterval(poll, 1000)'
    expect(scanTypeScriptTimers('web/src/lib/Poll.ts', body, pollAbs)[0]?.line).toBe(2)
  })
})

describe('R1 the tray and web dashboard are display layers', () => {
  it('scans real sources (a scan that finds no files proves nothing)', () => {
    expect(walk(RUST_ROOT, /\.rs$/).length).toBeGreaterThan(3)
    expect(TS_ROOTS.flatMap((r) => walk(r, /\.tsx?$/)).length).toBeGreaterThan(10)
  })

  it('contains no isolated feature logic, scheduler, job or engine import', () => {
    const violations = scanRepository()
    expect(
      violations.map(describeViolation),
      `Display-layer violations (see ${RULE_DOC}):\n${violations.map(describeViolation).join('\n')}`
    ).toEqual([])
  })
})
