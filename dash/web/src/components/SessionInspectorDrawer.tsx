import * as React from 'react'
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { formatDuration } from '../lib/labels.js'
import { cn, usd, fmtTokens } from '../lib/utils.js'
import { fetchKyberSessionContent, fetchSpanAttributes, fetchTurnContentFull } from '../lib/kyberApi.js'
import type {
  KyberMessageLike,
  KyberTurnContentBlock,
  KyberTurnContentPart,
  KyberTurnContentResult,
  KyberTurnContentShadow,
} from '../lib/kyberApi.js'
import type { DrawerContent, SessionTimelineNode } from './AgentSessionDashboard.js'
import { Skeleton } from './ui/skeleton.js'

/**
 * Harness XML tags folded into collapsible <details> elements.
 */
export const XML_FOLD_TAGS = [
  'environment_info',
  'workspace_info',
  'instructions',
  'copilot_instructions',
  'context',
  'reminderInstructions',
  'editor_context',
  'tool_use_instructions',
  'notebook_info',
  'system_reminder',
  'file_contents',
  'attachment',
  'environment_details',
] as const

export type XmlFoldTag = (typeof XML_FOLD_TAGS)[number]

export interface XmlFoldChunk {
  type: 'text' | 'tag'
  tag?: string
  attributes?: string
  content: string
}

/**
 * HTML-active tags are never treated as structural readable sections. They
 * stay as escaped literal text so a payload like `<script>alert(1)</script>`
 * keeps its delimiters visibly escaped and never becomes an executable
 * element (React still escapes; no `dangerouslySetInnerHTML` anywhere).
 */
const READABLE_XML_LITERAL_TAGS = new Set(['script', 'style', 'iframe', 'object', 'embed', 'link', 'meta'])

const READABLE_XML_TAG_NAME = '[A-Za-z_][\\w:.-]*'
const READABLE_XML_ATTR_VALUE = '(?:"[^"]*"|\'[^\']*\'|[^\\s<>\"\'=]+)'
const READABLE_XML_ATTR = `(?:\\s+[^\\s<>/=]+(?:\\s*=\\s*${READABLE_XML_ATTR_VALUE})?)`
const READABLE_XML_TOKEN_RE = new RegExp(
  `<\\/\\s*(${READABLE_XML_TAG_NAME})\\s*>` +
    `|<(${READABLE_XML_TAG_NAME})((?:${READABLE_XML_ATTR})*)\\s*(\\/?)>`,
  'g',
)

interface ReadableXmlToken {
  index: number
  end: number
  raw: string
  kind: 'open' | 'close' | 'selfClosing'
  tag: string
  attributes?: string
}

function tokenizeReadableXml(raw: string): ReadableXmlToken[] {
  const tokens: ReadableXmlToken[] = []
  READABLE_XML_TOKEN_RE.lastIndex = 0
  let match: RegExpExecArray | null
  while ((match = READABLE_XML_TOKEN_RE.exec(raw)) !== null) {
    if (match[1] !== undefined) {
      tokens.push({
        index: match.index,
        end: match.index + match[0].length,
        raw: match[0],
        kind: 'close',
        tag: match[1],
      })
    } else {
      const tag = match[2] as string
      const attributes = (match[3] ?? '').trim() || undefined
      const selfClosing = match[4] === '/'
      tokens.push({
        index: match.index,
        end: match.index + match[0].length,
        raw: match[0],
        kind: selfClosing ? 'selfClosing' : 'open',
        tag,
        attributes,
      })
    }
  }
  return tokens
}

/**
 * Parses any well-formed XML-like sections into ordered text/tag chunks.
 * Only `<name ...>...</name>` and `<name .../>` with a valid tag name count
 * as structural; stray `<`, `>`, `<>`, `< 3`, or `a < b` stay in text chunks
 * so literal angle brackets are never silently removed. Denylisted
 * HTML-active tags stay literal too. Every occurrence is preserved in source
 * order with its exact inner bytes; nothing is deduplicated or reordered.
 */
export function parseReadableXmlChunks(raw: string): XmlFoldChunk[] {
  if (!raw) return []
  const tokens = tokenizeReadableXml(raw)
  const chunks: XmlFoldChunk[] = []
  let pos = 0
  const openStack: ReadableXmlToken[] = []

  const pushText = (slice: string): void => {
    if (slice) chunks.push({ type: 'text', content: slice })
  }

  let i = 0
  while (i < tokens.length) {
    const token = tokens[i] as ReadableXmlToken
    if (READABLE_XML_LITERAL_TAGS.has(token.tag.toLowerCase())) {
      i++
      continue
    }
    if (token.kind === 'selfClosing') {
      if (openStack.length === 0) {
        pushText(raw.slice(pos, token.index))
        chunks.push({ type: 'tag', tag: token.tag, attributes: token.attributes, content: '' })
        pos = token.end
      }
      i++
      continue
    }
    if (token.kind === 'open') {
      openStack.push(token)
      i++
      continue
    }
    const top = openStack[openStack.length - 1]
    if (top && top.tag.toLowerCase() === token.tag.toLowerCase()) {
      openStack.pop()
      if (openStack.length === 0) {
        pushText(raw.slice(pos, top.index))
        chunks.push({
          type: 'tag',
          tag: top.tag,
          attributes: top.attributes,
          content: raw.slice(top.end, token.index),
        })
        pos = token.end
      }
    }
    i++
  }

  if (openStack.length > 0) {
    const first = openStack[0] as ReadableXmlToken
    pushText(raw.slice(pos, first.index))
    chunks.push({
      type: 'tag',
      tag: first.tag,
      attributes: first.attributes,
      content: raw.slice(first.end),
    })
    return chunks
  }

  pushText(raw.slice(pos))
  return chunks
}

/**
 * Preview text for a folded section with structural delimiters removed but
 * body words kept. Structural tag tokens become a space; anything else
 * (including literal `<`/`>` that never parsed as a tag) stays verbatim.
 */
function readablePreviewText(inner: string, maxLength = 80): { preview: string; hasMore: boolean } {
  READABLE_XML_TOKEN_RE.lastIndex = 0
  const stripped = inner.replace(READABLE_XML_TOKEN_RE, (match, closeName, openName) => {
    const name = (closeName ?? openName ?? '') as string
    if (name && READABLE_XML_LITERAL_TAGS.has(String(name).toLowerCase())) return match
    return ' '
  })
  const collapsed = stripped.replace(/\s+/g, ' ').trim()
  return { preview: collapsed.slice(0, maxLength), hasMore: collapsed.length > maxLength }
}
/**
 * Parses raw text into plain text chunks and recognized harness XML blocks.
 * Kept for the allowlist unit tests; the formatted view below uses the
 * generic readable parser so representative tags outside the harness list
 * (for example `<system>`) render as readable sections too.
 */
export function parseXmlFoldChunks(raw: string): XmlFoldChunk[] {
  if (!raw) return []
  const tagsPattern = XML_FOLD_TAGS.join('|')
  const regex = new RegExp(`<(${tagsPattern})(\\s[^>]*)?>([\\s\\S]*?)(?:<\\/\\1>|$)`, 'gi')
  const chunks: XmlFoldChunk[] = []
  let lastIndex = 0
  let match: RegExpExecArray | null

  while ((match = regex.exec(raw)) !== null) {
    if (match.index > lastIndex) {
      const before = raw.slice(lastIndex, match.index)
      if (before) {
        chunks.push({ type: 'text', content: before })
      }
    }
    const tag = match[1]
    const attributes = match[2]?.trim()
    const inner = match[3] ?? ''
    chunks.push({
      type: 'tag',
      tag,
      attributes,
      content: inner,
    })
    lastIndex = match.index + match[0].length
  }

  if (lastIndex < raw.length) {
    const after = raw.slice(lastIndex)
    if (after) {
      chunks.push({ type: 'text', content: after })
    }
  }

  return chunks
}

/**
 * True when the formatted readable view would fold at least one structural
 * XML section. Denylisted HTML-active tags and stray angle brackets never
 * count, so literals route to plain text instead of folding.
 */
export function containsReadableXmlTag(value: string): boolean {
  if (!value) return false
  return parseReadableXmlChunks(value).some((chunk) => chunk.type === 'tag')
}

/**
 * Human-readable formatted view for API context text. Structural XML-like
 * sections become collapsible readable labels with no angle-bracket
 * delimiters; every body character and occurrence stays in source order via
 * recursive rendering. Literal `<`/`>` that never parsed as a tag stays as
 * escaped literal text. Raw and copy paths elsewhere keep the exact source.
 */
export function XmlFoldedText({ text, className }: { text: string; className?: string }) {
  if (!text) return null
  const chunks = parseReadableXmlChunks(text)

  if (chunks.length === 0) return null

  if (chunks.length === 1 && chunks[0].type === 'text') {
    return <div className={cn('whitespace-pre-wrap break-words leading-relaxed text-xs text-foreground/90', className)}>{chunks[0].content}</div>
  }

  return (
    <div className={cn('space-y-1 text-xs', className)} data-testid="xml-folded-content">
      {chunks.map((chunk, i) => {
        if (chunk.type === 'text') {
          const trimmed = chunk.content.trim()
          if (!trimmed) return null
          return (
            <div key={i} className="whitespace-pre-wrap break-words leading-relaxed text-foreground/90 my-1">
              {chunk.content}
            </div>
          )
        }

        const inner = chunk.content ?? ''
        if (inner.trim() === '') {
          const isWhitespaceOnly = inner.length > 0
          return (
            <div
              key={i}
              className="border border-border rounded my-1 bg-card/60 px-3 py-1.5 flex items-center gap-2"
              data-testid={`folded-tag-${chunk.tag}`}
              aria-label={
                isWhitespaceOnly
                  ? `${chunk.tag} section contains whitespace-only content`
                  : `${chunk.tag} section is empty`
              }
            >
              <span className="shrink-0 rounded bg-amber-500/15 border border-amber-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-amber-600 dark:text-amber-400">
                {chunk.tag}
              </span>
              {chunk.attributes ? (
                <span className="truncate font-mono text-[11px] text-tertiary-foreground flex-1">
                  {chunk.attributes}
                </span>
              ) : null}
              <span
                className="shrink-0 font-mono text-[11px] italic text-tertiary-foreground"
                data-testid={`folded-tag-${chunk.tag}-marker`}
              >
                {isWhitespaceOnly ? 'whitespace-only' : 'empty'}
              </span>
              <span
                className="whitespace-pre-wrap break-words font-mono text-[11px] text-tertiary-foreground"
                data-testid={`folded-tag-${chunk.tag}-whitespace`}
              >
                {inner}
              </span>
            </div>
          )
        }

        const { preview, hasMore } = readablePreviewText(inner)

        return (
          <details
            key={i}
            className="group border border-border rounded my-1 bg-card/60 overflow-hidden"
            data-testid={`folded-tag-${chunk.tag}`}
          >
            <summary className="cursor-pointer px-3 py-1.5 text-xs text-muted-foreground hover:text-foreground flex items-center gap-2 select-none hover:bg-interactive-secondary/50 transition-colors">
              <span className="shrink-0 rounded bg-amber-500/15 border border-amber-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-amber-600 dark:text-amber-400">
                {chunk.tag}
              </span>
              {chunk.attributes ? (
                <span className="truncate font-mono text-[10px] text-tertiary-foreground max-w-40">
                  {chunk.attributes}
                </span>
              ) : null}
              <span className="truncate font-mono text-[11px] text-tertiary-foreground flex-1">
                {preview}{hasMore ? '…' : ''}
              </span>
              <span className="text-[10px] text-tertiary-foreground font-mono transition-transform group-open:rotate-90">
                ▶
              </span>
            </summary>
            <div className="border-t border-border bg-muted/40 p-2">
              <XmlFoldedText text={inner} />
            </div>
          </details>
        )
      })}
    </div>
  )
}

function tryParseJson(value: unknown): unknown {
  if (value === null || value === undefined) return null
  if (typeof value !== 'string') return value
  const trimmed = value.trim()
  if ((trimmed.startsWith('{') && trimmed.endsWith('}')) || (trimmed.startsWith('[') && trimmed.endsWith(']'))) {
    try {
      return JSON.parse(trimmed)
    } catch {
      return value
    }
  }
  return value
}

function formatCredits(c?: number | null): string {
  if (c == null || !isFinite(c)) return '—'
  return c >= 100 ? c.toFixed(0) : c.toFixed(2)
}

/**
 * Key-Value List component for parameters and attributes.
 */
