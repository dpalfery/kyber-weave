import * as React from 'react'
import { useState, useMemo, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, fmtTokens } from '../lib/utils.js'
import {
  fetchTurnContent,
  fetchSpanAttributes,
  fetchQuarantineEntry,
  type KyberTurnContentResult,
  type KyberTurnContentBlock,
  type KyberTurnContentPart,
  type TurnTokenFigures,
} from '../lib/kyberApi.js'
import { Skeleton } from './ui/skeleton.js'
import { XmlFoldedText } from './SessionInspectorDrawer.js'
import { SearchableContentView, type ContentViewMode } from './ContentViewToolbar.js'

/**
 * Robust clipboard copy helper with fallback to document.execCommand('copy').
 * Ensures plain text copy works across all environments and headless tests.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  if (!text) return false
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // Fall back to execCommand below
  }
  try {
    if (typeof document !== 'undefined') {
      const textarea = document.createElement('textarea')
      textarea.value = text
      textarea.style.position = 'fixed'
      textarea.style.top = '0'
      textarea.style.left = '0'
      textarea.style.opacity = '0'
      textarea.style.pointerEvents = 'none'
      document.body.appendChild(textarea)
      textarea.focus()
      textarea.select()
      const successful = document.execCommand('copy')
      document.body.removeChild(textarea)
      return successful
    }
  } catch {
    return false
  }
  return false
}

/** True when at least one harness-measured counter is present (issue #184, Q3). */
function hasTurnTokenFigures(figures: TurnTokenFigures | undefined): boolean {
  if (!figures) return false
  return (
    figures.input != null || figures.output != null || figures.fresh != null || figures.cacheRead != null
  )
}

export interface CopyButtonProps {
  text: string
  label?: string
  copiedLabel?: string
  title?: string
  className?: string
  testId?: string
  onClick?: () => void
  disabled?: boolean
}

/**
 * Plain text copy button with visual copy feedback.
 */
export function CopyButton({
  text,
  label = 'Copy',
  copiedLabel = 'Copied!',
  title,
  className,
  testId = 'copy-button',
  onClick,
  disabled = false,
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false)

  const handleCopy = useCallback(
    async (e: React.MouseEvent) => {
      e.stopPropagation()
      if (disabled || !text) return
      const ok = await copyToClipboard(text)
      if (ok) {
        setCopied(true)
        onClick?.()
        setTimeout(() => setCopied(false), 2000)
      }
    },
    [text, disabled, onClick]
  )

  return (
    <button
      type="button"
      onClick={handleCopy}
      disabled={disabled || !text}
      title={title ?? (copied ? copiedLabel : label)}
      aria-label={title ?? label}
      data-testid={testId}
      className={cn(
        'inline-flex items-center justify-center gap-1.5 rounded border border-border px-2.5 py-1 text-xs font-medium transition-colors select-none',
        copied
          ? 'bg-primary/15 border-primary/40 text-primary font-semibold'
          : 'bg-interactive-secondary text-muted-foreground hover:bg-interactive-secondary/80 hover:text-foreground',
        (disabled || !text) && 'opacity-50 cursor-not-allowed',
        className
      )}
    >
      {copied ? (
        <>
          <span role="img" aria-label="copied" className="text-emerald-500 font-bold">✓</span>
          <span>{copiedLabel}</span>
        </>
      ) : (
        <>
          <span role="img" aria-label="copy" className="opacity-70">📋</span>
          <span>{label}</span>
        </>
      )}
    </button>
  )
}

export interface BlockRailProps {
  blocks: KyberTurnContentBlock[]
  activeBlockKey: string
  onSelectBlock: (key: string) => void
  className?: string
}

/**
 * Rail / selector for context composition blocks.
 */
