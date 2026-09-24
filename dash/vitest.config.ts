import { resolve } from 'node:path'
import { coverageConfigDefaults, defineConfig } from 'vitest/config'

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
    // Collected only when a run passes --coverage, as the review suite's ts-test gate
    // does, so the review has line evidence for dash/ and not only for .NET. The
    // report lands beside the .NET one under artifacts/, and keeps vitest's
    // cobertura-coverage.xml name deliberately: the gate runner reads only files named
    // coverage.cobertura.xml, so this report cannot displace the .NET numbers.
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}', 'web/src/**/*.{ts,tsx}'],
      exclude: [...coverageConfigDefaults.exclude, '**/__fixtures__/**'],
      reporter: ['text-summary', 'cobertura'],
      reportsDirectory: resolve(__dirname, '../artifacts/coverage-dash'),
    },
  },
})