export function KeyValueList({ data, className }: { data: unknown; className?: string }) {
  if (data === null || data === undefined) {
    return <p className="text-xs text-tertiary-foreground italic">empty</p>
  }

  const parsed = tryParseJson(data)

  if (typeof parsed === 'string') {
    if (containsReadableXmlTag(parsed)) {
      return <XmlFoldedText text={parsed} className={className} />
    }
    if (parsed.length > 120) {
      return (
        <details className="group border border-border rounded my-1 bg-card/60">
          <summary className="cursor-pointer px-2.5 py-1 text-xs text-tertiary-foreground hover:text-foreground flex items-center gap-2">
            <span className="truncate font-mono">{parsed.slice(0, 80).replace(/\s+/g, ' ')}…</span>
          </summary>
          <pre className="text-xs font-mono p-2 overflow-x-auto whitespace-pre-wrap border-t border-border bg-muted/40 text-foreground">
            {parsed}
          </pre>
        </details>
      )
    }
    return <p className="text-xs font-mono text-foreground break-words">{parsed}</p>
  }

  if (Array.isArray(parsed)) {
    if (parsed.length === 0) return <p className="text-xs text-tertiary-foreground italic">empty list</p>
    return (
      <div className={cn('space-y-1.5', className)}>
        {parsed.map((item, idx) => (
          <div key={idx} className="border-b border-border/50 pb-1.5 last:border-b-0 last:pb-0">
            <KeyValueList data={item} />
          </div>
        ))}
      </div>
    )
  }

  if (typeof parsed !== 'object') {
    return <span className="text-xs font-mono text-foreground">{String(parsed)}</span>
  }

  const entries = Object.entries(parsed as Record<string, unknown>)
  if (entries.length === 0) {
    return <p className="text-xs text-tertiary-foreground italic">empty object</p>
  }

  return (
    <div className={cn('divide-y divide-border/60 text-xs font-mono', className)} data-testid="key-value-list">
      {entries.map(([key, val]) => (
        <div key={key} className="flex flex-col sm:flex-row sm:items-start gap-1 sm:gap-3 py-1.5">
          <span className="shrink-0 font-medium text-muted-foreground w-28 sm:w-36 break-all">
            {key}:
          </span>
          <div className="min-w-0 flex-1 text-foreground">
            <KeyValueList data={val} />
          </div>
        </div>
      ))}
    </div>
  )
}

/**
 * Canonical turn-content buckets every harness shares (see
 * `CANONICAL_CONTENT_KEYS` in dash/src/canon/types.ts), in stable display
 * order. Copilot-shaped rows carry these flat strings on `turn.content`
 * rather than in input/output messages, which is why the system prompt and
 * its associated data previously stayed invisible in the turn view or
 * surfaced elsewhere as one raw JSON wall.
 */
export const TURN_CONTENT_SECTION_ORDER = [
  'system_prompt',
  'instruction_context',
  'tool_definitions',
  'conversation_history',
  'tool_result_content',
] as const

const TURN_CONTENT_SECTION_LABELS: Record<string, string> = {
  system_prompt: 'System prompt',
  instruction_context: 'Instruction context',
  tool_definitions: 'Tool definitions',
  conversation_history: 'Conversation history',
  tool_result_content: 'Tool results',
}

const KNOWN_CONTENT_KEYS = new Set<string>(TURN_CONTENT_SECTION_ORDER)

/** Message-shaped content keys TurnInspector already renders elsewhere. */
const MESSAGE_CONTENT_KEYS = new Set([
  'input_messages',
  'output_messages',
  'prompt_text',
  'response_text',
  'reasoning_text',
  'thinking_text',
])

