---
name: go-dev/modules-and-cross-compilation
description: Go module hygiene — go and toolchain directives, adding and upgrading dependencies, go.sum, vendoring — and GOOS/GOARCH cross-compilation with or without cgo.
---

# Modules and Cross-Compilation

Which Go version the module targets, and which dependencies the repository accepts, are in
**<go-coding-standard>**.

## `go.mod`

- The `go` directive is the minimum language version the module needs. The `toolchain`
  directive pins the toolchain that builds it. Do not raise either as a side effect of another
  change.
- Add a dependency with `go get example.com/mod@vX.Y.Z` — an explicit version, not `@latest`
  in a committed change. Then `go mod tidy`.
- `go.sum` is committed and never edited by hand.
- A `replace` directive pointing at a local path must not be committed. One pointing at a fork
  carries a comment saying why and when it can go.
- Vendor (`go mod vendor`) only when the standard says to; if `vendor/` exists, keep it in
  sync with `go mod vendor` after every dependency change.
- Before upgrading, `govulncheck ./...` shows whether a known vulnerability is reachable.

## Package layout

- Commands live in `cmd/<name>/main.go`; keep `main` thin — parse flags, build dependencies,
  call into a package.
- `internal/` for packages no other module should import.
- Package names are short, lowercase, singular, and never `util` or `common`.

## Cross-compilation

Pure Go cross-compiles with environment variables alone:

```bash
GOOS=linux   GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -o dist/app-linux-amd64 ./cmd/app
GOOS=darwin  GOARCH=arm64 CGO_ENABLED=0 go build -trimpath -o dist/app-darwin-arm64 ./cmd/app
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -o dist/app-windows-amd64.exe ./cmd/app
```

- `CGO_ENABLED=0` gives a static binary and removes the need for a cross C toolchain. Prefer
  pure-Go dependencies (for example `modernc.org/sqlite` over `mattn/go-sqlite3`) when the
  standard allows, so this stays true.
- If cgo is unavoidable, cross-compiling needs a C cross-compiler per target (`zig cc` is a
  common choice); say so when handing the build to `github-devops`.
- `-trimpath` and `-ldflags "-s -w -X main.version=…"` make builds reproducible and stamp the
  version. Release automation is `github-devops`; hand them the target list and the command.
