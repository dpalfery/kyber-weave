function fmtTokens(n: number): string {
  if (n >= 1e9) return (n / 1e9).toFixed(2) + 'B'
  if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M'
  if (n >= 1e3) return (n / 1e3).toFixed(1) + 'K'
  return String(Math.round(n))
}

export type DerivedTokenProps = {
  count: number
  model: string
  derived: boolean
  /** Optional test id for assertions */
  testId?: string
}

/**
 * Wherever a token count has derived:true, render the count as a lower bound
 * with its model name (R4.6). A measured count renders plain.
 */
export function DerivedTokens({ count, model, derived, testId }: DerivedTokenProps) {
  if (!derived) {
    return <span data-testid={testId}>{fmtTokens(count)}</span>
  }
  return (
    <span data-testid={testId ?? 'derived-caveat'}>
      {fmtTokens(count)} <span className="text-tertiary-foreground">lower bound (model: {model})</span>
    </span>
  )
}
