#!/usr/bin/env node
// OpenCode live acceptance gate: the runner.
//
// An agent runs this AFTER installing the build under test, against the real OpenCode and the live
// kyberDash on 127.0.0.1:4747. It drives one real two-prompt OpenCode session, then asks whether
// kyberDash recorded what OpenCode itself recorded (compare.mjs), and whether the dashboard page
// shows it (dash/e2e/opencode-live-gate.spec.ts). It calls a real model, so it is never part of the
// test suite.
//
//   node dash/scripts/opencode-live-gate/run.mjs [--dry-run] [--help]
//   npm --prefix dash run gate:opencode-live
//
// --dry-run prints the planned commands and paths. It does not run OpenCode, open either database,
// touch the network, or write a file, so the script can be checked without a live run.
//
// What a live run does, in order (the plan printed by --dry-run is the same list):
//   1  scratch project outside the repository: a skill and a three-line notes.txt
//   2  prompt 1 (tool + skill) through the shared OpenCode background service
//   3  prompt 2 in the same session, the skill attached, the model explicit
//   4  wait for the session to go idle in opencode.db (read-only); interrupt it on timeout
//   5  wait for kyberDash's canon.db to hold one step record per assistant message, then project
//   6  read both sides: kyberDash's API and opencode.db (read-only)
//   7  compare them (compare.mjs) and write comparison.json
//   8  run the UI spec against the live kyberDash, which writes the screenshot
//   9  clean up: no gate `opencode run` left, the session not busy, the scratch project removed
//   10 evidence under artifacts/evidence/opencode-v2-live-gate/<UTC-stamp>/
//
// Exit status: 0 when every comparator row, the UI spec and the cleanup pass; 1 on any failed row,
// timeout, or busy session left behind; 2 on a usage error. The evidence path is printed last.
//
// Models: only opencode-go/muse-spark-1.3-contributor, or opencode/big-pickle when the first has no
// route. `-m` is never omitted and no Anthropic model is ever used.
//
// Secrets: every evidence file passes through redact(), which strips Authorization and key headers,
// bearer tokens, provider-token shapes, and secret-named JSON fields. The runner never reads
// OpenCode's auth store and sends no credentials to kyberDash.

