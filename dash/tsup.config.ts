import { defineConfig } from 'tsup'

export default defineConfig({
  entry: ['src/cli/main.ts', 'src/ingest/parse-worker.ts'],
  format: ['esm'],
  target: 'node22',
  outDir: 'dist',
  clean: true,
  splitting: false,
  sourcemap: true,
  dts: false,
  external: [/^node:/, '@modelcontextprotocol/sdk', 'zod', 'node:sqlite'],
  // tsup 8 strips the `node:` prefix by default. `node:sea` (dash/src/sea.ts)
  // is a prefix-only builtin with no bare alias, so a stripped
  // `import … from "sea"` cannot resolve and crashes the CLI bundle at
  // module-link time. Keeping the prefix is safe for the other builtins.
  removeNodeProtocol: false,
})
