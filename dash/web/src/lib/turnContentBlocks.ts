// Build the turn-content view — labelled parts, canonical blocks, and assembled text —
// from the parts the C# engine serves. The retired TypeScript server built these
// server-side; the C# engine serves the byte-budgeted parts alone so no text is
// duplicated on the wire, which makes this pure module the one place that owns the
// mapping. Ported from the retired server's mapper: the same labels, ids, block
// definitions, and assembly, with no recurrence, shared context, or skills — those
// need earlier turns' text and stay absent for consumers to render without.

import type { KyberTurnContentBlock, KyberTurnContentPart } from './kyberApi.js'

/**
 * One served content part as the engine's turn-content route serializes it: the
 * part key, its plain text, and the measured detail it carries. Labels, ids, and
 * block membership are derived here, never trusted off the wire.
 */
export interface TurnContentSourcePart {
  spanId?: string
  part: string
  text: string
  tokens?: number
  server?: string
  /** False on a tool definition offered without keeping it resident; absent means resident. */
  resident?: boolean
  /** True when the captured part's text was cut at the sender's size limit. */
  truncated?: boolean
}

/**
 * Splits a conversation-history payload into its user and assistant messages.
 * A harness emits history either as a JSON message array or as prefixed dialog
 * lines; both shapes are read, and text that is neither yields nothing.
 */
export function extractMessagesFromHistory(text: string): {
  userMessages: string[]
  assistantMessages: string[]
} {
  const userMessages: string[] = []
  const assistantMessages: string[] = []
  if (!text) return { userMessages, assistantMessages }

  let parsed: unknown = null
  try {
    parsed = JSON.parse(text)
  } catch {
    // The history is plain dialog text, not JSON; the prefixed-line pass below reads it.
  }

  if (Array.isArray(parsed)) {
    for (const msg of parsed) {
      if (!msg || typeof msg !== 'object') continue
      const record = msg as Record<string, unknown>
      const role = String(record.role || '').toLowerCase()
      let msgText = ''
      const content = record.content
      const textVal = record.text
      const partsVal = record.parts

      if (typeof content === 'string') {
        msgText = content
      } else if (typeof textVal === 'string') {
        msgText = textVal
      } else if (Array.isArray(partsVal)) {
        msgText = partsVal
          .map((pt: unknown) =>
            typeof pt === 'string'
              ? pt
              : typeof (pt as Record<string, unknown>)?.text === 'string'
                ? String((pt as Record<string, unknown>).text)
                : typeof (pt as Record<string, unknown>)?.content === 'string'
                  ? String((pt as Record<string, unknown>).content)
                  : JSON.stringify(pt),
          )
          .join('\n')
      } else if (content !== undefined && content !== null) {
        msgText = JSON.stringify(content, null, 2)
      }

      if (role === 'user') {
        if (msgText.trim()) userMessages.push(msgText.trim())
      } else if (role === 'assistant' || role === 'model') {
        if (msgText.trim()) assistantMessages.push(msgText.trim())
      }
    }
  }

  if (userMessages.length === 0 && assistantMessages.length === 0) {
    const lines = text.split('\n')
    let currentRole: 'user' | 'assistant' | null = null
    let currentChunk: string[] = []

    for (const line of lines) {
      const userMatch = line.match(/^(?:user|human):\s*(.*)$/i)
      const asstMatch = line.match(/^(?:assistant|model|bot):\s*(.*)$/i)

      if (userMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'user'
        currentChunk = userMatch[1] ? [userMatch[1]] : []
      } else if (asstMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'assistant'
        currentChunk = asstMatch[1] ? [asstMatch[1]] : []
      } else if (currentRole) {
        currentChunk.push(line)
      }
    }
    if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
    if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
  }

  return { userMessages, assistantMessages }
}

/**
 * Labels the served parts. Conversation history splits into user messages and
 * assistant turns with the source part's tokens divided proportionally; history
 * that yields no split emits the raw text as user messages and as conversation
 * history, so nothing served is dropped. Unknown kinds keep their key as a label.
 */
