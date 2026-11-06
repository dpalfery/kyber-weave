import { describe, expect, it } from 'vitest'
import { analyticsToolNames } from './tools.js'
import { normalizeToolName } from '../providers/session-message.js'
import { copilot } from '../providers/copilot.js'

// The MCP server key this KyberDash server is registered under. Tool names are
// qualified by clients as mcp__<server key>__<tool name>.
const SERVER_KEY = 'kw-dash'

// Old tool name -> new tool name. The rename keeps each tool's `operation` value,
// its /api/kyber/analytics/* route and its argument mapping unchanged.
const renamedTools = {
  dash_status: 'dash_list_available_analytics_operations',
  dash_catalog: 'dash_list_queryable_metrics_dimensions_limits',
  dash_query: 'dash_query_session_cost_tokens_and_duration',
  dash_search_sessions: 'dash_search_sessions_by_text_and_scope',
  dash_inspect: 'dash_read_captured_run_session_or_turn_context',
  dash_findings: 'dash_list_detected_context_and_tool_problems',
  dash_compare: 'dash_compare_metrics_between_two_scopes',
  dash_outcomes: 'dash_get_run_success_or_failure_outcomes',
} as const

// Name limits. Sources:
// - The MCP specification and the Anthropic Messages API allow tool names of up to 128 characters.
// - Cursor's 60-character limit on server key plus tool name comes from staff forum replies, not official docs.
// - The 64-character figure comes from third-party docs and older Claude Code reports.
// These guards are deliberately conservative; they are not the published maximums.
const TOOL_NAME_PATTERN = /^[a-zA-Z0-9_-]{1,64}$/
const MAX_TOOL_NAME_LENGTH = 64
const MAX_QUALIFIED_NAME_LENGTH = 64
const MAX_SERVER_PLUS_TOOL_LENGTH = 60

describe('kw-dash analytics tool names', () => {
  it('pins the eight analytics tools to their renamed kw-dash names in registration order', () => {
    expect([...analyticsToolNames]).toEqual(Object.values(renamedTools))
  })

  it('keeps every kw-dash tool name inside the allowed character pattern with no double underscore', () => {
    for (const name of analyticsToolNames) {
      expect(name, `${name} must match ${TOOL_NAME_PATTERN}`).toMatch(TOOL_NAME_PATTERN)
      expect(name.includes('__'), `${name} must not contain a double underscore`).toBe(false)
    }
  })

  it('keeps the qualified mcp__kw-dash__<tool> name within 64 characters', () => {
    for (const name of analyticsToolNames) {
      const qualified = `mcp__${SERVER_KEY}__${name}`
      expect(qualified.length, `${qualified} is ${qualified.length} characters`).toBeLessThanOrEqual(MAX_QUALIFIED_NAME_LENGTH)
    }
  })

  it('keeps the kw-dash server key plus each tool name within 60 characters', () => {
    for (const name of analyticsToolNames) {
      const combined = SERVER_KEY.length + name.length
      expect(combined, `${SERVER_KEY} + ${name} is ${combined} characters`).toBeLessThanOrEqual(MAX_SERVER_PLUS_TOOL_LENGTH)
    }
  })

  it('keeps every kw-dash tool name within the 64-character tool name limit', () => {
    for (const name of analyticsToolNames) {
      expect(name.length, `${name} is ${name.length} characters`).toBeLessThanOrEqual(MAX_TOOL_NAME_LENGTH)
    }
  })
})

// CHARACTERIZATION TESTS: exempt from RED. These pin how the transcript and Copilot
// readers already split an MCP tool name whose server key contains a hyphen. They
// are green before and after the rename and are not counted as RED evidence.
describe('MCP tool name splitting for hyphenated server keys (characterization)', () => {
  it('splits a transcript tool name at the first underscore into the kw-docs server', () => {
    expect(normalizeToolName('kw-docs_docs_answer_question_from_repo_documentation')).toBe(
      'mcp__kw-docs__docs_answer_question_from_repo_documentation',
    )
  })

  it('turns the hyphens of a Copilot server key into underscores before the tool name', () => {
    expect(copilot.toolDisplayName('kw-docs-docs_answer_question_from_repo_documentation')).toBe(
      'mcp__kw_docs__docs_answer_question_from_repo_documentation',
    )
  })
})
