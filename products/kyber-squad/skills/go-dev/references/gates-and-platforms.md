---
name: go-dev/gates-and-platforms
description: Running gofmt, go vet, staticcheck, build, and go test with the race detector on macOS, Linux, and Windows before reporting done.
---

# Build, Vet, and Test Gates

The exact commands and linter configuration are in **<go-coding-standard>**. When the standard
names a command, run that one; the table below is the shape it usually takes.

| Gate | Command | Passes when |
|---|---|---|
| Format | `gofmt -l .` (or `goimports -l .`) | Prints nothing |
| Vet | `go vet ./...` | Exit 0 |
| Lint | `staticcheck ./...` or `golangci-lint run` — whichever the standard names | Exit 0 |
| Build | `go build ./...` | Exit 0 |
| Modules | `go mod tidy -diff` (Go 1.23+) or `go mod tidy` then `git diff --exit-code go.mod go.sum` | No diff |
| Test | `go test -race ./...` | Exit 0 — authored by `test-dev`, but run it to prove you broke nothing |

Fix the cause of a finding. A `//lint:ignore` or `//nolint` directive names the check and says
why on the same line.

## Per platform

**macOS and Linux** — the toolchain is self-contained. `-race` needs cgo, so a C compiler must
be present (Xcode command-line tools; `build-essential`). If `CGO_ENABLED=0` is set for
release builds, still run tests with cgo on so the race detector works.

**Windows** — `-race` also needs cgo, which means a GCC-compatible compiler (MinGW-w64 via
MSYS2 or the standard's choice) on `PATH`. Without one, run `go test ./...` and report in the
completion digest that the race detector did not run. Watch for:

- Paths — use `path/filepath`, not `path`, for file-system paths; `filepath.Join`, never `+ "/"`.
- Line endings — `gofmt` emits LF; a `.gitattributes` keeps Windows checkouts from tripping it.
- Signals and syscalls — Unix-only code lives in `_unix.go` files or behind
  `//go:build !windows`, with a Windows counterpart, or the build fails on the other OS.

Check the other OS without a second machine:

```bash
GOOS=windows go vet ./...
GOOS=linux go build ./...
```

When you could not run a gate on an OS, say which in the completion digest.
