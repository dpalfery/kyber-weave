// Comparator for the OpenCode live gate.
//
// The gate runs a real OpenCode session, then reads two things: kyberDash's session API
// (GET /api/kyber/session/<id>) and OpenCode's own database (opencode.db, read-only). This module
// answers one question: do they agree, line by line? It is pure: no I/O, no clock, no network.
//
//   compareSession({ kyberSession, opencode, gate }) -> { pass, rows }
//
// Every row is { field, kyberdash, opencode, pass, reason }. `pass` is true only when every row
// passes. A key the session API does not carry yields a failing row that names the missing key; it
// never throws and never passes by default. The contract tests are
// dash/scripts/opencode-live-gate/compare.test.ts.
//
// Inputs
//   kyberSession  parsed JSON of the session API. Totals, cost, tools_offered, tool_calls and
//                 tools_invoked sit under `summary`; label, requests, context, skills and turns sit
//                 at the top level.
//   opencode      { session: { tokens_input, tokens_output, tokens_reasoning, tokens_cache_read,
//                              tokens_cache_write, cost },
//                   userMessages: number,
//                   assistantMessages: [{ input, output, reasoning, cache_read, cache_write,
//                                         requestIndex? }],
//                   toolParts: [{ name, status }],
//                   models: string[] }
//                 `requestIndex` (optional) is the zero-based index of the user message an assistant
//                 message answers. With it the comparator checks each request's turn count against
//                 OpenCode's own grouping; without it that check is only possible for a session with
//                 one request.
//   gate          { prompts: string[], skill, wallClockMs, expectedModel, expectedTitle }

/** The API caps a request's prompt preview at this many characters. */
const PROMPT_PREVIEW_LIMIT = 200

/** Cost values agree when they differ by no more than this. */
const COST_TOLERANCE = 1e-9

/** The duration may exceed the measured wall-clock by this much before it is implausible. */
const DURATION_SLACK_MS = 1000

/** Tools the gate's first prompt makes the model use. */
const REQUIRED_TOOLS = ['skill', 'read']

/**
 * @typedef {{ field: string, kyberdash: unknown, opencode: unknown, pass: boolean, reason: string }} CompareRow
 */

/**
 * @param {unknown} value
 * @returns {boolean}
 */
function isRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isCount(value) {
  return typeof value === 'number' && Number.isFinite(value)
}

function isNonEmptyString(value) {
  return typeof value === 'string' && value.trim() !== ''
}

/** Reads a nested key; any non-object on the way yields `undefined`, never a throw. */
function dig(root, ...keys) {
  let current = root
  for (const key of keys) {
    if (!isRecord(current)) return undefined
    current = current[key]
  }
  return current
}

/** A short, single-line rendering of a value for a failure reason. */
function show(value) {
  if (value === undefined) return 'missing'
  let text
  try {
    text = JSON.stringify(value)
  } catch {
    text = String(value)
  }
  if (text === undefined) return String(value)
  return text.length > 120 ? `${text.slice(0, 117)}...` : text
}

/** A JSON-safe copy of a value for a row: absence is `null`, and the reason says "missing". */
function cell(value) {
  return value === undefined ? null : value
}

/**
 * @param {string} field
 * @param {unknown} kyberdash
 * @param {unknown} opencode
 * @param {boolean} pass
 * @param {string} reason
 * @returns {CompareRow}
 */
function row(field, kyberdash, opencode, pass, reason) {
  return { field, kyberdash: cell(kyberdash), opencode: cell(opencode), pass, reason }
}

/**
 * Describes why a value is not a usable number: absent, not-measurable block, or the wrong type.
 * @param {string} path
 * @param {unknown} value
 */
function notANumber(path, value) {
  if (value === undefined) return `${path} is missing from the session API`
  if (isRecord(value) && typeof value.availability === 'string') {
    return `${path} is not measurable: ${show(value)}`
  }
  return `${path} is not a number: ${show(value)}`
}

/**
 * A row that requires an exact numeric match.
 * @param {string} field
 * @param {string} path  where the kyberDash value lives, for the reason
 * @param {unknown} actual
 * @param {number} expected
 * @param {{ positive?: boolean, derivation: string }} options
 */