import { spawn } from 'node:child_process'
import { appendFileSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { basename, dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseArgs } from 'node:util'

import { compareSession } from './compare.mjs'

// ---------------------------------------------------------------------------------------------
// What the gate sends
// ---------------------------------------------------------------------------------------------

export const SKILL_NAME = 'kw-gate-skill'
export const SKILL_TOKEN = 'KW-GATE-SKILL-LOADED'

/** Prompt 1: uses the skill tool and the read tool. */
export const PROMPT_ONE =
  "Load the skill named kw-gate-skill using the skill tool, then read notes.txt with the read tool, then follow the skill's instruction. Do not do anything else."

/** Prompt 2: the skill is attached to the prompt, so no tool call is needed. */
export const PROMPT_TWO = 'Follow the attached kw-gate-skill skill. Do not call any tools; reply with its token and the number 3.'

/**
 * `opencode run <message>` wraps a message that contains whitespace in double quotes before it sends it
 * (inner quotes escaped), so OpenCode records, and the plugin captures, prompt 1 with those quotes. The
 * `api` route sends prompt 2 verbatim.
 */
export function recordedByRun(prompt) {
  return /\s/.test(prompt) ? `"${prompt.replace(/"/g, '\\"')}"` : prompt
}

export const PRIMARY_MODEL = 'opencode-go/muse-spark-1.3-contributor'
export const FALLBACK_MODEL = 'opencode/big-pickle'

const NOTES_TEXT = 'alpha\nbeta\ngamma\n'

const SKILL_MARKDOWN = `---
name: ${SKILL_NAME}
description: Live-gate skill for the kyberDash OpenCode acceptance run. Use when asked to load or follow the ${SKILL_NAME} skill.
---

# KW gate skill

When this skill is loaded, reply with the exact token ${SKILL_TOKEN} and the number of lines in notes.txt.
`

/** The span name the V2 plugin gives one assistant step (SPAN_NAMES.step in opencode-plugin/contract.ts). */
const STEP_SPAN_NAME = 'opencode.step'

const KYBER_BASE_URL = 'http://127.0.0.1:4747'

const LIMITS = {
  helpMs: 30_000,
  runMs: 6 * 60_000,
  apiMs: 30_000,
  idleMs: 5 * 60_000,
  interruptSettleMs: 30_000,
  ingestMs: 2 * 60_000,
  buildMs: 5 * 60_000,
  uiMs: 3 * 60_000,
  httpMs: 15_000,
  sessionApiAttempts: 10,
  sessionApiDelayMs: 3_000,
  pollMs: 2_000,
  maxMessages: 5_000,
  maxLogStreamChars: 200_000,
  maxEvidenceFileChars: 5_000_000,
}

// ---------------------------------------------------------------------------------------------
// Small pure helpers
// ---------------------------------------------------------------------------------------------

export class GateError extends Error {
  /**
   * @param {string} message
   * @param {{ cause?: unknown }} [options]
   */
  constructor(message, options) {
    super(message, options)
    this.name = 'GateError'
  }
}

function describeError(error) {
  return error instanceof Error ? error.message : String(error)
}

/** 20261030T161205Z */
export function utcStamp(date) {
  return date.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z')
}

/** Quotes one argument for display only; the commands themselves run with argv, never a shell. */
export function shellQuote(arg) {
  if (/^[\w@%+=:,./-]+$/.test(arg)) return arg
  return `'${arg.replace(/'/g, `'\\''`)}'`
}

export function formatCommand(command, args) {
  return [command, ...args].map(shellQuote).join(' ')
}

/** `opencode-go/muse-spark-1.3-contributor` -> { provider: 'opencode-go', id: 'muse-spark-1.3-contributor' } */
export function splitModel(model) {
  const at = model.indexOf('/')
  if (at <= 0 || at === model.length - 1) throw new GateError(`model ${JSON.stringify(model)} is not provider/model`)
  return { provider: model.slice(0, at), id: model.slice(at + 1) }
}

const REDACTIONS = [
  [/\b(authorization|proxy-authorization)(\s*[:=]\s*)(?:bearer\s+|basic\s+)?[^\s,;"']+/gi, '$1$2[redacted]'],
  [/\b(x-api-key|api-key|apikey|x-auth-token)(\s*[:=]\s*)[^\s,;"']+/gi, '$1$2[redacted]'],
  [/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/g, 'Bearer [redacted]'],
  [
    /"(authorization|proxy-authorization|x-api-key|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|auth[_-]?token|client[_-]?secret|secret|password|token)"\s*:\s*"[^"]*"/gi,
    '"$1":"[redacted]"',
  ],
  [/"(reasoningEncryptedContent|encryptedContent|encrypted_content)"\s*:\s*"[^"]*"/g, '"$1":"[redacted]"'],
  [/\b(?:sk|pk|rk)-[A-Za-z0-9_-]{16,}/g, '[redacted]'],
  [/\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}/g, '[redacted]'],
  [/\bgithub_pat_[A-Za-z0-9_]{20,}/g, '[redacted]'],
  [/\bxox[abprs]-[A-Za-z0-9-]{10,}/g, '[redacted]'],
  [/\bAKIA[0-9A-Z]{16}\b/g, '[redacted]'],
]

/** Strips credentials from text before it reaches an evidence file. */
export function redact(text) {
  let out = text
  for (const [pattern, replacement] of REDACTIONS) out = out.replace(pattern, replacement)
  return out
}

/** Parses JSON lines; lines that are not JSON objects (log text) are counted, not guessed at. */
export function parseJsonLines(text) {
  const events = []
  let skipped = 0
  for (const line of text.split(/\r?\n/)) {
    const trimmed = line.trim()
    if (trimmed === '') continue
    if (!trimmed.startsWith('{')) {
      skipped += 1
      continue
    }
    try {
      events.push(JSON.parse(trimmed))
    } catch {
      skipped += 1
    }
  }
  return { events, skipped }
}

/**
 * Reads `opencode run --format json` output: the session id, and any error events.
 * @param {string} stdout
 */
export function inspectRunOutput(stdout) {
  const { events } = parseJsonLines(stdout)
  let sessionId = null
  const errors = []
  for (const event of events) {
    if (sessionId === null && typeof event.sessionID === 'string' && event.sessionID.startsWith('ses_')) {
      sessionId = event.sessionID
    }
    if (event.type === 'error') {
      const type = event.error && typeof event.error.type === 'string' ? event.error.type : 'unknown'
      const message = event.error && typeof event.error.message === 'string' ? event.error.message : ''
      errors.push({ type, message, sessionId: typeof event.sessionID === 'string' ? event.sessionID : null })
    }
  }
  return { sessionId, errors }
}

// ---------------------------------------------------------------------------------------------
// Command builders (shared by the real run and --dry-run, so the plan is the run)
// ---------------------------------------------------------------------------------------------

/** `opencode run` for prompt 1 through the shared background service. The prompt is one argv element. */
export function promptOneArgs({ model, title, prompt }) {
  return ['run', '-m', model, '--format', 'json', '--title', title, '--auto', prompt]
}

/** `opencode run` continuation, used only when `opencode run --help` offers a skill-attach flag. */
export function continueRunArgs({ sessionId, model, prompt, skill }) {
  return ['run', '--session', sessionId, '-m', model, '--format', 'json', '--auto', '--skill', skill, prompt]
}

export function sessionPath(sessionId, suffix) {
  return `/api/session/${encodeURIComponent(sessionId)}${suffix}`
}

export function modelSwitchRequest({ sessionId, model }) {
  const { provider, id } = splitModel(model)
  return { method: 'POST', path: sessionPath(sessionId, '/model'), body: { model: { id, providerID: provider } } }
}

export function promptTwoApiRequest({ sessionId, prompt, skill }) {
  return {
    method: 'POST',
    path: sessionPath(sessionId, '/prompt'),
    body: { text: prompt, skills: [{ id: skill }], delivery: 'steer' },
  }
}

export function interruptRequest({ sessionId }) {
  return { method: 'POST', path: sessionPath(sessionId, '/interrupt') }
}

/** `opencode api` argv: flags first, as its usage line shows, then `<method> <path>`. */
export function apiArgs(request, extraHeaders = []) {
  return [
    'api',
    ...(request.body === undefined ? [] : ['-d', JSON.stringify(request.body)]),
    ...extraHeaders.flatMap((header) => ['-H', header]),
    request.method,
    request.path,
  ]
}

export function expectedModelId(model) {
  return splitModel(model).id
}

// ---------------------------------------------------------------------------------------------
// Configuration and plan
// ---------------------------------------------------------------------------------------------

/**
 * Paths and endpoints, with the environment and home directory injected.
 * @param {NodeJS.ProcessEnv} [env]
 * @param {string} [home]
 */
export function defaultConfig(env = process.env, home = homedir()) {
  const here = dirname(fileURLToPath(import.meta.url))
  const dashRoot = resolve(here, '..', '..')
  const repoRoot = resolve(dashRoot, '..')
  const xdgData = env.XDG_DATA_HOME && env.XDG_DATA_HOME.trim() !== '' ? env.XDG_DATA_HOME : join(home, '.local', 'share')
  const canonOverride = env.KYBER_CANON_DB && env.KYBER_CANON_DB.trim() !== '' ? env.KYBER_CANON_DB : null
  const kyberdashBin = env.KYBERDASH_BIN && env.KYBERDASH_BIN.trim() !== '' ? env.KYBERDASH_BIN : 'kyberdash'
  return {
    repoRoot,
    dashRoot,
    evidenceRoot: join(repoRoot, 'artifacts', 'evidence', 'opencode-v2-live-gate'),
    opencodeDb: join(xdgData, 'opencode', 'opencode.db'),
    canonDb: canonOverride ?? join(home, '.kyberdash', 'canon.db'),
    kyberBaseUrl: KYBER_BASE_URL,
    opencodeBin: 'opencode',
    kyberdashBin,
    tmpRoot: tmpdir(),
  }
}

const SESSION_PLACEHOLDER = 'ses_SESSION_ID'

/**
 * The ordered plan, as display lines. Built from the same command builders the run uses.
 * @param {ReturnType<typeof defaultConfig>} config
 * @param {string} stamp
 */
export function buildPlan(config, stamp) {
  const title = `kw-live-gate ${stamp}`
  const evidenceDir = join(config.evidenceRoot, stamp)
  const scratchPattern = join(config.tmpRoot, 'kw-opencode-live-gate-XXXXXX')
  const oc = config.opencodeBin
  const placeholderRoute = (request) => formatCommand(oc, apiArgs(request))
  const specEnv = `KW_OPENCODE_LIVE_SESSION_ID=${SESSION_PLACEHOLDER} KW_OPENCODE_LIVE_EXPECTED=${join(evidenceDir, 'ui-expected.json')} KW_OPENCODE_LIVE_EVIDENCE_DIR=${evidenceDir}`

  return {
    stamp,
    title,
    evidenceDir,
    scratchPattern,
    opencodeDb: config.opencodeDb,
    canonDb: config.canonDb,
    kyberBaseUrl: config.kyberBaseUrl,
    steps: [
      {
        name: 'Preflight (before any model call)',
        lines: [
          formatCommand(oc, ['--version']),
          formatCommand(config.kyberdashBin, ['--version']),
          `GET ${config.kyberBaseUrl}/api/kyber/version   (the live kyberDash must answer; the UI spec reuses it)`,
          `open ${config.opencodeDb} read-only; PRAGMA table_info(session_v2), PRAGMA table_info(session_message)`,
          `open ${config.canonDb} read-only; PRAGMA table_info(records)`,
        ],
      },
      {
        name: '1. Scratch project (outside the repository)',
        lines: [
          `mkdtemp ${scratchPattern}`,
          `write <scratch>/.opencode/skills/${SKILL_NAME}/SKILL.md   (reply ${SKILL_TOKEN} and the line count of notes.txt)`,
          'write <scratch>/notes.txt   (three lines)',
        ],
      },
      {
        name: '2. Prompt 1: tool + skill, shared background service, cwd = scratch',
        lines: [
          formatCommand(oc, promptOneArgs({ model: PRIMARY_MODEL, title, prompt: PROMPT_ONE })),
          `if the output reports provider.no-route, rerun with -m ${FALLBACK_MODEL} and record it`,
          'the session id is read from the JSON output',
        ],
      },
      {
        name: '3. Prompt 2: same session, skill attached, model explicit',
        lines: [
          `discover the route at run time: ${formatCommand(oc, ['run', '--help'])}, else ${formatCommand(oc, ['api', '--help'])}`,
          `route "run" only if the run help lists --skill: ${formatCommand(oc, continueRunArgs({ sessionId: SESSION_PLACEHOLDER, model: PRIMARY_MODEL, prompt: PROMPT_TWO, skill: SKILL_NAME }))}`,
          `otherwise route "api" (the 2.2.x route): ${placeholderRoute(modelSwitchRequest({ sessionId: SESSION_PLACEHOLDER, model: PRIMARY_MODEL }))}`,
          placeholderRoute(promptTwoApiRequest({ sessionId: SESSION_PLACEHOLDER, prompt: PROMPT_TWO, skill: SKILL_NAME })),
        ],
      },
      {
        name: '4. Idle wait (max 5 minutes)',
        lines: [
          `poll ${config.opencodeDb} read-only every ${LIMITS.pollMs / 1000}s: session_v2 idle (time_idle at or after time_busy), both prompts recorded, last session_message is idle`,
          `on timeout: ${placeholderRoute(interruptRequest({ sessionId: SESSION_PLACEHOLDER }))}  then record it and fail`,
        ],
      },
      {
        name: '5. Ingest and project (max 2 minutes)',
        lines: [
          `poll ${config.canonDb} read-only: records of ${STEP_SPAN_NAME} for the session equal OpenCode's assistant-message count`,
          formatCommand(config.kyberdashBin, ['kyber', 'build']),
        ],
      },
      {
        name: '6. Read both sides',
        lines: [
          `GET ${config.kyberBaseUrl}/api/kyber/session/${SESSION_PLACEHOLDER}`,
          `GET ${config.kyberBaseUrl}/api/kyber/sessions?harness=opencode`,
          `GET ${config.kyberBaseUrl}/api/kyber/capture`,
          `${config.opencodeDb} (read-only): the session_v2 row; user and assistant session_message rows; per-message tokens; tool parts (name, status); models`,
        ],
      },
      {
        name: '7. Compare',
        lines: [`compareSession(...) -> ${join(evidenceDir, 'comparison.json')}`],
      },
      {
        name: '8. UI spec against the live kyberDash (no webServer of its own)',
        lines: [
          `write ${join(evidenceDir, 'playwright.live.config.mjs')}   (no webServer entry)`,
          `${specEnv} npx playwright test --config ${join(evidenceDir, 'playwright.live.config.mjs')} opencode-live-gate   (cwd ${config.dashRoot}, CI unset)`,
        ],
      },
      {
        name: '9. Cleanup',
        lines: [
          'stop any `opencode run` the gate started; confirm the gate session is not busy (interrupt it if it is)',
          `copy <scratch>/.opencode/skills/${SKILL_NAME}/SKILL.md into the evidence directory, then remove the scratch project`,
        ],
      },
      {
        name: '10. Evidence',
        lines: [
          evidenceDir,
          'commands.log, opencode-prompt1-output.jsonl, opencode-prompt2-api-response.json, kyberdash-api-session.json,',
          'kyberdash-api-sessions-opencode.json, kyberdash-api-capture.json, opencode-session_v2.json,',
          'opencode-session_message-summary.json, comparison.json, ui-expected.json, session-page.png, summary.md',
        ],
      },
    ],
  }
}

