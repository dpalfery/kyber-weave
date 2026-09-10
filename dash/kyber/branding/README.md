# User-facing brand overlay

KyberDash is a `git subtree` of `getagentseal/codeburn`. The **npm package,
bin entry `codeburn`, `CODEBURN_*` environment variables, and upstream file
names** stay as Codeburn ships them. That is what keeps three-way merges
cheap.

What the user sees is overlaid:

| Surface | Overlay |
| --- | --- |
| Installed binary | `scripts/install.sh` and the SEA job name it `kyberdash` |
| CLI help (`Usage:`) | `dash/src/brand-overlay.ts` → `program.name(resolveCliName())` |
| Web title / favicon | `index.html` plus `applyHtmlBrand` when the CLI serves `index.html` |
| Web chrome logo | `LightsaberLogo` / `kyberdash-logo.*` (keep `codeburn-logo.png` on disk) |

`KYBERDASH_CLI_NAME` overrides the display command if a wrapper needs it.

Do not rename `dash/`, `dash/package.json` `"name"`, or delete unshipped
upstream trees (`dash/windows`, `dash/gnome`).
