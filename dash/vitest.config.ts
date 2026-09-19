import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  resolve: {
    alias: [
      { find: /^@\/(.*)/, replacement: resolve(__dirname, './web/src/$1') },
      { find: '@', replacement: resolve(__dirname, './web/src') },
    ],
  },
  test: {
    // Include backend tests, operator-tool tests, and UI component tests
    include: [
      'src/**/*.{test,spec}.?(c|m)[jt]s?(x)',
      'tests/**/*.{test,spec}.?(c|m)[jt]s?(x)',
      'scripts/**/*.{test,spec}.?(c|m)[jt]s?(x)',
      'web/src/**/*.{test,spec}.?(c|m)[jt]s?(x)',
    ],
    // A worktree under .claude/ carries its own tests/ copy. They are stale by
    // definition (another branch's checkout) and are not this run's subject.
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      '**/.claude/worktrees/**',
    ],
    // Runs once per worker before any test. Scrubs the developer's shell so
    // session-discovery env vars (CLAUDE_CONFIG_DIRS, HOME, XDG_*, every
    // provider-specific *_HOME) don't bleed real local data into fixtures.
    setupFiles: [resolve(__dirname, './tests/setup/env-isolation.ts')],
    // Real-I/O tests (session parses, sqlite fixtures, worker pools) exceed the
    // 5s default under CI runner load; a hung test still fails at 30s.
    testTimeout: 30_000,
  },
})