export function BlockRail({
  blocks,
  activeBlockKey,
  onSelectBlock,
  className,
}: BlockRailProps) {
  return (
    <div
      className={cn('flex flex-col gap-1 border-r border-border p-2 min-w-[160px] sm:min-w-[190px]', className)}
      data-testid="block-rail"
      role="navigation"
      aria-label="Context Blocks"
    >
      <div className="px-2 py-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
        Context Blocks
      </div>

      <button
        type="button"
        onClick={() => onSelectBlock('all')}
        data-testid="block-item-all"
        className={cn(
          'flex items-center justify-between rounded px-2.5 py-1.5 text-xs text-left font-medium transition-colors',
          activeBlockKey === 'all'
            ? 'bg-primary/15 text-primary font-semibold border border-primary/30'
            : 'text-muted-foreground hover:bg-interactive-secondary hover:text-foreground'
        )}
      >
        <span>Whole Turn</span>
        <span className="text-[10px] text-tertiary-foreground font-mono">All</span>
      </button>

      {blocks.map((b) => {
        const isSelected = activeBlockKey === b.key
        const isNotMeasurable = Boolean(b.notMeasurable)
        return (
          <button
            key={b.key}
            type="button"
            onClick={() => onSelectBlock(b.key)}
            data-testid={`block-item-${b.key}`}
            className={cn(
              'flex items-center justify-between rounded px-2.5 py-1.5 text-xs text-left font-medium transition-colors group',
              isSelected
                ? 'bg-primary/15 text-primary font-semibold border border-primary/30'
                : 'text-muted-foreground hover:bg-interactive-secondary hover:text-foreground'
            )}
          >
            <span className="truncate pr-1">{b.label}</span>
            {isNotMeasurable ? (
              <span className="shrink-0 rounded bg-muted/80 px-1 py-0.5 text-[9px] text-muted-foreground font-mono">
                n/a
              </span>
            ) : b.tokens != null && b.tokens > 0 ? (
              <span className="shrink-0 font-mono text-[10px] text-tertiary-foreground">
                {fmtTokens(b.tokens)}
              </span>
            ) : b.text ? (
              <span className="shrink-0 font-mono text-[10px] text-tertiary-foreground">
                {b.text.length > 1000 ? `${(b.text.length / 1000).toFixed(1)}k` : `${b.text.length}c`}
              </span>
            ) : (
              <span className="shrink-0 text-[10px] text-tertiary-foreground italic">empty</span>
            )}
          </button>
        )
      })}
    </div>
  )
}

export interface PartTabsProps {
  parts: KyberTurnContentPart[]
  activePartId: string
  onSelectPart: (id: string) => void
  className?: string
}

/**
 * Tabs for selecting individual parts (system prompt, tools, user messages, assistant turns, etc.).
 */
export function PartTabs({
  parts,
  activePartId,
  onSelectPart,
  className,
}: PartTabsProps) {
  if (parts.length <= 1) return null

  return (
    <div
      className={cn('flex flex-wrap items-center gap-1 border-b border-border pb-2', className)}
      role="tablist"
      data-testid="part-tabs"
      aria-label="Context Parts"
    >
      <button
        type="button"
        role="tab"
        aria-selected={activePartId === 'all'}
        onClick={() => onSelectPart('all')}
        data-testid="part-tab-all"
        className={cn(
          'rounded px-2.5 py-1 text-xs font-medium transition-colors select-none',
          activePartId === 'all'
            ? 'bg-card text-foreground shadow-sm border border-border font-semibold'
            : 'text-muted-foreground hover:bg-interactive-secondary hover:text-foreground'
        )}
      >
        All ({parts.length})
      </button>

      {parts.map((p) => {
        const isSelected = activePartId === (p.id ?? p.part)
        return (
          <button
            key={p.id ?? p.part}
            type="button"
            role="tab"
            aria-selected={isSelected}
            onClick={() => onSelectPart(p.id ?? p.part)}
            data-testid={`part-tab-${p.part}`}
            className={cn(
              'rounded px-2.5 py-1 text-xs font-medium transition-colors select-none flex items-center gap-1.5',
              isSelected
                ? 'bg-card text-foreground shadow-sm border border-border font-semibold'
                : 'text-muted-foreground hover:bg-interactive-secondary hover:text-foreground'
            )}
          >
            <span>{p.label}</span>
            {p.server && (
              <span className="rounded bg-interactive-secondary px-1 text-[10px] text-tertiary-foreground font-mono">
                {p.server}
              </span>
            )}
          </button>
        )
      })}
    </div>
  )
}

