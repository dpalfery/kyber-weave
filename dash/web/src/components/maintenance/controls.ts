// Shared presentation classes for the Maintenance panel's controls.
//
// The panel's buttons and numeric fields shipped with no classes at all: "Pause
// scheduled jobs" and "Refresh data now" rendered as one run of body text, and
// the weeks and cadence inputs had no visible edge. Two actions that read as a
// sentence are one action to a reader, and a number field with no boundary
// invites the spinner arrows to be discovered by accident.
//
// Everything here is composed from the tokens the dashboard already ships —
// `border`/`input`/`card`/`interactive-secondary`/`primary`/`ring` and the
// density scale — so this is not a second visual system. It exists so three
// components cannot drift apart on what a button or a bounded field looks like.

/** One keyboard-visible focus ring, shared by every control here. */
const FOCUS = 'focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring'

/**
 * Disabled is stated, never silent. A control the panel turned off on purpose
 * (an invalid window, an unreadable settings read) has to look off, not look
 * like a control that simply ignores the operator.
 */
const DISABLED = 'disabled:cursor-not-allowed disabled:opacity-50'

const BUTTON_BASE = `inline-flex items-center justify-center gap-density-inline rounded-md border px-chrome py-density-tight text-density-xs font-medium transition-colors ${FOCUS} ${DISABLED}`

/** The default action: a bordered, filled-into-on-hover button. */
export const ACTION_BUTTON = `${BUTTON_BASE} border-border bg-card text-foreground hover:bg-interactive-secondary`

/**
 * The one action a reader should land on first in a row. Brand-coloured rather
 * than heavier, so it does not compete with the destructive control further
 * down the panel.
 */
export const PRIMARY_ACTION_BUTTON = `${BUTTON_BASE} border-primary bg-primary text-primary-foreground hover:opacity-90`

/**
 * The destructive action. Deliberately the only red surface in the panel: the
 * wipe is the one control here that cannot be undone, and it has to read as
 * different in kind from "apply cadence" before it is read, not after.
 */
export const DESTRUCTIVE_BUTTON = `${BUTTON_BASE} border-red-500/40 bg-red-500/10 text-red-700 hover:bg-red-500/20 dark:border-red-500/40 dark:bg-red-500/15 dark:text-red-400 dark:hover:bg-red-500/25`

/** A bounded numeric field: a visible edge, a fixed width, no layout shift. */
export const NUMBER_FIELD = `w-16 rounded-md border border-input bg-background px-chrome-xs py-density-hair text-density-xs tabular-nums text-foreground ${FOCUS} ${DISABLED}`

export const CHECKBOX = 'h-3.5 w-3.5 shrink-0 accent-primary'

/**
 * A quiet inset block for the read-only facts and the grouped forms. It is what
 * separates facts, actions and forms from one another without adding another
 * card: the panel is already the card, and nesting cards on a phone costs
 * width the operator needs.
 */
export const INSET_BLOCK = 'flex flex-col gap-density-hair rounded-md border border-border/60 bg-interactive-secondary/30 px-chrome-sm py-chrome-sm'

/** The same boundary, for a fieldset that carries a legend and controls. */
export const INSET_FIELD = 'flex flex-col gap-density-hair rounded-md border border-border/60 px-chrome-sm py-chrome-sm'

export const FIELD_LABEL = 'flex flex-wrap items-center justify-between gap-density-cluster text-density-xs'

/** Helper and status copy: readable, never competing with a control. */
export const HELPER_TEXT = 'text-density-xs leading-density text-muted-foreground'

/** A group heading inside the panel, matching the section headings above it. */
export const GROUP_HEADING = 'text-density-xs font-semibold uppercase tracking-density text-heading'