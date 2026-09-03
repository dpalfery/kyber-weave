/// Paths shown in copy use the reader's own OS spelling.

export const IS_WINDOWS = navigator.userAgent.includes('Windows')

const HOME = IS_WINDOWS ? '%USERPROFILE%' : '~'
const SEP = IS_WINDOWS ? '\\' : '/'

export function homePath(...parts: string[]): string {
  return [HOME, ...parts].join(SEP)
}

/// Today's spend in the tray is a second tray icon carrying the number as its bitmap. Only
/// the Windows notification area gives us one; the Linux SNI tray has no equivalent, and
/// macOS ships the Swift menubar instead. Where this is false the control is hidden and the
/// Rust command is never called.
export const TRAY_BADGE_SUPPORTED = IS_WINDOWS