function exactCountRow(field, path, actual, expected, { positive = false, derivation }) {
  if (!isCount(actual)) return row(field, actual, expected, false, notANumber(path, actual))
  if (actual !== expected) {
    return row(field, actual, expected, false, `${path} is ${actual}, OpenCode recorded ${expected} (${derivation})`)
  }
  if (positive && !(expected > 0)) {
    return row(field, actual, expected, false, `${path} is ${actual}; a measured run must report more than zero (${derivation})`)
  }
  return row(field, actual, expected, true, `${path} equals ${derivation}`)
}

/** Checks the OpenCode reading is shaped as documented, so a malformed reading is one clear failure. */
function readingProblems(opencode) {
  const problems = []
  if (!isRecord(opencode)) return ['opencode reading is not an object']
  const session = opencode.session
  if (!isRecord(session)) {
    problems.push('opencode.session is missing')
  } else {
    for (const key of ['tokens_input', 'tokens_output', 'tokens_reasoning', 'tokens_cache_read', 'tokens_cache_write', 'cost']) {
      if (!isCount(session[key])) problems.push(`opencode.session.${key} is not a number: ${show(session[key])}`)
    }
  }
  if (!isCount(opencode.userMessages)) problems.push(`opencode.userMessages is not a number: ${show(opencode.userMessages)}`)
  if (!Array.isArray(opencode.assistantMessages)) {
    problems.push('opencode.assistantMessages is not an array')
  } else {
    opencode.assistantMessages.forEach((message, index) => {
      for (const key of ['input', 'output', 'reasoning', 'cache_read', 'cache_write']) {
        if (!isRecord(message) || !isCount(message[key])) {
          problems.push(`opencode.assistantMessages[${index}].${key} is not a number`)
        }
      }
    })
  }
  if (!Array.isArray(opencode.toolParts)) problems.push('opencode.toolParts is not an array')
  if (!Array.isArray(opencode.models)) problems.push('opencode.models is not an array')
  return problems
}

function gateProblems(gate) {
  const problems = []
  if (!isRecord(gate)) return ['gate is not an object']
  if (!Array.isArray(gate.prompts) || gate.prompts.some((prompt) => typeof prompt !== 'string')) {
    problems.push('gate.prompts is not an array of strings')
  }
  for (const key of ['skill', 'expectedModel', 'expectedTitle']) {
    if (!isNonEmptyString(gate[key])) problems.push(`gate.${key} is not a non-empty string`)
  }
  if (!isCount(gate.wallClockMs)) problems.push(`gate.wallClockMs is not a number: ${show(gate?.wallClockMs)}`)
  return problems
}

/** Σ over a list of numbers, or `undefined` when any entry is not a number. */
function sumOf(values) {
  let total = 0
  for (const value of values) {
    if (!isCount(value)) return undefined
    total += value
  }
  return total
}

function tokenRows(kyberSession, opencode) {
  const { session } = opencode
  const summary = dig(kyberSession, 'summary')
  const rows = []

  const expectedInput = session.tokens_input + session.tokens_cache_read + session.tokens_cache_write
  rows.push(
    exactCountRow('total_input', 'summary.total_input', dig(summary, 'total_input'), expectedInput, {
      positive: true,
      derivation: 'tokens_input + tokens_cache_read + tokens_cache_write',
    }),
  )

  const expectedOutput = session.tokens_output + session.tokens_reasoning
  rows.push(
    exactCountRow('total_output', 'summary.total_output', dig(summary, 'total_output'), expectedOutput, {
      positive: true,
      derivation: 'tokens_output + tokens_reasoning',
    }),
  )

  rows.push(turnsReasoningRow(kyberSession, session.tokens_reasoning))

  rows.push(
    exactCountRow('total_cache_read', 'summary.total_cache_read', dig(summary, 'total_cache_read'), session.tokens_cache_read, {
      derivation: 'tokens_cache_read',
    }),
  )
  rows.push(
    exactCountRow(
      'total_cache_creation',
      'summary.total_cache_creation',
      dig(summary, 'total_cache_creation'),
      session.tokens_cache_write,
      { derivation: 'tokens_cache_write' },
    ),
  )

  rows.push(costRow(dig(summary, 'cost'), session.cost))
  return rows
}

