import { SectionSkeleton } from 'codeburn-desktop'

/** The default: a head line plus four body lines of decreasing width. */
export function Default() {
  return <SectionSkeleton label="Scanning model usage…" />
}

/** `rows` sets how many body lines stand in for the real rows. */
export function ListRows() {
  return <SectionSkeleton label="Scanning sessions…" rows={5} />
}

/** `chart` adds the tall block a charted section reserves above its rows. */
export function WithChart() {
  return <SectionSkeleton label="Scanning spend…" rows={3} chart />
}

/** A short section — quota cards resolve into two rows, so it reserves two. */
export function Compact() {
  return <SectionSkeleton label="Loading quota…" rows={2} />
}
