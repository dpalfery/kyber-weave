import type { ReactNode } from 'react'

import { RangeCalendar } from 'codeburn-desktop'

/**
 * The calendar opens on the current month and disables every day after today,
 * so its ranges have to be generated relative to `new Date()` rather than
 * written as literals — a fixed date outside the running month renders on a
 * page the card never navigates to.
 */
const TODAY = new Date()

function key(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** `offset` days from today. */
function day(offset: number): string {
  return key(new Date(TODAY.getFullYear(), TODAY.getMonth(), TODAY.getDate() + offset))
}

/** `offset` days from the 1st of the shown month (negative lands in the previous one). */
function fromFirst(offset: number): string {
  return key(new Date(TODAY.getFullYear(), TODAY.getMonth(), 1 + offset))
}

/** The popover TopBar drops the calendar into, pinned open. */
function Popover({ children }: { children: ReactNode }) {
  return (
    <div className="calendar-wrap" style={{ width: 286, height: 296 }}>
      <div className="calendar-popover" style={{ top: 0 }}>{children}</div>
    </div>
  )
}

/** Nothing chosen yet: only the future days are muted, and Next is disabled. */
export function NoSelection() {
  return (
    <Popover>
      <RangeCalendar value={null} onSelect={() => {}} />
    </Popover>
  )
}

/** A committed range: tinted body, solid accent endpoints at both ends. */
export function LastSevenDays() {
  return (
    <Popover>
      <RangeCalendar value={{ from: day(-6), to: day(0) }} onSelect={() => {}} />
    </Popover>
  )
}

/** A one-day range — `from === to`, so a single endpoint carries both roles. */
export function SingleDay() {
  return (
    <Popover>
      <RangeCalendar value={{ from: day(-1), to: day(-1) }} onSelect={() => {}} />
    </Popover>
  )
}

/** Month to date: the range starts on the 1st and ends on today. */
export function MonthToDate() {
  return (
    <Popover>
      <RangeCalendar value={{ from: fromFirst(0), to: day(0) }} onSelect={() => {}} />
    </Popover>
  )
}

/**
 * A range that starts in the previous month: the leading days keep the `outside`
 * muting and still take the in-range tint.
 */
export function AcrossTheMonthBoundary() {
  return (
    <Popover>
      <RangeCalendar value={{ from: fromFirst(-3), to: fromFirst(4) }} onSelect={() => {}} />
    </Popover>
  )
}