function humanizeContentKey(key: string): string {
  const words = key.replace(/[-_]+/g, ' ').trim()
  if (!words) return key
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/**
 * True for strings shaped like a JSON object or array. Only the cheap shape
 * check — the parse gate in the formatters below decides validity.
 */
export function isJsonText(value: string): boolean {
  const trimmed = value.trim()
  if (trimmed.length < 2) return false
  const first = trimmed.charAt(0)
  const last = trimmed.charAt(trimmed.length - 1)
  return (first === '{' && last === '}') || (first === '[' && last === ']')
}

/**
 * True for a JSON string literal whose decoded value is itself a JSON
 * object or array (a root-encoded string such as
 * `"[{\"type\":\"text\",\"text\":\"prompt\"}]"`). The outer quotes make
 * `isJsonText` false, so without this check a root-encoded payload renders
 * as one escaped line instead of reaching the structured view. Parsing here
 * is only the shape gate; the original outer text is never re-encoded and
 * stays the exact source for raw/copy.
 */
export function isRootEncodedJsonText(value: string): boolean {
  const trimmed = value.trim()
  if (trimmed.length < 2 || !trimmed.startsWith('"') || !trimmed.endsWith('"')) return false
  let decoded: unknown
  try {
    decoded = JSON.parse(trimmed)
  } catch {
    return false
  }
  if (typeof decoded !== 'string') return false
  const inner = decoded.trim()
  if (!isJsonText(inner)) return false
  try {
    JSON.parse(inner)
    return true
  } catch {
    return false
  }
}

/**
 * Lossless JSON AST scoped to the nested-string expansion below. Numbers and
 * literals keep their exact source slices so large integers, key order, and
 * array order survive; strings keep both the raw literal and its decoded
 * value so JSON-encoded strings can be detected without re-escaping.
 */
interface JsonLosslessObjectNode {
  kind: 'object'
  entries: Array<{ keyRaw: string; value: JsonLosslessNode }>
}

interface JsonLosslessArrayNode {
  kind: 'array'
  items: JsonLosslessNode[]
}

interface JsonLosslessStringNode {
  kind: 'string'
  raw: string
  decoded: string
}

interface JsonLosslessNumberNode {
  kind: 'number'
  raw: string
}

interface JsonLosslessLiteralNode {
  kind: 'literal'
  raw: string
}

type JsonLosslessNode =
  | JsonLosslessObjectNode
  | JsonLosslessArrayNode
  | JsonLosslessStringNode
  | JsonLosslessNumberNode
  | JsonLosslessLiteralNode

/**
 * Structural recursion cap for the lossless JSON AST below. Parsing and
 * printing recurse once per object/array level, so unbounded OTLP content
 * could otherwise exhaust the stack; past this depth the nested printer
 * throws and `prettyPrintJsonNested` falls back to the iterative
 * whitespace-only formatter, which keeps every byte visible.
 */
const MAX_JSON_LOSSLESS_STRUCTURE_DEPTH = 100

/**
 * Cap on JSON-encoded string layers unwrapped along one root-to-leaf path
 * (a string whose decoded text is itself a JSON object/array, repeatedly).
 * The budget advances only when a string layer is actually unwrapped, so
 * wide values keep a full budget per branch while a hostile nesting chain
 * stops expanding and stays visible as an escaped string literal.
 */
const MAX_NESTED_JSON_STRING_EXPANSIONS = 10

function parseJsonLosslessNode(text: string, pos: { index: number }, depth: number): JsonLosslessNode {
  if (depth > MAX_JSON_LOSSLESS_STRUCTURE_DEPTH) throw new Error('Max JSON depth exceeded')
  skipJsonWs(text, pos)
  const ch = text.charAt(pos.index)
  if (ch === '{') return parseJsonLosslessObject(text, pos, depth)
  if (ch === '[') return parseJsonLosslessArray(text, pos, depth)
  if (ch === '"') return parseJsonLosslessString(text, pos)
  if (ch === '-' || (ch >= '0' && ch <= '9')) return parseJsonLosslessNumber(text, pos)
  return parseJsonLosslessLiteral(text, pos)
}

function skipJsonWs(text: string, pos: { index: number }): void {
  while (pos.index < text.length && /\s/.test(text.charAt(pos.index))) pos.index++
}

function parseJsonLosslessObject(text: string, pos: { index: number }, depth: number): JsonLosslessObjectNode {
  pos.index++
  const entries: Array<{ keyRaw: string; value: JsonLosslessNode }> = []
  skipJsonWs(text, pos)
  if (text.charAt(pos.index) === '}') {
    pos.index++
    return { kind: 'object', entries }
  }
  for (;;) {
    skipJsonWs(text, pos)
    if (text.charAt(pos.index) !== '"') throw new Error('Expected object key')
    const keyNode = parseJsonLosslessString(text, pos)
    skipJsonWs(text, pos)
    if (text.charAt(pos.index) !== ':') throw new Error('Expected colon')
    pos.index++
    const value = parseJsonLosslessNode(text, pos, depth + 1)
    entries.push({ keyRaw: keyNode.raw, value })
    skipJsonWs(text, pos)
    const sep = text.charAt(pos.index)
    if (sep === ',') {
      pos.index++
      continue
    }
    if (sep === '}') {
      pos.index++
      return { kind: 'object', entries }
    }
    throw new Error('Expected comma or closing brace')
  }
}

function parseJsonLosslessArray(text: string, pos: { index: number }, depth: number): JsonLosslessArrayNode {
  pos.index++
  const items: JsonLosslessNode[] = []
  skipJsonWs(text, pos)
  if (text.charAt(pos.index) === ']') {
    pos.index++
    return { kind: 'array', items }
  }
  for (;;) {
    const value = parseJsonLosslessNode(text, pos, depth + 1)
    items.push(value)
    skipJsonWs(text, pos)
    const sep = text.charAt(pos.index)
    if (sep === ',') {
      pos.index++
      continue
    }
    if (sep === ']') {
      pos.index++
      return { kind: 'array', items }
    }
    throw new Error('Expected comma or closing bracket')
  }
}

function parseJsonLosslessString(text: string, pos: { index: number }): JsonLosslessStringNode {
  const start = pos.index
  pos.index++
  let escaped = false
  while (pos.index < text.length) {
    const ch = text.charAt(pos.index)
    pos.index++
    if (escaped) {
      escaped = false
      continue
    }
    if (ch === '\\') {
      escaped = true
      continue
    }
    if (ch === '"') {
      const raw = text.slice(start, pos.index)
      return { kind: 'string', raw, decoded: JSON.parse(raw) as string }
    }
  }
  throw new Error('Unterminated string')
}

function parseJsonLosslessNumber(text: string, pos: { index: number }): JsonLosslessNumberNode {
  const rest = text.slice(pos.index)
  const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(rest)
  if (!match) throw new Error('Invalid number')
  pos.index += match[0].length
  return { kind: 'number', raw: match[0] }
}

function parseJsonLosslessLiteral(text: string, pos: { index: number }): JsonLosslessLiteralNode {
  const rest = text.slice(pos.index)
  const match = /^(?:true|false|null)/.exec(rest)
  if (!match) throw new Error('Invalid literal')
  pos.index += match[0].length
  return { kind: 'literal', raw: match[0] }
}

function parseJsonLosslessDocument(trimmed: string): JsonLosslessNode | null {
  const pos = { index: 0 }
  const node = parseJsonLosslessNode(trimmed, pos, 0)
  skipJsonWs(trimmed, pos)
  if (pos.index !== trimmed.length) return null
  return node
}

function printJsonLosslessNode(node: JsonLosslessNode, depth: number, seen = 0): string {
  if (depth > MAX_JSON_LOSSLESS_STRUCTURE_DEPTH) throw new Error('Max JSON depth exceeded')
  const pad = '  '.repeat(depth)
  const childPad = '  '.repeat(depth + 1)
  if (node.kind === 'object') {
    if (node.entries.length === 0) return '{}'
    const parts = node.entries.map((entry) => {
      const expanded = expandJsonLosslessStrings(entry.value, seen)
      const childSeen = expanded !== entry.value ? seen + 1 : seen
      return `${childPad}${entry.keyRaw}: ${printJsonLosslessNode(expanded, depth + 1, childSeen)}`
    })
    return `{\n${parts.join(',\n')}\n${pad}}`
  }
  if (node.kind === 'array') {
    if (node.items.length === 0) return '[]'
    const parts = node.items.map((item) => {
      const expanded = expandJsonLosslessStrings(item, seen)
      const childSeen = expanded !== item ? seen + 1 : seen
      return `${childPad}${printJsonLosslessNode(expanded, depth + 1, childSeen)}`
    })
    return `[\n${parts.join(',\n')}\n${pad}]`
  }
  if (node.kind === 'string' || node.kind === 'number' || node.kind === 'literal') {
    return node.raw
  }
  return ''
}

/**
 * Resolves one string node whose decoded text is itself a JSON object or
 * array. Non-string nodes pass through untouched; strings that are not JSON
 * objects/arrays keep their exact raw literal. Callers thread `seen` along
 * each root-to-leaf path and step it only when a layer is actually
 * unwrapped, so the cap bounds hostile nesting while the unexpanded tail
 * stays visible as an escaped string literal.
 */
function expandJsonLosslessStrings(node: JsonLosslessNode, seen: number): JsonLosslessNode {
  if (node.kind !== 'string' || seen >= MAX_NESTED_JSON_STRING_EXPANSIONS) return node
  const inner = node.decoded.trim()
  if (!isJsonText(inner)) return node
  try {
    JSON.parse(inner)
  } catch {
    return node
  }
  const parsed = (() => {
    try {
      return parseJsonLosslessDocument(inner)
    } catch {
      return null
    }
  })()
  if (!parsed || parsed.kind === 'string' || parsed.kind === 'number' || parsed.kind === 'literal') return node
  return parsed
}

/**
 * Pretty-prints valid JSON while recursing into JSON-encoded string values
 * (an object/array serialized as a string inside another JSON value). Outer
 * and inner numbers, literals, key order, and array order are copied from
 * exact source slices — nothing is parsed into JS numbers for display — so
 * large integers survive; only whitespace/indentation changes and nested
 * strings render as their decoded structure. Returns null when the text is
 * not valid JSON; callers fall back to plain text rendering.
 */
export function prettyPrintJsonNested(raw: string): string | null {
  const trimmed = raw.trim()
  if (trimmed === '') return null
  try {
    JSON.parse(trimmed)
  } catch {
    return null
  }
  try {
    const node = parseJsonLosslessDocument(trimmed)
    if (!node) return prettyPrintJson(raw)
    // A root-encoded JSON string (a string literal whose decoded value is a
    // JSON object/array) expands one layer so the formatted view is readable
    // instead of a single escaped line. The expansion consumes one layer of
    // the string-expansion budget, and the original outer text is untouched:
    // raw/copy paths keep using the caller's exact input, never this output.
    if (node.kind === 'string') {
      const expanded = expandJsonLosslessStrings(node, 0)
      if (expanded !== node) return printJsonLosslessNode(expanded, 0, 1)
    }
    return printJsonLosslessNode(node, 0)
  } catch {
    return prettyPrintJson(raw)
  }
}

/**
 * Pretty-prints raw JSON text with a whitespace-only transformation: the
 * scan copies every string, number, and literal byte-for-byte and only adds
 * newlines and indentation outside strings. Values are never parsed for
 * display, so large-number precision, key order, and array order survive
 * exactly as the harness sent them — `JSON.parse` here is only the validity
 * gate and its result is discarded.
 *
 * Returns null when the text is not valid JSON; callers fall back to plain
 * text rendering, which still shows everything.
 */
export function prettyPrintJson(raw: string): string | null {
  try {
    JSON.parse(raw)
  } catch {
    return null
  }
  const trimmed = raw.trim()
  if (trimmed === '') return null
  let out = ''
  let depth = 0
  let inString = false
  let escaped = false
  for (let i = 0; i < trimmed.length; i++) {
    const ch = trimmed.charAt(i)
    if (inString) {
      out += ch
      if (escaped) {
        escaped = false
      } else if (ch === '\\') {
        escaped = true
      } else if (ch === '"') {
        inString = false
      }
      continue
    }
    if (ch === '"') {
      inString = true
      out += ch
      continue
    }
    if (ch === '{' || ch === '[') {
      let j = i + 1
      while (j < trimmed.length && /\s/.test(trimmed.charAt(j))) j++
      const close = ch === '{' ? '}' : ']'
      if (trimmed.charAt(j) === close) {
        out += ch + close
        i = j
        continue
      }
      depth++
      out += ch + '\n' + '  '.repeat(depth)
      continue
    }
    if (ch === '}' || ch === ']') {
      depth--
      if (depth < 0) return null
      out += '\n' + '  '.repeat(depth) + ch
      continue
    }
    if (ch === ',') {
      out += ',\n' + '  '.repeat(depth)
      continue
    }
    if (ch === ':') {
      out += ': '
      continue
    }
    if (/\s/.test(ch)) continue
    out += ch
  }
  if (inString || depth !== 0) return null
  return out
}

/**
 * Pretty-prints newline-delimited JSON (one value per line, as a flattened
 * conversation history arrives) by formatting each line on its own.
 * Returns null unless at least two non-blank lines are present and every one
 * is valid JSON — a single JSON line is the prettyPrintJson case, and mixed
 * text stays plain text.
 */
export function prettyPrintJsonLines(raw: string): string | null {
  const lines = raw.split('\n').map((line) => line.trim()).filter((line) => line !== '')
  if (lines.length < 2) return null
  const formatted: string[] = []
  for (const line of lines) {
    if (!isJsonText(line)) return null
    const pretty = prettyPrintJson(line)
    if (pretty === null) return null
    formatted.push(pretty)
  }
  return formatted.join('\n')
}

/**
 * Nested-aware JSON-lines formatter: each line recurses into JSON-encoded
 * strings via prettyPrintJsonNested. Same shape contract as
 * prettyPrintJsonLines — null unless at least two non-blank lines are all
 * valid JSON.
 */
export function prettyPrintJsonLinesNested(raw: string): string | null {
  const lines = raw.split('\n').map((line) => line.trim()).filter((line) => line !== '')
  if (lines.length < 2) return null
  const formatted: string[] = []
  for (const line of lines) {
    if (!isJsonText(line)) return null
    const pretty = prettyPrintJsonNested(line) ?? prettyPrintJson(line)
    if (pretty === null) return null
    formatted.push(pretty)
  }
  return formatted.join('\n')
}

/**
 * Splits a top-level JSON array into its exact item source slices, in order.
 * Returns null when the text is not a top-level array, so callers can fall
 * back to the single pretty block. Slices are trimmed source text — no value
 * is parsed, so nothing is reordered or re-serialized.
 */
export function splitTopLevelArrayItems(raw: string): string[] | null {
  const trimmed = raw.trim()
  if (!trimmed.startsWith('[') || !trimmed.endsWith(']')) return null
  try {
    JSON.parse(trimmed)
  } catch {
    return null
  }
  const items: string[] = []
  let depth = 0
  let inString = false
  let escaped = false
  let itemStart = -1
  for (let i = 0; i < trimmed.length; i++) {
    const ch = trimmed.charAt(i)
    if (inString) {
      if (escaped) {
        escaped = false
      } else if (ch === '\\') {
        escaped = true
      } else if (ch === '"') {
        inString = false
      }
      continue
    }
    if (ch === '"') {
      if (depth === 1 && itemStart === -1) itemStart = i
      inString = true
      continue
    }
    if (ch === '[' || ch === '{') {
      if (depth === 1 && itemStart === -1) itemStart = i
      depth++
      continue
    }
    if (ch === ']' || ch === '}') {
      depth--
      if (depth < 0) return null
      if (depth === 0 && itemStart !== -1) {
        const slice = trimmed.slice(itemStart, i).trim()
        if (slice !== '') items.push(slice)
        itemStart = -1
      }
      continue
    }
    if (depth === 1) {
      if (ch === ',') {
        if (itemStart === -1) return null
        const slice = trimmed.slice(itemStart, i).trim()
        if (slice === '') return null
        items.push(slice)
        itemStart = -1
        continue
      }
      if (itemStart === -1 && !/\s/.test(ch)) itemStart = i
    }
  }
  if (inString || depth !== 0 || itemStart !== -1) return null
  return items
}

/**
 * Readable view for a string whose content is JSON (a tool-definitions blob,
 * a JSON-encoded system prompt, JSON-lines history). The formatted view only
 * re-indents the exact source text, so every literal — including integers
 * outside JavaScript's safe range — renders byte-for-byte; the raw view shows
 * the untouched original and the copy button copies it exactly. Nothing is
 * parsed into values for display, summarized, or truncated: long blocks
 * scroll instead of clipping.
 */
export function JsonStringView({
  text,
  label,
  defaultView = 'formatted',
  controls = 'full',
}: {
  text: string
  label?: string
  defaultView?: 'formatted' | 'raw'
  /**
   * `minimal` hides this view's own Formatted/Raw switch and Copy button
   * for embeddings that already provide one outer toggle and exact Copy
   * (whole-turn blocks). The formatted content and descriptor stay.
   */
  controls?: 'full' | 'minimal'
}) {
  const [viewMode, setViewMode] = useState<'formatted' | 'raw'>(defaultView)
  const [copied, setCopied] = useState(false)

  // Recurse into JSON-encoded string values so nested Copilot context stays
  // readable; the nested printer copies every literal byte-for-byte and only
  // changes indentation plus string expansion. Raw and copy below still use
  // the exact original `text`.
  const singleNested = prettyPrintJsonNested(text)
  const single = singleNested ?? prettyPrintJson(text)
  const lines = single === null ? prettyPrintJsonLinesNested(text) ?? prettyPrintJsonLines(text) : null
  if (single === null && lines === null) {
    // Shaped like JSON but not valid JSON — show the text as-is so nothing
    // is dropped; the structured view simply has nothing to add.
    return <XmlFoldedText text={text} />
  }

  const split = single !== null ? splitTopLevelArrayItems(text) : null
  const multiItems = split !== null && split.length > 1 ? split : null
  const multiPretty = multiItems !== null ? multiItems.map((item) => prettyPrintJsonNested(item) ?? prettyPrintJson(item)) : null
  const showItems = multiPretty !== null && multiPretty.every((part): part is string => part !== null)

  const nonBlankLines = text.split('\n').filter((line) => line.trim() !== '').length
  const descriptor = multiItems !== null
    ? `Array · ${multiItems.length} items`
    : lines !== null
      ? `JSON lines · ${nonBlankLines} lines`
      : text.trim().startsWith('{') ? 'Object' : 'Value'

  const copyExact = () => {
    try {
      void navigator.clipboard.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // ignore
    }
  }

  return (
    <div className="space-y-2" data-testid="json-string-view">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <div className="flex items-center gap-2 min-w-0">
          {label ? (
            <span className="text-xs font-semibold text-foreground">{label}</span>
          ) : null}
          <span className="rounded bg-muted border border-border px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
            {descriptor}
          </span>
        </div>
        {controls === 'full' ? (
          <div className="flex items-center gap-1.5">
            <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs">
              <button
                type="button"
                onClick={() => setViewMode('formatted')}
                className={cn(
                  'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
                  viewMode === 'formatted' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
                )}
              >
                Formatted
              </button>
              <button
                type="button"
                onClick={() => setViewMode('raw')}
                className={cn(
                  'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
                  viewMode === 'raw' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
                )}
              >
                Raw
              </button>
            </div>
            <button
              type="button"
              onClick={copyExact}
              className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:text-foreground hover:bg-interactive-secondary transition-colors"
            >
              {copied ? 'Copied!' : 'Copy'}
            </button>
          </div>
        ) : null}
      </div>
      {viewMode === 'formatted' ? (
        showItems && multiItems !== null && multiPretty !== null ? (
          <div className="space-y-2">
            {multiPretty.map((pretty, idx) => (
              <div key={idx} className="rounded border border-border bg-card/60 overflow-hidden">
                <div className="border-b border-border bg-interactive-secondary/60 px-3 py-1.5 font-mono text-[11px] text-muted-foreground">
                  Item {idx + 1} of {multiItems.length}
                </div>
                <pre
                  className="text-xs font-mono p-2.5 overflow-auto whitespace-pre text-foreground max-h-[400px]"
                  data-testid={`json-item-${idx}`}
                >
                  {pretty}
                </pre>
              </div>
            ))}
          </div>
        ) : (
          <pre
            className="text-xs font-mono p-2.5 overflow-auto whitespace-pre rounded border border-border bg-muted/40 text-foreground max-h-[400px]"
            data-testid="json-formatted"
          >
            {single ?? lines ?? text}
          </pre>
        )
      ) : (
        <pre
          className="text-xs font-mono p-2.5 overflow-auto whitespace-pre rounded border border-border bg-muted/40 text-foreground max-h-[400px]"
          data-testid="json-raw"
        >
          {text}
        </pre>
      )}
    </div>
  )
}

/**
 * Readable rendering for a content string of unknown shape: JSON text (or a
 * root-encoded JSON string) gets the structured view (with its exact-raw
 * toggle), everything else keeps the XML-folding text view. Either way the
 * complete text stays visible. An empty string is an explicit value, not
 * absent data, so it renders a visible marker instead of nothing.
 */
export function ReadableContentText({ text, label, suppressJsonControls }: { text: string; label?: string; suppressJsonControls?: boolean }) {
  if (text === '') {
    return (
      <p className="text-xs font-mono text-tertiary-foreground" data-testid="empty-string-value">
        &quot;&quot; (empty string)
      </p>
    )
  }
  if (!text) return null
  if (isJsonText(text) || isRootEncodedJsonText(text)) {
    return <JsonStringView text={text} label={label} controls={suppressJsonControls === true ? 'minimal' : 'full'} />
  }
  return <XmlFoldedText text={text} />
}

/**
 * Lossless plain-text view scoped to turn-content sections. Unlike the
 * generic `XmlFoldedText`, nothing is trimmed, folded into a preview, or
 * dropped: the exact string renders as literal text (React escapes XML
 * markup) with whitespace preserved, inside a scrollable container so long
 * content stays reachable without expanding anything.
 */
export function TurnLosslessText({ text }: { text: string }) {
  return (
    <pre
      className="text-xs font-mono p-2.5 overflow-auto whitespace-pre-wrap break-words rounded border border-border bg-muted/40 text-foreground max-h-[400px]"
      data-testid="turn-lossless-text"
    >
      {text}
    </pre>
  )
}

/**
 * Lossless recursive value renderer scoped to turn-content sections
 * (including nested objects, arrays, and tool-definition cards). The
 * generic `KeyValueList` collapses long strings behind a preview `<details>`
 * and routes XML-bearing text through the trimming `XmlFoldedText`; this
 * renderer instead shows every string in full and in order:
 *
 * - strings render byte-for-byte in a scrollable `<pre>` (whitespace and
 *   XML markup preserved as literal content, no trimming, no preview);
 * - JSON-shaped strings that parse keep the readable `JsonStringView`
 *   (formatted view is a whitespace-only re-indent so large integer
 *   literals, key order, and array order survive; raw/copy stay exact),
 *   while JSON-shaped-but-invalid text falls back to the lossless `<pre>`
 *   instead of the folding view so nothing is trimmed;
 * - object key order (`Object.entries` insertion order) and array order
 *   are never reordered;
 * - null, undefined, empty string, empty object/list, false, and zero each
 *   keep a visibly distinct rendering.
 */
export function TurnLosslessValue({ value, label }: { value: unknown; label?: string }) {
  if (value === null) {
    return (
      <span className="text-xs font-mono text-foreground" data-testid="explicit-null-value">
        null
      </span>
    )
  }
  if (value === undefined) {
    return (
      <span className="text-xs font-mono text-foreground" data-testid="explicit-undefined-value">
        undefined
      </span>
    )
  }
  if (typeof value === 'string') {
    if (value === '') {
      return (
        <p className="text-xs font-mono text-tertiary-foreground" data-testid="empty-string-value">
          &quot;&quot; (empty string)
        </p>
      )
    }
    if (isJsonText(value) || isRootEncodedJsonText(value)) {
      const single = prettyPrintJson(value)
      const lines = single === null ? prettyPrintJsonLines(value) : null
      if (single !== null || lines !== null) {
        return <JsonStringView text={value} label={label} />
      }
      // Shaped like JSON but invalid: stay lossless instead of routing
      // through the folding view, which would trim.
    }
    return <TurnLosslessText text={value} />
  }
  if (typeof value === 'number' || typeof value === 'boolean') {
    return (
      <span className="text-xs font-mono text-foreground" data-testid="turn-lossless-scalar">
        {String(value)}
      </span>
    )
  }
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return <p className="text-xs text-tertiary-foreground italic">empty list</p>
    }
    return (
      <div className="space-y-2" data-testid="turn-lossless-array">
        {value.map((item, idx) => (
          <div key={idx} className="rounded border border-border bg-card/60 overflow-hidden" data-testid={`turn-lossless-array-item-${idx}`}>
            <div className="border-b border-border bg-interactive-secondary/60 px-3 py-1.5 font-mono text-[11px] text-muted-foreground">
              Item {idx + 1} of {value.length}
            </div>
            <div className="p-2.5">
              <TurnLosslessValue value={item} />
            </div>
          </div>
        ))}
      </div>
    )
  }
  if (isRecord(value)) {
    const entries = Object.entries(value)
    if (entries.length === 0) {
      return <p className="text-xs text-tertiary-foreground italic">empty object</p>
    }
    return (
      <div className="divide-y divide-border/60 text-xs font-mono" data-testid="turn-lossless-object">
        {entries.map(([key, val]) => (
          <div key={key} className="flex flex-col sm:flex-row sm:items-start gap-1 sm:gap-3 py-1.5" data-testid={`turn-lossless-field-${key}`}>
            <span className="shrink-0 font-medium text-muted-foreground w-28 sm:w-36 break-all">
              {key}:
            </span>
            <div className="min-w-0 flex-1 text-foreground">
              <TurnLosslessValue value={val} />
            </div>
          </div>
        ))}
      </div>
    )
  }
  return (
    <span className="text-xs font-mono text-foreground" data-testid="turn-lossless-scalar">
      {String(value)}
    </span>
  )
}