/** @param {ReturnType<typeof buildPlan>} plan */
export function formatPlan(plan) {
  const out = []
  out.push('OpenCode live gate: dry run. Nothing is executed, no database is opened, no file is written.')
  out.push('')
  out.push(`stamp:             ${plan.stamp}`)
  out.push(`session title:     ${plan.title}`)
  out.push(`evidence dir:      ${plan.evidenceDir}`)
  out.push(`scratch project:   ${plan.scratchPattern}   (outside the repository; removed at the end)`)
  out.push(`opencode.db:       ${plan.opencodeDb}   (read-only)`)
  out.push(`canon.db:          ${plan.canonDb}   (read-only)`)
  out.push(`kyberDash:         ${plan.kyberBaseUrl}`)
  out.push(`models:            ${PRIMARY_MODEL}, fallback ${FALLBACK_MODEL} on provider.no-route; -m is always given`)
  out.push('')
  for (const step of plan.steps) {
    out.push(step.name)
    for (const line of step.lines) out.push(`    ${line}`)
    out.push('')
  }
  out.push('Exit status: 0 only when every comparator row, the UI spec and the cleanup pass.')
  return out.join('\n')
}

// ---------------------------------------------------------------------------------------------
// Run context: evidence, processes, HTTP, SQLite
// ---------------------------------------------------------------------------------------------

/**
 * @typedef {{
 *   dir: string,
 *   path: (name: string) => string,
 *   log: (line: string) => void,
 *   writeText: (name: string, text: string) => void,
 *   writeJson: (name: string, value: unknown) => void,
 * }} Evidence
 */

/** @returns {Evidence} */
function createEvidence(dir) {
  mkdirSync(dir, { recursive: true })
  const logPath = join(dir, 'commands.log')
  return {
    dir,
    path: (name) => join(dir, name),
    log(line) {
      appendFileSync(logPath, `[${new Date().toISOString()}] ${redact(line)}\n`)
    },
    // The recorded text includes kyberDash and OpenCode responses: that is what the gate is evidence of.
    // It lands only under a plain file name in the gate-owned evidence directory, redacted and bounded.
    writeText(name, text) {
      // CodeQL flags this write as network data to a file: accepted, the responses are the evidence.
      writeFileSync(join(dir, evidenceFileName(name)), boundedEvidence(redact(text)))
    },
    writeJson(name, value) {
      // CodeQL flags this write as network data to a file: accepted, the responses are the evidence.
      writeFileSync(join(dir, evidenceFileName(name)), `${boundedEvidence(redact(JSON.stringify(value, null, 2)))}\n`)
    },
  }
}

/** An evidence file is a plain name inside the evidence directory, never a path. */
function evidenceFileName(name) {
  if (name === '' || name !== basename(name)) throw new GateError(`evidence file name is not a plain file name: ${name}`)
  return name
}

function boundedEvidence(text) {
  if (text.length <= LIMITS.maxEvidenceFileChars) return text
  return `${text.slice(0, LIMITS.maxEvidenceFileChars)}\n... [${text.length - LIMITS.maxEvidenceFileChars} more characters not recorded]`
}

function progress(message) {
  process.stderr.write(`[opencode-live-gate] ${message}\n`)
}

function clip(text) {
  if (text.length <= LIMITS.maxLogStreamChars) return text
  return `${text.slice(0, LIMITS.maxLogStreamChars)}\n... [${text.length - LIMITS.maxLogStreamChars} more characters; the full output is in its own evidence file]`
}

function isAlive(child) {
  return child.exitCode === null && child.signalCode === null
}

function killChild(child) {
  if (!isAlive(child)) return
  child.kill('SIGTERM')
  const hard = setTimeout(() => {
    if (isAlive(child)) child.kill('SIGKILL')
  }, 5_000)
  hard.unref()
}

function sleep(ms) {
  return new Promise((done) => setTimeout(done, ms))
}

function throwIfAborted(ctx) {
  if (ctx.aborted) throw new GateError('the gate was interrupted')
}

/**
 * Runs one command with argv (no shell), logs it and its output, and always resolves.
 * @returns {Promise<{ code: number | null, signal: string | null, stdout: string, stderr: string, timedOut: boolean, error: Error | null }>}
 */
function runCommand(ctx, command, args, { cwd, env, timeoutMs, logOutput = true }) {
  throwIfAborted(ctx)
  ctx.evidence.log(`$ ${formatCommand(command, args)}   (cwd ${cwd})`)
  return new Promise((done) => {
    let stdout = ''
    let stderr = ''
    let timedOut = false
    let settled = false
    // OpenCode 2.2.x takes a session's directory from $PWD, not the process cwd. npm leaves $PWD at
    // dash/, so without this the scratch project (and its skill) is never the session's project.
    const child = spawn(command, args, { cwd, env: { ...(env ?? process.env), PWD: cwd }, stdio: ['ignore', 'pipe', 'pipe'] })
    ctx.children.add(child)
    const timer = setTimeout(() => {
      timedOut = true
      killChild(child)
    }, timeoutMs)
    const finish = (code, signal, error) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      ctx.children.delete(child)
      ctx.evidence.log(`  exit ${code === null ? `signal ${signal}` : code}${timedOut ? ' (timed out and killed)' : ''}${error ? ` error: ${error.message}` : ''}`)
      if (logOutput && stdout !== '') ctx.evidence.log(`  stdout:\n${clip(stdout)}`)
      if (logOutput && stderr !== '') ctx.evidence.log(`  stderr:\n${clip(stderr)}`)
      done({ code, signal, stdout, stderr, timedOut, error })
    }
    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString('utf8')
    })
    child.stderr.on('data', (chunk) => {
      stderr += chunk.toString('utf8')
    })
    child.on('error', (error) => finish(null, null, error))
    child.on('close', (code, signal) => finish(code, signal, null))
  })
}

function timeoutSignal(ms) {
  return globalThis.AbortSignal.timeout(ms)
}

/** GET against kyberDash. Always resolves; a transport failure is a result, not a throw. */
async function httpGet(ctx, path) {
  const url = `${ctx.config.kyberBaseUrl}${path}`
  ctx.evidence.log(`GET ${url}`)
  try {
    const response = await fetch(url, { signal: timeoutSignal(LIMITS.httpMs), headers: { accept: 'application/json' } })
    const text = await response.text()
    let json
    try {
      json = JSON.parse(text)
    } catch {
      json = undefined
    }
    ctx.evidence.log(`  -> HTTP ${response.status}, ${text.length} bytes${json === undefined ? ', not JSON' : ''}`)
    return { status: response.status, ok: response.ok, text, json, error: null }
  } catch (error) {
    ctx.evidence.log(`  -> failed: ${describeError(error)}`)
    return { status: null, ok: false, text: '', json: undefined, error }
  }
}

async function openReadOnly(path) {
  if (!existsSync(path)) throw new GateError(`database not found: ${path}`)
  const { DatabaseSync } = await import('node:sqlite')
  const db = new DatabaseSync(path, { open: true, readOnly: true })
  db.exec('PRAGMA busy_timeout = 5000')
  return db
}

/** Logs a statement once per distinct text so polling loops do not flood commands.log. */
function logSql(ctx, label, sql) {
  const key = `${label}|${sql}`
  if (ctx.loggedSql.has(key)) return
  ctx.loggedSql.add(key)
  ctx.evidence.log(`sqlite (read-only) ${label}: ${sql.replace(/\s+/g, ' ').trim()}`)
}

const TABLE_NAME = /^[a-z_][a-z0-9_]*$/

/** Column names from PRAGMA table_info; the table name is a constant checked against an allowlist pattern. */
function tableColumns(ctx, db, label, table) {
  if (!TABLE_NAME.test(table)) throw new GateError(`refusing table name ${JSON.stringify(table)}`)
  const sql = `PRAGMA table_info(${table})`
  logSql(ctx, label, sql)
  return db.prepare(sql).all().map((column) => String(column.name))
}

