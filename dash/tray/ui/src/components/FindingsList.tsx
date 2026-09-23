/**
 * The findings, at most three (R8.4).
 *
 * Three because the popover is glanced at, not read: a list that scrolls is a
 * list nobody finishes. The rest stay one click away in the dashboard.
 *
 * Recommendation text is rendered verbatim (R14.4). The engine writes it to be
 * acted on, and a popover that paraphrased it would be inventing advice.
 */

import type { ContextReport } from '../../../../src/analysis/report/types.ts'
import { tokens } from '../format'

/** R8.4. */
export const MAX_FINDINGS = 3

type Props = {
  report: ContextReport
  onOpenView: (view: string) => void
}

export function FindingsList({ report, onOpenView }: Props) {
  // Absent and empty both mean "nothing to act on"; the section is optional.
  const all = report.findings ?? []
  const findings = all.slice(0, MAX_FINDINGS)
  const hidden = all.length - findings.length

  if (findings.length === 0) {
    return (
      <section className="findings" data-testid="findings">
        <h2 className="findings__heading">Findings</h2>
        <p className="findings__empty" data-testid="findings-empty">
          Nothing to act on in this window.
        </p>
      </section>
    )
  }

  return (
    <section className="findings" data-testid="findings">
      <h2 className="findings__heading">Findings</h2>
      <ol className="findings__list">
        {findings.map((finding) => (
          <li key={finding.id} className="finding" data-testid="finding">
            <button
              type="button"
              className="finding__title"
              onClick={() => onOpenView(finding.view)}
              data-testid="open-finding"
            >
              {finding.title}
            </button>
            <p className="finding__recovery" data-testid="finding-recovery">
              {tokens(finding.recoverableTokens)} recoverable
            </p>
            <p className="finding__recommendation" data-testid="finding-recommendation">
              {finding.recommendation}
            </p>
          </li>
        ))}
      </ol>
      {hidden > 0 && (
        <p className="findings__more" data-testid="findings-more">
          {hidden} more in the dashboard
        </p>
      )}
    </section>
  )
}
