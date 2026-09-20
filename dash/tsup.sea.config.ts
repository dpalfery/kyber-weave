import { fileURLToPath } from 'node:url'
import { defineConfig } from 'tsup'

// ESM bundle for the Node SEA build (release.yml, build-kyberdash).
//
// Emitted as ESM, not CJS: several dependencies are ESM-only and use top-level
// await, which esbuild cannot express in CommonJS. Node runs a SEA main as
// CommonJS regardless, so src/sea-shim.cjs is the actual entry and evaluates
// this bundle from a data: URL. See that file.
export default defineConfig({
  entry: ['src/cli/main.ts'],
  format: ['esm'],
  target: 'node22',
  outDir: 'dist-sea',
  clean: true,
  splitting: false,
  sourcemap: false,
  dts: false,
  // A SEA has no node_modules at runtime, so everything but Node's own
  // builtins must be inlined or the binary dies on first require.
  external: [/^node:/],
  noExternal: [/.*/],
  // Bundled CommonJS dependencies call require() for builtins, which esbuild
  // leaves as a shim that throws in ESM scope — and several modules derive
  // paths from import.meta.url, which here is the shim's data: URL and is
  // rejected by both createRequire and fileURLToPath. Bind both to the running
  // binary instead.
  banner: {
    js: [
      "import { createRequire as __seaCreateRequire } from 'node:module'",
      "import { pathToFileURL as __seaPathToFileURL } from 'node:url'",
      'const __seaImportMetaUrl = __seaPathToFileURL(process.execPath).href',
      'const require = __seaCreateRequire(__seaImportMetaUrl)',
    ].join('\n'),
  },
  define: { 'import.meta.url': '__seaImportMetaUrl' },
  esbuildOptions(options) {
    // A leftover bare specifier cannot resolve inside a data: URL scope.
    options.alias = {
      ...(options.alias ?? {}),
      'react-devtools-core': fileURLToPath(
        new URL('./src/sea-devtools-stub.js', import.meta.url),
      ),
    }
  },
})
