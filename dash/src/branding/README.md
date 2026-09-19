# User-facing brand overlay

KyberDash is first-party code (ADR 0020): the package, the binary and the
surfaces all carry the KyberDash identity directly. This directory holds the
one place a surface reads the brand from, so no renderer hard-codes a string
it might later disagree with.

| Surface | Source |
| --- | --- |
| Installed binary | `scripts/install.sh` and the SEA job name it `kyberdash` |
| CLI help (`Usage:`) | `dash/src/brand-overlay.ts` → `program.name(resolveCliName())` |
| Web title / favicon | `index.html` plus `applyHtmlBrand` when the CLI serves `index.html` |
| Web chrome logo | `LightsaberLogo` / `kyberdash-logo.*` |

`KYBERDASH_CLI_NAME` overrides the display command if a wrapper needs it.
