# `@dpalfery/kyber-weave`

Thin Node wrapper around **self-contained** Kyber-Weave binaries (`kyber-weave`, `kyber-weave-mcp`). End users do **not** need a .NET runtime.

## How binaries are obtained

1. `npm i -g @dpalfery/kyber-weave` installs this small package.
2. **postinstall** (and first CLI run if scripts were skipped) downloads the matching GitHub Release assets for your platform:
   - Tag = `v` + `package.json` `version` (e.g. `0.1.0` → `v0.1.0`)
   - Assets: `kyber-weave-<rid>.tar.gz` / `.zip` and `kyber-weave-mcp-<rid>.tar.gz` / `.zip`
3. Binaries land under `vendor/<rid>/` inside the package install directory.

Supported RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`.

### Overrides

| Variable | Effect |
| --- | --- |
| `KYBER_WEAVE_SKIP_DOWNLOAD=1` | Skip postinstall download |
| `KYBER_WEAVE_BINARY_DIR=/path` | Use binaries from this directory instead of vendor |

## Commands

```bash
npm i -g @dpalfery/kyber-weave
kyber-weave --help
kyber-weave-mcp --help
```

## Offline / air-gapped

Download Release assets from https://github.com/dpalfery/kyber-weave/releases, extract, and set `KYBER_WEAVE_BINARY_DIR` to that folder (must contain `kyber-weave` and `kyber-weave-mcp`, with `.exe` on Windows).

## Licence

MIT — see the repository root [LICENSE](https://github.com/dpalfery/kyber-weave/blob/main/LICENSE).
