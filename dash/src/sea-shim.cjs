// SEA entry for the kyberdash single-file binary (release.yml, build-kyberdash).
//
// Node runs a SEA main script as CommonJS (`embedderRunCjs`, whatever the file
// extension), and esbuild cannot emit top-level await in CJS — which ink and
// its yoga-layout dependency both use. So the application is bundled as ESM
// (tsup.sea.config.ts), embedded as a SEA asset, and evaluated here through a
// data: URL, which is a real ESM scope where top-level await works.
//
// This file is the SEA main verbatim; it is never bundled, so it must stay
// plain CommonJS using only node: builtins.
// Buffer and process are imported rather than taken from the globals so this
// file needs no eslint env of its own.
const { Buffer } = require('node:buffer')
const Module = require('node:module')
const process = require('node:process')
const { getAsset } = require('node:sea')

// src/main.ts and src/plugins/loader.ts both read the CLI version with
// createRequire(...)('../package.json'), resolving off disk at a path that does
// not exist inside a SEA. They each build their own resolver, so intercepting
// at the loader catches every one of them rather than chasing call sites.
const manifest = JSON.parse(getAsset('package.json', 'utf8'))
const load = Module._load
Module._load = function (request, ...rest) {
  if (/(^|[/\\])package\.json$/.test(request)) return manifest
  return load.call(this, request, ...rest)
}

const source = getAsset('main.js', 'utf8')
const url =
  'data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64')

import(url).catch((err) => {
  process.stderr.write(String(err?.stack ?? err?.message ?? err) + '\n')
  process.exit(1)
})