/**
 * One labeled card per tool definition, in the order given. Names are read
 * from the already-parsed objects (plain strings, safe to read); every
 * field renders through the lossless turn-content value view so long
 * descriptions, parameters, and nested strings survive in full with no
 * preview collapse or whitespace trimming.
 */
function ToolDefinitionCards({ definitions }: { definitions: unknown[] }) {
  if (definitions.length === 0) {
    return <p className="text-xs text-tertiary-foreground italic">empty list</p>
  }
  return (
    <div className="space-y-2">
      {definitions.map((def, idx) => {
        const record = isRecord(def) ? def : null
        let name = `Tool ${idx + 1}`
        if (record) {
          if (typeof record.name === 'string' && record.name !== '') {
            name = record.name
          } else if (isRecord(record.function) && typeof record.function.name === 'string' && record.function.name !== '') {
            name = record.function.name
          }
        }
        return (
          <div key={idx} className="rounded border border-border bg-card/60 overflow-hidden" data-testid={`tool-definition-${idx}`}>
            <div className="border-b border-border bg-interactive-secondary/60 px-3 py-1.5 flex items-center gap-2 min-w-0">
              <span className="shrink-0 font-mono text-[11px] text-muted-foreground">
                Tool {idx + 1} of {definitions.length}
              </span>
              <span className="font-mono text-xs font-semibold text-foreground break-words whitespace-pre-wrap min-w-0 flex-1">{name}</span>
            </div>
            <div className="p-2.5">
              <TurnLosslessValue value={def} />
            </div>
          </div>
        )
      })}
    </div>
  )
}

/**
 * Labeled sections for a turn's content object. Copilot-shaped rows carry the
 * flat canonical buckets here — system prompt, instruction context, tool
 * definitions, conversation history, tool results — which the message blocks
 * above do not read, so without this they stay invisible (or surface
 * elsewhere as one raw JSON wall). Every own bucket renders losslessly in
 * full: JSON strings get the structured view (formatted re-indent plus an
 * exact raw/copy path), every other string renders byte-for-byte in a
 * scrollable block with whitespace and XML markup preserved as literal
 * content, and nested objects/arrays render through the lossless value view
 * in key/array order with no preview collapse. Only absent keys render nothing —
 * an own key with null, an empty string, or undefined renders an explicit
 * marker so missing data is never confused with an explicit empty value —
 * and array order is never touched.
 */
export function TurnContentSections({ content }: { content: unknown }) {
  // The serialized API cannot distinguish an own undefined from a missing
  // key without a separate presence bit, so undefined counts as absent at
  // this component boundary. Every other explicit top-level value renders
  // visibly so missing data is never confused with an explicit value.
  if (content === undefined) return null
  if (content === null) {
    return (
      <div className="space-y-2" data-testid="turn-content-sections">
        <section data-testid="turn-content-section-content">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Turn content
          </h4>
          <div className="rounded border border-border bg-card p-3 overflow-hidden">
            <span className="text-xs font-mono text-foreground" data-testid="explicit-null-value">
              null
            </span>
          </div>
        </section>
      </div>
    )
  }
  if (typeof content === 'string') {
    if (content === '') {
      return (
        <div className="space-y-2" data-testid="turn-content-sections">
          <section data-testid="turn-content-section-content">
            <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
              Turn content
            </h4>
            <div className="rounded border border-border bg-card p-3 overflow-hidden">
              <p className="text-xs font-mono text-tertiary-foreground" data-testid="empty-string-value">
                &quot;&quot; (empty string)
              </p>
            </div>
          </section>
        </div>
      )
    }
    return (
      <div className="space-y-2" data-testid="turn-content-sections">
        <section data-testid="turn-content-section-content">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Turn content
          </h4>
          <div className="rounded border border-border bg-card p-3 overflow-hidden">
            <TurnLosslessValue value={content} />
          </div>
        </section>
      </div>
    )
  }
  // Booleans, numbers, and arrays render through the lossless value view so the
  // full value survives: scalars via their string form, arrays in order,
  // and an empty array as the distinct "empty list" marker.
  if (typeof content === 'boolean' || typeof content === 'number' || Array.isArray(content)) {
    return (
      <div className="space-y-2" data-testid="turn-content-sections">
        <section data-testid="turn-content-section-content">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Turn content
          </h4>
          <div className="rounded border border-border bg-card p-3 overflow-hidden">
            <TurnLosslessValue value={content} />
          </div>
        </section>
      </div>
    )
  }
  if (!isRecord(content)) return null
  // An explicitly empty object is a value, not absent data.
  if (Object.keys(content).length === 0) {
    return (
      <div className="space-y-2" data-testid="turn-content-sections">
        <section data-testid="turn-content-section-content">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Turn content
          </h4>
          <div className="rounded border border-border bg-card p-3 overflow-hidden">
            <TurnLosslessValue value={content} />
          </div>
        </section>
      </div>
    )
  }
  // Every own key counts as present: null, '', false, 0, and undefined
  // are explicit values with visible markers below. Only a key that is not
  // present at all is absent.
  const hasOwn = (key: string): boolean => Object.prototype.hasOwnProperty.call(content, key)
  const ordered = [
    ...TURN_CONTENT_SECTION_ORDER.filter(hasOwn),
    ...Object.keys(content).filter((key) => !KNOWN_CONTENT_KEYS.has(key) && !MESSAGE_CONTENT_KEYS.has(key)),
  ]
  if (ordered.length === 0) return null
  return (
    <div className="space-y-3" data-testid="turn-content-sections">
      {ordered.map((key) => {
        const value = content[key]
        const label = TURN_CONTENT_SECTION_LABELS[key] ?? humanizeContentKey(key)
        return (
          <section key={key} data-testid={`turn-content-section-${key}`}>
            <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
              {label}
            </h4>
            <div className="rounded border border-border bg-card p-3 overflow-hidden">
              {typeof value === 'string' ? (
                value === '' ? (
                  <p className="text-xs font-mono text-tertiary-foreground" data-testid="empty-string-value">
                    &quot;&quot; (empty string)
                  </p>
                ) : (
                  <TurnLosslessValue value={value} label={label} />
                )
              ) : value === null ? (
                <span className="text-xs font-mono text-foreground" data-testid="explicit-null-value">
                  null
                </span>
              ) : value === undefined ? (
                <span className="text-xs font-mono text-foreground" data-testid="explicit-undefined-value">
                  undefined
                </span>
              ) : key === 'tool_definitions' && Array.isArray(value) ? (
                <ToolDefinitionCards definitions={value} />
              ) : (
                <TurnLosslessValue value={value} />
              )}
            </div>
          </section>
        )
      })}
    </div>
  )
}

/**
 * Tool call component: displays wrench icon, tool name, and parameters.
 */
export function ToolCallView({ call, className }: { call: KyberMessageLike | null; className?: string }) {
  const src: KyberMessageLike = (call?.raw as KyberMessageLike | undefined) ?? call ?? {}
  const toolName = String(src.name ?? src.tool ?? src.tool_name ?? 'tool_call')
  const rawParams = src.arguments !== undefined ? src.arguments : (src.parameters !== undefined ? src.parameters : (src.input !== undefined ? src.input : src.args))
  const params = tryParseJson(rawParams)

  return (
    <div className={cn('rounded border border-border bg-card/60 my-1.5 overflow-hidden', className)} data-testid="tool-call-view">
      <div className="flex items-center gap-2 border-b border-border bg-interactive-secondary/60 px-3 py-2">
        <span className="shrink-0 rounded bg-primary/10 border border-primary/20 px-2 py-0.5 text-xs font-medium text-primary flex items-center gap-1.5">
          <span role="img" aria-label="tool">🔧</span>
          <span>tool call</span>
        </span>
        <span className="font-mono text-xs font-semibold text-foreground truncate" data-testid="tool-call-name">
          {toolName}
        </span>
      </div>
      <div className="p-3">
        {params !== null && params !== undefined ? (
          <KeyValueList data={params} />
        ) : (
          <p className="text-xs text-tertiary-foreground italic">No parameters</p>
        )}
      </div>
    </div>
  )
}

/**
 * Tool result component: displays return icon and formatted output.
 */
