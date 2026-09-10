// Context Review Panel for KyberDash
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D8, Decision D10; ADR 0006, ADR 0011).
//
// Acceptance Criteria:
// 1. Decision D10 compliance: Explicit opt-in per invocation. Review is NEVER invoked automatically or during ingestion.
// 2. Shows payload preview and token size before user confirms and sends to provider.
// 3. Default is unconfigured (graceful notice with instructions on setting API key / endpoint, no error throws).
// 4. Decision D8 compliance: Recommendations strictly emphasize relocation, progressive disclosure, on-demand loading,
//    or tool deferral, and forbid advising deletion of skills or rules outright.
// 5. Output is strictly informational and is NEVER written into a `Finding` or `findings` table in SQLite.

import { useState, useId } from 'react'
import {
  requestContextReview,
  type KyberReviewRecommendation,
  type KyberReviewResult,
} from '../../lib/kyberApi.js'

export interface ReviewContextBlock {
  key?: string
  label?: string
  tokens?: number
  text: string
}

export interface ContextReviewPanelProps {
  /** The assembled context content to review */
  content: string
  /** Optional turn index */
  turnIndex?: number
  /** Optional session ID */
  sessionId?: string
  /** Optional structured blocks composing the turn */
  blocks?: ReviewContextBlock[]
  /** Harness name (e.g. 'claude-code', 'cursor', 'copilot') */
  harness?: string
  /** Model name */
  model?: string
  /** Initial provider selection */
  initialProvider?: string
  /** Custom review runner override (for testing or direct injection) */
  onRunReview?: (
    payload: {
      content: string
      blocks?: ReviewContextBlock[]
      sessionId?: string
      turnIndex?: number
      harness?: string
      model?: string
      focus?: string
    },
    options?: {
      provider?: string
      model?: string
      endpoint?: string
      apiKey?: string
    },
  ) => Promise<KyberReviewResult>
  /** Optional callback to close or dismiss panel */
  onClose?: () => void
  /** Optional custom CSS classes */
  className?: string
}

/**
 * Approximate token count using standard 1 token ~= 4 characters estimation.
 */
function estimateTokens(text: string): number {
  if (!text) return 0
  return Math.ceil(text.length / 4)
}

