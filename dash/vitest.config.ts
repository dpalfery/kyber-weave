import { resolve } from 'node:path'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  resolve: {
    alias: [
      { find: /^@\/(.*)/, replacement: resolve(__dirname, './dash/src/$1') },
      { find: '@', replacement: resolve(__dirname, './dash/src') },
    ],
  },
  test: {
    // Include backend tests and UI component tests
    include: [
      'tests/**/*.{test,spec}.?(c|m)[jt]s?(x)',
      'dash/src/**/*.{test,spec}.?(c|m)[jt]s?(x)',
      'kyber/**/*.{test,spec}.?(c|m)[jt]s?(x)',
    ],
    // A worktree under .claude/ carries its own tests/ copy. They are stale by
    // definition (another branch's checkout) and are not this run's subject.
    // app/ has its own separate desktop package and test suite.
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
      '**/.claude/worktrees/**',
      'app/**',
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