export function ToolResultView({ result, className }: { result: KyberMessageLike | null; className?: string }) {
  const src: unknown = result?.raw !== undefined ? result.raw : (result?.response !== undefined ? result.response : (result?.result !== undefined ? result.result : (result?.output !== undefined ? result.output : result)))
  const parsed = tryParseJson(src)
  const isJson = parsed !== null && typeof parsed === 'object'

  return (
    <div className={cn('rounded border border-border bg-card/60 my-1.5 overflow-hidden', className)} data-testid="tool-result-view">
      <div className="flex items-center gap-2 border-b border-border bg-interactive-secondary/60 px-3 py-2">
        <span className="shrink-0 rounded bg-chart-2/10 border border-chart-2/20 px-2 py-0.5 text-xs font-medium text-chart-2 flex items-center gap-1.5">
          <span role="img" aria-label="result">↩</span>
          <span>tool result</span>
        </span>
      </div>
      <div className="p-3">
        {isJson ? (
          <KeyValueList data={parsed} />
        ) : typeof src === 'string' ? (
          containsReadableXmlTag(src) ? (
            <XmlFoldedText text={src} />
          ) : (
            <pre className="text-xs font-mono p-2 overflow-x-auto whitespace-pre-wrap border border-border rounded bg-muted/40 text-foreground">
              {src}
            </pre>
          )
        ) : (
          <KeyValueList data={src} />
        )}
      </div>
    </div>
  )
}

/**
 * Formats a single message part (text, reasoning, tool_call, tool_result).
 */
export function MessagePartView({ part }: { part: KyberMessageLike | null }) {
  if (!part) return null
  if (typeof part === 'string') {
    return <XmlFoldedText text={part} />
  }

  const type = part.type

  if (type === 'text') {
    const text = (part.content ?? part.text ?? '') as string
    return <XmlFoldedText text={text} />
  }

  if (type === 'reasoning' || type === 'thinking') {
    const rawReasoning: unknown = part.content ?? part.text ?? ((typeof part.raw === 'object' && part.raw !== null) ? ((part.raw as KyberMessageLike).content ?? (part.raw as KyberMessageLike).text) : undefined) ?? part.raw ?? part
    const reasoningStr = typeof rawReasoning === 'string' ? rawReasoning : JSON.stringify(rawReasoning, null, 2)

    return (
      <details className="group border border-border rounded my-1 bg-card/60 overflow-hidden" data-testid="reasoning-block">
        <summary className="cursor-pointer px-3 py-1.5 text-xs text-muted-foreground hover:text-foreground flex items-center gap-2 select-none hover:bg-interactive-secondary/50 transition-colors">
          <span className="shrink-0 rounded bg-violet-500/15 border border-violet-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-violet-600 dark:text-violet-400">
            {type === 'thinking' ? 'thinking' : 'reasoning'}
          </span>
          <span className="truncate font-mono text-[11px] text-tertiary-foreground flex-1">
            {reasoningStr.slice(0, 80).replace(/\s+/g, ' ')}…
          </span>
        </summary>
        <pre className="text-xs font-mono p-2 overflow-x-auto whitespace-pre-wrap border-t border-border bg-muted/40 text-foreground">
          {reasoningStr}
        </pre>
      </details>
    )
  }

  if (type === 'tool_call' || (part.name && (part.arguments !== undefined || part.input !== undefined || part.args !== undefined || part.parameters !== undefined))) {
    return <ToolCallView call={part} />
  }

  if (type === 'tool_result' || type === 'tool_call_response' || type === 'tool_call_result') {
    return <ToolResultView result={part} />
  }

  return (
    <details className="group border border-border rounded my-1 bg-card/60 overflow-hidden">
      <summary className="cursor-pointer px-3 py-1.5 text-xs text-muted-foreground hover:text-foreground flex items-center gap-2 select-none">
        <span className="shrink-0 rounded bg-muted border border-border px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
          {type || 'part'}
        </span>
      </summary>
      <pre className="text-xs font-mono p-2 overflow-x-auto whitespace-pre-wrap border-t border-border bg-muted/40 text-foreground">
        {JSON.stringify(part, null, 2)}
      </pre>
    </details>
  )
}

/**
 * Message view with role badge and border styling.
 */
function MessageView({ message }: { message: KyberMessageLike }) {
  const role = (message.role as string | undefined) ?? 'unknown'
  const isUser = role === 'user'
  const isAssistant = role === 'assistant'
  const isSystem = role === 'system'

  const borderClass = isUser
    ? 'border-l-2 border-l-primary'
    : isAssistant
      ? 'border-l-2 border-l-chart-2'
      : isSystem
        ? 'border-l-2 border-l-chart-4'
        : 'border-l-2 border-l-muted-foreground'

  const badgeClass = isUser
    ? 'bg-primary/10 text-primary border-primary/20'
    : isAssistant
      ? 'bg-chart-2/10 text-chart-2 border-chart-2/20'
      : 'bg-muted text-muted-foreground border-border'

  const parts = Array.isArray(message.parts) ? message.parts : null
  const content = message.content

  return (
    <div className={cn('rounded border border-border bg-card p-3 my-2 space-y-2', borderClass)} data-testid={`message-${role}`}>
      <div className="flex items-center justify-between">
        <span className={cn('rounded border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider', badgeClass)}>
          {role}
        </span>
        {message.name ? (
          <span className="text-[11px] font-mono text-tertiary-foreground">{String(message.name)}</span>
        ) : null}
      </div>
      <div className="space-y-1.5">
        {parts ? (
          parts.map((part, idx: number) => <MessagePartView key={idx} part={part as KyberMessageLike} />)
        ) : typeof content === 'string' ? (
          <XmlFoldedText text={content} />
        ) : content ? (
          <KeyValueList data={content} />
        ) : null}
      </div>
    </div>
  )
}

/**
 * The turn row the Turn Inspector reads. The token fields must be numbers to
 * chart; everything the payload may carry beside them stays unknown through
 * the index signature.
 */
interface TurnInspectorRow {
  cache_read?: number
  cache_creation?: number
  fresh?: number
  fresh_input?: number
  visible_output?: number
  output?: number
  reasoning?: number
  total?: number
  tokens?: {
    cache_read?: number
    cache_creation?: number
    fresh?: number
    fresh_input?: number
    output?: number
    reasoning?: number
    total?: number
  }
  content?: KyberTurnContentShadow | string | null
  input_messages?: KyberMessageLike[]
  output_messages?: KyberMessageLike[]
  model?: string | null
  durationMs?: number
  ttft_ms?: number
  credits?: number | null
  usd?: number | null
  fresh_jump_pct?: number
  request_start?: string
  tool_calls?: KyberMessageLike[]
  [key: string]: unknown
}

/**
 * Turn Inspector rendering token spend, minibar, model, and message content.
 */
function TurnInspector({
  turn,
}: {
  turn: TurnInspectorRow
}) {
  const cacheRead = turn.cache_read ?? turn.tokens?.cache_read ?? 0
  const cacheCreation = turn.cache_creation ?? turn.tokens?.cache_creation ?? 0
  const fresh = turn.fresh ?? turn.fresh_input ?? turn.tokens?.fresh ?? turn.tokens?.fresh_input ?? 0
  const output = turn.visible_output ?? turn.output ?? turn.tokens?.output ?? 0
  const reasoning = turn.reasoning ?? turn.tokens?.reasoning ?? 0

  const segs = [
    { label: 'Cache-read input', value: cacheRead, color: '#3ecf8e' },
    { label: 'Cache-creation input', value: cacheCreation, color: '#5b9bef' },
    { label: 'Fresh input', value: fresh, color: '#e8b93e' },
    { label: 'Output', value: output, color: '#4fd394' },
    { label: 'Reasoning output', value: reasoning, color: '#a98b4f' },
  ].filter(s => s.value > 0)

  const totalTokens = segs.reduce((a, s) => a + s.value, 0) || (turn.total ?? turn.tokens?.total ?? 1)

  const content: KyberTurnContentShadow = (
    typeof turn.content === 'object' && turn.content !== null ? turn.content : {}
  ) as KyberTurnContentShadow
  const inMsgs = content.input_messages ?? turn.input_messages
  const outMsgs = content.output_messages ?? turn.output_messages

  return (
    <div className="space-y-4" data-testid="turn-inspector">
      {/* Token Spend Summary */}
      <div className="rounded border border-border bg-card p-3 space-y-2.5">
        <div className="flex items-center justify-between">
          <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Token Spend</span>
          <span className="text-xs font-mono font-semibold text-foreground">{fmtTokens(totalTokens)} total</span>
        </div>

        {/* Minibar */}
        {segs.length > 0 && (
          <div className="h-2 rounded overflow-hidden flex bg-muted" data-testid="token-minibar">
            {segs.map((seg, i) => (
              <div
                key={i}
                style={{ width: `${((seg.value / totalTokens) * 100).toFixed(2)}%`, backgroundColor: seg.color }}
                title={`${seg.label}: ${fmtTokens(seg.value)}`}
              />
            ))}
          </div>
        )}

        {/* Breakdown Rows */}
        <div className="space-y-1">
          {segs.map((seg, i) => (
            <div key={i} className="flex items-center justify-between text-xs py-0.5">
              <div className="flex items-center gap-2">
                <span className="w-2 h-2 rounded-sm shrink-0" style={{ backgroundColor: seg.color }} />
                <span className="text-muted-foreground">{seg.label}</span>
              </div>
              <div className="flex items-center gap-2 font-mono tabular-nums">
                <span className="font-semibold text-foreground">{fmtTokens(seg.value)}</span>
                <span className="text-tertiary-foreground text-[11px]">
                  {((seg.value / totalTokens) * 100).toFixed(1)}%
                </span>
              </div>
            </div>
          ))}
        </div>

        {/* Metric stats */}
        <div className="border-t border-border pt-2 space-y-1 text-xs">
          {turn.model && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Model</span>
              <span className="font-mono text-foreground">{turn.model}</span>
            </div>
          )}
          {turn.durationMs != null && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Duration</span>
              <span className="font-mono text-foreground">{formatDuration(turn.durationMs)}</span>
            </div>
          )}
          {turn.ttft_ms != null && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Time to first token</span>
              <span className="font-mono text-foreground">{Math.round(turn.ttft_ms)}ms</span>
            </div>
          )}
          {(turn.credits != null || turn.usd != null) && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Cost</span>
              <span className="font-mono text-foreground">
                {turn.credits != null ? `${formatCredits(turn.credits)} credits` : ''}
                {turn.credits != null && turn.usd != null ? ' · ' : ''}
                {turn.usd != null ? usd(turn.usd) : ''}
              </span>
            </div>
          )}
        </div>

      </div>

      {/* Fresh Jump Warning */}
      {turn.fresh_jump_pct != null && (
        <div className="rounded border border-amber-500/30 bg-amber-500/10 p-2.5 text-xs text-amber-600 dark:text-amber-400">
          Fresh input jumped <strong>+{turn.fresh_jump_pct}%</strong> vs previous turn — cache miss or new material pulled in.
        </div>
      )}

      {/* Request Start Marker */}
      {turn.request_start && (
        <div className="rounded border border-border bg-interactive-secondary p-2.5 text-xs text-foreground/90">
          New request starts at this turn: <span className="font-mono font-medium">{turn.request_start}</span>
        </div>
      )}

      {/* Input Messages */}
      {Array.isArray(inMsgs) && inMsgs.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            User / Conversation Input
          </h4>
          {inMsgs.map((m, idx: number) => (
            <MessageView key={idx} message={m} />
          ))}
        </div>
      )}

      {/* Prompt text (flattened harness) */}
      {!inMsgs && content.prompt_text ? (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Prompt (flattened)
          </h4>
          <div className="rounded border border-border bg-card p-3">
            <XmlFoldedText text={content.prompt_text as string} />
          </div>
        </div>
      ) : null}

      {/* Canonical content buckets. Copilot-shaped rows carry system_prompt,
          tool_definitions, conversation_history, and friends here rather than
          in input/output messages, which the blocks above would otherwise
          drop without a trace. */}
      <TurnContentSections content={turn.content} />

      {/* Output Messages */}
      {Array.isArray(outMsgs) && outMsgs.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Model Response
          </h4>
          {outMsgs.map((m, idx: number) => (
            <MessageView key={idx} message={m} />
          ))}
        </div>
      )}

      {/* Response text (flattened harness) */}
      {!outMsgs && content.response_text ? (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Model Response
          </h4>
          <div className="rounded border border-border bg-card p-3">
            <XmlFoldedText text={content.response_text as string} />
          </div>
        </div>
      ) : null}

      {/* Reasoning text */}
      {(content.reasoning_text || content.thinking_text) ? (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            {content.thinking_text && !content.reasoning_text ? 'Thinking' : 'Reasoning'}
          </h4>
          <MessagePartView part={{ type: content.thinking_text && !content.reasoning_text ? 'thinking' : 'reasoning', content: content.reasoning_text || content.thinking_text }} />
        </div>
      ) : null}

      {/* Tool Calls */}
      {Array.isArray(turn.tool_calls) && turn.tool_calls.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Tool Calls ({turn.tool_calls.length})
          </h4>
          {turn.tool_calls.map((tc, idx: number) => (
            <ToolCallView key={idx} call={tc} />
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * Timeline Span / Node Inspector rendering attributes and metadata.
 */
function SpanInspector({ node }: { node: SessionTimelineNode }) {
  // Timeline nodes carry structure and metadata; their attribute map is the
  // raw span payload and is no longer copied into every session row. A node
  // that still has one inline renders it directly, otherwise the span on
  // screen is fetched from compressed storage.
  const inlineAttrs = (node.attributes ?? {}) as Record<string, unknown>
  const hasInlineAttrs = Object.keys(inlineAttrs).length > 0
  const spanId: string | undefined = typeof node.spanId === 'string' ? node.spanId : undefined
  const [loadedAttrs, setLoadedAttrs] = useState<Record<string, unknown> | null>(null)

  useEffect(() => {
    setLoadedAttrs(null)
    if (spanId === undefined || hasInlineAttrs) return
    let cancelled = false
    fetchSpanAttributes(spanId)
      .then((result) => {
        if (!cancelled) setLoadedAttrs(result.attributes)
      })
      .catch(() => {
        // The inspector renders "no attributes" rather than an error here; the
        // span's own metadata above is still the useful part of this pane.
        if (!cancelled) setLoadedAttrs(null)
      })
    return () => {
      cancelled = true
    }
  }, [spanId, hasInlineAttrs])

  const attrs = hasInlineAttrs ? inlineAttrs : (loadedAttrs ?? {})
  const [filter, setFilter] = useState('')

  const filteredAttrs = useMemo(() => {
    if (!filter) return attrs
    const q = filter.toLowerCase()
    const res: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(attrs)) {
      if (k.toLowerCase().includes(q) || String(v).toLowerCase().includes(q)) {
        res[k] = v
      }
    }
    return res
  }, [attrs, filter])

  return (
    <div className="space-y-4" data-testid="span-inspector">
      <div className="rounded border border-border bg-card p-3 space-y-2">
        <div className="flex items-center justify-between">
          <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Span Details</span>
          <div className="flex items-center gap-1.5">
            {node.isSubagent && (
              <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-700 dark:text-amber-400">
                subagent
              </span>
            )}
            {node.isAuxiliary && (
              <span className="rounded bg-violet-500/15 px-1.5 py-0.5 text-[10px] font-medium text-violet-700 dark:text-violet-400">
                auxiliary
              </span>
            )}
            {node.kind && (
              <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
                {node.kind}
              </span>
            )}
          </div>
        </div>

        <div className="space-y-1 text-xs font-mono">
          <div className="flex justify-between py-0.5">
            <span className="text-muted-foreground">Name:</span>
            <span className="text-foreground font-semibold truncate">{node.name}</span>
          </div>
          {node.spanId && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Span ID:</span>
              <span className="text-tertiary-foreground">{node.spanId}</span>
            </div>
          )}
          {node.traceId && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Trace ID:</span>
              <span className="text-tertiary-foreground">{node.traceId}</span>
            </div>
          )}
          {node.durationMs != null && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Duration:</span>
              <span className="text-foreground">{formatDuration(node.durationMs)}</span>
            </div>
          )}
          {node.status && (
            <div className="flex justify-between py-0.5">
              <span className="text-muted-foreground">Status:</span>
              <span className="text-foreground">{node.status}</span>
            </div>
          )}
        </div>
      </div>

      {/* Span Content if present */}
      {node.content && (
        <div className="space-y-2">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Content</h4>
          <InspectorContent data={node.content} />
        </div>
      )}

      {/* Span Attributes */}
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Span Attributes ({Object.keys(attrs).length})
          </h4>
          {Object.keys(attrs).length > 5 && (
            <input
              type="text"
              placeholder="Filter attributes..."
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              className="rounded border border-border bg-interactive-secondary px-2 py-0.5 text-xs text-foreground placeholder:text-tertiary-foreground focus:outline-none focus:ring-1 focus:ring-primary"
            />
          )}
        </div>

        {Object.keys(filteredAttrs).length === 0 ? (
          <p className="text-xs text-tertiary-foreground italic py-2">
            {filter ? 'No matching attributes.' : 'No attributes recorded.'}
          </p>
        ) : (
          <div className="rounded border border-border bg-card p-3 overflow-hidden">
            <KeyValueList data={filteredAttrs} />
          </div>
        )}
      </div>
    </div>
  )
}

