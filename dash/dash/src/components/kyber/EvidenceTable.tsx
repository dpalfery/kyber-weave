import { cn } from '../../lib/utils'
import { Card } from '../ui/card'
import type { KyberEvidenceLink } from '../../lib/kyberApi'

export interface EvidenceTableProps {
  evidenceLinks?: KyberEvidenceLink[]
  onSelectEvidence?: (link: KyberEvidenceLink) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, spanId?: string) => void
  onSelectSpan?: (spanId: string) => void
  className?: string
}

/**
 * Deep-linkable table of evidence rows resolving to span/turn per Decision D5.
 * Every finding carries >=2 evidence links connecting the diagnosis to concrete telemetry spans.
 */
export function EvidenceTable({
  evidenceLinks = [],
  onSelectEvidence,
  onSelectTurn,
  onSelectSpan,
  className,
}: EvidenceTableProps) {
  const handleInspect = (link: KyberEvidenceLink) => {
    onSelectEvidence?.(link)
    onSelectTurn?.(link.turnIndex, link.executionId, link.spanId)
    if (link.spanId) {
      onSelectSpan?.(link.spanId)
    }
  }

  return (
    <Card className={cn('p-5', className)} data-testid="evidence-table-container">
      <div className="flex flex-wrap items-baseline justify-between gap-2 border-b border-border/60 pb-3 mb-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Evidence Records & Deep Links
            </h3>
            <span
              className="rounded bg-interactive-secondary px-2 py-0.5 text-[10.5px] font-mono text-tertiary-foreground"
              data-testid="evidence-count-badge"
            >
              {evidenceLinks.length} record{evidenceLinks.length === 1 ? '' : 's'}
            </span>
          </div>
          <p className="mt-0.5 text-[11px] text-muted-foreground">
            Decision D5: Every finding requires ≥2 verifiable telemetry links resolving directly to the originating turn, span, or execution.
          </p>
        </div>
      </div>

      {evidenceLinks.length === 0 ? (
        <div
          data-testid="evidence-table-empty"
          className="my-4 rounded-lg border border-dashed border-border p-6 text-center text-xs text-muted-foreground"
        >
          <p className="font-medium text-foreground">No evidence links recorded</p>
          <p className="mt-1 text-[11px] text-tertiary-foreground">
            Telemetry links missing or finding lacks verifiable span references.
          </p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-xs" data-testid="evidence-table">
            <thead>
              <tr className="border-b border-border text-[10.5px] font-semibold text-tertiary-foreground uppercase tracking-wider">
                <th scope="col" className="py-2 px-2.5">Target Turn</th>
                <th scope="col" className="py-2 px-2.5">Span ID</th>
                <th scope="col" className="py-2 px-2.5">Execution</th>
                <th scope="col" className="py-2 px-2.5">Observation / Evidence</th>
                <th scope="col" className="py-2 px-2.5 text-right">Action</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40">
              {evidenceLinks.map((link, idx) => {
                const hasTurn = link.turnIndex !== undefined && link.turnIndex !== null
                const hasSpan = Boolean(link.spanId)
                const hasExec = Boolean(link.executionId)

                return (
                  <tr
                    key={`${link.spanId || 'evidence'}-${link.turnIndex}-${idx}`}
                    data-testid={`evidence-row-${idx}`}
                    className="hover:bg-interactive-secondary/30 transition-colors"
                  >
                    {/* Target Turn */}
                    <td className="py-2.5 px-2.5 whitespace-nowrap">
                      {hasTurn ? (
                        <button
                          type="button"
                          onClick={() => handleInspect(link)}
                          data-testid={`deep-link-turn-${link.turnIndex}`}
                          className="rounded bg-primary/10 border border-primary/20 px-2 py-0.5 font-mono text-xs font-semibold text-primary hover:bg-primary/20 transition-colors inline-flex items-center gap-1"
                          title={`Jump to turn #${link.turnIndex}`}
                        >
                          <span>Turn #{link.turnIndex}</span>
                        </button>
                      ) : (
                        <span className="text-tertiary-foreground font-mono">—</span>
                      )}
                    </td>

                    {/* Span ID */}
                    <td className="py-2.5 px-2.5 whitespace-nowrap">
                      {hasSpan ? (
                        <button
                          type="button"
                          onClick={() => handleInspect(link)}
                          data-testid={`deep-link-span-${link.spanId}`}
                          className="font-mono text-[11px] text-foreground hover:text-primary transition-colors underline decoration-dotted"
                          title={`Inspect telemetry span ${link.spanId}`}
                        >
                          span:{link.spanId.length > 12 ? `${link.spanId.slice(0, 10)}…` : link.spanId}
                        </button>
                      ) : (
                        <span className="text-tertiary-foreground font-mono">—</span>
                      )}
                    </td>

                    {/* Execution ID */}
                    <td className="py-2.5 px-2.5 whitespace-nowrap">
                      {hasExec ? (
                        <span className="rounded bg-interactive-secondary px-1.5 py-0.5 font-mono text-[10px] text-tertiary-foreground">
                          {link.executionId!.length > 8 ? link.executionId!.slice(0, 8) : link.executionId}
                        </span>
                      ) : (
                        <span className="text-tertiary-foreground font-mono">—</span>
                      )}
                    </td>

                    {/* Observation / Evidence Description */}
                    <td className="py-2.5 px-2.5 text-foreground/90">
                      <span className="line-clamp-2" title={link.description}>
                        {link.description}
                      </span>
                    </td>

                    {/* Deep Link Action */}
                    <td className="py-2.5 px-2.5 text-right whitespace-nowrap">
                      <button
                        type="button"
                        onClick={() => handleInspect(link)}
                        data-testid={`inspect-evidence-${idx}`}
                        className="rounded bg-interactive-secondary border border-border px-2.5 py-1 text-xs font-medium text-foreground hover:bg-primary hover:text-primary-foreground transition-colors inline-flex items-center gap-1.5"
                      >
                        <span>Inspect</span>
                        <span aria-hidden="true">→</span>
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}