function turnsReasoningRow(kyberSession, expected) {
  const turns = dig(kyberSession, 'turns')
  if (!Array.isArray(turns)) {
    return row('turns_reasoning', turns, expected, false, `turns is missing from the session API; cannot sum turns[].reasoning to ${expected}`)
  }
  const reasoning = turns.map((turn) => dig(turn, 'reasoning'))
  const total = sumOf(reasoning)
  if (total === undefined) {
    const bad = reasoning.findIndex((value) => !isCount(value))
    return row('turns_reasoning', reasoning, expected, false, notANumber(`turns[${bad}].reasoning`, reasoning[bad]))
  }
  if (total !== expected) {
    return row('turns_reasoning', total, expected, false, `turns[].reasoning sums to ${total}, OpenCode recorded tokens_reasoning ${expected}`)
  }
  return row('turns_reasoning', total, expected, true, 'turns[].reasoning sums to tokens_reasoning')
}

function costRow(cost, expected) {
  if (!isRecord(cost)) {
    return row('cost', cost, expected, false, notANumber('summary.cost', cost))
  }
  if (cost.basis !== 'harness' || cost.status !== 'priced') {
    return row(
      'cost',
      cost,
      expected,
      false,
      `summary.cost must be a priced harness block (basis "harness", status "priced"); got basis ${show(cost.basis)}, status ${show(cost.status)}`,
    )
  }
  if (!isCount(cost.value)) {
    return row('cost', cost, expected, false, notANumber('summary.cost.value', cost.value))
  }
  if (Math.abs(cost.value - expected) > COST_TOLERANCE) {
    return row('cost', cost, expected, false, `summary.cost.value ${cost.value} differs from session_v2.cost ${expected}`)
  }
  return row('cost', cost, expected, true, 'priced harness cost equals session_v2.cost')
}

function durationRow(kyberSession, wallClockMs) {
  const duration = dig(kyberSession, 'summary', 'duration_ms')
  const limit = wallClockMs + DURATION_SLACK_MS
  if (!isCount(duration)) return row('duration_ms', duration, limit, false, notANumber('summary.duration_ms', duration))
  if (!(duration > 0)) {
    return row('duration_ms', duration, limit, false, `summary.duration_ms is ${duration}; a measured run takes more than zero milliseconds`)
  }
  if (duration > limit) {
    return row(
      'duration_ms',
      duration,
      limit,
      false,
      `summary.duration_ms ${duration} exceeds the measured wall-clock ${wallClockMs} ms plus ${DURATION_SLACK_MS} ms`,
    )
  }
  return row('duration_ms', duration, limit, true, `0 < duration <= wall-clock ${wallClockMs} ms + ${DURATION_SLACK_MS} ms`)
}

function countRows(kyberSession, opencode, gate) {
  const rows = []
  const promptsSent = gate.prompts.length

  const requestCount = dig(kyberSession, 'summary', 'request_count')
  if (!isCount(requestCount)) {
    rows.push(row('request_count', requestCount, opencode.userMessages, false, notANumber('summary.request_count', requestCount)))
  } else if (requestCount !== opencode.userMessages || opencode.userMessages !== promptsSent) {
    rows.push(
      row(
        'request_count',
        requestCount,
        opencode.userMessages,
        false,
        `summary.request_count ${requestCount}, OpenCode user messages ${opencode.userMessages}, prompts sent ${promptsSent}; all three must agree`,
      ),
    )
  } else {
    rows.push(row('request_count', requestCount, opencode.userMessages, true, 'request_count = user messages = prompts sent'))
  }

  rows.push(
    exactCountRow('turn_count', 'summary.turn_count', dig(kyberSession, 'summary', 'turn_count'), opencode.assistantMessages.length, {
      derivation: 'assistant messages',
    }),
  )
  return rows
}