/**
 * One context-bucket drill-down as the dashboard's `openDrawerForTurn`
 * assembles it.
 */
interface ContextBucketItem {
  bucket?: string
  key?: string
  label?: string
  value?: number
  tokens?: number
  total?: number
  content?: unknown
  [key: string]: unknown
}

/**
 * Context Bucket Inspector.
 */
function ContextBucketInspector({
  item,
}: {
  item: ContextBucketItem
}) {
  const tokens = item.value ?? item.tokens ?? 0
  const total = item.total ?? 0
  const pct = total > 0 ? ((tokens / total) * 100).toFixed(1) + '%' : '—'

  return (
    <div className="space-y-4" data-testid="context-bucket-inspector">
      <div className="rounded border border-border bg-card p-3 space-y-2 text-xs">
        <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
          {item.label ?? item.key ?? 'Context Bucket'}
        </h4>
        <div className="flex justify-between py-0.5">
          <span className="text-muted-foreground">Tokens (approx.):</span>
          <span className="font-mono font-semibold text-foreground">{fmtTokens(tokens)}</span>
        </div>
        <div className="flex justify-between py-0.5">
          <span className="text-muted-foreground">Share of reported input:</span>
          <span className="font-mono text-foreground">{pct}</span>
        </div>
        {total > 0 && (
          <div className="flex justify-between py-0.5">
            <span className="text-muted-foreground">Reported input total:</span>
            <span className="font-mono text-foreground">{fmtTokens(total)}</span>
          </div>
        )}
      </div>

      {item.content ? (
        <div className="space-y-2">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Bucket Content</h4>
          <InspectorContent data={item.content} />
        </div>
      ) : null}
    </div>
  )
}

/**
 * True for the plain objects every inspector branch dispatches on; arrays and
 * null fall through to the generic renderer below.
 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * Helper component InspectorContent to format turn messages, timeline span attributes, and raw JSON.
 */
