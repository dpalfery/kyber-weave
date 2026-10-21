---
id: standards/go
title: "Go coding standard"
doc-type: coding-standard
status: draft
technology: go
owner: unassigned
last-reviewed: 2026-10-21
---

# Go coding standard

How Go is written in this repository. Agents and skills resolve this document as
`<go-coding-standard>`.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Toolchain:** the current stable Go release. `go.mod` states the minimum language version
  in the `go` directive and pins the build toolchain in the `toolchain` directive. Raise
  either deliberately, in its own change.
- **Modules:** one module per repository unless there is a stated reason for more. `go.sum` is
  committed. No `replace` directive pointing at a local path is committed.
- **Layout:** commands in `cmd/<name>/`, packages no other module may import under
  `internal/`. `main` is thin: it parses flags, builds dependencies, and calls a package.
- **cgo:** off (`CGO_ENABLED=0`) for shipped binaries. Prefer pure-Go dependencies so that
  stays possible; a cgo dependency needs a reason in the change description.

Application code consumes collaborators through parameters. It accepts interfaces, returns
concrete types, and does not construct clients, connections, or clocks internally or read a
package-level variable for something a caller could pass.

## The formatter and the linters are the first reviewers

`gofmt` (or `goimports`), `go vet`, and `staticcheck` are authoritative for formatting and
mechanical rules. Do not restate those rules here, and do not merge code that fails them.

- **Lint:** `staticcheck ./...`. A repository that already uses `golangci-lint` keeps it, with
  `staticcheck` and `govet` enabled. A suppression names the check and says why on the same
  line.
- **Naming:** MixedCaps, never underscores. Initialisms keep one case (`ID`, `URL`, `HTTP`).
  Package names are short, lowercase, and singular — never `util`, `common`, or `helpers`.
  Exported names are not stuttered (`config.Load`, not `config.LoadConfig`).
- Every exported identifier has a doc comment starting with its name.

## Errors

- Wrap with context using `fmt.Errorf("<operation> <input>: %w", err)`. Use `%v` only to
  deliberately cut the chain at an API boundary.
- Callers branch with `errors.Is` (exported sentinel values) or `errors.As` (exported error
  types). Never compare `err.Error()` strings.
- An error is handled once: returned or logged, not both.
- No `panic` for expected failure; libraries return errors. `log.Fatal` and `os.Exit` appear
  only in `main`.

## Context

- `ctx context.Context` is the first parameter of every function that does I/O or blocks.
- `context.Background()` appears only in `main`, tests, and the root of a background job.
  `context.TODO()` does not ship.
- Contexts are never stored in structs. Every `WithTimeout` / `WithCancel` is followed by
  `defer cancel()`.

## Concurrency

- Every goroutine has a defined exit: it returns when its context is cancelled or its input
  channel closes. The code that starts it waits for it (`sync.WaitGroup` or
  `golang.org/x/sync/errgroup`).
- The sender closes a channel; receivers never do. Channels are unbuffered unless a buffer
  size has a stated reason. Function signatures use directional channel types.
- Shared mutable state is guarded by a `sync.Mutex` or confined to one goroutine — one or the
  other per piece of state.
- `go test -race` passes. A data race is a bug even when the test is green.
- Services shut down gracefully on `os.Interrupt` and `SIGTERM` via `signal.NotifyContext`.

## Safety

- `os/exec` takes an argument list; never pass interpolated input to `sh -c`.
- SQL is parameterized. Templates that render HTML use `html/template`, not `text/template`.
- Secrets come from the environment or a secret store, never a literal. Do not log them.
- `govulncheck ./...` passes before a dependency upgrade merges.

## Cross-platform

The shipped binaries build for:

| OS | `GOOS` / `GOARCH` |
|---|---|
| macOS | `darwin/arm64`, `darwin/amd64` |
| Linux | `linux/amd64`, `linux/arm64` |
| Windows | `windows/amd64` |

- File-system paths use `path/filepath`, never `path` or string concatenation.
- Platform-specific code lives in `_unix.go` / `_windows.go` files or behind `//go:build`
  constraints, each with a counterpart for the other OS.
- Release builds use `-trimpath` and stamp the version with `-ldflags "-X ..."`. Release
  automation belongs to `github-devops`.

## Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in as interfaces, no package-level mutable state. Test
authorship belongs to `test-dev`.

## Commands

The same on macOS, Linux, and Windows (PowerShell; `-race` needs a C compiler on `PATH`):

```bash
gofmt -l .                # prints nothing
go vet ./...
staticcheck ./...
go build ./...
go mod tidy -diff         # Go 1.23+; no output means go.mod and go.sum are tidy
go test -race ./...
```
