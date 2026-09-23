// ESLint flat config for KyberDash.
//
// One tree, one severity. This config used to run two tiers: `error` over the
// KyberDash merge zone and `warn` over the vendored upstream subtree, because a
// `git subtree pull` could land code nobody here had reviewed. The one-time fork
// (ADR 0020) ended that — every file under `dash/` is first-party now, judged on
// its merits under the same gates as the rest of the repository — so the tiers
// collapse into one and a violation fails CI wherever it is.
//
// `no-undef` stays off for TypeScript because `tsc` is the canonical name
// resolver for those files (the typecheck gate is what reports undefined names),
// and running both produced 120+ duplicate complaints that buried the signal.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';

const ignorePaths = [
  'dist/**',
  'dist-sea/**',
  'build/**',
  'node_modules/**',
  'web/dist/**',
  'web/node_modules/**',
  'tray/ui/dist/**',
  'tray/ui/node_modules/**',
  'tray/src-tauri/**',
  'scripts/upgrade-path/**', // release-verification helpers, run by node directly
  'src/tools/**/*.mjs',
  'tests/fixtures/**',
  'eslint.config.js',
];

export default [
  { ignores: ignorePaths },

  // Plain JS — js/recommended at its default severity. There is no TS here to
  // double-report against, so the override problem below does not arise.
  //
  // The Node globals are declared rather than pulled from the `globals` package:
  // these files are build and release scripts run by `node`, and this list is the
  // whole of what they touch. Core takes dependencies sparingly, and a dozen
  // names is not worth one.
  {
    files: ['**/*.{js,mjs,cjs}'],
    ...js.configs.recommended,
    languageOptions: {
      ...(js.configs.recommended.languageOptions ?? {}),
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: {
        Buffer: 'readonly',
        URL: 'readonly',
        console: 'readonly',
        fetch: 'readonly',
        global: 'readonly',
        process: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        setInterval: 'readonly',
        clearInterval: 'readonly',
        structuredClone: 'readonly',
        TextDecoder: 'readonly',
        TextEncoder: 'readonly',
        __dirname: 'readonly',
        __filename: 'readonly',
        require: 'readonly',
        module: 'writable',
      },
    },
  },

  ...tseslint.configs.recommended.map((cfg) => ({
    ...cfg,
    files: ['**/*.{ts,tsx}'],
    rules: {
      ...(cfg.rules ?? {}),
      'no-undef': 'off',
      // A `let` read inside a closure before its single assignment cannot be
      // `const`; without this the rule reports code that would not compile.
      'prefer-const': ['error', { ignoreReadBeforeAssign: true }],
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-explicit-any': 'error',
    },
  })),
];
