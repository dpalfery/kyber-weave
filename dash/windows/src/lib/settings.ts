/// The few preferences that live in the webview (everything the CLI also needs, like the
/// currency, lives in ~/.config/codeburn/config.json via the Rust side).

const KEYS = {
  theme: 'codeburn.theme',
  insight: 'codeburn.insight',
  starBannerDismissed: 'codeburn.starBannerDismissed',
  trayBadge: 'codeburn.trayBadge',
} as const

type Key = keyof typeof KEYS

export function readSetting(key: Key): string | null {
  try {
    return localStorage.getItem(KEYS[key])
  } catch {
    return null
  }
}

export function writeSetting(key: Key, value: string | null): void {
  try {
    if (value === null) localStorage.removeItem(KEYS[key])
    else localStorage.setItem(KEYS[key], value)
  } catch {
    // Storage can be unavailable in a locked-down webview; preferences are best-effort.
  }
}

export type Theme = 'light' | 'dark'

export function currentTheme(): Theme {
  const stamped = document.documentElement.getAttribute('data-theme')
  if (stamped === 'dark' || stamped === 'light') return stamped
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

export function applyTheme(theme: Theme | null): void {
  if (theme) document.documentElement.setAttribute('data-theme', theme)
  else document.documentElement.removeAttribute('data-theme')
  writeSetting('theme', theme)
}