function requireColumns(ctx, db, label, table, needed) {
  const have = new Set(tableColumns(ctx, db, label, table))
  if (have.size === 0) throw new GateError(`${label}: table ${table} does not exist`)
  const missing = needed.filter((column) => !have.has(column))
  if (missing.length > 0) {
    throw new GateError(`${label}: table ${table} lacks column(s) ${missing.join(', ')}; it has ${[...have].join(', ')}`)
  }
}

const SESSION_V2_COLUMNS = [
  'id',
  'cost',
  'tokens_input',
  'tokens_output',
  'tokens_reasoning',
  'tokens_cache_read',
  'tokens_cache_write',
  'time_busy',
  'time_idle',
  'idle_outcome',
]
const SESSION_MESSAGE_COLUMNS = ['id', 'session_id', 'type', 'seq', 'time_created', 'data']
const RECORDS_COLUMNS = ['harness', 'session_id', 'name']

/** Confirms the column names the gate queries before it queries anything. */
function confirmSchemas(ctx, opencodeDb, canonDb) {
  requireColumns(ctx, opencodeDb, 'opencode.db', 'session_v2', SESSION_V2_COLUMNS)
  requireColumns(ctx, opencodeDb, 'opencode.db', 'session_message', SESSION_MESSAGE_COLUMNS)
  requireColumns(ctx, canonDb, 'canon.db', 'records', RECORDS_COLUMNS)
}

// ---------------------------------------------------------------------------------------------
// Reading OpenCode's database
// ---------------------------------------------------------------------------------------------

/**
 * OpenCode 2.2.x stamps `time_busy` when an execution starts and leaves it set afterwards; the
 * execution ended when `time_idle` is at or after it. A session is busy only while `time_busy` is the
 * newer stamp (or `time_idle` was never written).
 */
function isBusy(session) {
  if (session.time_busy === null) return false
  return session.time_idle === null || Number(session.time_idle) < Number(session.time_busy)
}

function readIdleState(ctx, db, sessionId) {
  const sessionSql = 'SELECT time_busy, time_idle, idle_outcome FROM session_v2 WHERE id = ?'
  const lastSql = 'SELECT type FROM session_message WHERE session_id = ? ORDER BY seq DESC LIMIT 1'
  const usersSql = "SELECT COUNT(*) AS n FROM session_message WHERE session_id = ? AND type = 'user'"
  logSql(ctx, 'opencode.db', sessionSql)
  logSql(ctx, 'opencode.db', lastSql)
  logSql(ctx, 'opencode.db', usersSql)
  const session = db.prepare(sessionSql).get(sessionId)
  const last = db.prepare(lastSql).get(sessionId)
  const users = db.prepare(usersSql).get(sessionId)
  return {
    exists: session !== undefined,
    busy: session !== undefined && isBusy(session),
    idleOutcome: session?.idle_outcome ?? null,
    lastType: last?.type ?? null,
    userMessages: users ? Number(users.n) : 0,
  }
}

function isCount(value) {
  return typeof value === 'number' && Number.isFinite(value)
}

/**
 * Reduces session_message rows to what the comparator needs. An assistant message without numeric
 * token counts is an error: an unmeasured value is never read as zero.
 * @param {Array<{ id: string, type: string, seq: number, data: string }>} rows ordered by seq
 */
export function summarizeMessages(rows) {
  let userMessages = 0
  const userTexts = []
  const assistantMessages = []
  const toolParts = []
  const models = []
  const messages = []

  for (const rowData of rows) {
    let data
    try {
      data = JSON.parse(rowData.data)
    } catch (error) {
      throw new GateError(`session_message ${rowData.id} (${rowData.type}) holds data that is not JSON`, { cause: error })
    }
    const entry = { id: rowData.id, seq: rowData.seq, type: rowData.type }

    if (rowData.type === 'user') {
      userMessages += 1
      const text = typeof data.text === 'string' ? data.text : null
      userTexts.push(text)
      messages.push({
        ...entry,
        requestIndex: userMessages - 1,
        textChars: text === null ? null : text.length,
        skills: Array.isArray(data.skills) ? data.skills.map((skill) => skill?.name ?? skill?.id ?? null) : [],
      })
    } else if (rowData.type === 'assistant') {
      const tokens = data.tokens
      const cache = tokens?.cache
      const counts = {
        input: tokens?.input,
        output: tokens?.output,
        reasoning: tokens?.reasoning,
        cache_read: cache?.read,
        cache_write: cache?.write,
      }
      for (const [key, value] of Object.entries(counts)) {
        if (!isCount(value)) throw new GateError(`assistant session_message ${rowData.id} has no numeric tokens ${key}; refusing to read it as 0`)
      }
      const requestIndex = userMessages - 1
      assistantMessages.push({ ...counts, requestIndex })

      const parts = []
      for (const part of Array.isArray(data.content) ? data.content : []) {
        if (part && part.type === 'tool') {
          const tool = {
            name: typeof part.name === 'string' ? part.name : '',
            status: typeof part.state?.status === 'string' ? part.state.status : 'unknown',
          }
          parts.push(tool)
          toolParts.push(tool)
        }
      }
      const modelId = typeof data.model?.id === 'string' ? data.model.id : null
      if (modelId !== null && !models.includes(modelId)) models.push(modelId)
      messages.push({
        ...entry,
        requestIndex,
        tokens: counts,
        cost: isCount(data.cost) ? data.cost : null,
        model: modelId,
        finish: typeof data.finish === 'string' ? data.finish : null,
        toolParts: parts,
      })
    } else {
      messages.push(entry)
    }
  }
  return { userMessages, userTexts, assistantMessages, toolParts, models, messages }
}

function readOpenCodeSide(ctx, db, sessionId) {
  const sessionSql = 'SELECT * FROM session_v2 WHERE id = ?'
  const messagesSql = 'SELECT id, type, seq, data FROM session_message WHERE session_id = ? ORDER BY seq ASC LIMIT ?'
  logSql(ctx, 'opencode.db', sessionSql)
  logSql(ctx, 'opencode.db', messagesSql)
  const sessionRow = db.prepare(sessionSql).get(sessionId)
  if (sessionRow === undefined) throw new GateError(`session_v2 has no row for ${sessionId}`)
  const rows = db.prepare(messagesSql).all(sessionId, LIMITS.maxMessages + 1)
  if (rows.length > LIMITS.maxMessages) throw new GateError(`session ${sessionId} has more than ${LIMITS.maxMessages} messages; the gate session should have a handful`)

  const session = {}
  for (const key of ['tokens_input', 'tokens_output', 'tokens_reasoning', 'tokens_cache_read', 'tokens_cache_write', 'cost']) {
    const value = sessionRow[key]
    if (!isCount(value)) throw new GateError(`session_v2.${key} is ${JSON.stringify(value)}, not a number`)
    session[key] = value
  }
  const summary = summarizeMessages(rows)
  return { sessionRow, session, summary }
}

// ---------------------------------------------------------------------------------------------
// Waiting
// ---------------------------------------------------------------------------------------------

/**
 * Waits until the session is idle: not busy, `expectedUserMessages` prompts recorded, and the newest
 * session_message is the idle marker OpenCode appends when an execution ends.
 */
async function waitForIdle(ctx, db, sessionId, { expectedUserMessages, timeoutMs }) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    throwIfAborted(ctx)
    const state = readIdleState(ctx, db, sessionId)
    if (state.exists && !state.busy && state.userMessages >= expectedUserMessages && state.lastType === 'idle') {
      return { idle: true, state }
    }
    if (Date.now() >= deadline) return { idle: false, state }
    await sleep(LIMITS.pollMs)
  }
}

async function interruptSession(ctx, sessionId) {
  const request = interruptRequest({ sessionId })
  ctx.notes.push(`interrupted ${sessionId} through ${request.method} ${request.path}`)
  return opencodeApi(ctx, request)
}

/** Calls `opencode api`. A body request is retried once with an explicit JSON content type if that is what failed. */
async function opencodeApi(ctx, request) {
  const run = (headers) =>
    runCommand(ctx, ctx.config.opencodeBin, apiArgs(request, headers), { cwd: ctx.scratch ?? ctx.config.repoRoot, timeoutMs: LIMITS.apiMs })
  let result = await run([])
  if (request.body !== undefined && result.code !== 0 && /content-?type|unsupported media|415/i.test(`${result.stdout}\n${result.stderr}`)) {
    ctx.evidence.log('retrying with an explicit content-type header')
    result = await run(['content-type: application/json'])
  }
  return result
}