/**
 * Where an empty block sits, so the empty state can name it (issue #184).
 * `turnIndex` is the 0-based transport index the content route resolved.
 */
export interface ContentPaneContext {
  sessionId?: string
  turnIndex?: number
  spanId?: string
}

export interface ContentPaneProps {
  text: string
  label?: string
  truncated?: boolean
  totalLength?: number
  notMeasurable?: { reason: string }
  context?: ContentPaneContext
  className?: string
}

/**
 * Content display pane with unclipped text, budget clipping indicators,
 * not_measurable handling, and XML folding.
 */
export function ContentPane({
  text,
  label,
  truncated = false,
  totalLength,
  notMeasurable,
  context,
  className,
}: ContentPaneProps) {
  const [viewMode, setViewMode] = useState<ContentViewMode>('formatted')

  // Decision D4: Where content is not measurable, state the reason and show nothing.
  // Never render a placeholder that could be mistaken for content.
  if (notMeasurable) {
    return (
      <div
        className={cn('rounded border border-border bg-card/60 p-4 space-y-2', className)}
        data-testid="not-measurable-pane"
      >
        <div className="flex items-center gap-2 text-amber-600 dark:text-amber-400 font-semibold text-xs">
          <span role="img" aria-label="notice">ℹ️</span>
          <span>Not Measurable</span>
        </div>
        <p className="text-xs text-muted-foreground leading-relaxed">
          {notMeasurable.reason}
        </p>
      </div>
    )
  }

  if (!text) {
    // Issue #184: a turn that resolved but recorded no parts names where it
    // sits (session, 0-based turn, span) so an audit can tell an honest data
    // gap from a mis-resolved neighbor. Without that context the legacy copy
    // stands, unchanged.
    const whereabouts =
      context && (context.sessionId !== undefined || context.turnIndex !== undefined || context.spanId !== undefined)
        ? [
            context.sessionId ? `session ${context.sessionId}` : null,
            context.turnIndex !== undefined ? `turn ${context.turnIndex}` : null,
            context.spanId ? `span ${context.spanId}` : null,
          ]
            .filter((part): part is string => part !== null)
            .join(' · ')
        : null
    return (
      <div
        className={cn('rounded border border-border bg-card/40 p-8 text-center text-xs text-tertiary-foreground italic', className)}
        data-testid="empty-content-pane"
      >
        <p>No content recorded for this block.</p>
        {whereabouts ? (
          <p className="mt-1 font-mono not-italic text-[11px]">
            {whereabouts} — the harness captured no unclipped content parts here.
          </p>
        ) : null}
      </div>
    )
  }

  return (
    <div className={cn('space-y-3', className)} data-testid="context-content-pane">
      {/* Decision D14: Budget clipping banner if limits apply */}
      {truncated && (
        <div
          className="rounded border border-amber-500/30 bg-amber-500/10 px-3.5 py-2 text-xs text-amber-700 dark:text-amber-300 flex items-center justify-between"
          data-testid="budget-truncated-banner"
        >
          <div className="flex items-center gap-2">
            <span role="img" aria-label="warning">⚠️</span>
            <span>
              {totalLength != null
                ? `Showing ${text.length.toLocaleString()} of ${totalLength.toLocaleString()} characters (length limit applied)`
                : `Showing ${text.length.toLocaleString()} characters (truncated by response budget)`}
            </span>
          </div>
          <span className="font-mono text-[10px] uppercase font-semibold text-amber-600 dark:text-amber-400">
            Truncated
          </span>
        </div>
      )}

      {/* Shared Formatted/Raw toolbar with search and exact Copy */}
      <SearchableContentView
        view={viewMode}
        onViewChange={setViewMode}
        text={text}
        label={label}
        viewPrefix="context-content-pane"
        leading={
          <span className="text-[11px] font-mono text-tertiary-foreground">
            {label ? `${label} · ` : ''}{text.length.toLocaleString()} chars
          </span>
        }
        copy={{ text, testId: 'context-content-pane-copy' }}
        formatted={<XmlFoldedText text={text} className="text-xs font-mono" />}
        raw={
          <pre className="text-xs font-mono whitespace-pre-wrap break-words text-foreground">
            {text}
          </pre>
        }
        bodyClassName="rounded border border-border bg-card p-3 max-h-[min(65vh,36rem)] min-h-[160px] overflow-auto"
      />
    </div>
  )
}