export function labelTurnContentParts(source: readonly TurnContentSourcePart[]): KyberTurnContentPart[] {
  const parts: KyberTurnContentPart[] = []
  let orderCounter = 0

  for (const p of source) {
    const spanId = p.spanId
    const tokens = p.tokens
    const server = p.server
    const truncated = p.truncated === true ? true : undefined

    if (p.part === 'system_prompt') {
      parts.push({
        id: `part-sys-${orderCounter++}`,
        spanId,
        part: 'system_prompt',
        label: 'System Prompt',
        text: p.text,
        tokens,
        server,
        truncated,
        order: orderCounter,
      })
    } else if (p.part === 'tool_definitions') {
      parts.push({
        id: `part-tool-${orderCounter++}`,
        spanId,
        part: 'tool_definitions',
        label: server ? `Tools (${server})` : 'Tools',
        text: p.text,
        tokens,
        server,
        truncated,
        order: orderCounter,
        ...(p.resident === false ? { resident: false } : {}),
      })
    } else if (p.part === 'instruction_context') {
      parts.push({
        id: `part-inst-${orderCounter++}`,
        spanId,
        part: 'instruction_context',
        label: 'Instruction Context',
        text: p.text,
        tokens,
        server,
        truncated,
        order: orderCounter,
      })
    } else if (p.part === 'tool_result_content') {
      parts.push({
        id: `part-toolres-${orderCounter++}`,
        spanId,
        part: 'tool_result_content',
        label: 'Tool Results',
        text: p.text,
        tokens,
        server,
        truncated,
        order: orderCounter,
      })
    } else if (p.part === 'conversation_history') {
      const { userMessages, assistantMessages } = extractMessagesFromHistory(p.text)
      // The served count is split by message share so the two parts sum to it
      // exactly: rounding each share alone would turn 1 token into 2.
      const userTokens = tokens
        ? Math.round(tokens * (userMessages.length / (userMessages.length + assistantMessages.length)))
        : undefined
      const assistantTokens = tokens && userTokens !== undefined ? tokens - userTokens : undefined
      if (userMessages.length > 0 || assistantMessages.length > 0) {
        if (userMessages.length > 0) {
          parts.push({
            id: `part-user-${orderCounter++}`,
            spanId,
            part: 'user_messages',
            label: 'User Messages',
            text: userMessages.join('\n\n'),
            tokens: userTokens,
            truncated,
            order: orderCounter,
          })
        }
        if (assistantMessages.length > 0) {
          parts.push({
            id: `part-asst-${orderCounter++}`,
            spanId,
            part: 'assistant_turns',
            label: 'Assistant Turns',
            text: assistantMessages.join('\n\n'),
            tokens: assistantTokens,
            truncated,
            order: orderCounter,
          })
        }
      } else {
        // History that carries no readable split stays whole: the raw text is both
        // the user messages and the conversation history, so the reader loses none
        // of the served bytes.
        parts.push({
          id: `part-user-${orderCounter++}`,
          spanId,
          part: 'user_messages',
          label: 'User Messages',
          text: p.text,
          tokens,
          truncated,
          order: orderCounter,
        })
        parts.push({
          id: `part-conv-${orderCounter++}`,
          spanId,
          part: 'conversation_history',
          label: 'Conversation History',
          text: p.text,
          // The same served text as the user part above: its tokens are counted
          // there once, so this copy carries none.
          tokens: undefined,
          truncated,
          order: orderCounter,
        })
      }
    } else {
      parts.push({
        id: `part-other-${orderCounter++}`,
        spanId,
        part: p.part,
        label: String(p.part).replace(/_/g, ' '),
        text: p.text,
        tokens,
        server,
        truncated,
        order: orderCounter,
      })
    }
  }

  return parts
}

/** The canonical context blocks, in rail order, with the part kinds each aggregates. */
const BLOCK_DEFS: ReadonlyArray<{ key: string; label: string; partKeys: string[] }> = [
  { key: 'system_prompt', label: 'System prompt', partKeys: ['system_prompt'] },
  { key: 'tool_definitions', label: 'Tools', partKeys: ['tool_definitions'] },
  { key: 'instruction_context', label: 'Instruction context', partKeys: ['instruction_context'] },
  { key: 'conversation_history', label: 'Conversation history', partKeys: ['conversation_history', 'user_messages', 'assistant_turns'] },
  { key: 'tool_result_content', label: 'Tool results', partKeys: ['tool_result_content'] },
]

/**
 * Builds the canonical blocks over the labelled parts. Every definition answers a
 * block, empty ones included, so the rail keeps its shape; block text joins member
 * texts with a blank line, and the block's token figure sums resident members — a
 * non-resident definition stays visible but adds no tokens. A conversation history
 * part whose text a user part already carries is the fallback's duplicate of the
 * same bytes and the block keeps it once.
 */
export function buildTurnContentBlocks(parts: readonly KyberTurnContentPart[]): KyberTurnContentBlock[] {
  const blocks: KyberTurnContentBlock[] = []
  for (const def of BLOCK_DEFS) {
    const matching = parts.filter((p) => def.partKeys.includes(p.part))
    const unique = matching.filter(
      (p) =>
        !(
          p.part === 'conversation_history' &&
          matching.some((other) => other.part === 'user_messages' && other.text === p.text)
        ),
    )
    const text = unique.map((p) => p.text).join('\n\n')
    // A zero sum is no figure: the block measured nothing resident, never zero tokens.
    const tokenSum = unique.reduce((sum, p) => sum + (p.resident === false ? 0 : (p.tokens ?? 0)), 0)

    blocks.push({
      key: def.key,
      label: def.label,
      parts: unique,
      text,
      tokens: tokenSum > 0 ? tokenSum : undefined,
    })
  }

  return blocks
}

/**
 * Whole-turn plain text: each non-blank block under an upper-cased heading of its
 * label, sections joined with a blank line. Blank blocks are dropped, and member
 * text is trimmed per section so headings sit flush against the content.
 */
export function assembleTurnContentText(blocks: readonly KyberTurnContentBlock[]): string {
  const sections: string[] = []
  for (const block of blocks) {
    if (block.text.trim()) {
      sections.push(`=== ${block.label.toUpperCase()} ===\n${block.text.trim()}`)
    }
  }

  return sections.join('\n\n')
}

/** The complete view the turn page renders: labelled parts, canonical blocks, and assembled text. */
export function buildTurnContentView(source: readonly TurnContentSourcePart[]): {
  parts: KyberTurnContentPart[]
  blocks: KyberTurnContentBlock[]
  assembledText: string
} {
  const parts = labelTurnContentParts(source)
  const blocks = buildTurnContentBlocks(parts)
  return { parts, blocks, assembledText: assembleTurnContentText(blocks) }
}