/**
 * The turns OpenCode recorded for request `index`, or `undefined` when the reading cannot say.
 * Uses the optional `requestIndex` grouping; a one-request session needs none.
 */
function expectedTurnsFor(opencode, index) {
  const grouped = opencode.assistantMessages.every((message) => isCount(message.requestIndex))
  if (grouped) return opencode.assistantMessages.filter((message) => message.requestIndex === index).length
  if (opencode.userMessages === 1) return index === 0 ? opencode.assistantMessages.length : 0
  return undefined
}

function requestTurnRows(kyberSession, opencode) {
  const rows = []
  const requests = dig(kyberSession, 'requests')
  const assistantCount = opencode.assistantMessages.length

  if (!Array.isArray(requests)) {
    rows.push(row('requests[].turns', requests, assistantCount, false, 'requests is missing from the session API'))
  } else {
    const turns = requests.map((request) => dig(request, 'turns'))
    const total = sumOf(turns)
    if (total === undefined) {
      const bad = turns.findIndex((value) => !isCount(value))
      rows.push(row('requests[].turns', turns, assistantCount, false, notANumber(`requests[${bad}].turns`, turns[bad])))
    } else if (total !== assistantCount) {
      rows.push(row('requests[].turns', total, assistantCount, false, `requests[].turns sums to ${total}, OpenCode recorded ${assistantCount} assistant messages`))
    } else {
      rows.push(row('requests[].turns', total, assistantCount, true, 'requests[].turns sums to the assistant message count'))
    }
  }

  const listed = Array.isArray(requests) ? requests.length : 0
  const slots = Math.max(listed, opencode.userMessages)
  for (let index = 0; index < slots; index += 1) {
    const field = `requests[${index}].turns`
    const shown = Array.isArray(requests) ? dig(requests[index], 'turns') : undefined
    const expected = expectedTurnsFor(opencode, index)
    if (!isCount(shown)) {
      rows.push(row(field, shown, expected, false, notANumber(field, shown)))
    } else if (expected === undefined) {
      rows.push(
        row(
          field,
          shown,
          null,
          false,
          `OpenCode's per-request assistant counts were not provided (assistantMessages[].requestIndex), so ${field} = ${shown} cannot be verified for a session with ${opencode.userMessages} requests`,
        ),
      )
    } else if (shown !== expected) {
      rows.push(row(field, shown, expected, false, `${field} shows ${shown} turn(s), OpenCode recorded ${expected} assistant message(s) for that request`))
    } else {
      rows.push(row(field, shown, expected, true, `${field} equals the assistant messages OpenCode recorded for the request`))
    }
  }
  return rows
}

/** Names present in a list, or `undefined` when the list is not an array of strings. */
function nameList(value) {
  if (!Array.isArray(value) || value.some((entry) => typeof entry !== 'string')) return undefined
  return value
}

