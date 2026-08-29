import { FLAME_PATH } from './Icons'

/// The macOS BurnLoadingOverlay: a blurred sheet over the scroll area with a flame that
/// fills bottom-to-top on a 1.4s loop while a soft glow pulses behind it.

type Props = { periodLabel: string }

export function LoadingOverlay({ periodLabel }: Props) {
  return (
    <div className="loading-overlay" role="status" aria-live="polite">
      <div className="loading-content">
        <BurnFlame />
        <div className="loading-text">Loading {periodLabel}…</div>
      </div>
    </div>
  )
}

export function BurnFlame({ size = 64 }: { size?: number }) {
  return (
    <div className="burn-flame" style={{ width: size, height: size }}>
      <svg className="burn-flame-glow" viewBox="0 0 16 16" width={size} height={size} aria-hidden="true">
        <path d={FLAME_PATH} />
      </svg>
      <svg className="burn-flame-outline" viewBox="0 0 16 16" width={size} height={size} aria-hidden="true">
        <path d={FLAME_PATH} />
      </svg>
      <svg className="burn-flame-fill" viewBox="0 0 16 16" width={size} height={size} aria-hidden="true">
        <defs>
          <linearGradient id="burn-gradient" x1="0" y1="1" x2="0" y2="0">
            <stop offset="0%" stopColor="#F0A070" />
            <stop offset="33.33%" stopColor="#E8774A" />
            <stop offset="66.66%" stopColor="#C9521D" />
            <stop offset="100%" stopColor="#8B3E13" />
          </linearGradient>
          <clipPath id="burn-clip">
            <rect className="burn-clip-rect" x="0" y="0" width="16" height="16" />
          </clipPath>
        </defs>
        <path d={FLAME_PATH} fill="url(#burn-gradient)" clipPath="url(#burn-clip)" />
      </svg>
    </div>
  )
}
