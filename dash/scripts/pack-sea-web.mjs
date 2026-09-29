#!/usr/bin/env node
// Packs a built web directory (dash/dist/dash, web/vite.config.ts's outDir) into the
// single-file asset the SEA release build embeds and `dash/src/cli/web.ts` serves from
// memory (D1-A, docs/plans/2026-09-28-kyberdash-sea-release-integrity.md).
//
// The asset format is `{"format":"kyberdash-web/1","files":{"<posix relative
// path>":"<base64>"}}`, with keys sorted so the blob is reproducible across builds of the
// same source tree. `index.html` must be present in the source directory: a build that
// skipped `npm run build` (or pointed this at the wrong directory) fails loudly here rather
// than embedding a dashboard with no entry point.
//
// Usage: node scripts/pack-sea-web.mjs <source-dir> <output-file>
//   <source-dir>   the built web directory, e.g. dist/dash
//   <output-file>  where to write the kyberdash-web/1 JSON, e.g. dist-sea/web.json

import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join, relative, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

export const WEB_ASSET_FORMAT = 'kyberdash-web/1'

/**
 * Walks `sourceDir` and returns the `kyberdash-web/1` asset shape: every file under it,
 * keyed by its POSIX-relative path with base64-encoded contents, sorted so the emitted JSON
 * is byte-for-byte reproducible for an unchanged source tree.
 *
 * Throws if `sourceDir` holds no `index.html`: the SEA build serves this asset as a static
 * site, and a site with no entry point is a packing mistake, not a valid (if empty) result.
 */
export async function packWebDirectory(sourceDir) {
  const files = {}

  async function walk(dir) {
    const entries = await readdir(dir, { withFileTypes: true })
    for (const entry of entries) {
      const full = join(dir, entry.name)
      if (entry.isDirectory()) {
        await walk(full)
      } else if (entry.isFile()) {
        const relPath = relative(sourceDir, full).split(sep).join('/')
        const body = await readFile(full)
        files[relPath] = body.toString('base64')
      }
    }
  }

  await walk(sourceDir)

  if (!Object.prototype.hasOwnProperty.call(files, 'index.html')) {
    throw new Error(`no index.html found under ${sourceDir} - did the web build run?`)
  }

  const sortedFiles = {}
  for (const key of Object.keys(files).sort()) {
    sortedFiles[key] = files[key]
  }

  return { format: WEB_ASSET_FORMAT, files: sortedFiles }
}

async function main() {
  const [sourceDir, outputFile] = process.argv.slice(2)
  if (!sourceDir || !outputFile) {
    console.error('usage: pack-sea-web.mjs <source-dir> <output-file>')
    process.exitCode = 1
    return
  }

  try {
    const asset = await packWebDirectory(sourceDir)
    await mkdir(dirname(outputFile), { recursive: true })
    await writeFile(outputFile, JSON.stringify(asset))
    console.log(`pack-sea-web: wrote ${Object.keys(asset.files).length} files from ${sourceDir} to ${outputFile}`)
  } catch (err) {
    console.error(`pack-sea-web: ${err instanceof Error ? err.message : String(err)}`)
    process.exitCode = 1
  }
}

// Only run the CLI when this file is the process entry point, not when a test imports
// `packWebDirectory` via a dynamic import.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main()
}
