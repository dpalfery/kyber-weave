/**
 * Scopes the popover to one harness, or to all of them.
 *
 * First in reading order (R8.1): everything below is scoped by it, so a reader
 * who does not check it first can misread every figure under it.
 */

import type { ContextReport } from '../../../../src/analysis/report/types.ts'

type Props = {
  report: ContextReport | null
  selected: string
  onSelect: (harness: string) => void
}

export function HarnessSelector({ report, selected, onSelect }: Props) {
  // The harnesses with data in the window, which is what the coverage block
  // reports. A harness with no session is still listed: its absence is a fact
  // about the window, not about the harness.
  const harnesses = report?.coverage?.harnesses ?? []

  return (
    <label className="harness-selector" data-testid="harness-selector">
      <span className="harness-selector__label">Harness</span>
      <select
        value={selected}
        onChange={(event) => onSelect(event.target.value)}
        data-testid="harness-select"
      >
        <option value="all">All harnesses</option>
        {harnesses.map((harness) => (
          <option key={harness.harness} value={harness.harness}>
            {harness.name} ({harness.sessionsInWindow})
          </option>
        ))}
      </select>
    </label>
  )
}