function toolRows(kyberSession, opencode) {
  const rows = []
  const summary = dig(kyberSession, 'summary')

  const offered = dig(summary, 'tools_offered')
  const offeredNames = nameList(offered)
  if (offeredNames === undefined) {
    rows.push(row('tools_offered', offered, REQUIRED_TOOLS, false, offered === undefined ? 'summary.tools_offered is missing from the session API' : `summary.tools_offered is not a list of tool names: ${show(offered)}`))
  } else if (offeredNames.length === 0) {
    rows.push(row('tools_offered', offeredNames, REQUIRED_TOOLS, false, 'summary.tools_offered is empty; the model was offered tools'))
  } else {
    const absent = REQUIRED_TOOLS.filter((tool) => !offeredNames.includes(tool))
    rows.push(
      row(
        'tools_offered',
        offeredNames,
        REQUIRED_TOOLS,
        absent.length === 0,
        absent.length === 0 ? 'tools_offered contains skill and read' : `summary.tools_offered lacks ${absent.join(', ')}`,
      ),
    )
  }

  const toolCalls = dig(summary, 'tool_calls')
  const unnamed = opencode.toolParts.findIndex((part) => !isRecord(part) || !isNonEmptyString(part.name))
  if (!isCount(toolCalls)) {
    rows.push(row('tool_calls', toolCalls, opencode.toolParts.length, false, notANumber('summary.tool_calls', toolCalls)))
  } else if (unnamed !== -1) {
    rows.push(
      row('tool_calls', toolCalls, opencode.toolParts.length, false, `OpenCode tool part ${unnamed} has no name, so it cannot be matched to a kyberDash tool call`),
    )
  } else if (toolCalls !== opencode.toolParts.length) {
    rows.push(
      row('tool_calls', toolCalls, opencode.toolParts.length, false, `summary.tool_calls is ${toolCalls}, OpenCode recorded ${opencode.toolParts.length} tool parts`),
    )
  } else {
    rows.push(row('tool_calls', toolCalls, opencode.toolParts.length, true, 'tool_calls equals the OpenCode tool parts, each named'))
  }

  const invoked = dig(summary, 'tools_invoked')
  const invokedNames = nameList(invoked)
  if (invokedNames === undefined) {
    rows.push(row('tools_invoked', invoked, REQUIRED_TOOLS, false, invoked === undefined ? 'summary.tools_invoked is missing from the session API' : `summary.tools_invoked is not a list of tool names: ${show(invoked)}`))
  } else {
    const absent = REQUIRED_TOOLS.filter((tool) => !invokedNames.includes(tool))
    rows.push(
      row(
        'tools_invoked',
        invokedNames,
        REQUIRED_TOOLS,
        absent.length === 0,
        absent.length === 0 ? 'tools_invoked contains skill and read' : `summary.tools_invoked lacks ${absent.join(', ')}`,
      ),
    )
  }
  return rows
}

function contextRows(kyberSession) {
  const rows = []
  const measurable = dig(kyberSession, 'context', 'measurable')
  rows.push(
    row(
      'context.measurable',
      measurable,
      true,
      measurable === true,
      measurable === true
        ? 'context.measurable is true'
        : `context.measurable must be true; got ${show(measurable)} (reason ${show(dig(kyberSession, 'context', 'reason'))})`,
    ),
  )

  const systemPrompt = dig(kyberSession, 'context', 'first', 'buckets', 'system_prompt')
  if (!isCount(systemPrompt)) {
    rows.push(row('system_prompt', systemPrompt, '> 0', false, notANumber('context.first.buckets.system_prompt', systemPrompt)))
  } else if (!(systemPrompt > 0)) {
    rows.push(row('system_prompt', systemPrompt, '> 0', false, `context.first.buckets.system_prompt is ${systemPrompt}; the system prompt must measure more than zero`))
  } else {
    rows.push(row('system_prompt', systemPrompt, '> 0', true, 'system_prompt bucket is measured and positive'))
  }
  return rows
}

function promptRows(kyberSession, gate) {
  const requests = dig(kyberSession, 'requests')
  return gate.prompts.map((sent, index) => {
    const field = `requests[${index}].prompt`
    const expectedPrefix = sent.slice(0, PROMPT_PREVIEW_LIMIT)
    const shown = Array.isArray(requests) ? dig(requests[index], 'prompt') : undefined
    if (typeof shown !== 'string' || shown === '') {
      return row(field, shown, expectedPrefix, false, shown === undefined || shown === null ? `${field} is missing from the session API` : `${field} is not prompt text: ${show(shown)}`)
    }
    if (shown.length > PROMPT_PREVIEW_LIMIT) {
      return row(field, shown, expectedPrefix, false, `${field} is ${shown.length} characters; the preview is capped at ${PROMPT_PREVIEW_LIMIT}`)
    }
    if (!shown.startsWith(expectedPrefix)) {
      return row(field, shown, expectedPrefix, false, `${field} does not start with the first ${PROMPT_PREVIEW_LIMIT} characters of prompt ${index + 1}`)
    }
    return row(field, shown, expectedPrefix, true, `${field} starts with the prompt sent`)
  })
}

