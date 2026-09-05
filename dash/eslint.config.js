// ESLint flat config — minimal, KyberDash-owned.
//
// Lives at the dash/ subtree root because that is where the upstream
// package.json and tsconfig.json are. It is additive — no upstream-only
// file is touched — so a `git subtree pull` from codeburn merges this
// alongside the script/devDependency entries added in dash/package.json.
//
// The goal is a blocking gate (`ts-lint`) that fails on real problems in
// the merge zone while still running over the vendored subtree to surface
// drift, without failing today's tree on the baseline debt it inherits
// from upstream.
//
// Two-tier severity:
//   - Vendored upstream (`dash/src/**`, `dash/tests/**`, …): the
//     typescript-eslint recommended set runs as warnings. The
//     `inspectcode` and `duplicates` gates already follow this pattern
//     for their analyzers — the analyzer must *run*; the gate does not
//     close over the codebase's pre-existing debt.
//   - Merge zone (`dash/kyber/**`, KyberDash-only): the same recommended
//     set runs at `error` severity, so a new violation in code KyberDash
//     ships is the one that fails CI.
//
// `no-undef` is disabled for TS because `tsc` is the canonical name
// resolver for those files (the `ts-typecheck` gate is the one that
// reports undefined names), and applying both surfaced 120+ duplicate
// complaints that obscured the gate signal.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';

const ignorePaths = [
  'dist/**',
  'build/**',
  'node_modules/**',
  'dash/**',         // nested Electron subdashboard build/install
  'app/**',          // upstream Electron renderer + demo bridge
  'mac/**',          // upstream Swift menu-bar surface
  'windows/**',      // upstream unshipped
  'gnome/**',        // upstream unshipped
  'assets/**',
  'scripts/**',      // upstream build/release helpers
  'kyber/tools/**/*.mjs',
  'tests/fixtures/**',
  'eslint.config.js',
  '.release-0.9.21-runbook.md',
];

// Downgrade each rule's severity to "warn" so the recommended baseline
// stays informational over the vendored subtree without silently
// disabling it. Rule arrays keep their options; bare strings/integers
// become the string "warn".
const downgradeToWarn = (cfg) => ({
  ...cfg,
  rules: Object.fromEntries(
    Object.entries(cfg.rules ?? {}).map(([k, v]) => {
      if (Array.isArray(v)) return [k, ['warn', ...v.slice(1)]];
      if (v === 'error' || v === 2) return [k, 'warn'];
      return [k, v];
    }),
  ),
});

export default [
  { ignores: ignorePaths },

  // Plain JS — js/recommended at default severity (it has no override
  // problem for JS files because there is no TS there to double-report).
  {
    files: ['**/*.{js,mjs,cjs}'],
    ...js.configs.recommended,
  },

  // TypeScript, vendored zone — recommended as warnings.
  ...tseslint.configs.recommended.map((cfg) =>
    downgradeToWarn({ ...cfg, files: ['**/*.{ts,tsx}'] }),
  ),

  // TypeScript, merge zone — recommended at default severity, plus
  // recompute the same configs targeting dash/kyber/** so a KyberDash
  // file lands errors instead of warnings. ESLint flat-config "files"
  // blocks are last-match-wins, so this stricter block overrides the
  // softer baseline for files under dash/kyber/.
  ...tseslint.configs.recommended.map((cfg) => ({
    ...cfg,
    files: ['kyber/**/*.{ts,tsx}'],
    rules: {
      ...(cfg.rules ?? {}),
      'no-undef': 'off',
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-explicit-any': 'error',
    },
  })),
];
