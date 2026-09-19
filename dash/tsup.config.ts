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
})
