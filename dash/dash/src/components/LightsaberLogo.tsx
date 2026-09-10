import type { SVGProps } from 'react'

export function LightsaberLogo({ className = 'h-6 w-6', ...props }: SVGProps<SVGSVGElement>) {
  return (
    <svg
      viewBox="0 0 32 32"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      aria-label="kyberDash lightsaber logo"
      role="img"
      {...props}
    >
      <defs>
        {/* Outer energy bloom filter */}
        <filter id="kyber-blade-glow" x="-50%" y="-50%" width="200%" height="200%">
          <feGaussianBlur in="SourceGraphic" stdDeviation="1.8" result="blur1" />
          <feGaussianBlur in="SourceGraphic" stdDeviation="3.6" result="blur2" />
          <feMerge>
            <feMergeNode in="blur2" />
            <feMergeNode in="blur1" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>

        {/* Blade energy gradient */}
        <linearGradient id="kyber-blade-grad" x1="0%" y1="100%" x2="100%" y2="0%">
          <stop offset="0%" stopColor="#10b981" />
          <stop offset="50%" stopColor="#34d399" />
          <stop offset="100%" stopColor="#6ee7b7" />
        </linearGradient>

        {/* Metallic hilt gradient */}
        <linearGradient id="kyber-hilt-metal" x1="0%" y1="100%" x2="100%" y2="0%">
          <stop offset="0%" stopColor="#334155" />
          <stop offset="35%" stopColor="#64748b" />
          <stop offset="70%" stopColor="#94a3b8" />
          <stop offset="100%" stopColor="#475569" />
        </linearGradient>

        {/* Emitter gold accent */}
        <linearGradient id="kyber-emitter-gold" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stopColor="#fbbf24" />
          <stop offset="100%" stopColor="#d97706" />
        </linearGradient>
      </defs>

      {/* 1. Outer energy bloom */}
      <line
        x1="12"
        y1="20"
        x2="26.5"
        y2="5.5"
        stroke="#10b981"
        strokeWidth="7"
        strokeLinecap="round"
        opacity="0.35"
        filter="url(#kyber-blade-glow)"
      />

      {/* 2. Mid energy glow */}
      <line
        x1="12"
        y1="20"
        x2="26"
        y2="6"
        stroke="url(#kyber-blade-grad)"
        strokeWidth="4"
        strokeLinecap="round"
        opacity="0.9"
        filter="url(#kyber-blade-glow)"
      />

      {/* 3. Intense blade core */}
      <line x1="12" y1="20" x2="25.5" y2="6.5" stroke="#ffffff" strokeWidth="1.8" strokeLinecap="round" />

      {/* 4. Emitter spark / ignition flare */}
      <circle cx="12" cy="20" r="2" fill="#ffffff" opacity="0.9" filter="url(#kyber-blade-glow)" />
      <polygon
        points="12,17.5 12.6,19.4 14.5,20 12.6,20.6 12,22.5 11.4,20.6 9.5,20 11.4,19.4"
        fill="#ffffff"
        opacity="0.95"
      />

      {/* 5. Hilt / Handle */}
      {/* Main metallic hilt body */}
      <line x1="5.5" y1="26.5" x2="12" y2="20" stroke="url(#kyber-hilt-metal)" strokeWidth="3.2" strokeLinecap="round" />

      {/* Hilt grip bands */}
      <line x1="6.8" y1="23.8" x2="8.2" y2="25.2" stroke="#1e293b" strokeWidth="1.1" strokeLinecap="round" />
      <line x1="8.3" y1="22.3" x2="9.7" y2="23.7" stroke="#1e293b" strokeWidth="1.1" strokeLinecap="round" />
      <line x1="9.8" y1="20.8" x2="11.2" y2="22.2" stroke="#1e293b" strokeWidth="1.1" strokeLinecap="round" />

      {/* Pommel cap (base) */}
      <circle cx="5.5" cy="26.5" r="1.4" fill="#1e293b" />
      <circle cx="5.5" cy="26.5" r="0.7" fill="#94a3b8" />

      {/* Emitter collar (top of hilt) */}
      <line x1="10.5" y1="19.5" x2="12.5" y2="21.5" stroke="url(#kyber-emitter-gold)" strokeWidth="1.6" strokeLinecap="round" />
      <line x1="11.2" y1="18.8" x2="13.2" y2="20.8" stroke="#334155" strokeWidth="1.2" strokeLinecap="round" />

      {/* Activation switch */}
      <circle cx="7.7" cy="24.3" r="0.6" fill="#ef4444" />
    </svg>
  )
}