function skillRows(kyberSession, gate) {
  const rows = []
  const skillRows = dig(kyberSession, 'skills', 'rows')
  if (!Array.isArray(skillRows)) {
    const reason = `skills.rows is missing from the session API (skills: ${show(dig(kyberSession, 'skills'))})`
    rows.push(row('skills.listed', skillRows, gate.skill, false, reason))
    rows.push(row('skills.invoked', skillRows, gate.skill, false, reason))
    return rows
  }
  const entry = skillRows.find((candidate) => dig(candidate, 'name') === gate.skill)
  if (entry === undefined) {
    const reason = `skills.rows has no row for ${gate.skill}`
    rows.push(row('skills.listed', null, gate.skill, false, reason))
    rows.push(row('skills.invoked', null, gate.skill, false, reason))
    return rows
  }

  const listed = dig(entry, 'listed')
  rows.push(
    row('skills.listed', listed, true, listed === true, listed === true ? `${gate.skill} is listed` : `skills.rows[${gate.skill}].listed must be true; got ${show(listed)}`),
  )

  const invocations = dig(entry, 'invocations')
  if (!isCount(invocations)) {
    rows.push(row('skills.invoked', invocations, '>= 1', false, notANumber(`skills.rows[${gate.skill}].invocations`, invocations)))
  } else if (invocations < 1) {
    rows.push(row('skills.invoked', invocations, '>= 1', false, `${gate.skill} was invoked in the run but kyberDash counts ${invocations} invocations`))
  } else {
    rows.push(row('skills.invoked', invocations, '>= 1', true, `${gate.skill} has ${invocations} invocation(s)`))
  }
  return rows
}

function identityRows(kyberSession, opencode, gate) {
  const rows = []

  const models = dig(kyberSession, 'summary', 'models')
  const modelNames = nameList(models)
  if (modelNames === undefined) {
    rows.push(row('models', models, gate.expectedModel, false, models === undefined ? 'summary.models is missing from the session API' : `summary.models is not a list of model ids: ${show(models)}`))
  } else if (!modelNames.includes(gate.expectedModel)) {
    rows.push(row('models', modelNames, gate.expectedModel, false, `summary.models ${show(modelNames)} lacks the expected model ${gate.expectedModel}`))
  } else if (!opencode.models.includes(gate.expectedModel)) {
    rows.push(row('models', modelNames, opencode.models, false, `OpenCode's assistant messages ${show(opencode.models)} lack the expected model ${gate.expectedModel}`))
  } else {
    rows.push(row('models', modelNames, opencode.models, true, `both sides report ${gate.expectedModel}`))
  }

  const label = dig(kyberSession, 'label')
  if (typeof label !== 'string') {
    rows.push(row('label', label, gate.expectedTitle, false, label === undefined ? 'label is missing from the session API' : `label is not text: ${show(label)}`))
  } else if (label !== gate.expectedTitle) {
    rows.push(row('label', label, gate.expectedTitle, false, `label is ${show(label)}, the session was titled ${show(gate.expectedTitle)}`))
  } else {
    rows.push(row('label', label, gate.expectedTitle, true, 'label equals the OpenCode session title'))
  }
  return rows
}

/**
 * Compares kyberDash's session API against OpenCode's own record of the same session.
 *
 * @param {{ kyberSession: unknown, opencode: unknown, gate: unknown }} input
 * @returns {{ pass: boolean, rows: CompareRow[] }}
 */
export function compareSession({ kyberSession, opencode, gate }) {
  const problems = [...readingProblems(opencode), ...gateProblems(gate)]
  if (problems.length > 0) {
    // A malformed reading or gate cannot be compared; say so as one failing line.
    return {
      pass: false,
      rows: [row('inputs', null, null, false, `comparator inputs are malformed: ${problems.join('; ')}`)],
    }
  }

  const rows = [
    ...tokenRows(kyberSession, opencode),
    durationRow(kyberSession, gate.wallClockMs),
    ...countRows(kyberSession, opencode, gate),
    ...requestTurnRows(kyberSession, opencode),
    ...toolRows(kyberSession, opencode),
    ...contextRows(kyberSession),
    ...promptRows(kyberSession, gate),
    ...skillRows(kyberSession, gate),
    ...identityRows(kyberSession, opencode, gate),
  ]
  return { pass: rows.every((candidate) => candidate.pass), rows }
}