function countStepRecords(ctx, canonDb, sessionId) {
  const sql = 'SELECT COUNT(*) AS n FROM records WHERE harness = ? AND session_id = ? AND name = ?'
  logSql(ctx, 'canon.db', sql)
  const row = canonDb.prepare(sql).get('opencode', sessionId, STEP_SPAN_NAME)
  return row ? Number(row.n) : 0
}

async function waitForStepRecords(ctx, canonDb, sessionId, expected, timeoutMs) {
  const deadline = Date.now() + timeoutMs
  let count = 0
  for (;;) {
    throwIfAborted(ctx)
    count = countStepRecords(ctx, canonDb, sessionId)
    if (count === expected) return { reached: true, count }
    if (Date.now() >= deadline) return { reached: false, count }
    await sleep(LIMITS.pollMs)
  }
}

function recordNamesForSession(ctx, canonDb, sessionId) {
  const sql = 'SELECT name, COUNT(*) AS n FROM records WHERE harness = ? AND session_id = ? GROUP BY name ORDER BY name LIMIT 50'
  logSql(ctx, 'canon.db', sql)
  return canonDb
    .prepare(sql)
    .all('opencode', sessionId)
    .map((entry) => ({ name: String(entry.name), count: Number(entry.n) }))
}

// ---------------------------------------------------------------------------------------------
// Steps
// ---------------------------------------------------------------------------------------------

function createScratchProject(ctx) {
  const scratch = mkdtempSync(join(ctx.config.tmpRoot, 'kw-opencode-live-gate-'))
  const real = realpathSync(scratch)
  const repo = realpathSync(ctx.config.repoRoot)
  if (real === repo || real.startsWith(repo + sep)) {
    throw new GateError(`scratch project ${real} is inside the repository`)
  }
  const skillDir = join(scratch, '.opencode', 'skills', SKILL_NAME)
  mkdirSync(skillDir, { recursive: true })
  writeFileSync(join(skillDir, 'SKILL.md'), SKILL_MARKDOWN)
  writeFileSync(join(scratch, 'notes.txt'), NOTES_TEXT)
  ctx.scratch = scratch
  ctx.evidence.log(`scratch project: ${scratch}`)
  return scratch
}

async function preflight(ctx) {
  const { config } = ctx
  const opencode = await runCommand(ctx, config.opencodeBin, ['--version'], { cwd: config.repoRoot, timeoutMs: LIMITS.helpMs })
  if (opencode.code !== 0) throw new GateError(`\`${config.opencodeBin} --version\` failed: ${(opencode.stderr || opencode.error?.message || '').trim()}`)
  ctx.opencodeVersion = opencode.stdout.trim()

  const kyberdash = await runCommand(ctx, config.kyberdashBin, ['--version'], { cwd: config.repoRoot, timeoutMs: LIMITS.helpMs })
  if (kyberdash.code !== 0) throw new GateError(`\`${config.kyberdashBin} --version\` failed: ${(kyberdash.stderr || kyberdash.error?.message || '').trim()}`)
  ctx.kyberdashVersion = kyberdash.stdout.trim()

  const version = await httpGet(ctx, '/api/kyber/version')
  if (!version.ok) {
    throw new GateError(`the live kyberDash did not answer GET ${config.kyberBaseUrl}/api/kyber/version (${version.status ?? describeError(version.error)}); start it first`)
  }
  ctx.kyberApiVersion = version.json ?? null

  ctx.opencodeDb = await openReadOnly(config.opencodeDb)
  ctx.canonDb = await openReadOnly(config.canonDb)
  confirmSchemas(ctx, ctx.opencodeDb, ctx.canonDb)
}

/** Runs prompt 1, falling back to the second allowed model only on provider.no-route. */
async function runPromptOne(ctx, title) {
  const attempts = []
  for (const model of [PRIMARY_MODEL, FALLBACK_MODEL]) {
    const result = await runCommand(ctx, ctx.config.opencodeBin, promptOneArgs({ model, title, prompt: PROMPT_ONE }), {
      cwd: ctx.scratch,
      timeoutMs: LIMITS.runMs,
    })
    const inspected = inspectRunOutput(result.stdout)
    attempts.push({ model, result, ...inspected })
    const noRoute = inspected.errors.some((error) => error.type === 'provider.no-route')
    if (!noRoute) break
    ctx.notes.push(`${model} reported provider.no-route (${inspected.errors.map((error) => error.message).join('; ')}); rerunning with ${model === PRIMARY_MODEL ? FALLBACK_MODEL : 'no further model'}`)
    const abandoned = inspected.errors.map((error) => error.sessionId).filter((id) => id !== null)
    if (abandoned.length > 0) ctx.notes.push(`the ${model} attempt created session(s) ${abandoned.join(', ')} that never ran a model; they are not the gate session`)
    ctx.evidence.writeText(`opencode-prompt1-attempt-${attempts.length}-no-route-output.jsonl`, result.stdout)
  }
  const last = attempts[attempts.length - 1]
  ctx.evidence.writeText('opencode-prompt1-output.jsonl', last.result.stdout)
  if (last.result.timedOut) throw new GateError(`prompt 1 did not finish within ${LIMITS.runMs / 1000}s and the client was stopped`)
  if (last.errors.length > 0) throw new GateError(`prompt 1 (${last.model}) reported: ${last.errors.map((error) => `${error.type} ${error.message}`).join('; ')}`)
  if (last.result.code !== 0) throw new GateError(`prompt 1 (${last.model}) exited ${last.result.code}: ${last.result.stderr.trim().slice(0, 300)}`)
  if (last.sessionId === null) throw new GateError('prompt 1 printed no session id (no JSON event carried a sessionID)')
  return { model: last.model, sessionId: last.sessionId, fellBack: attempts.length > 1, attempts }
}

/** Finds how to send a prompt with a skill attached to an existing session. */
async function discoverContinuationRoute(ctx) {
  const runHelp = await runCommand(ctx, ctx.config.opencodeBin, ['run', '--help'], { cwd: ctx.scratch, timeoutMs: LIMITS.helpMs })
  if (runHelp.code === 0 && /--skill\b/.test(`${runHelp.stdout}\n${runHelp.stderr}`)) {
    return { route: 'run', detail: '`opencode run --help` lists --skill' }
  }
  const apiHelp = await runCommand(ctx, ctx.config.opencodeBin, ['api', '--help'], { cwd: ctx.scratch, timeoutMs: LIMITS.helpMs })
  if (apiHelp.code === 0 && /opencode api/.test(`${apiHelp.stdout}\n${apiHelp.stderr}`)) {
    return { route: 'api', detail: '`opencode run --help` has --session but no skill-attach flag; `opencode api` takes POST /api/session/<id>/prompt with skills[]' }
  }
  throw new GateError('neither `opencode run --help` (a --skill flag) nor `opencode api --help` offers a way to attach a skill to a prompt')
}

async function runPromptTwo(ctx, { sessionId, model, route }) {
  if (route.route === 'run') {
    const result = await runCommand(ctx, ctx.config.opencodeBin, continueRunArgs({ sessionId, model, prompt: PROMPT_TWO, skill: SKILL_NAME }), {
      cwd: ctx.scratch,
      timeoutMs: LIMITS.runMs,
    })
    ctx.evidence.writeText('opencode-prompt2-output.jsonl', result.stdout)
    const inspected = inspectRunOutput(result.stdout)
    if (result.timedOut || result.code !== 0 || inspected.errors.length > 0) {
      throw new GateError(`prompt 2 through \`opencode run\` failed (exit ${result.code}): ${inspected.errors.map((error) => error.message).join('; ') || result.stderr.trim().slice(0, 300)}`)
    }
    return
  }

  // Make the model explicit on the session. The prompt route has no model field; a rejected switch
  // is recorded, not fatal, because prompt 1 already set the model and the comparator checks it.
  const switched = await opencodeApi(ctx, modelSwitchRequest({ sessionId, model }))
  if (switched.code !== 0) {
    ctx.notes.push(`the model switch to ${model} was rejected (exit ${switched.code}); the session keeps the model prompt 1 set with -m, and the models row checks it`)
  }

  const prompt = await opencodeApi(ctx, promptTwoApiRequest({ sessionId, prompt: PROMPT_TWO, skill: SKILL_NAME }))
  ctx.evidence.writeText('opencode-prompt2-api-response.json', prompt.stdout)
  if (prompt.code !== 0) {
    throw new GateError(`prompt 2 through \`opencode api\` failed (exit ${prompt.code}): ${(prompt.stderr || prompt.stdout).trim().slice(0, 300)}`)
  }
}