/**
 * The packet a quarantine row kept, pretty-printed when it is a JSON document
 * and shown verbatim when it is not. The row stores the sender's decoded packet
 * as text, so this is presentation of one served string — never a re-derivation
 * of it, and never a reason to hide a payload that will not parse.
 */
function decodeKeptPacket(rawPayload: string | null | undefined): string | null {
  if (typeof rawPayload !== 'string' || rawPayload.trim() === '') return null
  try {
    return JSON.stringify(JSON.parse(rawPayload), null, 2)
  } catch {
    return rawPayload
  }
}

export interface RawPacketDisclosureProps {
  /**
   * The request span whose attributes were captured. Fetched by id, not by the
   * served path: `fetchSpanAttributes` builds `/api/kyber/span/<id>/attributes`
   * itself, so handing it a path would nest a second one.
   */
  spanId?: string
  /** The turn record's parent span — where a quarantined packet is filed (B7). */
  parentSpanId?: string
  /** The pointers the server named. Absent means the server linked no packet. */
  rawPacket?: { spanAttributesPath: string; interactionPath?: string }
  className?: string
}

interface RawPacketPayloadProps {
  testId: string
  title: string
  value: unknown
  isPending: boolean
  error: unknown
  children?: React.ReactNode
}

/**
 * Character ceiling for one rendered packet. Two layers keep a multi-megabyte
 * payload affordable: the stringify replacer below bounds each string's
 * contribution before serialization, so no single multi-megabyte field is
 * ever materialized whole, and `capPacketText` bounds what is mounted. Past
 * the ceiling the render cuts and says so; the full payload stays served by
 * the API endpoint the disclosure fetched it from.
 */
const MAX_RENDERED_PACKET_CHARS = 100_000

/** Longest single string value the stringify materializes — longer values are cut here. */
const MAX_PACKET_STRING_CHARS = 1_000

/**
 * A JSON.stringify replacer that bounds the serialize cost at its worst
 * term: one multi-megabyte base64 blob would otherwise be walked into a
 * multi-megabyte string before any cap could look at it. Values beyond the
 * per-string ceiling are cut with a marker; the walk itself still visits
 * every node, but no unbounded string is ever built.
 */
function packetStringReplacer(_key: string, value: unknown): unknown {
  if (typeof value === 'string' && value.length > MAX_PACKET_STRING_CHARS) {
    return `${value.slice(0, MAX_PACKET_STRING_CHARS)}… truncated (${value.length} characters total; the full value is served by the API endpoint this disclosure fetched)`
  }
  return value
}

/** The capped form of one packet's text: whole below the ceiling, cut with a pointer past it. */
function capPacketText(text: string): string {
  if (text.length <= MAX_RENDERED_PACKET_CHARS) return text
  return `${text.slice(0, MAX_RENDERED_PACKET_CHARS)}\n… truncated at ${MAX_RENDERED_PACKET_CHARS} characters — the full packet is served by the API endpoint this disclosure fetched.`
}

/**
 * One fetched packet, rendered as served. Below the display ceiling nothing
 * is clipped — the tail of a payload is often the part that explains a turn,
 * so the body scrolls instead of being cut — and the stringify replacer plus
 * the mount cap bound the cost of rendering one that is not small.
 */
function RawPacketPayload({ testId, title, value, isPending, error, children }: RawPacketPayloadProps) {
  return (
    <div className="space-y-1" data-testid={testId}>
      <div className="text-[10px] font-semibold uppercase tracking-wider text-tertiary-foreground">{title}</div>
      {isPending ? (
        <p className="text-xs italic text-tertiary-foreground">Loading packet…</p>
      ) : error ? (
        <p className="text-xs text-red-600 dark:text-red-400">
          {error instanceof Error ? error.message : 'Unable to load this packet.'}
        </p>
      ) : (
        <pre className="max-h-[min(50vh,28rem)] overflow-auto rounded border border-border bg-muted/40 p-2 text-[11px] font-mono whitespace-pre-wrap break-words text-foreground">
          {capPacketText(JSON.stringify(value, packetStringReplacer, 2))}
        </pre>
      )}
      {children}
    </div>
  )
}

