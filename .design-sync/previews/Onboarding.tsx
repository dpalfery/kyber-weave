import { useEffect } from 'react'

import { Onboarding } from 'codeburn-desktop'

/**
 * Which of the four screens is showing is `Onboarding`'s own internal state —
 * there is no prop for it, and `defaultEnabled` only becomes visible on the
 * last one. A card that documents a later screen therefore advances the overlay
 * through its own Next control, the way a first-run user reaches it. Nothing
 * about the screen itself is authored here.
 */
function useScreen(index: number): void {
  useEffect(() => {
    for (let step = 0; step < index; step++) {
      document.querySelector<HTMLButtonElement>('.onboard-btn.primary')?.click()
    }
  }, [index])
}

/** Screen 1 of 4 — first launch, before anything has been scanned. */
export function Welcome() {
  useScreen(0)
  return <Onboarding defaultEnabled onDone={() => {}} />
}

/** Screen 2 — the local-first promise, with Back now occupying the left slot. */
export function LocalFirst() {
  useScreen(1)
  return <Onboarding defaultEnabled onDone={() => {}} />
}

/** Screen 3 — the last feature screen; the dots track three of four. */
export function FindTheWaste() {
  useScreen(2)
  return <Onboarding defaultEnabled onDone={() => {}} />
}

/**
 * Screen 4, the consent screen. Outside the EU/EEA/UK/CH the region default is
 * on, so the switch is seeded on and the primary action becomes Get started.
 */
export function TelemetryConsent() {
  useScreen(3)
  return <Onboarding defaultEnabled onDone={() => {}} />
}

/** The same screen under the EU/EEA/UK/CH default: `defaultEnabled={false}`. */
export function TelemetryConsentOptedOut() {
  useScreen(3)
  return <Onboarding defaultEnabled={false} onDone={() => {}} />
}