async function fetchKyberSession(ctx, sessionId) {
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}`
  let last
  for (let attempt = 1; attempt <= LIMITS.sessionApiAttempts; attempt += 1) {
    throwIfAborted(ctx)
    last = await httpGet(ctx, path)
    if (last.ok && last.json !== undefined) return last
    if (attempt < LIMITS.sessionApiAttempts) await sleep(LIMITS.sessionApiDelayMs)
  }
  return last
}

/** The comparator's extra checks: facts about the OpenCode side that make the comparison meaningful. */
function openCodeSelfChecks(reading, prompts) {
  const rows = []
  const recorded = reading.summary.userTexts
  const matches = recorded.length === prompts.length && recorded.every((text, index) => text === prompts[index])
  rows.push({
    field: 'opencode_prompts_recorded',
    kyberdash: null,
    opencode: recorded,
    pass: matches,
    reason: matches
      ? 'OpenCode recorded exactly the prompts the gate sent'
      : `OpenCode's user messages ${JSON.stringify(recorded)} differ from the prompts sent ${JSON.stringify(prompts)}`,
  })

  const sums = { input: 0, output: 0, reasoning: 0, cache_read: 0, cache_write: 0 }
  for (const message of reading.summary.assistantMessages) {
    for (const key of Object.keys(sums)) sums[key] += message[key]
  }
  const { session } = reading
  const mismatches = []
  if (sums.input !== session.tokens_input) mismatches.push(`input ${sums.input} vs tokens_input ${session.tokens_input}`)
  if (sums.output !== session.tokens_output) mismatches.push(`output ${sums.output} vs tokens_output ${session.tokens_output}`)
  if (sums.reasoning !== session.tokens_reasoning) mismatches.push(`reasoning ${sums.reasoning} vs tokens_reasoning ${session.tokens_reasoning}`)
  if (sums.cache_read !== session.tokens_cache_read) mismatches.push(`cache_read ${sums.cache_read} vs tokens_cache_read ${session.tokens_cache_read}`)
  if (sums.cache_write !== session.tokens_cache_write) mismatches.push(`cache_write ${sums.cache_write} vs tokens_cache_write ${session.tokens_cache_write}`)
  rows.push({
    field: 'opencode_session_vs_messages',
    kyberdash: null,
    opencode: sums,
    pass: mismatches.length === 0,
    reason: mismatches.length === 0 ? 'session_v2 token totals equal the sum of the assistant messages' : `session_v2 totals differ from the assistant message sums: ${mismatches.join('; ')}`,
  })
  return rows
}

function uniqueNames(toolParts) {
  const names = []
  for (const part of toolParts) {
    if (part.name !== '' && !names.includes(part.name)) names.push(part.name)
  }
  return names
}

function skillsListedCount(kyberSession) {
  const skills = kyberSession && typeof kyberSession === 'object' ? kyberSession.skills : null
  if (!skills || typeof skills !== 'object') return null
  if (isCount(skills.listed_count)) return skills.listed_count
  if (Array.isArray(skills.rows)) return skills.rows.filter((entry) => entry && entry.listed === true).length
  return null
}

/** A Playwright config without a `webServer`, so the spec runs against the live host and starts nothing. */
export function playwrightConfigSource({ testDir, outputDir, baseUrl }) {
  return `// Generated by dash/scripts/opencode-live-gate/run.mjs for one live-gate run.
// It has no webServer entry on purpose: the spec targets the live kyberDash on ${baseUrl}, and
// dash/playwright.config.ts would otherwise try to build and start a second server on that port.
export default {
  testDir: ${JSON.stringify(testDir)},
  testMatch: 'opencode-live-gate.spec.ts',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 1,
  reporter: 'list',
  outputDir: ${JSON.stringify(outputDir)},
  use: { baseURL: ${JSON.stringify(baseUrl)} },
}
`
}

async function runUiSpec(ctx, { sessionId, expected }) {
  const expectedPath = ctx.evidence.path('ui-expected.json')
  ctx.evidence.writeJson('ui-expected.json', expected)
  const configPath = ctx.evidence.path('playwright.live.config.mjs')
  ctx.evidence.writeText(
    'playwright.live.config.mjs',
    playwrightConfigSource({
      testDir: join(ctx.config.dashRoot, 'e2e'),
      outputDir: ctx.evidence.path('playwright-results'),
      baseUrl: ctx.config.kyberBaseUrl,
    }),
  )
  const env = { ...process.env }
  delete env.CI // dash/playwright.config.ts only reuses a running server when CI is unset
  env.FORCE_COLOR = '0'
  env.KW_OPENCODE_LIVE_SESSION_ID = sessionId
  env.KW_OPENCODE_LIVE_EXPECTED = expectedPath
  env.KW_OPENCODE_LIVE_EVIDENCE_DIR = ctx.evidence.dir
  ctx.evidence.log(`env for the UI spec: KW_OPENCODE_LIVE_SESSION_ID=${sessionId} KW_OPENCODE_LIVE_EXPECTED=${expectedPath} KW_OPENCODE_LIVE_EVIDENCE_DIR=${ctx.evidence.dir} (CI unset)`)
  const result = await runCommand(ctx, 'npx', ['playwright', 'test', '--config', configPath, 'opencode-live-gate'], {
    cwd: ctx.config.dashRoot,
    env,
    timeoutMs: LIMITS.uiMs,
  })
  ctx.evidence.writeText('playwright-output.txt', `${result.stdout}\n${result.stderr}`)
  const screenshot = existsSync(ctx.evidence.path('session-page.png'))
  return { passed: result.code === 0 && !result.timedOut && screenshot, code: result.code, timedOut: result.timedOut, screenshot }
}

// ---------------------------------------------------------------------------------------------
// Cleanup
// ---------------------------------------------------------------------------------------------

async function listGateRuns(ctx) {
  if (process.platform === 'win32') return { supported: false, pids: [] }
  // The process table is not logged: other processes' arguments are not the gate's to publish.
  const result = await runCommand(ctx, 'ps', ['-axo', 'pid=,command='], { cwd: ctx.config.repoRoot, timeoutMs: LIMITS.helpMs, logOutput: false })
  if (result.code !== 0) return { supported: false, pids: [] }
  const marker = `--title kw-live-gate ${ctx.stamp}`
  const pids = []
  for (const line of result.stdout.split('\n')) {
    const match = /^\s*(\d+)\s+(.*)$/.exec(line)
    if (!match) continue
    const command = match[2]
    if (Number(match[1]) === process.pid) continue
    if (/\bopencode\b/.test(command) && command.includes(' run ') && (command.includes(marker) || command.includes(PROMPT_TWO))) {
      pids.push(Number(match[1]))
    }
  }
  return { supported: true, pids }
}

function isInside(parent, child) {
  const rel = relative(parent, child)
  return rel !== '' && !rel.startsWith('..') && !rel.startsWith(sep)
}

async function cleanup(ctx) {
  const result = { runsLeft: [], busyAfter: null, scratchRemoved: false, skillCopied: false }

  for (const child of [...ctx.children]) killChild(child)
  const settleBy = Date.now() + 10_000
  while ([...ctx.children].some(isAlive) && Date.now() < settleBy) await sleep(200)

  try {
    const { supported, pids } = await listGateRuns(ctx)
    if (!supported) ctx.notes.push('the stray `opencode run` check was skipped: `ps` is unavailable on this platform')
    for (const pid of pids) {
      ctx.evidence.log(`stopping stray gate process ${pid}`)
      try {
        process.kill(pid, 'SIGTERM')
      } catch (error) {
        ctx.evidence.log(`  could not signal ${pid}: ${describeError(error)}`)
      }
    }
    if (pids.length > 0) {
      await sleep(1_500)
      result.runsLeft = (await listGateRuns(ctx)).pids
      if (result.runsLeft.length > 0) ctx.failures.push(`opencode run process(es) left behind: ${result.runsLeft.join(', ')}`)
      else ctx.notes.push(`stopped ${pids.length} stray opencode run process(es): ${pids.join(', ')}`)
    }
  } catch (error) {
    ctx.failures.push(`the stray-process check failed: ${describeError(error)}`)
  }

  if (ctx.sessionId !== null && ctx.opencodeDb) {
    try {
      let state = readIdleState(ctx, ctx.opencodeDb, ctx.sessionId)
      if (state.busy) {
        ctx.evidence.log(`session ${ctx.sessionId} is still busy at cleanup; interrupting`)
        await interruptSession(ctx, ctx.sessionId)
        const settle = await waitForIdleOrNotBusy(ctx, ctx.opencodeDb, ctx.sessionId)
        state = settle
      }
      result.busyAfter = state.busy
      if (state.busy) ctx.failures.push(`session ${ctx.sessionId} is still busy after the gate`)
    } catch (error) {
      ctx.failures.push(`could not confirm the session is idle: ${describeError(error)}`)
    }
  }

  if (ctx.scratch !== null) {
    try {
      const skillFile = join(ctx.scratch, '.opencode', 'skills', SKILL_NAME, 'SKILL.md')
      if (existsSync(skillFile)) {
        copyFileSync(skillFile, ctx.evidence.path(`${SKILL_NAME}.SKILL.md`))
        result.skillCopied = true
      }
      const real = realpathSync(ctx.scratch)
      const tmp = realpathSync(ctx.config.tmpRoot)
      if (!isInside(tmp, real) || !basename(real).startsWith('kw-opencode-live-gate-') || isInside(realpathSync(ctx.config.repoRoot), real)) {
        throw new GateError(`refusing to remove ${real}: not a gate scratch directory under ${tmp}`)
      }
      rmSync(real, { recursive: true, force: true })
      result.scratchRemoved = true
      ctx.evidence.log(`removed scratch project ${real}`)
    } catch (error) {
      ctx.failures.push(`the scratch project was not removed: ${describeError(error)}`)
    }
  }

  for (const db of [ctx.opencodeDb, ctx.canonDb]) {
    try {
      db?.close()
    } catch (error) {
      ctx.evidence.log(`closing a database failed: ${describeError(error)}`)
    }
  }
  return result
}

