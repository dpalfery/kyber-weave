# design-sync notes — codeburn-desktop

The synced design system is the **Electron renderer** at `dash/app/renderer/components`
(package `codeburn-desktop`). It is an app, not a published component library, so several
things had to be built for the sync. Read this before re-running.

## The exact build command

```sh
# 1. regenerate declarations + combined stylesheet (cfg.buildCmd)
cd dash/app && npx tsc -p tsconfig.design-sync.json \
  && cat renderer/styles/indigo.css renderer/styles/plain.css > .ds-sync-styles.css

# 2. converter (from the repo root) — note the --entry flag, it is required
node .ds-sync/package-build.mjs --config .design-sync/config.json \
  --node-modules dash/app/node_modules --entry dash/app/ds-sync-entry.tsx --out ./ds-bundle
node .ds-sync/package-validate.mjs ./ds-bundle
```

`npm ci` in `dash/app` needs `ELECTRON_SKIP_BINARY_DOWNLOAD=1` — the components only need
react/react-dom, and the Electron binary is a ~150MB download the sync never uses.

## Why each piece of scaffolding exists

- **`dash/app/ds-sync-entry.tsx`** (committed) — the package has no library build, so this
  barrel is the `--entry`. It re-exports every component file except `Splash.tsx`.
  **Do not rename it to a dot-prefixed name**: the converter's dts extractor globs
  `types/**/*.d.ts` with fast-glob, which skips dotfiles, and the props extraction silently
  degrades to `[key: string]: unknown` for every component.
- **`dash/app/tsconfig.design-sync.json`** (committed) + **`dash/app/types/`** (gitignored) —
  the package ships no `.d.ts`, and the converter reads prop contracts *only* from a `.d.ts`
  tree. Without this every emitted `<Name>Props` is `[key: string]: unknown` and the design
  agent gets no API at all. `package.json` gained `"types": "types/ds-sync-entry.d.ts"` so
  `findTypesRoot`/`projectFor` resolve the tree; the field is inert for the app itself
  (private package, nothing consumes its types).
- **`dash/app/.ds-sync-styles.css`** (gitignored, generated) — **the single most important
  fix.** `main.tsx` imports `indigo.css` *then* `plain.css`. `indigo.css` is the structural
  layer (all the `display:flex`, padding, radius, backgrounds — it is the verbatim wireframe
  port); `plain.css` is only the light-theme override (`border-color`, `box-shadow`, type
  sizes). Pointing `cssEntry` at `plain.css` alone renders every component as unstyled serif
  text with tokens but no layout. Concatenation order matters — indigo first.

## Excluded / changed

- **`Splash` is not synced.** It imports `../assets/splash-loader.webm`; the converter's
  esbuild loader map covers `.svg/.png/.woff/.woff2` only, and `lib/bundle.mjs` must not be
  forked (it defines the output contract). Nothing else imports Splash except `App.tsx`.
- **5 provider logos were converted `.jpg` → `.png`** (`cursor-agent`, `openclaw`, `warp`,
  `zcode`, `zed`) in `renderer/assets/providers/`, with `ProviderLogo.tsx` imports updated —
  same loader-map limitation. Approved by the repo owner during the first sync.
- **`dash/app/ds-sync-bridge-stub.ts`** (committed) — see "The Electron bridge and its
  fixtures" below. Imported first by the barrel; nothing under `renderer/` imports it.
- `dash/app/package.json` gained only the `types` field. No component source was changed apart
  from `ProviderLogo.tsx`'s five image imports.

## Environment

- **Playwright must be 1.62.0**, installed into `.ds-sync/`. The repo pins 1.63.0, which
  wants chromium-1243; the machine cache has chromium-1234, which 1.62.0 pins. A mismatch
  fails with `browserType.launch: Executable doesn't exist`.

## Authoring previews

- Previews import from the package name: `import { Panel } from 'codeburn-desktop'`.
  JSX is `automatic` — no React import needed.
- The DS is **plain CSS classes**, not props-as-styles: components emit `.panel`, `.phead`,
  `.pbody`, `.li`, `.lx`, `.val`, `.chev`, `.stat`, `.hint`, `.empty-note`, and all styling
  lives in the two stylesheets. Layout glue in a preview should use those classes or the
  `var(--sp-*)` spacing scale, never invented class names.
- **Compose rows inside their container.** `ListRow`, `EmptyNote` and friends are designed to
  sit in a `Panel` body; previewing one bare looks broken even though it renders.
