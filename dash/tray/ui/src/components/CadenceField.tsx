/**
 * The refresh cadence, edited as a draft and applied on purpose.
 *
 * The cadence is a server-owned setting with a range the server enforces, and a
 * `PUT` per keystroke would persist every intermediate value: typing 15 over a
 * saved 60 first sends `1`, which is a one-minute cadence, and clearing the
 * field sends `0`, which the server rejects. So the field holds a draft, shows
 * the saved value as the placeholder, validates the range locally, and sends
 * one patch when Apply (or Enter) is pressed. Same rule as the web panel's
 * cadence control: both surfaces edit one server document, so both must behave
 * the same way.
 *
 * The draft lives here rather than in `SettingsView` so the surrounding view
 * stays a function of its props: this control is the only part of it that has
 * an intermediate state worth remembering.
 */

import { useState } from 'react'

/** The bounds the server enforces on `refreshCadenceMinutes`. */
export const MIN_CADENCE_MINUTES = 1
export const MAX_CADENCE_MINUTES = 1440

export const CADENCE_ERROR = `Enter a whole number of minutes from ${MIN_CADENCE_MINUTES} to ${MAX_CADENCE_MINUTES}.`

/**
 * The draft as a cadence, or `null` when it is not one. Blank is `null`, not
 * zero: an empty field means "not edited yet", not "a zero-minute cadence".
 */
export function parseCadenceMinutes(draft: string): number | null {
  const trimmed = draft.trim()
  if (!/^\d+$/.test(trimmed)) return null
  const minutes = Number(trimmed)
  return minutes >= MIN_CADENCE_MINUTES && minutes <= MAX_CADENCE_MINUTES ? minutes : null
}

type Props = {
  /** What the server last said, shown as the placeholder when the box is empty. */
  savedMinutes: number
  onApply: (minutes: number) => void
}

export function CadenceField({ savedMinutes, onApply }: Props) {
  const [draft, setDraft] = useState(() => String(savedMinutes))
  const [touched, setTouched] = useState(false)
  const minutes = parseCadenceMinutes(draft)

  function apply() {
    if (minutes === null) return
    onApply(minutes)
    setTouched(false)
    // Back to the placeholder: from here the saved value governs, and the
    // server's read-back is the only number that should look authoritative.
    setDraft('')
  }

  return (
    <>
      <label className="settings__row">
        <span>Refresh every (minutes)</span>
        <input
          type="number"
          min={MIN_CADENCE_MINUTES}
          max={MAX_CADENCE_MINUTES}
          data-testid="shared-refresh-cadence"
          value={draft}
          placeholder={String(savedMinutes)}
          aria-invalid={touched && minutes === null}
          onChange={(event) => {
            setDraft(event.target.value)
            setTouched(true)
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter') apply()
          }}
        />
      </label>

      {touched && minutes === null && (
        <p className="settings__error" role="alert" data-testid="shared-refresh-cadence-error">
          {CADENCE_ERROR}
        </p>
      )}

      <nav className="actions">
        <button
          type="button"
          onClick={apply}
          disabled={minutes === null}
          data-testid="shared-refresh-cadence-apply"
        >
          Apply cadence
        </button>
      </nav>
    </>
  )
}