/** After an interrupt, waits briefly for the session to stop being busy. Returns the last idle state. */
async function waitForIdleOrNotBusy(ctx, db, sessionId) {
  const deadline = Date.now() + LIMITS.interruptSettleMs
  let state = readIdleState(ctx, db, sessionId)
  while (state.busy && Date.now() < deadline) {
    await sleep(LIMITS.pollMs)
    state = readIdleState(ctx, db, sessionId)
  }
  return state
}

// ---------------------------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------------------------

function cellText(value) {
  let text
  if (value === null || value === undefined) text = '-'
  else if (typeof value === 'string') text = value
  else text = JSON.stringify(value)
  const clipped = text.length > 80 ? `${text.slice(0, 77)}...` : text
  return clipped.replace(/\|/g, '\\|').replace(/\n/g, ' ')
}

function summaryMarkdown(ctx, outcome) {
  const lines = []
  lines.push(`# OpenCode live gate: ${outcome.pass ? 'PASS' : 'FAIL'}`)
  lines.push('')
  lines.push(`- stamp: ${ctx.stamp}`)
  lines.push(`- evidence: ${ctx.evidence.dir}`)
  lines.push(`- session: ${ctx.sessionId ?? 'none (the run stopped before a session existed)'}`)
  lines.push(`- title: ${ctx.title}`)
  lines.push(`- model: ${ctx.modelUsed ?? 'none'}${ctx.fellBack ? ` (fell back from ${PRIMARY_MODEL}: provider.no-route)` : ''}`)
  lines.push(`- continuation route: ${ctx.route ? `${ctx.route.route} - ${ctx.route.detail}` : 'not reached'}`)
  lines.push(`- opencode: ${ctx.opencodeVersion ?? 'unknown'}; kyberdash: ${ctx.kyberdashVersion ?? 'unknown'}`)
  lines.push(`- wall clock for both prompts: ${ctx.wallClockMs ?? 'not measured'} ms`)
  lines.push(`- idle: ${ctx.idleReached === null ? 'not reached' : ctx.idleReached ? 'reached' : 'TIMED OUT, session interrupted'}`)
  lines.push(`- step records in canon.db: ${ctx.stepRecords === null ? 'not read' : `${ctx.stepRecords.count} (expected ${ctx.stepRecords.expected})${ctx.stepRecords.reached ? '' : ' TIMED OUT'}`}`)
  lines.push(`- cleanup: ${ctx.cleanup ? `gate runs left ${ctx.cleanup.runsLeft.length === 0 ? 'none' : ctx.cleanup.runsLeft.join(', ')}; session busy after ${ctx.cleanup.busyAfter === null ? 'not checked' : ctx.cleanup.busyAfter}; scratch removed ${ctx.cleanup.scratchRemoved}; SKILL.md copied ${ctx.cleanup.skillCopied}` : 'not run'}`)
  lines.push(`- UI spec: ${ctx.ui === null ? 'not run' : ctx.ui.passed ? 'passed, screenshot session-page.png' : `FAILED (exit ${ctx.ui.code}${ctx.ui.timedOut ? ', timed out' : ''}${ctx.ui.screenshot ? '' : ', no screenshot'})`}`)
  lines.push('')
  if (outcome.failures.length > 0) {
    lines.push('## Failures')
    lines.push('')
    for (const failure of outcome.failures) lines.push(`- ${failure}`)
    lines.push('')
  }
  if (ctx.notes.length > 0) {
    lines.push('## Notes')
    lines.push('')
    for (const note of ctx.notes) lines.push(`- ${note}`)
    lines.push('')
  }
  if (ctx.comparison) {
    lines.push('## Comparator rows')
    lines.push('')
    lines.push('| field | pass | kyberDash | OpenCode | reason |')
    lines.push('| --- | --- | --- | --- | --- |')
    for (const row of ctx.comparison.rows) {
      lines.push(`| ${cellText(row.field)} | ${row.pass ? 'pass' : row.waived === true ? 'WAIVED' : 'FAIL'} | ${cellText(row.kyberdash)} | ${cellText(row.opencode)} | ${cellText(row.reason)} |`)
    }
    lines.push('')
  }
  lines.push('Files: commands.log, comparison.json, opencode-session_v2.json, opencode-session_message-summary.json, kyberdash-api-*.json, ui-expected.json, playwright-output.txt.')
  lines.push('')
  return lines.join('\n')
}

// ---------------------------------------------------------------------------------------------
// The run
// ---------------------------------------------------------------------------------------------

