# dash/kyber/ — KyberDash merge zone

This directory is the **only** place KyberDash code lives inside the
`dash/` tree. Everything adjacent to it is vendored upstream and is off
limits to direct edits.

## The rule

KyberDash ships code **only** under `dash/kyber/**`. It does **not** edit,
add, or delete files under:

| Path              | Owner                                  | Edit? |
| ----------------- | -------------------------------------- | ----- |
| `dash/src/**`     | upstream `codeburn` core               | **no** — read-only conflict surface |
| `dash/dash/**`    | upstream React dashboard (extended)    | **adapt at boundary, never inside** |
| `dash/app/**`     | upstream Electron app (extended)       | **adapt at boundary, never inside** |
| `dash/mac/**`     | upstream Swift menu-bar (extended)     | **adapt at boundary, never inside** |
| `dash/windows/**` | upstream, unshipped                    | leave untouched (R14.4) |
| `dash/gnome/**`   | upstream, unshipped                    | leave untouched (R14.4) |
| `dash/kyber/**`   | **KyberDash only**                     | yes — this is the merge zone |

The "adapt at boundary, never inside merge zone" rule came from design
decision **D6** — when we need to change how upstream behaves, we do it by
consuming or shimming upstream's output, not by patching its source.

## Why this matters

Acceptance criteria:

- **R14.1** — when upstream publishes changes, KyberDash takes them through
  a three-way `git subtree` merge rather than a hand-port of forks. That
  only works if upstream's directories stay structurally intact.
- **R14.2** — when KyberDash code is added, it lives outside upstream's
  directories and consumes upstream's output rather than modifying its
  internals. That keeps our diffs limited to `dash/kyber/**`.
- **R14.4** — when an upstream directory serves a surface KyberDash does
  not ship (e.g. Windows, GNOME), we leave it in place unmodified.
  Deleting such a directory manufactures a conflict on every future merge.

## How upstream arrives here

`dash/` is a `git subtree` import of
[`getagentseal/codeburn`](https://github.com/getagentseal/codeburn)
(MIT, TypeScript). The upstream remote is registered as `codeburn`:

```bash
git remote -v
# codeburn   https://github.com/getagentseal/codeburn.git (fetch)
# codeburn   https://github.com/getagentseal/codeburn.git (push)

# Pull a new upstream release (squashed into a single merge commit):
git subtree pull --prefix=dash codeburn <ref> --squash
```

KyberWeave does not build upstream directly; it consumes upstream's
published output. Day-to-day changes stay in `dash/kyber/**` and its peer
subdirectories inside the merge zone.

## User-facing names (brand overlay)

Do **not** rename the subtree, `dash/package.json` `"name": "codeburn"`,
`CODEBURN_*` env vars, or `dash/dash/public/codeburn-logo.png`. Those are
upstream identity; renaming them is a conflict on every pull.

Install and SEA already ship the binary as `kyberdash`. Display names live
in `dash/src/brand-overlay.ts` (CLI `Usage:` line, served HTML title/favicon)
and in the React chrome (`LightsaberLogo`, `kyberdash-logo.*`). Extra bin
alias: `dash/package.json` `"bin"."kyberdash"`.

See [branding/README.md](branding/README.md).

## Pulling Codeburn upstream

```bash
git fetch codeburn
git subtree pull --prefix=dash codeburn <tag-or-sha> --squash
```

Expect conflicts only on files we already shim (`dash/src/main.ts`,
`dash/src/web-dashboard.ts`, `dash/src/dashboard.tsx`, `dash/dash/index.html`,
and other intentional KyberDash boundary edits). Keep `dash/src/brand-overlay.ts`
(ours). Leave `dash/windows/**` and `dash/gnome/**` untouched. Re-run
`MergeBoundaryTests`.

## See also

- `docs/specs/kyberdash/design.md` — Repository layout and merge-zone
  table; decisions D1, D2, D6, D9.
- `docs/specs/kyberdash/tasks.md` — Task 2.1 establishes this boundary.
- `tests/KyberWeave.Tests/MergeBoundaryTests.cs` — enforces that no
  KyberDash source lands under `dash/src/`.
