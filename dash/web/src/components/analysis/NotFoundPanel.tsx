import type { ReactNode } from 'react'

export interface NotFoundPanelProps {
  id: string
  onHome?: () => void
}

/**
 * Shown inside the dashboard shell when a deep-link names an id the store
 * does not hold (R5.4). A blank page or a thrown render would look like the
 * app crashed; keeping the chrome and pointing at `/` is the recovery path.
 */
export function NotFoundPanel({ id, onHome }: NotFoundPanelProps): ReactNode {
  return (
    <div
      className="mx-auto max-w-md space-y-3 rounded-md border border-border bg-card p-6 text-center"
      data-testid="not-found-panel"
    >
      <h2 className="text-sm font-semibold text-foreground">Not found</h2>
      <p className="text-xs text-muted-foreground">
        No record named <code className="font-mono text-foreground">{id}</code> is in the store.
      </p>
      <a
        href="/"
        data-testid="not-found-home"
        onClick={(event) => {
          if (!onHome) return
          event.preventDefault()
          onHome()
        }}
        className="inline-block rounded-md bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground"
      >
        Context Doctor
      </a>
    </div>
  )
}
