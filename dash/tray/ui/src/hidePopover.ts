/** Hide the popover. No-ops outside the Tauri webview so Node tests can render. */
export async function hidePopover(): Promise<void> {
  const runtime = globalThis as typeof globalThis & { __TAURI_INTERNALS__?: unknown }
  if (runtime.__TAURI_INTERNALS__ == null) {
    return
  }
  const { invoke } = await import('@tauri-apps/api/core')
  await invoke('hide_popover')
}