- No provider wrapper is needed. The only React context (`RefreshCadenceContext` in
  `renderer/lib/refreshCadence.tsx`) is created with a real default value, so components that
  read it render fine unwrapped.
- **The Electron bridge and its fixtures.** `renderer/lib/ipc.ts` captures `window.codeburn`
  at module scope, so it is import-safe but *unusable* unless a bridge exists before the
  bundle evaluates. `dash/app/ds-sync-bridge-stub.ts` (imported first by the barrel — keep it
  first) installs a neutral one when absent: every method resolves to `null`, `platform`/`arch`
  are the only non-method members. Components therefore render their real empty state instead
  of throwing. A preview opts into realistic input per card:

  ```tsx
  // module scope in the preview file, or the first lines of the exported cell
  window.codeburn.__fixtures.getUpdateStatus = { version: '0.9.24', url: '…' }
  window.codeburn.__fixtures.onUpdateStatus = (cb) => cb({ available: true })   // subscriptions
  ```

  A fixture may be a value or a function of the call args. Fixtures set at module scope apply
  to every cell in that file; set them inside the exported function for per-cell data.
- Realistic content for this DS is AI-coding spend data: project names, model ids
  (`claude-opus-5`), session counts, USD costs, token counts.

## Known render warns

- `SectionSkeleton` sheets are **not pixel-reproducible**: `.skel::after` runs a 1.3s shimmer
  loop, so the sweep position differs between captures. A diff there is not a regression.
- **`[FONT_MISSING]` for "SF Pro Text" and "Berkeley Mono" is expected and correct.** Both are
  named *inside* fallback stacks whose first entry is already a system font
  (`--sans: system-ui, -apple-system, "SF Pro Text", "Segoe UI", sans-serif`;
  `--mono: ui-monospace, "SF Mono", "Berkeley Mono", Menlo, Consolas, monospace`). The repo
  ships no font files and never intended to — neither family is redistributable (SF Pro is
  Apple-licensed, Berkeley Mono is commercial), and on macOS `system-ui`/`ui-monospace` resolve
  to the very same faces. Nothing to add via `cfg.extraFonts`. Deliberately NOT suppressed with
  `cfg.runtimeFontPrefixes`: no font service serves these, and asserting one would mislead the
  next sync.
- `ErrorBoundary`'s two crash cells are also not pixel-reproducible: the rendered component
  stack embeds the preview server's ephemeral port and source line numbers. React also logs
  the caught error to the console — expected, not a failure.

## Capture-time traps

- **The capture clock is pinned to 2024-05-15** (`page.clock.setFixedTime` in
  `package-capture.mjs`). Any component that windows on `new Date()` renders empty if its
  preview uses fixed 2026 dates — `ActivityHeatmap` generates its date keys relative to
  `new Date()` for exactly this reason. Curated fixed dates are fine for everything that does
  not window on "now".
- `Sankey` data must balance per node or ribbons walk off the bar (`segmentSize` divides by
  node cost and accumulates offsets). `shortenProjectPath` splits on hyphens when there is no
  path separator, so a project id must be a real path (`kyber-weave` renders as `kyber/weave`).
  Dated model ids (`claude-opus-5-20260815`) overrun the right-anchored label column — use the
  short house-style ids in stories.

## Re-sync risks

- `ds-sync-entry.tsx` is hand-maintained. **A component added to
  `renderer/components/` will not sync until it is added there AND to
  `cfg.componentSrcMap`** (the map is the component list — with an explicit `--entry` the
  converter cannot auto-discover, because `deriveComponentsFromSrc` only runs in synth-entry
  mode). A removed component must be dropped from both.
- `cfg.dtsPropsFor` hand-writes contracts for `ErrorBoundary` (its props type is a local
  `Props`, not `ErrorBoundaryProps`) and for the three no-prop components (`ToastHost`,
  `SwitchingBanner`, `UpdateBanner`). If those components gain real props, delete the entry
  and let extraction run.
- The `.ds-sync-styles.css` concatenation is a snapshot. If the app ever adds a third
  stylesheet to `main.tsx`, add it to `cfg.buildCmd` in the same order or previews silently
  lose styling.
- `dash/app/types/` and `.ds-sync-styles.css` are gitignored, so a fresh clone must run
  `cfg.buildCmd` before the converter or the build reports zero props and unstyled cards.

## Config decisions carried from the first sync

