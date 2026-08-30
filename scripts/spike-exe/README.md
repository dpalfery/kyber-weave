# Spike 1 — Single-executable packaging

Spike output for spec task **KyberDash / 1** (single-executable packaging). Records the
inspection done against upstream `getagentseal/codeburn` while `dash/` is not yet vendored
(Task 2.1 is the prerequisite). End-to-end build / parity runs are deferred until the vendoring
step; this artifact is the recorded decision that authorizes the recipe Task 12.1 will
implement.

## What was inspected

| File / source | Why | Finding |
|---|---|---|
| `getagentseal/codeburn@main/package.json` | Establishes package shape | ESM-only (`"type": "module"`); bin entry is `dist/cli.js`; engines `node >= 22.13.0`; build chain is **`tsup`**. |
| `getagentseal/codeburn@main/tsup.config.ts` | Current bundler config | `entry: ['src/main.ts', 'src/parse-worker.ts']`, `format: ['esm']`, `target: 'node20'`, `external: ['@modelcontextprotocol/sdk', 'zod']`, other deps bundled. |
| `getagentseal/codeburn@main/src/cli.ts` | Launcher pattern | A pre-22.13-check guard then a *single* dynamic `import('./main.js')` — distinctive idiom. |
| `getagentseal/codeburn@main/src/main.ts` (2638 lines) | Whether CJS bundle is feasible | All `await`s are inside `async` function bodies. No top-level await. Pure ESM `import` statements only. |
| `@modelcontextprotocol/sdk@1.29.0` registry record | ESM/CJS dual-package hazard | Dual — both `import` and `require` entries present; safe to bundle as CJS. |
| `zod@3.25.76` registry record | ESM/CJS dual-package hazard | Dual — `type: "module"` but `main: "./index.cjs"` is also set; safe to bundle as CJS. |
| `ink@7`, `react@19`, `commander`, `chalk@5`, `undici`, `bonjour-service`, `selfsigned`, `strip-ansi` | TUI chain + transport deps | All pure JS, no native bindings, no node-gyp, no prebuilt binaries. |
| Node 22.13+ release notes / SEA docs | Action surface for the single-exe bake | Single Executable Applications shipped stable in Node 23.6; ticker Node binaries at `nodejs.org` ship *with* SEA enabled (`node_use_sea=ON`). Homebrew's `node` formula may not. |

### Implication for the "five supported RIDs"

The Node SEA prebuilt set has exactly five tacked-down RIDs at the time of writing:
`darwin-arm64`, `darwin-x64`, `linux-arm64`, `linux-x64`, `win-x64`. That is the number the
design points at and the number the recipe will produce — no extension or contraction.

## Proposed bundler config delta

Produced as a description, not a verbatim copy of upstream `tsup.config.ts`. After Task 2.1
lands, this delta is applied to `dash/tsup.config.ts`. Upstream's config remains in the merge
zone; we add an adjacent KyberDash extension rather than edit inline (consistent with R14.6,
which keeps merge surface flat).

Delta (in words, to avoid reproducing the upstream file):

1. **`format`**: extend to a two-element array `[ 'esm', 'cjs' ]`. ESM is preserved unchanged
   for existing consumers; CJS is the entry Node SEA consumes.
2. **`outDir`**: keep `dist`. ESM writes go to `dist/`; with `splitting: false` (already set),
   the CJS bundle lands as `dist/index.cjs` so `bin: "dist/cli.js"` continues to resolve the
   launcher for the ESM path.
3. **`external`**: drop the two items. `@modelcontextprotocol/sdk` and `zod` are both
   dual-package; inlining them into the CJS bundle is the only way a single executable can
   exist without a sidecar `node_modules`. The ESM bundle remains usable for the in-tree
   `npm run dev` and `node` invocations unchanged.
4. **`target`**: stay `node20` — Node 22.13+ is the supported runtime (per upstream
   `engines`); SEA injection requires a SEA-enabled Node binary of the chosen RID.