async function runSteps(ctx) {
  const { config } = ctx
  progress('preflight')
  await preflight(ctx)

  progress('1/9 scratch project')
  createScratchProject(ctx)

  progress(`2/9 prompt 1 (${PRIMARY_MODEL})`)
  ctx.gateStartedAt = Date.now()
  const one = await runPromptOne(ctx, ctx.title)
  ctx.sessionId = one.sessionId
  ctx.modelUsed = one.model
  ctx.fellBack = one.fellBack
  progress(`   session ${ctx.sessionId}, model ${ctx.modelUsed}`)

  const afterOne = await waitForIdle(ctx, ctx.opencodeDb, ctx.sessionId, { expectedUserMessages: 1, timeoutMs: LIMITS.idleMs })
  if (!afterOne.idle) {
    ctx.idleReached = false
    await timeoutInterrupt(ctx, afterOne.state, 'after prompt 1')
    throw new GateError(`the session was not idle ${LIMITS.idleMs / 1000}s after prompt 1 (state ${JSON.stringify(afterOne.state)}); interrupted`)
  }

  progress('3/9 prompt 2 (skill attached)')
  ctx.route = await discoverContinuationRoute(ctx)
  progress(`   route: ${ctx.route.route}`)
  await runPromptTwo(ctx, { sessionId: ctx.sessionId, model: ctx.modelUsed, route: ctx.route })

  progress('4/9 waiting for the session to go idle')
  const afterTwo = await waitForIdle(ctx, ctx.opencodeDb, ctx.sessionId, { expectedUserMessages: 2, timeoutMs: LIMITS.idleMs })
  ctx.idleReached = afterTwo.idle
  if (!afterTwo.idle) {
    await timeoutInterrupt(ctx, afterTwo.state, 'after prompt 2')
    throw new GateError(`the session was not idle ${LIMITS.idleMs / 1000}s after prompt 2 (state ${JSON.stringify(afterTwo.state)}); interrupted`)
  }
  ctx.wallClockMs = Date.now() - ctx.gateStartedAt

  progress('5/9 reading OpenCode, waiting for kyberDash ingest')
  const reading = readOpenCodeSide(ctx, ctx.opencodeDb, ctx.sessionId)
  ctx.evidence.writeJson('opencode-session_v2.json', reading.sessionRow)
  ctx.evidence.writeJson('opencode-session_message-summary.json', {
    sessionId: ctx.sessionId,
    userMessages: reading.summary.userMessages,
    assistantMessages: reading.summary.assistantMessages.length,
    models: reading.summary.models,
    toolParts: reading.summary.toolParts,
    session: reading.session,
    messages: reading.summary.messages,
  })

  const assistantCount = reading.summary.assistantMessages.length
  const ingest = await waitForStepRecords(ctx, ctx.canonDb, ctx.sessionId, assistantCount, LIMITS.ingestMs)
  ctx.stepRecords = { ...ingest, expected: assistantCount }
  ctx.evidence.writeJson('canon-records-by-name.json', recordNamesForSession(ctx, ctx.canonDb, ctx.sessionId))
  if (!ingest.reached) {
    ctx.failures.push(`canon.db held ${ingest.count} ${STEP_SPAN_NAME} record(s) for the session after ${LIMITS.ingestMs / 1000}s; OpenCode recorded ${assistantCount} assistant messages`)
  }

  const build = await runCommand(ctx, config.kyberdashBin, ['kyber', 'build'], { cwd: config.repoRoot, timeoutMs: LIMITS.buildMs })
  if (build.code !== 0 || build.timedOut) {
    ctx.failures.push(`\`${config.kyberdashBin} kyber build\` failed (exit ${build.code}${build.timedOut ? ', timed out' : ''})`)
  }

  progress('6/9 reading kyberDash')
  const session = await fetchKyberSession(ctx, ctx.sessionId)
  ctx.evidence.writeText('kyberdash-api-session.json', session.text)
  const sessions = await httpGet(ctx, '/api/kyber/sessions?harness=opencode')
  ctx.evidence.writeText('kyberdash-api-sessions-opencode.json', sessions.text)
  const capture = await httpGet(ctx, '/api/kyber/capture')
  ctx.evidence.writeText('kyberdash-api-capture.json', capture.text)
  if (!session.ok || session.json === undefined) {
    ctx.failures.push(`GET /api/kyber/session/${ctx.sessionId} did not return JSON (HTTP ${session.status ?? describeError(session.error)})`)
  }

  progress('7/9 comparing')
  const prompts = [recordedByRun(PROMPT_ONE), PROMPT_TWO]
  const gate = {
    prompts,
    skill: SKILL_NAME,
    wallClockMs: ctx.wallClockMs,
    expectedModel: expectedModelId(ctx.modelUsed),
    expectedTitle: ctx.title,
  }
  const opencode = {
    session: reading.session,
    userMessages: reading.summary.userMessages,
    assistantMessages: reading.summary.assistantMessages,
    toolParts: reading.summary.toolParts,
    models: reading.summary.models,
  }
  const comparison = compareSession({ kyberSession: session.json ?? null, opencode, gate })
  const extra = openCodeSelfChecks({ session: reading.session, summary: reading.summary }, prompts)
  const rows = [...comparison.rows, ...extra]
  ctx.comparison = { pass: rows.every((row) => row.pass || row.waived === true), rows }
  ctx.evidence.writeJson('comparison.json', { pass: ctx.comparison.pass, gate, opencode, rows })
  for (const row of rows) {
    if (!row.pass && row.waived !== true) ctx.failures.push(`comparator ${row.field}: ${row.reason}`)
  }

  progress('8/9 UI spec against the live kyberDash')
  if (session.json === undefined) {
    ctx.notes.push('the UI spec was skipped: kyberDash returned no session to display')
  } else {
    // The label row can be waived (see compare.mjs); the page then shows the fallback label, so the UI
    // spec has no title to look for.
    const labelShown = rows.some((row) => row.field === 'label' && row.pass)
    const expected = {
      title: labelShown ? ctx.title : null,
      skill: SKILL_NAME,
      prompts,
      requestCount: reading.summary.userMessages,
      turnCount: assistantCount,
      totalInput: reading.session.tokens_input + reading.session.tokens_cache_read + reading.session.tokens_cache_write,
      totalOutput: reading.session.tokens_output + reading.session.tokens_reasoning,
      toolCalls: reading.summary.toolParts.length,
      toolsInvoked: uniqueNames(reading.summary.toolParts),
      skillsListed: skillsListedCount(session.json),
    }
    ctx.ui = await runUiSpec(ctx, { sessionId: ctx.sessionId, expected })
    if (!ctx.ui.passed) ctx.failures.push(`the UI spec failed (exit ${ctx.ui.code}${ctx.ui.timedOut ? ', timed out' : ''}${ctx.ui.screenshot ? '' : ', no screenshot'}); see playwright-output.txt`)
  }
}

/** Interrupts a session that never went idle and records what the state was. */
async function timeoutInterrupt(ctx, state, when) {
  ctx.evidence.log(`session ${ctx.sessionId} not idle ${when}: ${JSON.stringify(state)}`)
  await interruptSession(ctx, ctx.sessionId)
  const after = await waitForIdleOrNotBusy(ctx, ctx.opencodeDb, ctx.sessionId)
  ctx.notes.push(`idle wait timed out ${when}; after the interrupt the session ${after.busy ? 'is still busy' : 'is not busy'} (last message ${after.lastType})`)
  try {
    const reading = readOpenCodeSide(ctx, ctx.opencodeDb, ctx.sessionId)
    ctx.evidence.writeJson('opencode-session_v2.json', reading.sessionRow)
  } catch (error) {
    ctx.notes.push(`could not read the timed-out session for evidence: ${describeError(error)}`)
  }
}

const USAGE = `Usage: node dash/scripts/opencode-live-gate/run.mjs [--dry-run] [--help]

Runs the OpenCode live acceptance gate against the real OpenCode and the live kyberDash.
It calls a real model and writes evidence under artifacts/evidence/opencode-v2-live-gate/.

  --dry-run   print the planned commands and paths; run nothing, open no database, write no file
  --help      print this text
`

/**
 * @param {string[]} argv
 * @param {{ config?: ReturnType<typeof defaultConfig>, now?: () => Date }} [deps]
 * @returns {Promise<number>} the exit status
 */
export async function main(argv, { config = defaultConfig(), now = () => new Date() } = {}) {
  let values
  try {
    const parsed = parseArgs({ args: argv, options: { 'dry-run': { type: 'boolean' }, help: { type: 'boolean', short: 'h' } }, allowPositionals: false })
    values = parsed.values
  } catch (error) {
    process.stderr.write(`${describeError(error)}\n\n${USAGE}`)
    return 2
  }
  if (values.help) {
    process.stdout.write(USAGE)
    return 0
  }

  const stamp = utcStamp(now())
  const plan = buildPlan(config, stamp)
  if (values['dry-run']) {
    process.stdout.write(`${formatPlan(plan)}\n`)
    return 0
  }

  const evidence = createEvidence(plan.evidenceDir)
  const ctx = {
    config,
    stamp,
    title: plan.title,
    evidence,
    children: new Set(),
    loggedSql: new Set(),
    aborted: false,
    failures: [],
    notes: [],
    scratch: null,
    opencodeDb: null,
    canonDb: null,
    sessionId: null,
    modelUsed: null,
    fellBack: false,
    route: null,
    idleReached: null,
    stepRecords: null,
    comparison: null,
    ui: null,
    wallClockMs: null,
    gateStartedAt: null,
    opencodeVersion: null,
    kyberdashVersion: null,
    kyberApiVersion: null,
    cleanup: null,
  }
  evidence.log(`OpenCode live gate ${stamp}; evidence ${evidence.dir}`)

  const onSignal = (signal) => {
    ctx.aborted = true
    ctx.failures.push(`interrupted by ${signal}`)
    for (const child of ctx.children) killChild(child)
  }
  const onSigint = () => onSignal('SIGINT')
  const onSigterm = () => onSignal('SIGTERM')
  process.on('SIGINT', onSigint)
  process.on('SIGTERM', onSigterm)

  try {
    await runSteps(ctx)
  } catch (error) {
    ctx.failures.push(describeError(error))
    evidence.log(`FAILED: ${error instanceof Error ? (error.stack ?? error.message) : String(error)}`)
  } finally {
    ctx.aborted = false // cleanup must run to completion even after an interrupt
    try {
      ctx.cleanup = await cleanup(ctx)
    } catch (error) {
      ctx.failures.push(`cleanup failed: ${describeError(error)}`)
    }
    process.off('SIGINT', onSigint)
    process.off('SIGTERM', onSigterm)
  }

  const pass = ctx.failures.length === 0 && ctx.comparison !== null && ctx.comparison.pass
  const outcome = { pass, failures: ctx.failures }
  evidence.writeText('summary.md', summaryMarkdown(ctx, outcome))
  process.stdout.write(`${pass ? 'PASS' : 'FAIL'}\n`)
  for (const failure of ctx.failures) process.stdout.write(`  - ${failure}\n`)
  process.stdout.write(`evidence: ${evidence.dir}\n`)
  return pass ? 0 : 1
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isMain) {
  process.exitCode = await main(process.argv.slice(2))
}
