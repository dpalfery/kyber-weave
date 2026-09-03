import * as React from 'react'
import { useEffect, useMemo, useState } from 'react'
import { cn, usd, fmtTokens } from '../lib/utils'

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
 * Parses raw text into plain text chunks and recognized harness XML blocks.
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
 * Formats text containing harness XML blocks into collapsible <details> elements.
 */
export function XmlFoldedText({ text, className }: { text: string; className?: string }) {
  if (!text) return null
  const chunks = parseXmlFoldChunks(text)

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

        const trimmedInner = chunk.content.trim()
        const preview = trimmedInner.slice(0, 80).replace(/\s+/g, ' ')
        const hasMore = trimmedInner.length > 80

        return (
          <details
            key={i}
            className="group border border-border rounded my-1 bg-card/60 overflow-hidden"
            data-testid={`folded-tag-${chunk.tag}`}
          >
            <summary className="cursor-pointer px-3 py-1.5 text-xs text-muted-foreground hover:text-foreground flex items-center gap-2 select-none hover:bg-interactive-secondary/50 transition-colors">
              <span className="shrink-0 rounded bg-amber-500/15 border border-amber-500/30 px-1.5 py-0.5 font-mono text-[10px] font-semibold text-amber-600 dark:text-amber-400">
                &lt;{chunk.tag}&gt;
              </span>
              <span className="truncate font-mono text-[11px] text-tertiary-foreground flex-1">
                {preview}{hasMore ? '…' : ''}
              </span>
              <span className="text-[10px] text-tertiary-foreground font-mono transition-transform group-open:rotate-90">
                ▶
              </span>
            </summary>
            <pre className="text-xs font-mono p-2 overflow-x-auto whitespace-pre-wrap border-t border-border bg-muted/40 text-foreground">
              {trimmedInner}
            </pre>
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

function formatDuration(ms?: number | null): string {
  if (ms == null || !isFinite(ms)) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const minutes = Math.floor(ms / 60000)
  const seconds = Math.round((ms % 60000) / 1000)
  return `${minutes}m ${seconds}s`
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
    if (XML_FOLD_TAGS.some(t => parsed.includes(`<${t}`))) {
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
 * Tool call component: displays wrench icon, tool name, and parameters.
 */
export function ToolCallView({ call, className }: { call: any; className?: string }) {
  const src = call?.raw ?? call ?? {}
  const toolName = src.name ?? src.tool ?? src.tool_name ?? 'tool_call'
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
export function ToolResultView({ result, className }: { result: any; className?: string }) {
  const src = result?.raw !== undefined ? result.raw : (result?.response !== undefined ? result.response : (result?.result !== undefined ? result.result : (result?.output !== undefined ? result.output : result)))
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
          XML_FOLD_TAGS.some(t => src.includes(`<${t}`)) ? (
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
export function MessagePartView({ part }: { part: any }) {
  if (!part) return null
  if (typeof part === 'string') {
    return <XmlFoldedText text={part} />
  }

  const type = part.type

  if (type === 'text') {
    const text = part.content !== undefined ? part.content : (part.text ?? '')
    return <XmlFoldedText text={text} />
  }

  if (type === 'reasoning' || type === 'thinking') {
    const rawReasoning = part.content ?? part.text ?? (part.raw && (part.raw.content || part.raw.text)) ?? part.raw ?? part
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
function MessageView({ message }: { message: any }) {
  const role = message.role ?? 'unknown'
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
        {message.name && (
          <span className="text-[11px] font-mono text-tertiary-foreground">{message.name}</span>
        )}
      </div>
      <div className="space-y-1.5">
        {parts ? (
          parts.map((part: any, idx: number) => <MessagePartView key={idx} part={part} />)
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
 * Turn Inspector rendering token spend, minibar, model, and message content.
 */
function TurnInspector({ turn }: { turn: any }) {
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

  const content = turn.content ?? {}
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
          {inMsgs.map((m: any, idx: number) => (
            <MessageView key={idx} message={m} />
          ))}
        </div>
      )}

      {/* Prompt text (flattened harness) */}
      {!inMsgs && content.prompt_text && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Prompt (flattened)
          </h4>
          <div className="rounded border border-border bg-card p-3">
            <XmlFoldedText text={content.prompt_text} />
          </div>
        </div>
      )}

      {/* Output Messages */}
      {Array.isArray(outMsgs) && outMsgs.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Model Response
          </h4>
          {outMsgs.map((m: any, idx: number) => (
            <MessageView key={idx} message={m} />
          ))}
        </div>
      )}

      {/* Response text (flattened harness) */}
      {!outMsgs && content.response_text && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Model Response
          </h4>
          <div className="rounded border border-border bg-card p-3">
            <XmlFoldedText text={content.response_text} />
          </div>
        </div>
      )}

      {/* Reasoning text */}
      {(content.reasoning_text || content.thinking_text) && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            {content.thinking_text && !content.reasoning_text ? 'Thinking' : 'Reasoning'}
          </h4>
          <MessagePartView part={{ type: content.thinking_text && !content.reasoning_text ? 'thinking' : 'reasoning', content: content.reasoning_text || content.thinking_text }} />
        </div>
      )}

      {/* Tool Calls */}
      {Array.isArray(turn.tool_calls) && turn.tool_calls.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground mb-1.5">
            Tool Calls ({turn.tool_calls.length})
          </h4>
          {turn.tool_calls.map((tc: any, idx: number) => (
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
function SpanInspector({ node }: { node: any }) {
  const attrs = node.attributes ?? {}
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
 * Context Bucket Inspector.
 */
function ContextBucketInspector({ item }: { item: any }) {
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

      {item.content && (
        <div className="space-y-2">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">Bucket Content</h4>
          <InspectorContent data={item.content} />
        </div>
      )}
    </div>
  )
}

/**
 * Helper component InspectorContent to format turn messages, timeline span attributes, and raw JSON.
 */
export function InspectorContent({ data }: { data: any }) {
  const [viewMode, setViewMode] = useState<'formatted' | 'raw'>('formatted')
  const [copied, setCopied] = useState(false)

  if (data === null || data === undefined) {
    return <div className="text-xs text-tertiary-foreground italic py-4 text-center">No data to display.</div>
  }

  // String handling
  if (typeof data === 'string') {
    return <XmlFoldedText text={data} />
  }

  // Handle Turn payload
  if (typeof data === 'object' && (data.fresh !== undefined || data.cache_read !== undefined || (data.index !== undefined && data.tokens !== undefined))) {
    return <TurnInspector turn={data} />
  }

  // Handle Timeline Span Node
  if (typeof data === 'object' && ('spanId' in data || ('name' in data && 'attributes' in data))) {
    return <SpanInspector node={data} />
  }

  // Handle Context bucket item
  if (typeof data === 'object' && ('bucket' in data || ('key' in data && 'total' in data))) {
    return <ContextBucketInspector item={data} />
  }

  // Handle Tool Call
  if (typeof data === 'object' && (data.type === 'tool_call' || (data.name && (data.arguments !== undefined || data.input !== undefined)))) {
    return <ToolCallView call={data} />
  }

  // Handle Tool Result
  if (typeof data === 'object' && (data.type === 'tool_result' || data.type === 'tool_call_response' || data.type === 'tool_call_result' || data.result !== undefined || data.output !== undefined)) {
    return <ToolResultView result={data} />
  }

  // Handle Message
  if (typeof data === 'object' && ('role' in data && ('content' in data || 'parts' in data))) {
    return <MessageView message={data} />
  }

  // Handle array of messages
  if (Array.isArray(data) && data.length > 0 && typeof data[0] === 'object' && ('role' in data[0] || 'parts' in data[0])) {
    return (
      <div className="space-y-2">
        {data.map((m: any, idx: number) => (
          <MessageView key={idx} message={m} />
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

export interface SessionInspectorDrawerProps {
  open: boolean
  onClose: () => void
  title: string
  subtitle?: string
  children?: React.ReactNode
  rawContent?: any
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

        {/* Scrollable Body */}
        <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs" data-testid="drawer-body">
          {children}
          {rawContent !== undefined && rawContent !== null && (
            <InspectorContent data={rawContent} />
          )}
          {!children && (rawContent === undefined || rawContent === null) && (
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
