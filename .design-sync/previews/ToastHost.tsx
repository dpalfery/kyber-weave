import { ListRow, Panel, ToastHost, showToast } from 'codeburn-desktop'

/**
 * The toast store auto-dismisses after 3s. A card holds its toast open instead,
 * so the surface is on screen when the sheet is photographed.
 */
const HELD_OPEN = 10 * 60 * 1000

/** Whatever the toast is reporting on — the host paints over the live section. */
function SectionBehind() {
  return (
    <div className="ct">
      <Panel title="Sessions" right="Last 7 days">
        <ListRow no="01" title="Refactor retrieval index" sub="claude-opus-5 · 2h ago" value="$4.12" />
        <ListRow no="02" title="Fix flaky lock test" sub="claude-sonnet-5 · 5h ago" value="$0.88" />
        <ListRow no="03" title="Port punchcard to the new grid" sub="claude-opus-5 · yesterday" value="$6.40" />
      </Panel>
    </div>
  )
}

/** The `ok` kind: a green rule down the left edge, bottom-right of the window. */
export function ActionConfirmed() {
  showToast('Exported 412 sessions to codeburn-2024-05.csv', 'ok', HELD_OPEN)
  return (
    <>
      <SectionBehind />
      <ToastHost />
    </>
  )
}

/** The `error` kind swaps the rule to `--bad`; nothing else about the surface changes. */
export function ActionFailed() {
  showToast("Couldn't read ~/.codex/sessions — grant Full Disk Access", 'error', HELD_OPEN)
  return (
    <>
      <SectionBehind />
      <ToastHost />
    </>
  )
}

/**
 * Only one toast exists at a time: a second `showToast` replaces the first
 * rather than stacking, so the surface never grows past a single line block.
 */
export function LatestReplacesPrevious() {
  showToast('Rescanning Cursor…', 'ok', HELD_OPEN)
  showToast('Cursor rescanned · 1,204 calls, $96.10 across 3 models', 'ok', HELD_OPEN)
  return (
    <>
      <SectionBehind />
      <ToastHost />
    </>
  )
}