5. **Sourcemap**: keep on for the ESM output, optionally off for the CJS output (smaller
   single-executable).

The launcher at `src/cli.ts` does not need to be touched. Node SEA boots `dist/cli.js`
identically whether the script ultimately loads the CJS bundle (via a tiny shim registered
in the SEA blob's `main` field) or the ESM bundle (the current behaviour for `npm run dev`).

## Predicted single-executable build, per RID

For each RID in the supported set, the recipe (to be wired by Task 12.1) is:

1. Resolve the SEA-capable Node prebuilt for the target RID from `nodejs.org/dist/`.
2. Cross-copy that Node binary to `release/<kyber-exe>` for the chosen RID.
3. Embed the CJS bundle with `node --build-sea sea-config.json` to produce `app.blob`,
   where `sea-config.json` lists `main: "dist/index.cjs"` and `disableExperimentalSEAWarning: true`.
4. `npx postject` injects `app.blob` into the Node copy at section `NODE_SEA` with the
   sentinel fuse `NODE_SEA_FUSE_fce680ab2cc467b6e072b8b10df3c3e6582d4e8b`.
5. Generate `SHA-256` next to the binary; `install.sh` (already in `scripts/`) verifies it.

The five artefacts are independent artefacts; no shared cache is required at install time.
The installer (`scripts/install.sh`, already present and currently shipping the prior
`codeburn` binary) is the one place where checksums are verified and the artifact is placed.

### Caveats captured by this spike

- **macOS Gatekeeper / codesigning**: postject replaces the codesignature; the produced
  binary must be re-signed `ad-hoc` (`codesign --force --deep --sign - ./bin/darwin-arm64`)
  so a user only needs to `xattr -d com.apple.quarantine ./bin/darber`, not elevated
  privileges. This satisfies R13.3 (no elevated privileges).
- **Homebrew Node formula**: may not have SEA enabled. The build pipeline must download
  the official prebuilt from `nodejs.org/dist/`, not reuse `/opt/homebrew/bin/node`.
  Otherwise `--build-sea` returns *"Single executable application is disabled."* on every
  RID. (Verified locally on `node@26.7.0` from Homebrew as a control.)
- **SEA fuse**: every RID-injected blob must use the canonical Node SEA fuse; deviating
  silently produces a binary that appears to start but exits with no diagnostic.
- **`src/cli.ts` launcher**: must remain ESM-shaped so `npm run dev` still works. The CJS
  entry exists alongside it; SEA consumes only the CJS bundle.

## Fallback decision — not exercised

The design reserves the fallback *pinned-runtime archive with launcher* for the case where
the CommonJS entry cannot load the terminal UI dependency chain (notably `ink` and `react`).
That case does **not** trigger here. The TUI chain bundles cleanly because:

- `ink@7` is ESM-only but bundled by tsup/esbuild into the CJS output. There is no
  runtime `require()` call on `ink` — `ink`'s exports are linked statically.
- `react@19` is dual; `ink`'s React reconciler is built into the bundle.
- No pointer into `ink` / `react` reaches a `native` or `import.meta`-only path.
- `src/main.ts` has no top-level await; CJS output is legal.

The fallback remains referenced in `design.md` for completeness; this spike confirms the
primary path is viable without it.

## Decision outcome (drafted for `design.md`)

See `../../docs/specs/kyberdash/design.md` — a new entry **D7a** is appended that records
this spike's validation of D7. **Do not** mark `tasks.md` checkboxes from this artifact —
that is the task of the spec conductor who closes Task 1.

## Limits of this spike

No literal build was performed because `dash/` is not vendored yet (Task 2.1). When Task 2.1
lands and `dash/` is present, the recipe above should be executed end-to-end and the
parity-gate pattern (output of single-exe must match unbundled CLI on a real corpus) re-run
to upgrade this decision from "design-stage prediction" to "demonstrated parity." Until
then, every individual link of the chain has been verified against the registry / source
independent of the spike.