/**
 * The turn's raw packets, on demand (B7).
 *
 * Two payloads sit behind this disclosure: the request span's harness-emitted
 * attributes, and — only when the server named the link, which it does only when
 * the quarantine row actually kept a payload — the parent interaction packet that
 * was quarantined out of the canonical store. Both are large, so nothing is
 * requested until the disclosure is opened. What arrives is rendered as served:
 * which links exist was decided by the server, and this surface decides nothing
 * (ADR 0033, the display-layer rule).
 */
export function RawPacketDisclosure({ spanId, parentSpanId, rawPacket, className }: RawPacketDisclosureProps) {
  const [open, setOpen] = useState(false)

  // `enabled: open` is the whole of "closed fetches nothing". A turn view that
  // pulled two packets per turn to fill a collapsed header would pay for evidence
  // nobody asked to see.
  const spanAttributes = useQuery({
    queryKey: ['kyber-span-attributes', spanId],
    queryFn: () => fetchSpanAttributes(spanId!),
    enabled: open && Boolean(spanId),
  })

  // Only the link the server named: an absent `interactionPath` means the parent
  // row kept no payload, and a pointer to nothing is worse than none.
  const interactionPacket = useQuery({
    queryKey: ['kyber-quarantine-entry', parentSpanId],
    queryFn: () => fetchQuarantineEntry(parentSpanId!),
    enabled: open && Boolean(rawPacket?.interactionPath) && Boolean(parentSpanId),
  })

  // The server links packets only for a turn it resolved, so an absent
  // `rawPacket` is "this turn has none" — never a disclosure to render empty.
  if (!rawPacket) return null

  const keptPacket = decodeKeptPacket(interactionPacket.data?.rawPayload)

  return (
    <div className={cn('rounded border border-border bg-card/60', className)} data-testid="raw-packet-disclosure">
      <button
        type="button"
        data-testid="raw-packet-toggle"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full items-center gap-2 rounded px-3 py-2 text-left text-xs font-semibold text-muted-foreground transition-colors select-none hover:bg-interactive-secondary/50 hover:text-foreground"
      >
        <span aria-hidden="true" className={cn('font-mono text-[10px] transition-transform', open && 'rotate-90')}>
          ▶
        </span>
        <span>Raw packet</span>
        <span className="ml-auto font-mono text-[10px] font-normal text-tertiary-foreground">
          {open ? 'Hide' : 'Show'}
        </span>
      </button>

      {open && (
        <div className="space-y-3 border-t border-border p-3">
          <RawPacketPayload
            testId="raw-packet-span-attributes"
            title="Span attributes"
            value={spanAttributes.data}
            isPending={spanAttributes.isPending}
            error={spanAttributes.error}
          />

          {rawPacket.interactionPath ? (
            <RawPacketPayload
              testId="raw-packet-interaction"
              title="Quarantined parent packet, as filed"
              value={interactionPacket.data}
              isPending={interactionPacket.isPending}
              error={interactionPacket.error}
            >
              {keptPacket !== null ? (
                <div className="space-y-1">
                  <div className="text-[10px] font-semibold uppercase tracking-wider text-tertiary-foreground">
                    Packet as sent by the harness
                  </div>
                  <pre className="max-h-[min(50vh,28rem)] overflow-auto rounded border border-border bg-muted/40 p-2 text-[11px] font-mono whitespace-pre-wrap break-words text-foreground">
                    {capPacketText(keptPacket)}
                  </pre>
                </div>
              ) : null}
            </RawPacketPayload>
          ) : null}
        </div>
      )}
    </div>
  )
}

export interface ContextInspectorProps {
  sessionId?: string
  turnIndex?: number
  initialBlockKey?: string
  initialPart?: string
  data?: KyberTurnContentResult
  turnTokens?: TurnTokenFigures
  onClose?: () => void
  className?: string
}