- `cfg.overrides`: `Sankey` and `Punchcard` are `cardMode: "column"` — both hard-code
  `minWidth: 560` and the default grid cell (~420px at the 900px card viewport) clips them.
  `ToastHost` is `cardMode: "single"` — it portals to `document.body` at `position: fixed`.
  `ActivityHeatmap` (self-scrolling) and `StackedBars` (fluid) need no override.
- `cfg.dtsPropsFor.Hint` inlines the `HintItem` shape. The extractor emits `items: HintItem[]`
  but does not follow the local type alias across the export, so the design agent would see a
  name it has no definition for. Any future component with a local named prop-item type will
  hit the same gap.
- `dash/app/ds-sync-entry.tsx` also re-exports `renderer/lib/toast`. Without it esbuild drops
  `showToast` entirely (no exported component imports it) and a preview that imports
  `lib/toast` gets a second module instance whose state the global `ToastHost` never reads.

## API observations worth keeping (found while authoring)

- **`Sidebar.status` is a dead prop.** It is in the type, `App.tsx` passes a live `<StatusLine>`
  into it, and both stylesheets carry `.sb .status` rules — but `Sidebar.tsx` destructures only
  `{ active, onNavigate }` and renders nothing. Looks like a dropped feature. Flagged in the
  emitted contract; app source deliberately untouched.
- **Nothing `Dropdown`-backed can be shown open.** `open` is private `useState` with no prop,
  and the same holds for `TopBar`'s private `CalendarPop` and `ConfigPicker`. That makes the
  open listbox, the `RangeCalendar` popover, and `Dropdown`'s `footer` prop unrenderable in
  *any* preview — `footer` cannot be demonstrated at all.
- **`CliError`'s variant axis is not `kind`.** `cliErrorDisplay` branches four ways in an order
  that overrides kind: `cold === true`, then `not-found`, then a regex-matched permission
  `nonzero`, then a red catch-all. `bad-json`/`timeout`/`too-large`/`bad-args` are visually
  identical. `subject` is read only on the `not-found` branch.
- **`TeamTabContent` has no call site** anywhere in `renderer/`. It and `useTeamTabs` are a
  registry surface the app never mounts.
- `ProviderLogo`'s themed pairs are only half-capturable: the harness sets no `data-theme` and
  headless Chromium reports light, so `.pl-dark` is never visible in a sheet.
- The bridge stub's `platform` lives in fixed `SCALARS`, not `__fixtures`, so keycaps always
  render `⌘`; a Windows-keycap story is not authorable without extending the stub.

## Fixture gotchas (bridge-driven components)

- A synchronous `onUpdateStatus` push is silently overwritten when `getUpdateStatus` resolves —
  defer the push with `setTimeout(…, 0)`.
- `localStorage` leaks between cells on the shared preview origin; clear what you set.
- The pinned capture clock does **not** stop `setTimeout`, so toast stories need a long
  `durationMs` or the toast auto-dismisses before the screenshot.

## The emitted-contract problem (why `dtsPropsFor` has 19 entries)

The converter's dts extractor has two systematic gaps against this repo:

1. It does not follow **local type aliases** across the export — it emits `items: HintItem[]`,
   `value: DateRange`, `payload: MenubarPayload` and so on while never defining those names, so
   the design agent reads a contract full of unresolvable identifiers.
2. It **drops null unions** — tsc emits `value: DateRange | null` for `RangeCalendar` and
   `customRange: DateRange | null` / `configSource: string | null` for `TopBar`; the bundle
   emitted the non-null form, and `null` is the normal case in most real call sites.

`cfg.dtsPropsFor` therefore hand-writes self-contained bodies for the 13 affected components
(plus the 4 original extraction failures and `Hint`). **These are the highest-rot part of this
config**: they duplicate shapes from `renderer/lib/types.ts` and the component sources. On any
re-sync, diff them against `dash/app/types/renderer/components/*.d.ts` (tsc's own output, which
is always accurate) and update. If a future converter version fixes either gap, delete the
corresponding entries and let extraction run.

## First sync outcome (2026-09-05)

30 components, 114 authored preview cells, all graded `good`; render check 30/30 clean, zero
floor cards. Uploaded to Claude Design project `KyberDash UI`
(`6ac3a9c9-1627-4aaa-9336-7739411ba3f7`, pinned in config.json).

Verification state lives in the uploaded `_ds_sync.json`, not in git — a re-sync on any machine
fetches it and skips unchanged components. The local `.design-sync/.cache/` copy is gitignored
and disposable. `.design-sync/previews/` IS committed and is what makes a re-sync cheap.
