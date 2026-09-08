// Side-effect-free lists for tests/setup/env-isolation.ts.
// Imported by the setup file (which applies them) and by
// tests/env-isolation-declarations.test.ts (which asserts coverage).
// Do not put applyIsolation() here — importing this module from a test
// must not re-sandbox the process or register another beforeEach.
//
// A comment containing 'HERMES_HOME' is not isolation. The declaration
// test imports these arrays, so a commented-out name cannot false-green.

export const REDIRECTED = [
  'HOME',
  'XDG_CONFIG_HOME',
  'XDG_DATA_HOME',
  'XDG_CACHE_HOME',
  'XDG_STATE_HOME',
  'APPDATA',
  'LOCALAPPDATA',
] as const

export const CLEARED = [
  // Provider session-discovery dirs
  'CLAUDE_CONFIG_DIR',
  'CLAUDE_CONFIG_DIRS',
  'CLINE_DIR',
  'CLINE_DATA_DIR',
  'CLINE_SESSION_DATA_DIR',
  'CODEX_HOME',
  'CODEWHALE_HOME',
  'CRUSH_GLOBAL_DATA',
  'CODEBUFF_DATA_DIR',
  'DSH_HOME',
  'FACTORY_DIR',
  'GOOSE_PATH_ROOT',
  'GROK_HOME',
  'HERMES_HOME',
  'KIRO_HOME',
  'KIMI_CODE_HOME',
  'KIMI_SHARE_DIR',
  'LINGTAI_HOME',
  'LINGTAI_TUI_GLOBAL_DIR',
  'LINGTAI_TUI_HOME',
  'MUX_ROOT',
  'OPENCODE_DATA_DIR',
  'OPENCODE_DB_PREFIX',
  'QUICKWORK_HOME',
  'QWEN_DATA_DIR',
  'VIBE_HOME',
  'WARP_DB_PATH',
  'ZS_DATA_DIR',
  // codeburn override dirs / paths
  'CODEBURN_CACHE_DIR',
  'CODEBURN_COPILOT_GLOBAL_STORAGE_DIR',
  'CODEBURN_COPILOT_JETBRAINS_DIR',
  'CODEBURN_COPILOT_OTEL_DB',
  'CODEBURN_COPILOT_SESSION_STATE_DIR',
  'CODEBURN_COPILOT_SESSION_STORE_DB',
  'CODEBURN_COPILOT_WS_STORAGE_DIR',
  'CODEBURN_DESKTOP_SESSIONS_DIR',
  'CODEBURN_MUX_DIR',
  'CODEBURN_OPEN_DESIGN_DIR',
  'CODEBURN_OPENCLAUDE_DIR',
  'CODEBURN_ANTIGRAVITY_SETTINGS_PATH',
  // codeburn behavior toggles (set by the dev to tweak local runs)
  'CODEBURN_COPILOT_DISABLE_OTEL',
  'CODEBURN_TZ',
  'CODEBURN_VERBOSE',
  'CODEBURN_CURSOR_MAX_BUBBLES',
  'CODEBURN_FORCE_MACOS_MAJOR',
  // Provider model/credential overrides
  'KIMI_MODEL_NAME',
  'AI_GATEWAY_API_KEY',
  'VERCEL_OIDC_TOKEN',
  // Review-provider credentials and endpoints (kyber/analysis/review-providers).
  // getReviewProvider() picks its provider by which API key is present, and each
  // provider builds its URL from the matching *_BASE_URL, so a dev shell decides
  // both which provider a test gets and where it points. A gateway-shaped
  // ANTHROPIC_BASE_URL ('https://api.anthropic.com', no /v1) turned
  // review.test.ts red locally while CI stayed green.
  'ANTHROPIC_API_KEY',
  'ANTHROPIC_BASE_URL',
  'OPENAI_API_KEY',
  'OPENAI_BASE_URL',
  'OLLAMA_HOST',
  'OLLAMA_BASE_URL',
  // Read by detectBashBloat - a dev's real shell limit must not bleed in
  'BASH_MAX_OUTPUT_LENGTH',
  // Color forcing. CLI tests spawn src/cli.ts with `...process.env` and assert
  // on plain stdout; chalk writing to a pipe emits no escapes UNLESS one of
  // these overrides supports-color's tty check. A dev shell (or a test-runner
  // extension) that exports FORCE_COLOR turns every such assertion red - most
  // sharply in cli-models-unpriced, whose control-character assertion is a
  // terminal-injection guard and cannot distinguish chalk's own header styling
  // from an escape smuggled in through a model ID. Cleared, not pinned to '0':
  // piped stdio already means no color, and NO_COLOR stays the dev's to set.
  'FORCE_COLOR',
  'CLICOLOR_FORCE',
] as const

// Snapshotted from the dev's shell and restored every test. These can't be
// wiped (Node needs PATH for spawn / module resolution, dashboard/table layout
// reads COLUMNS) but a test that mutates them shouldn't leak.
export const PRESERVED = ['PATH', 'COLUMNS'] as const