/**
 * Context Inspector — unclipped block- and turn-level inspection with plain-text
 * copy-out. Spine entry is TurnDetail (docs/plans/2026-09-06-kyberdash-spine.md § B3).
 */
export function ContextInspector({
  sessionId,
  turnIndex = 0,
  initialBlockKey,
  initialPart,
  data: directData,
  turnTokens,
  onClose,
  className,
}: ContextInspectorProps) {
  // Query backend unclipped assembled turn content if not passed directly
  const { data: queriedData, isLoading, isError, error } = useQuery({
    queryKey: ['kyber-turn-content', sessionId, turnIndex],
    queryFn: () => fetchTurnContent(sessionId!, turnIndex),
    enabled: !directData && Boolean(sessionId) && turnIndex != null && turnIndex >= 0,
  })

  const turnData = directData ?? queriedData

  // State: selected block in BlockRail ('all' or canonical block key like 'system_prompt')
  const [selectedBlockKey, setSelectedBlockKey] = useState<string>(() => initialBlockKey ?? 'all')

  // State: selected part in PartTabs ('all' or part id)
  const [selectedPartId, setSelectedPartId] = useState<string>(() => initialPart ?? 'all')

  // Synchronize when initialBlockKey changes externally
  React.useEffect(() => {
    if (initialBlockKey) {
      setSelectedBlockKey(initialBlockKey)
      setSelectedPartId('all')
    }
  }, [initialBlockKey])

  // Active block
  const activeBlock = useMemo(() => {
    if (!turnData || selectedBlockKey === 'all') return null
    return turnData.blocks.find((b) => b.key === selectedBlockKey) ?? null
  }, [turnData, selectedBlockKey])

  // Available parts under currently selected block or whole turn
  const availableParts = useMemo(() => {
    if (!turnData) return []
    if (activeBlock) {
      return activeBlock.parts
    }
    return turnData.parts
  }, [turnData, activeBlock])

  // Active part
  const activePart = useMemo(() => {
    if (!availableParts || selectedPartId === 'all') return null
    return availableParts.find((p) => (p.id ?? p.part) === selectedPartId || p.part === selectedPartId) ?? null
  }, [availableParts, selectedPartId])

  // Displayed text, label, and budget status
  const displayedContent = useMemo(() => {
    if (!turnData) {
      return { text: '', label: '', truncated: false, totalLength: undefined, notMeasurable: undefined, context: undefined }
    }

    // Where this pane sits, so an empty block can name it (issue #184).
    const context = {
      sessionId,
      turnIndex: turnData.turnIndex,
      spanId: turnData.spanId,
    }

    // 1. Single part selected
    if (activePart) {
      return {
        text: activePart.text,
        label: activePart.label,
        truncated: activePart.truncated ?? false,
        totalLength: activePart.totalLength,
        notMeasurable: undefined,
        context,
      }
    }

    // 2. Single block selected
    if (activeBlock) {
      return {
        text: activeBlock.text,
        label: activeBlock.label,
        truncated: activeBlock.truncated ?? false,
        totalLength: activeBlock.totalLength,
        notMeasurable: activeBlock.notMeasurable,
        context,
      }
    }

    // 3. Whole turn assembled
    return {
      text: turnData.assembledText,
      label: 'Whole Turn Assembled Context',
      truncated: turnData.truncated ?? false,
      totalLength: turnData.totalLength,
      notMeasurable: undefined,
      context,
    }
  }, [turnData, activeBlock, activePart, sessionId])

  // Block-level copy text
  const blockCopyText = useMemo(() => {
    if (activeBlock) return activeBlock.text
    return turnData?.assembledText ?? ''
  }, [activeBlock, turnData])

  // Loading state
  if (isLoading) {
    return (
      <div className={cn('p-4 space-y-3', className)} data-testid="context-inspector-loading">
        <Skeleton className="h-6 w-48" />
        <div className="flex gap-4">
          <Skeleton className="h-64 w-40" />
          <Skeleton className="h-64 flex-1" />
        </div>
      </div>
    )
  }

  // Error state
  if (isError || !turnData) {
    return (
      <div
        className={cn('rounded border border-red-500/30 bg-red-500/10 p-4 text-xs text-red-600 dark:text-red-400 space-y-1.5', className)}
        data-testid="context-inspector-error"
      >
        <p className="font-semibold">Unable to load unclipped context.</p>
        <p className="text-muted-foreground">{error instanceof Error ? error.message : 'Session or turn content not found.'}</p>
      </div>
    )
  }

  return (
    <div className={cn('flex flex-col space-y-3', className)} data-testid="context-inspector">
      {/* Action Bar / Copy Controls */}
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border pb-3 bg-card/40">
        <div className="flex items-center gap-2">
          <span className="text-xs font-semibold text-foreground uppercase tracking-wide">
            {/* Issue #184: transport is 0-based; humans see 1-based. */}
            Turn {turnData.turnIndex + 1}
          </span>
          {turnData.model && (
            <span className="rounded bg-interactive-secondary px-2 py-0.5 text-[10px] font-mono text-muted-foreground">
              {turnData.model}
            </span>
          )}
          {hasTurnTokenFigures(turnTokens) ? (
            <span
              data-testid="turn-token-figures"
              title="Harness-measured token counters for this turn"
              className="rounded bg-interactive-secondary px-2 py-0.5 text-[10px] font-mono tabular-nums text-muted-foreground"
            >
              In {fmtTokens(turnTokens?.input)} · Out {fmtTokens(turnTokens?.output)} · Fresh{' '}
              {fmtTokens(turnTokens?.fresh)} · Cache {fmtTokens(turnTokens?.cacheRead)}
            </span>
          ) : (
            <span
              data-testid="turn-token-figures-absent"
              title="The harness recorded no per-turn token counters; nothing is estimated"
              className="rounded bg-interactive-secondary px-2 py-0.5 text-[10px] font-mono text-tertiary-foreground italic"
            >
              token counts not recorded
            </span>
          )}
        </div>

        <div className="flex items-center gap-2">
          {/* Block-level copy button */}
          <CopyButton
            text={blockCopyText}
            label={activeBlock ? `Copy ${activeBlock.label}` : 'Copy Block'}
            testId="copy-block-button"
            title="Copy plain text of the currently selected context block"
            disabled={!blockCopyText || Boolean(activeBlock?.notMeasurable)}
          />

          {/* Turn-level copy button */}
          <CopyButton
            text={turnData.assembledText}
            label="Copy Turn"
            testId="copy-turn-button"
            title="Copy whole turn assembled plain text with block headers"
            className="bg-primary/10 text-primary border-primary/30 hover:bg-primary/20"
            disabled={!turnData.assembledText}
          />

          {onClose && (
            <button
              type="button"
              onClick={onClose}
              aria-label="Close Inspector"
              data-testid="context-inspector-close-button"
              className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-interactive-secondary hover:text-foreground transition-colors ml-1"
            >
              ✕
            </button>
          )}
        </div>
      </div>

      {/* Main Inspector Layout: Block Rail on left, Parts & Content on right */}
      <div className="flex flex-col sm:flex-row gap-3 min-h-[300px]">
        {/* Block Rail */}
        <BlockRail
          blocks={turnData.blocks}
          activeBlockKey={selectedBlockKey}
          onSelectBlock={(k) => {
            setSelectedBlockKey(k)
            setSelectedPartId('all')
          }}
          className="shrink-0"
        />

        {/* Content & Part Tabs Column */}
        <div className="flex-1 min-w-0 space-y-3">
          {/* Part Tabs */}
          <PartTabs
            parts={availableParts}
            activePartId={selectedPartId}
            onSelectPart={setSelectedPartId}
          />

          {/* Content Pane */}
          <ContentPane
            text={displayedContent.text}
            label={displayedContent.label}
            truncated={displayedContent.truncated}
            totalLength={displayedContent.totalLength}
            notMeasurable={displayedContent.notMeasurable}
            context={displayedContent.context}
          />
        </div>
      </div>

      {/* B7: the raw packets this turn's server response named, on demand. */}
      <RawPacketDisclosure
        spanId={turnData.spanId}
        parentSpanId={turnData.parentSpanId}
        rawPacket={turnData.rawPacket}
      />
    </div>
  )
}

export default ContextInspector
