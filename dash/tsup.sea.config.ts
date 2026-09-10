import { fileURLToPath } from 'node:url'
import { defineConfig } from 'tsup'

// ESM bundle for the Node SEA build (release.yml, build-kyberdash).
//
// Emitted as ESM, not CJS: ink and its yoga-layout dependency are ESM-only and
// use top-level await, which esbuild cannot express in CommonJS. Node runs a
// SEA main as CommonJS regardless, so src/sea-shim.cjs is the actual entry and
// evaluates this bundle from a data: URL. See that file.
export default defineConfig({
  entry: ['src/main.ts'],
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
    // ink imports react-devtools-core at module scope but reaches it only when
    // DEV is set. It is not installed, and leaving it external would put a bare
    // specifier in the bundle, which cannot resolve inside a data: URL scope.
    options.alias = {
      ...(options.alias ?? {}),
      'react-devtools-core': fileURLToPath(
        new URL('./src/sea-devtools-stub.js', import.meta.url),
      ),
    }
  },
})