export function ContextReviewPanel({
  content,
  turnIndex,
  sessionId,
  blocks = [],
  harness,
  model,
  initialProvider = 'auto',
  onRunReview,
  onClose,
  className = '',
}: ContextReviewPanelProps) {
  const previewId = useId()
  const [provider, setProvider] = useState<string>(initialProvider)
  const [endpoint, setEndpoint] = useState<string>('')
  const [focus, setFocus] = useState<string>('')
  const [showPreview, setShowPreview] = useState<boolean>(false)
  const [loading, setLoading] = useState<boolean>(false)
  const [result, setResult] = useState<KyberReviewResult | null>(null)
  const [copied, setCopied] = useState<boolean>(false)

  const tokenEstimate = estimateTokens(content)
  const charCount = (content || '').length

  // Explicit opt-in handler (Decision D10 compliance: never invoked on mount or automatically)
  const handleExecuteReview = async () => {
    setLoading(true)
    setResult(null)

    const payload = {
      content,
      blocks,
      sessionId,
      turnIndex,
      harness,
      model,
      focus: focus.trim() || undefined,
    }

    const options = {
      provider: provider === 'auto' ? undefined : provider,
      endpoint: endpoint.trim() || undefined,
    }

    try {
      let res: KyberReviewResult
      if (onRunReview) {
        res = await onRunReview(payload, options)
      } else {
        res = await requestContextReview(payload, options)
      }
      setResult(res)
    } catch (err) {
      // Fallback graceful result without throwing unhandled exceptions
      setResult({
        status: 'error',
        source: 'model_review',
        provider,
        review: `Failed to execute review: ${err instanceof Error ? err.message : String(err)}`,
        error: err instanceof Error ? err.message : String(err),
        timestamp: new Date().toISOString(),
        inputTokens: tokenEstimate,
      })
    } finally {
      setLoading(false)
    }
  }

  const handleCopyReview = async () => {
    if (!result?.review) return
    try {
      await navigator.clipboard.writeText(result.review)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      // Ignore clipboard failure
    }
  }

  return (
    <div
      className={`rounded-lg border border-border bg-card p-5 shadow-sm space-y-5 text-card-foreground ${className}`}
      data-testid="context-review-panel"
    >
      {/* Header */}
      <div className="flex items-start justify-between gap-4 border-b border-border pb-4">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-base font-semibold tracking-tight">
              LLM Context Review
            </h3>
            <span className="rounded-full bg-primary/10 px-2 py-0.5 text-[11px] font-medium text-primary">
              Decision D10 Opt-In
            </span>
            <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
              Advisory Only
            </span>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            On-demand second-opinion diagnostic review. Never modifies canonical findings or runs automatically.
            Adheres strictly to Decision D8 non-destructive strategies (relocation, progressive disclosure, on-demand loading).
          </p>
        </div>

        {onClose && (
          <button
            type="button"
            onClick={onClose}
            aria-label="Close review panel"
            className="rounded p-1 text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            <svg className="h-4 w-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
      </div>

      {/* Payload Preview & Token Size Calculation (Acceptance Criterion 2) */}
      <div className="rounded-md border border-border/80 bg-muted/30 p-4 space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-center gap-3 text-xs">
            <span className="font-semibold text-foreground">Payload Size:</span>
            <span className="rounded bg-background px-2 py-0.5 font-mono text-[11px] font-medium border border-border">
              ~{tokenEstimate.toLocaleString()} tokens
            </span>
            <span className="text-muted-foreground">
              ({charCount.toLocaleString()} chars)
            </span>
            {turnIndex !== undefined && (
              <span className="text-muted-foreground">
                • Turn #{turnIndex}
              </span>
            )}
            {blocks.length > 0 && (
              <span className="text-muted-foreground">
                • {blocks.length} composition block{blocks.length === 1 ? '' : 's'}
              </span>
            )}
          </div>

          <button
            type="button"
            onClick={() => setShowPreview(!showPreview)}
            aria-expanded={showPreview}
            aria-controls={previewId}
            className="text-xs font-medium text-primary hover:underline flex items-center gap-1"
          >
            {showPreview ? 'Hide Payload Preview' : 'Inspect Payload Preview'}
            <svg
              className={`h-3 w-3 transition-transform ${showPreview ? 'rotate-180' : ''}`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
          </button>
        </div>

        {/* Collapsible Payload Inspection */}
        {showPreview && (
          <div id={previewId} className="mt-3 space-y-2 pt-2 border-t border-border/60">
            {blocks.length > 0 && (
              <div className="flex flex-wrap gap-1.5 pb-1">
                {blocks.map((b, i) => (
                  <span
                    key={b.key || i}
                    className="inline-flex items-center gap-1 rounded bg-background px-1.5 py-0.5 text-[10px] font-mono border border-border text-muted-foreground"
                  >
                    <span>{b.label || b.key || `Block ${i + 1}`}</span>
                    <span className="text-primary font-semibold">
                      ~{estimateTokens(b.text || '').toLocaleString()} t
                    </span>
                  </span>
                ))}
              </div>
            )}
            <div className="max-h-60 overflow-y-auto rounded bg-background p-3 font-mono text-[11px] text-muted-foreground border border-border whitespace-pre-wrap select-all">
              {content ? content.slice(0, 5000) : '<empty content>'}
              {content.length > 5000 && (
                <span className="block mt-2 italic text-primary">
                  ... [truncated in preview: {(content.length - 5000).toLocaleString()} more characters]
                </span>
              )}
            </div>
            <p className="text-[11px] text-muted-foreground italic">
              Decision D10 Guarantee: The exact text above will only leave this machine upon your confirmation.
            </p>
          </div>
        )}
      </div>

      {/* Controls & Configuration */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-3 text-xs">
        <div>
          <label htmlFor="review-provider-select" className="block font-medium text-muted-foreground mb-1">
            Review Provider
          </label>
          <select
            id="review-provider-select"
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            disabled={loading}
            className="w-full rounded border border-border bg-background px-2.5 py-1.5 text-xs focus:outline-none focus:ring-1 focus:ring-primary"
          >
            <option value="auto">Auto (Detect configured)</option>
            <option value="ollama">Local Ollama (Private / Local)</option>
            <option value="anthropic">Anthropic Claude (API Key)</option>
            <option value="openai">OpenAI (API Key)</option>
            <option value="mock">Mock Provider (Offline Test)</option>
          </select>
        </div>

        {provider === 'ollama' && (
          <div>
            <label htmlFor="review-endpoint-input" className="block font-medium text-muted-foreground mb-1">
              Ollama Endpoint
            </label>
            <input
              id="review-endpoint-input"
              type="text"
              placeholder="http://localhost:11434"
              value={endpoint}
              onChange={(e) => setEndpoint(e.target.value)}
              disabled={loading}
              className="w-full rounded border border-border bg-background px-2.5 py-1.5 text-xs focus:outline-none focus:ring-1 focus:ring-primary"
            />
          </div>
        )}

        <div className={provider === 'ollama' ? 'col-span-1' : 'col-span-2'}>
          <label htmlFor="review-focus-input" className="block font-medium text-muted-foreground mb-1">
            Diagnostic Focus (Optional)
          </label>
          <input
            id="review-focus-input"
            type="text"
            placeholder="e.g. Cache checkpoint stability, dormant tools, skill disclosure"
            value={focus}
            onChange={(e) => setFocus(e.target.value)}
            disabled={loading}
            className="w-full rounded border border-border bg-background px-2.5 py-1.5 text-xs focus:outline-none focus:ring-1 focus:ring-primary"
          />
        </div>
      </div>

      {/* Opt-In Trigger Button (Acceptance Criterion 1) */}
      <div className="flex items-center justify-between gap-4 pt-2">
        <div className="text-[11px] text-muted-foreground">
          Pressing the button below invokes an on-demand review for this specific turn.
        </div>

        <button
          type="button"
          onClick={handleExecuteReview}
          disabled={loading || !content.trim()}
          className="inline-flex items-center gap-2 rounded-md bg-primary px-4 py-2 text-xs font-semibold text-primary-foreground shadow hover:bg-primary/90 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {loading ? (
            <>
              <svg className="h-3.5 w-3.5 animate-spin" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
              </svg>
              <span>Running LLM Review...</span>
            </>
          ) : (
            <>
              <svg className="h-3.5 w-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
              </svg>
              <span>Run Context Review</span>
            </>
          )}
        </button>
      </div>

      {/* Unconfigured Notice (Acceptance Criterion 3) */}
      {result?.status === 'unconfigured' && (
        <div
          className="rounded-md border border-amber-500/30 bg-amber-500/10 p-4 space-y-3"
          data-testid="unconfigured-notice"
        >
          <div className="flex items-center gap-2 text-amber-800 dark:text-amber-200">
            <svg className="h-4 w-4 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <h4 className="text-xs font-semibold">LLM Review Provider Not Configured</h4>
          </div>
          <p className="text-xs text-muted-foreground leading-relaxed">
            KyberDash defaults to unconfigured per Decision D10 to ensure zero telemetry egress.
            To enable context reviews on-demand, configure one of the following:
          </p>
          <div className="rounded bg-background p-3 font-mono text-[11px] text-foreground border border-border/80 space-y-1">
            <p><strong>1. Local Ollama (private & local):</strong> Run <code>ollama run llama3.2</code> at http://localhost:11434</p>
            <p><strong>2. Anthropic Claude:</strong> Export <code>ANTHROPIC_API_KEY=sk-ant-...</code></p>
            <p><strong>3. OpenAI:</strong> Export <code>OPENAI_API_KEY=sk-...</code></p>
          </div>
        </div>
      )}

      {/* Error Notice */}
      {result?.status === 'error' && (
        <div className="rounded-md border border-destructive/30 bg-destructive/10 p-4 space-y-2">
          <div className="flex items-center gap-2 text-destructive">
            <svg className="h-4 w-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <h4 className="text-xs font-semibold">Context Review Error</h4>
          </div>
          <p className="text-xs text-destructive-foreground/90 font-mono">
            {result.error || result.review}
          </p>
        </div>
      )}

      {/* Review Output Display (Acceptance Criteria 4 & 5) */}
      {result?.status === 'completed' && (
        <div className="rounded-md border border-border bg-background p-4 space-y-4" data-testid="review-output">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border pb-3">
            <div className="flex items-center gap-2">
              <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[11px] font-semibold text-emerald-600 dark:text-emerald-400">
                Review Complete
              </span>
              <span className="text-xs font-medium text-muted-foreground">
                Provider: <strong className="text-foreground">{result.provider}</strong>
                {result.model && ` (${result.model})`}
              </span>
              <span className="text-[11px] text-muted-foreground">
                • {new Date(result.timestamp).toLocaleTimeString()}
              </span>
            </div>

            <button
              type="button"
              onClick={handleCopyReview}
              className="inline-flex items-center gap-1.5 rounded border border-border bg-card px-2.5 py-1 text-xs font-medium text-card-foreground hover:bg-muted transition-colors"
            >
              {copied ? (
                <>
                  <svg className="h-3.5 w-3.5 text-emerald-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                  </svg>
                  <span>Copied!</span>
                </>
              ) : (
                <>
                  <svg className="h-3.5 w-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                  </svg>
                  <span>Copy Review</span>
                </>
              )}
            </button>
          </div>

          {/* Explicit Informational / D5 Non-Finding Label (Acceptance Criterion 5) */}
          <div className="rounded bg-muted/40 p-2.5 text-[11px] text-muted-foreground flex items-center gap-2">
            <svg className="h-4 w-4 flex-shrink-0 text-primary" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span>
              <strong>Informational Output:</strong> This second opinion is strictly advisory and is NEVER written into canonical Findings tables or SQLite store.
            </span>
          </div>

          {/* Structured Recommendations (Decision D8) */}
          {result.recommendations && result.recommendations.length > 0 && (
            <div className="space-y-3 pt-2">
              <h4 className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">
                Recommendations (Adhering to Decision D8)
              </h4>
              <div className="grid grid-cols-1 gap-3">
                {result.recommendations.map((rec: KyberReviewRecommendation, idx: number) => (
                  <RecommendationCard key={idx} recommendation={rec} index={idx + 1} />
                ))}
              </div>
            </div>
          )}

          {/* Raw Markdown Output Fallback / Full Text */}
          <div className="pt-2">
            <h4 className="text-xs font-semibold tracking-wider text-muted-foreground uppercase mb-2">
              Complete Review Prose
            </h4>
            <div className="max-h-80 overflow-y-auto rounded border border-border/80 bg-muted/20 p-4 font-mono text-xs whitespace-pre-wrap leading-relaxed">
              {result.review}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * Recommendation card presenting a structured Decision D8 recommendation.
 */
function RecommendationCard({
  recommendation,
  index,
}: {
  recommendation: KyberReviewRecommendation
  index: number
}) {
  const getBadgeStyle = (type: KyberReviewRecommendation['type']) => {
    switch (type) {
      case 'relocate':
        return 'bg-sky-500/15 text-sky-700 dark:text-sky-300 border-sky-500/30'
      case 'progressive_disclosure':
        return 'bg-purple-500/15 text-purple-700 dark:text-purple-300 border-purple-500/30'
      case 'on_demand':
        return 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300 border-emerald-500/30'
      case 'defer':
        return 'bg-amber-500/15 text-amber-700 dark:text-amber-300 border-amber-500/30'
      default:
        return 'bg-muted text-muted-foreground border-border'
    }
  }

  return (
    <div className="rounded-md border border-border bg-card p-3.5 space-y-2.5">
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="text-xs font-bold text-muted-foreground">#{index}</span>
          <h5 className="text-xs font-semibold text-foreground">{recommendation.title}</h5>
        </div>
        <span
          className={`rounded border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider ${getBadgeStyle(
            recommendation.type,
          )}`}
        >
          {recommendation.strategy || recommendation.type}
        </span>
      </div>

      <div className="text-xs text-muted-foreground space-y-1.5">
        <div>
          <strong className="text-foreground">Suggested Action:</strong> {recommendation.suggestedAction}
        </div>
        {recommendation.outcomeRisk && (
          <div className="rounded bg-amber-500/10 p-2 border border-amber-500/20 text-amber-900 dark:text-amber-200">
            <strong>Outcome Risk Caveat:</strong> {recommendation.outcomeRisk}
          </div>
        )}
      </div>
    </div>
  )
}