export function InspectorContent({
  data,
  suppressJsonControls,
}: {
  data: unknown
  /**
   * Hides nested JSON views' own toggle/copy controls for embeddings that
   * already provide one outer toggle and exact Copy (whole-turn blocks).
   */
  suppressJsonControls?: boolean
}) {
  const [viewMode, setViewMode] = useState<'formatted' | 'raw'>('formatted')
  const [copied, setCopied] = useState(false)

  if (data === null || data === undefined) {
    return <div className="text-xs text-tertiary-foreground italic py-4 text-center">No data to display.</div>
  }

  // String handling: Copilot-shaped rows carry whole buckets (system prompt,
  // tool definitions, conversation history) as single strings, and JSON
  // blobs render as an undifferentiated wall through plain text. Route them
  // through the structured view; the exact original stays one toggle away.
  if (typeof data === 'string') {
    return <ReadableContentText text={data} suppressJsonControls={suppressJsonControls} />
  }

  const record = isRecord(data) ? data : null

  // Handle Turn payload
  if (record && (record.fresh !== undefined || record.cache_read !== undefined || (record.index !== undefined && record.tokens !== undefined))) {
    return <TurnInspector turn={record as TurnInspectorRow} />
  }

  // Handle Timeline Span Node
  if (record && ('spanId' in record || ('name' in record && 'attributes' in record))) {
    return <SpanInspector node={record as unknown as SessionTimelineNode} />
  }

  // Handle Context bucket item
  if (record && ('bucket' in record || ('key' in record && 'total' in record))) {
    return <ContextBucketInspector item={record as ContextBucketItem} />
  }

  // Handle Tool Call
  if (record && (record.type === 'tool_call' || (record.name && (record.arguments !== undefined || record.input !== undefined)))) {
    return <ToolCallView call={record} />
  }

  // Handle Tool Result
  if (record && (record.type === 'tool_result' || record.type === 'tool_call_response' || record.type === 'tool_call_result' || record.result !== undefined || record.output !== undefined)) {
    return <ToolResultView result={record} />
  }

  // Handle Message
  if (record && ('role' in record && ('content' in record || 'parts' in record))) {
    return <MessageView message={record} />
  }

  // Handle array of messages
  if (Array.isArray(data) && data.length > 0 && isRecord(data[0]) && ('role' in data[0] || 'parts' in data[0])) {
    return (
      <div className="space-y-2">
        {data.map((m, idx: number) => (
          <MessageView key={idx} message={m as KyberMessageLike} />
        ))}
      </div>
    )
  }

  // Handle generic object or array with formatted & raw tabs
  const copyJson = () => {
    try {
      void navigator.clipboard.writeText(JSON.stringify(data, null, 2))
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // ignore
    }
  }

  return (
    <div className="space-y-3" data-testid="inspector-content">
      <div className="flex items-center justify-between border-b border-border pb-2">
        <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs">
          <button
            type="button"
            onClick={() => setViewMode('formatted')}
            className={cn(
              'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
              viewMode === 'formatted' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Formatted
          </button>
          <button
            type="button"
            onClick={() => setViewMode('raw')}
            className={cn(
              'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
              viewMode === 'raw' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Raw JSON
          </button>
        </div>
        <button
          type="button"
          onClick={copyJson}
          className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:text-foreground hover:bg-interactive-secondary transition-colors"
        >
          {copied ? 'Copied!' : 'Copy JSON'}
        </button>
      </div>

      {viewMode === 'formatted' ? (
        <div className="rounded border border-border bg-card p-3 overflow-hidden">
          <KeyValueList data={data} />
        </div>
      ) : (
        <pre className="text-xs font-mono p-3 overflow-auto rounded border border-border bg-muted/40 text-foreground max-h-[500px]">
          {JSON.stringify(data, null, 2)}
        </pre>
      )}
    </div>
  )
}

export interface SessionContentRequest {
  sessionId: string
  span?: string
  part?: string
}

/**
 * Whole-turn lossless request: the exact zero-based transport turn index the
 * turn-content route resolves (issue #184), always via the explicit `full`
 * path so the complete source text returns intact.
 */
export interface TurnFullContentRequest {
  sessionId: string
  turnIndex: number
  full?: boolean
}

export type DrawerContentRequest = SessionContentRequest | TurnFullContentRequest

/**
 * Narrowing helper: a whole-turn request carries a numeric `turnIndex`; a
 * span/bucket request never does.
 */
export function isTurnFullContentRequest(request: unknown): request is TurnFullContentRequest {
  if (request === null || typeof request !== 'object') return false
  return typeof (request as { turnIndex?: unknown }).turnIndex === 'number'
}

/**
 * Exact-bytes copy button: writes `text` verbatim to the clipboard. Every
 * turn-content block and part gets one so raw access never depends on the
 * formatted view above it.
 */
export function CopyExactButton({ text, testId, label = 'Copy' }: { text: string; testId: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      type="button"
      onClick={() => {
        try {
          void navigator.clipboard.writeText(text)
          setCopied(true)
          setTimeout(() => setCopied(false), 2000)
        } catch {
          // ignore
        }
      }}
      className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:text-foreground hover:bg-interactive-secondary transition-colors"
      data-testid={testId}
    >
      <span aria-live="polite">{copied ? 'Copied!' : label}</span>
    </button>
  )
}

/**
 * Measured per-turn aggregate token figures, read off the served turn row.
 * Every field is a harness-measured counter; absent fields render honestly
 * as not recorded — never estimated from text.
 */
export interface TurnFullContentTurn {
  fresh?: number | null
  fresh_input?: number | null
  cache_read?: number | null
  cache_creation?: number | null
  tokens?: {
    fresh?: number | null
    fresh_input?: number | null
    cache_read?: number | null
    cache_creation?: number | null
  } | null
  model?: string | null
}

function readMeasuredFigure(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * Stable identity for matching a top-level part against block members.
 * Block parts and top-level parts are separate objects after the JSON
 * round-trip, so the server-assigned `id` matches them; part+text is the
 * fallback. Callers count matches so repeated identical parts pair off
 * instead of collapsing.
 */
export function turnPartKey(part: Pick<KyberTurnContentPart, 'id' | 'part' | 'text'>): string {
  return part.id ?? `${part.part}${part.text}`
}

/**
 * Top-level parts covered by no block (unknown part kinds the canonical
 * block set does not name), in server order. Rendered explicitly so no
 * returned part is silently dropped from the whole-turn view.
 */
export function orphanTurnParts(data: KyberTurnContentResult): KyberTurnContentPart[] {
  const remaining = new Map<string, number>()
  for (const block of data.blocks) {
    for (const p of block.parts) {
      const key = turnPartKey(p)
      remaining.set(key, (remaining.get(key) ?? 0) + 1)
    }
  }
  const orphans: KyberTurnContentPart[] = []
  for (const p of data.parts) {
    const key = turnPartKey(p)
    const count = remaining.get(key) ?? 0
    if (count > 0) {
      remaining.set(key, count - 1)
    } else {
      orphans.push(p)
    }
  }
  return orphans
}

/**
 * Shared session context entries to display for a turn, preserving every
 * captured occurrence in server order.
 *
 * The server builds `sharedContext` as non-turn session `system_prompt`
 * parts plus every turn-derived occurrence at or before the selected turn,
 * each carrying its contributing `turnIndex`. The turn's own occurrences
 * already render in their blocks, so showing them again under shared context
 * would display and copy the exact same captured occurrence twice. Every
 * other entry stays — an earlier-turn occurrence is a distinct API-seen
 * occurrence even when it shares text and span id with the selected turn.
 *
 * Overlap is identified by source turn index with multiplicity, never by
 * span id or text globally: a shared entry is skipped only when its
 * `turnIndex` equals the selected turn's `turnIndex` and its exact text
 * still has an unmatched own `system_prompt` occurrence. Counts pair off
 * per occurrence, so repeated identical parts do not collapse. Entries
 * without a `turnIndex` (session-scope parts) are never suppressed — absent
 * is not a turn and must never be guessed as one. Empty-text entries carry
 * no bytes and stay skipped.
 */
export function resolveSharedDisplay(
  data: KyberTurnContentResult,
): { parts: NonNullable<KyberTurnContentResult['sharedContext']>['parts']; seenBefore: boolean } | null {
  const shared = data.sharedContext
  if (!shared) return null
  // Own occurrences are the turn's `system_prompt` parts only: shared
  // context carries that kind alone, so matching any other kind's text
  // would suppress a distinct occurrence. `data.parts` is authoritative
  // (blocks are built from the same part objects server-side); when it is
  // empty, fall back to the blocks' member parts so an unexpected shape
  // still pairs off instead of double-displaying.
  const ownSources: KyberTurnContentPart[] =
    data.parts.length > 0 ? data.parts : data.blocks.flatMap((b) => b.parts)
  const ownCounts = new Map<string, number>()
  for (const p of ownSources) {
    if (p.part === 'system_prompt' && p.text !== '') {
      ownCounts.set(p.text, (ownCounts.get(p.text) ?? 0) + 1)
    }
  }
  const parts: NonNullable<KyberTurnContentResult['sharedContext']>['parts'] = []
  for (const sp of shared.parts) {
    if (sp.text === '') continue
    const isOwnOccurrence = sp.turnIndex !== undefined && sp.turnIndex === data.turnIndex
    if (isOwnOccurrence) {
      const remaining = ownCounts.get(sp.text) ?? 0
      if (remaining > 0) {
        ownCounts.set(sp.text, remaining - 1)
        continue
      }
    }
    parts.push(sp)
  }
  if (parts.length === 0) return null
  return { parts, seenBefore: shared.seenBefore }
}

/**
 * Exact text for the whole-turn copy action: the shared session context
 * (when shown), every block rebuilt from its member parts, then every
 * additional part under its own header. Sections join with blank lines;
 * within a section member texts join the same way — but no part text is
 * ever trimmed, so leading/trailing and whitespace-only source bytes
 * survive byte-exact (unlike `assembledText`, which trims and drops
 * whitespace-only sections for display). Part texts are concatenated
 * verbatim — never re-encoded — so the copy carries every returned byte
 * exactly once. Shared context stays separately copyable per part; it is
 * included here, clearly labeled, so a button called "Copy Turn" never
 * silently omits it.
 */
export function buildWholeTurnCopyText(
  data: KyberTurnContentResult,
  sharedTexts: string[],
): string {
  const sections: string[] = []
  const shared = sharedTexts.filter((t) => t !== '')
  if (shared.length > 0) {
    sections.push(`=== SHARED SESSION CONTEXT ===\n${shared.join('\n\n')}`)
  }
  for (const block of data.blocks) {
    // A block with neither member parts nor text carries no bytes; anything
    // else renders — including whitespace-only member text.
    if (block.parts.length === 0 && block.text === '') continue
    const body = block.parts.length > 0 ? block.parts.map((p) => p.text).join('\n\n') : block.text
    sections.push(`=== ${block.label.toUpperCase()} ===\n${body}`)
  }
  const orphans = orphanTurnParts(data)
  if (orphans.length > 0) {
    sections.push(orphans.map((p) => `=== ${p.label.toUpperCase()} ===\n${p.text}`).join('\n\n'))
  }
  return sections.join('\n\n')
}

export type TurnContentViewMode = 'formatted' | 'raw'

/**
 * Formatted-by-default content with exact raw one toggle away. Only one
 * rendering prints at a time so large content is not duplicated in the DOM
 * by default; the formatted view never trims (readable structure only) and
 * the raw view is the exact source bytes with its own copy button.
 */
export function ToggleRawView({
  text,
  label,
  rawTestId,
  copyTestId,
  viewPrefix,
  defaultView = 'formatted',
}: {
  text: string
  label?: string
  rawTestId: string
  copyTestId: string
  viewPrefix: string
  defaultView?: TurnContentViewMode
}) {
  const [view, setView] = useState<TurnContentViewMode>(defaultView)
  const groupLabel = `${label ?? 'Content'}: formatted or raw view`
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2" role="group" aria-label={groupLabel}>
        <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs">
          <button
            type="button"
            onClick={() => setView('formatted')}
            data-testid={`${viewPrefix}-view-formatted`}
            aria-pressed={view === 'formatted'}
            className={cn(
              'px-2 py-0.5 rounded-[4px] font-medium transition-colors',
              view === 'formatted' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Formatted
          </button>
          <button
            type="button"
            onClick={() => setView('raw')}
            data-testid={`${viewPrefix}-view-raw`}
            aria-pressed={view === 'raw'}
            className={cn(
              'px-2 py-0.5 rounded-[4px] font-medium transition-colors',
              view === 'raw' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
            )}
          >
            Raw
          </button>
        </div>
        <CopyExactButton text={text} testId={copyTestId} label="Copy" />
      </div>
      {view === 'formatted' ? (
        <InspectorContent data={text} suppressJsonControls />
      ) : (
        <pre
          className="text-xs font-mono p-2.5 overflow-auto whitespace-pre-wrap break-words rounded border border-border bg-muted/40 text-foreground max-h-[400px]"
          data-testid={rawTestId}
        >
          {text}
        </pre>
      )}
    </div>
  )
}

/**
 * One canonical turn-content block: measured tokens (or an honest absent
 * marker — zero renders as an explicit zero), measurability and truncation
 * notices, then the formatted/raw toggle. No per-block cache attribution.
 */
export function TurnContentBlockView({
  block,
  defaultView = 'formatted',
}: {
  block: KyberTurnContentBlock
  defaultView?: TurnContentViewMode
}) {
  const prefix = `turn-block-${block.key}`
  return (
    <section data-testid={prefix}>
      <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
        {block.label}
      </h4>
      {/* Independent recurrence labels: either, neither, or both. Capture
          only — never converted into a cache claim (see the cache note). */}
      {block.recurrence?.newThisTurn || block.recurrence?.seenBefore ? (
        <div className="flex items-center gap-1.5 flex-wrap mb-1.5">
          {block.recurrence?.newThisTurn ? (
            <span
              className="shrink-0 rounded bg-emerald-500/15 border border-emerald-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-emerald-600 dark:text-emerald-400"
              data-testid={`${prefix}-recurrence-new`}
            >
              New this turn
            </span>
          ) : null}
          {block.recurrence?.seenBefore ? (
            <span
              className="shrink-0 rounded bg-sky-500/15 border border-sky-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-sky-600 dark:text-sky-400"
              data-testid={`${prefix}-recurrence-seen`}
            >
              Also present in prior captured context
            </span>
          ) : null}
        </div>
      ) : null}
      <div className="rounded border border-border bg-card p-3 overflow-hidden space-y-2">
        {typeof block.tokens === 'number' ? (
          <p className="text-[11px] font-mono text-tertiary-foreground" data-testid={`${prefix}-tokens`}>
            {fmtTokens(block.tokens)} tokens
          </p>
        ) : (
          <p className="text-[11px] font-mono text-tertiary-foreground italic" data-testid={`${prefix}-tokens-absent`}>
            token count not recorded
          </p>
        )}
        {/* Separate dimension from recurrence: cache attribution is not
            reported for individual blocks. Measured cache totals render once
            per turn, above. */}
        <p className="text-[11px] text-tertiary-foreground" data-testid={`${prefix}-cache-note`}>
          Cache: attribution is not reported for individual blocks.
        </p>
        {block.notMeasurable ? (
          <p
            className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
            data-testid={`${prefix}-not-measurable`}
          >
            Not measurable: {block.notMeasurable.reason}
          </p>
        ) : null}
        {block.truncated === true ? (
          <p
            className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
            data-testid={`${prefix}-truncated`}
          >
            {block.totalLength != null
              ? `showing ${block.text.length} of ${block.totalLength} characters`
              : `showing ${block.text.length} characters (total length not measurable)`}
          </p>
        ) : null}
        {block.text !== '' ? (
          <ToggleRawView
            text={block.text}
            label={block.label}
            rawTestId={`${prefix}-raw`}
            copyTestId={`copy-${prefix}`}
            viewPrefix={prefix}
            defaultView={defaultView}
          />
        ) : (
          <p className="text-xs text-tertiary-foreground italic" data-testid={`${prefix}-empty`}>
            No content recorded for this block.
          </p>
        )}
      </div>
    </section>
  )
}

/**
 * One additional (non-block) turn part with the same formatted/raw/copy
 * contract as canonical blocks.
 */
export function TurnContentPartView({
  part,
  index,
  defaultView = 'formatted',
}: {
  part: KyberTurnContentPart
  index: number
  defaultView?: TurnContentViewMode
}) {
  const prefix = `turn-part-${part.part}-${index}`
  return (
    <div data-testid={prefix}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-xs font-semibold text-foreground">{part.label}</span>
        <span className="rounded bg-muted border border-border px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
          {part.part}
        </span>
        {part.server ? (
          <span className="rounded bg-interactive-secondary px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
            {part.server}
          </span>
        ) : null}
        {typeof part.tokens === 'number' ? (
          <span className="font-mono text-[10px] text-tertiary-foreground">{fmtTokens(part.tokens)} tokens</span>
        ) : null}
      </div>
      {part.truncated === true ? (
        <p
          className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300 mt-1.5"
          data-testid={`${prefix}-truncated`}
        >
          {part.totalLength != null
            ? `showing ${part.text.length} of ${part.totalLength} characters`
            : `showing ${part.text.length} characters (total length not measurable)`}
        </p>
      ) : null}
      <div className="mt-1.5">
        <ToggleRawView
          text={part.text}
          label={part.label}
          rawTestId={`${prefix}-raw`}
          copyTestId={`copy-${prefix}`}
          viewPrefix={prefix}
          defaultView={defaultView}
        />
      </div>
    </div>
  )
}

/**
 * Whole-turn inspector panel backed by the single lossless turn-content
 * endpoint (one bounded request: the turn plus its preceding-turn
 * recurrence, no browser fan-out).
 *
 * - Renders every returned block and part in server order, including unknown
 *   part kinds (as an explicit "Additional parts" section) and zero-token
 *   blocks; nothing is summarized, dropped, trimmed, or truncated for
 *   display. When the API flags `truncated`, the exact limitation is stated
 *   while all returned bytes still render.
 * - Every captured `system_prompt` part in `sharedContext.parts`, in
 *   capture order, renders as its own card under `Shared session context`,
 *   explicitly labeled as session-level content rather than captured for
 *   this turn; only the selected turn's own source occurrence (same span
 *   id, paired per occurrence) is not shown twice, while identical text
 *   from distinct earlier spans stays visible. Each card carries its own
 *   exact-match recurrence badge, independent of the session-level label.
 * - Per-block `New this turn` / `Also present in prior captured context`
 *   badges aggregate member-part recurrence (either, neither, or both).
 *   Recurrence is capture-only: a separate per-block cache note states that
 *   attribution is not reported for individual blocks, and measured
 *   per-turn fresh/cache-read/cache-creation totals render separately above.
 */
export function TurnFullContentPanel({
  request,
  turn,
  defaultView = 'formatted',
}: {
  request: TurnFullContentRequest
  turn?: TurnFullContentTurn | null
  defaultView?: TurnContentViewMode
}) {
  const turnQuery = useQuery({
    queryKey: ['kyber-turn-content-full', request.sessionId, request.turnIndex],
    queryFn: () => fetchTurnContentFull(request.sessionId, request.turnIndex),
  })

  const data: KyberTurnContentResult | undefined = turnQuery.data
  const isTurnError = turnQuery.isError

  const fresh = readMeasuredFigure(turn?.fresh ?? turn?.fresh_input ?? turn?.tokens?.fresh ?? turn?.tokens?.fresh_input)
  const cacheRead = readMeasuredFigure(turn?.cache_read ?? turn?.tokens?.cache_read)
  const cacheCreation = readMeasuredFigure(turn?.cache_creation ?? turn?.tokens?.cache_creation)
  const hasTotals = fresh !== undefined || cacheRead !== undefined || cacheCreation !== undefined

  const orphanParts: KyberTurnContentPart[] = useMemo(() => {
    if (!data) return []
    return orphanTurnParts(data)
  }, [data])

  // Shared session context arrives embedded in the single turn-content
  // response (one bounded request, no N-query fan-out). Occurrence
  // preserving: every entry renders except the selected turn's own source
  // occurrence(s), paired by span id with multiplicity (see
  // `resolveSharedDisplay`).
  const sharedDisplay = useMemo(() => {
    if (!data) return null
    return resolveSharedDisplay(data)
  }, [data])

  // Copy Turn carries every returned byte exactly once (shared context
  // labeled, blocks verbatim, additional parts under their own headers) so
  // the action never silently omits content the view shows.
  const wholeTurnCopyText = useMemo(() => {
    if (!data) return ''
    return buildWholeTurnCopyText(
      data,
      sharedDisplay ? sharedDisplay.parts.map((p) => p.text) : [],
    )
  }, [data, sharedDisplay])

  const renderFigure = (label: string, value: number | undefined, testId: string) => (
    <div className="flex justify-between py-0.5" data-testid={testId}>
      <span className="text-muted-foreground">{label}</span>
      {value !== undefined ? (
        <span className="font-mono font-semibold text-foreground">{fmtTokens(value)}</span>
      ) : (
        <span className="font-mono text-tertiary-foreground italic">not recorded</span>
      )}
    </div>
  )

  return (
    <div className="space-y-3" data-testid="turn-content-full">
      {hasTotals ? (
        <div className="rounded border border-border bg-card p-3 space-y-1 text-xs" data-testid="turn-token-totals">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Measured turn totals
          </h4>
          {renderFigure('Fresh input', fresh, 'turn-total-fresh')}
          {renderFigure('Cache read', cacheRead, 'turn-total-cache-read')}
          {renderFigure('Cache creation', cacheCreation, 'turn-total-cache-creation')}
          <p className="text-[11px] text-tertiary-foreground leading-relaxed pt-1">
            Cache totals are measured for the whole turn; the collector does not map them to specific blocks.
          </p>
        </div>
      ) : (
        <div className="rounded border border-border bg-card p-3 text-xs" data-testid="turn-token-totals-absent">
          <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Measured turn totals</span>
          <p className="text-tertiary-foreground italic mt-1">token counts not recorded</p>
        </div>
      )}

      {!isTurnError && data === undefined ? (
        <div className="space-y-2" data-testid="turn-content-loading">
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-32 w-full" />
          <p className="text-xs text-tertiary-foreground">Loading full turn content…</p>
        </div>
      ) : null}

      {isTurnError ? (
        <div
          className="rounded border border-red-500/30 bg-red-500/10 p-4 text-xs text-red-600 dark:text-red-400 space-y-1.5"
          data-testid="turn-content-error"
        >
          <p className="font-semibold">Unable to load full turn content.</p>
          <p className="text-muted-foreground">
            {turnQuery.error instanceof Error ? turnQuery.error.message : 'Turn content not found.'}
          </p>
        </div>
      ) : null}

      {data ? (
        <div className="space-y-3">
          {data.truncated === true ? (
            <p
              className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
              data-testid="turn-content-truncated"
            >
              {data.totalLength != null
                ? `showing ${data.assembledText.length} of ${data.totalLength} characters`
                : `showing ${data.assembledText.length} characters (total length not measurable)`}
            </p>
          ) : null}

          {sharedDisplay ? (
            <section data-testid="shared-session-context">
              <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
                Shared session context
              </h4>
              <p className="text-[11px] text-tertiary-foreground leading-relaxed mb-1.5">
                Session-level system content · applies to every turn · not captured separately for this turn.
              </p>
              {/* Shared context carries its own exact-match recurrence,
                  independent of the session-level label. A badge renders
                  only with earlier-turn evidence — never "new", since first
                  capture outside the turn spans is unverifiable. */}
              {sharedDisplay.seenBefore ? (
                <div className="flex items-center gap-1.5 flex-wrap mb-1.5">
                  <span
                    className="shrink-0 rounded bg-sky-500/15 border border-sky-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-sky-600 dark:text-sky-400"
                    data-testid="shared-session-context-recurrence-seen"
                  >
                    Also present in prior captured context
                  </span>
                </div>
              ) : null}
              <div className="space-y-2">
                {sharedDisplay.parts.map((part, idx) => (
                  <div key={`${part.spanId ?? 'session'}-${idx}`} className="rounded border border-border bg-card p-3 overflow-hidden space-y-2" data-testid={`shared-session-context-part-${idx}`}>
                    {part.truncated === true ? (
                      <p
                        className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
                        data-testid={`shared-session-context-part-${idx}-truncated`}
                      >
                        {part.totalLength != null
                          ? `showing ${part.text.length} of ${part.totalLength} characters`
                          : `showing ${part.text.length} characters (total length not measurable)`}
                      </p>
                    ) : null}
                    <ToggleRawView
                      text={part.text}
                      label="Shared system prompt"
                      rawTestId={`shared-session-context-part-${idx}-raw`}
                      copyTestId={`copy-shared-session-context-${idx}`}
                      viewPrefix={`shared-session-context-part-${idx}`}
                      defaultView={defaultView}
                    />
                  </div>
                ))}
              </div>
            </section>
          ) : null}

          <div className="flex items-center justify-between gap-2 flex-wrap">
            <span className="text-xs font-semibold text-foreground">
              Turn {data.turnIndex + 1}
              {data.model ? <span className="ml-2 font-mono text-[11px] font-normal text-tertiary-foreground">{data.model}</span> : null}
            </span>
            <CopyExactButton text={wholeTurnCopyText} testId="copy-turn-button" label="Copy Turn" />
          </div>

          {data.blocks.map((block) => (
            <TurnContentBlockView key={block.key} block={block} defaultView={defaultView} />
          ))}

          {orphanParts.length > 0 ? (
            <section data-testid="turn-additional-parts">
              <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
                Additional parts
              </h4>
              <div className="rounded border border-border bg-card p-3 overflow-hidden space-y-3">
                {orphanParts.map((part, idx) => (
                  <TurnContentPartView key={part.id ?? `${part.part}-${idx}`} part={part} index={idx} defaultView={defaultView} />
                ))}
              </div>
            </section>
          ) : null}

          {data.blocks.length === 0 && orphanParts.length === 0 ? (
            <p className="text-xs text-tertiary-foreground italic" data-testid="turn-content-empty">
              session {data.sessionId} · turn {data.turnIndex}
              {data.spanId ? ` · span ${data.spanId}` : ''} — the harness captured no unclipped content parts here.
            </p>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}

function FullContentPanel({
  request,
  fallback,
}: {
  request: SessionContentRequest
  fallback: unknown
}) {
  const { data, isError } = useQuery({
    queryKey: ['kyber-session-content', request.sessionId, request.span, request.part],
    queryFn: () =>
      fetchKyberSessionContent(request.sessionId, {
        span: request.span,
        part: request.part,
      }),
  })

  // SSR and a still-in-flight observer both arrive here with no data and no
  // error; isLoading is false on the server when fetchStatus is idle.
  if (!isError && data === undefined) {
    return (
      <div className="space-y-2" data-testid="drawer-content-loading">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-32 w-full" />
        <p className="text-xs text-tertiary-foreground">Loading full content…</p>
      </div>
    )
  }

  if (isError || data === undefined || data.parts.length === 0) {
    return (
      <div className="space-y-2">
        <p
          className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
          data-testid="drawer-content-fallback-note"
        >
          Showing truncated payload text — full content could not be loaded.
        </p>
        {fallback != null && fallback !== '' && (
          <div
            className="max-h-[min(60vh,32rem)] min-h-0 overflow-auto rounded border border-border"
            data-testid="drawer-content-scroll"
          >
            <div className="p-3">
              <InspectorContent data={fallback} />
            </div>
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="space-y-3">
      {data.parts.map((part, idx) => (
        <div key={`${part.spanId}-${part.part}-${idx}`} className="space-y-2">
          {part.truncated === true && (
            <p
              className="rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300"
              data-testid="drawer-content-truncated"
            >
              {part.totalLength != null
                ? `showing ${part.text.length} of ${part.totalLength} characters`
                : `showing ${part.text.length} characters (total length not measurable)`}
            </p>
          )}
          <div
            className="max-h-[min(60vh,32rem)] min-h-0 overflow-auto rounded border border-border"
            data-testid="drawer-content-scroll"
          >
            <div className="p-3">
              <InspectorContent data={part.text} />
            </div>
          </div>
        </div>
      ))}
    </div>
  )
}

/**
 * The drawer's own content union, shared with the dashboard that assembles
 * it; the body only spreads it and probes a legacy `content` leaf.
 */
export type { DrawerContent }

export interface SessionInspectorDrawerProps {
  open: boolean
  onClose: () => void
  title: string
  subtitle?: string
  children?: React.ReactNode
  rawContent?: DrawerContent
  contentRequest?: DrawerContentRequest
}

/**
 * Slide-out Inspector Drawer.
 * Slides in from right (z-50) with dark backdrop.
 * Closed via Escape key, close button ('✕'), or clicking backdrop.
 */
export function SessionInspectorDrawer({
  open,
  onClose,
  title,
  subtitle,
  children,
  rawContent,
  contentRequest,
}: SessionInspectorDrawerProps) {
  useEffect(() => {
    if (!open || typeof window === 'undefined') return
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [open, onClose])

  if (!open) return null

  // Drop the clipped leaf while the unclipped route is in play, otherwise the
  // 2000-char stub renders next to (or instead of) the text this route exists for.
  const rawRecord =
    rawContent !== null && rawContent !== undefined && typeof rawContent === 'object' ? rawContent : null
  const turnRequest = isTurnFullContentRequest(contentRequest) ? contentRequest : null
  // A whole-turn request carries its own panel (with the turn row as the
  // measured-totals source); the clipped payload row must not render
  // alongside the lossless content.
  const turnFallback =
    turnRequest !== null && rawRecord !== null && 'turn' in rawRecord
      ? (rawRecord as { turn?: TurnFullContentTurn }).turn ?? undefined
      : undefined
  const inspectorData =
    turnRequest !== null ? null : contentRequest && rawRecord ? { ...rawRecord, content: undefined } : rawContent
  const fallbackContent = rawRecord && 'content' in rawRecord ? rawRecord.content : undefined

  return (
    <div className="relative z-50">
      {/* Dark backdrop */}
      <div
        className="fixed inset-0 bg-black/60 backdrop-blur-[1px] transition-opacity"
        onClick={onClose}
        aria-hidden="true"
        data-testid="drawer-backdrop"
      />

      {/* Drawer panel sliding in from right */}
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={cn(
          'fixed top-0 right-0 bottom-0 z-50 flex flex-col',
          'w-full max-w-full sm:max-w-xl md:max-w-2xl',
          'bg-card border-l border-border shadow-2xl',
          'animate-in slide-in-from-right duration-200 ease-in-out'
        )}
        onClick={(e) => e.stopPropagation()}
        data-testid="session-inspector-drawer"
      >
        {/* Header */}
        <div className="flex items-center justify-between border-b border-border px-5 py-3.5 shrink-0 bg-card">
          <div className="min-w-0 flex-1 pr-3">
            <h2 className="text-sm font-semibold text-foreground truncate" data-testid="drawer-title">
              {title}
            </h2>
            {subtitle && (
              <p className="text-xs text-muted-foreground truncate mt-0.5" data-testid="drawer-subtitle">
                {subtitle}
              </p>
            )}
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <button
              type="button"
              onClick={onClose}
              aria-label="Close"
              className="flex h-8 w-8 items-center justify-center rounded-md border border-border text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground"
              data-testid="drawer-close-button"
            >
              ✕
            </button>
          </div>
        </div>

        {/* Scrollable Body */}
        <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs" data-testid="drawer-body">
          {children}
          {inspectorData !== undefined && inspectorData !== null && (
            <InspectorContent data={inspectorData} />
          )}
          {turnRequest !== null ? (
            <TurnFullContentPanel request={turnRequest} turn={turnFallback} />
          ) : null}
          {contentRequest && !isTurnFullContentRequest(contentRequest) && (
            <FullContentPanel request={contentRequest} fallback={fallbackContent} />
          )}
          {!children && (rawContent === undefined || rawContent === null) && !contentRequest && (
            <p className="text-xs text-tertiary-foreground italic py-8 text-center">
              No content to inspect.
            </p>
          )}
        </div>
      </div>
    </div>
  )
}

export default SessionInspectorDrawer
