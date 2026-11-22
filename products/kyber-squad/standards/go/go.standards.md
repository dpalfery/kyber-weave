---
id: standards/go
title: "Go coding standard"
doc-type: coding-standard
status: draft
technology: go
owner: unassigned
last-reviewed: 2026-11-22
---

# Go coding standard

How Go is written in this repository. Agents and skills resolve this document as
`<go-coding-standard>`.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Naming and layout

### Stack

- **Toolchain:** the current stable Go release. `go.mod` states the minimum language version
  in the `go` directive and pins the build toolchain in the `toolchain` directive.
- **Modules:** one module per repository unless there is a stated reason for more. `go.sum` is
  committed.
- **Layout:** commands in `cmd/<name>/`, packages no other module may import under
  `internal/`. `main` is thin: it parses flags, builds dependencies, and calls a package.
- **cgo:** off (`CGO_ENABLED=0`) for shipped binaries. Prefer pure-Go dependencies so that
  stays possible; a cgo dependency needs a reason in the change description.

Application code consumes collaborators through parameters. It accepts interfaces, returns
concrete types, and does not construct clients, connections, or clocks internally or read a
package-level variable for something a caller could pass.

### Modules and dependencies

- Add a dependency with `go get example.com/mod@vX.Y.Z`, then `go mod tidy`.
- A `replace` directive pointing at a fork carries a comment saying why and when it can go.
- Vendor (`go mod vendor`) only when the repository already does; if `vendor/` exists, re-run
  `go mod vendor` after every dependency change.
- Before an upgrade, `govulncheck ./...` shows whether a known vulnerability is reachable.

### Cross-platform

The shipped binaries build for:

| OS | `GOOS` / `GOARCH` |
|---|---|
| macOS | `darwin/arm64`, `darwin/amd64` |
| Linux | `linux/amd64`, `linux/arm64` |
| Windows | `windows/amd64` |

- File-system paths use `path/filepath`, never `path` or string concatenation.
- Platform-specific code lives in `_unix.go` / `_windows.go` files or behind `//go:build`
  constraints, each with a counterpart for the other OS.
- Release builds use `-trimpath` and stamp the version with `-ldflags "-s -w -X main.version=..."`.
  Release automation belongs to `devops`; hand it the target list and the build command.
- Pure Go cross-compiles with environment variables alone, for example
  `GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -o dist/app-linux-amd64 ./cmd/app`.
  `CGO_ENABLED=0` gives a static binary and needs no cross C toolchain; prefer pure-Go
  dependencies (`modernc.org/sqlite` over `mattn/go-sqlite3`) so that stays true.
- If cgo is unavoidable, each target needs a C cross-compiler (`zig cc` is common); say so
  when handing the build to `devops`.
- Check the other OS without a second machine: `GOOS=windows go vet ./...` and
  `GOOS=linux go build ./...`.

## Coding standards

### Errors

- Wrap with context using `fmt.Errorf("<operation> <input>: %w", err)`. Use `%v` only to
  deliberately cut the chain at an API boundary.
- An error is handled once: returned or logged, not both.
- The message adds what this frame knows (the operation and its key input): lowercase, no
  trailing punctuation, no "failed to" repeated at every level.
- Pick the shape by need: an exported sentinel (`var ErrNotFound = errors.New("not found")`)
  for a fixed condition, an exported type for a condition carrying data, and a plain wrapped
  `fmt.Errorf` when nothing branches on it. Do not export a type nobody inspects.
- `panic` is for programmer errors that cannot be recovered (an impossible switch arm, a nil
  dependency at construction). No `panic` for expected failure; libraries return errors.
  `log.Fatal` and `os.Exit` appear only in `main`.

### Context

- `ctx context.Context` is the first parameter of every function that does I/O, blocks, or
  calls something that does.
- `context.Background()` appears only in `main`, tests, and the root of a background job.
- A timeout is set where the budget is known. Long loops check `ctx.Err()` or `<-ctx.Done()`
  so cancellation is prompt.
- Context values carry request-scoped data (trace ids), never optional parameters.

### Concurrency

- A goroutine exits when its context is cancelled or its input channel closes; wait with
  `sync.WaitGroup` or `golang.org/x/sync/errgroup`.
- Channels are unbuffered unless a buffer size has a stated reason. Function signatures use
  directional channel types.
- Every blocking point selects on `ctx.Done()`.
- Shared mutable state is guarded by a `sync.Mutex` or confined to one goroutine — one or the
  other per piece of state. Hold a mutex for the shortest span.
- `http.ErrServerClosed` from `ListenAndServe` after `Shutdown` is not a failure.
- `go test -race` passes. A data race is a bug even when the test is green.
- Fan-out with errors uses `errgroup.WithContext` and `g.SetLimit(n)`; the first error cancels
  the context for the rest, and `g.Wait()` returns it wrapped. Below Go 1.22 the loop variable
  is shared by every closure, so copy it (`item := item`) before `g.Go`; check the `go`
  directive before deleting the copy.
- `go vet` reports a copied `sync.Mutex`. `sync/atomic` types (`atomic.Int64`) are for
  counters and flags only.
- A `select` with a `default` is a busy loop unless something else blocks.
- Services shut down gracefully on `os.Interrupt` and `SIGTERM` via `signal.NotifyContext`.
  Bound the drain with `context.WithTimeout`: call `srv.Shutdown` with it and wait for it to
  finish before `main` returns. `os.Interrupt` covers Ctrl+C on every platform;
  `syscall.SIGTERM` is never delivered on Windows, and a Windows service needs
  `golang.org/x/sys/windows/svc` if the standard requires one.

## Testing

### Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in as interfaces, no package-level mutable state. A task whose
deliverable is tests goes to a developer tier like any other.

## Commands

### The formatter and the linters are the first reviewers

`gofmt` (or `goimports`), `go vet`, and `staticcheck` are authoritative for formatting and
mechanical rules. Do not restate those rules here, and do not merge code that fails them.

- **Gates:** format (`gofmt -l .`, prints nothing), vet, lint, build, modules
  (`go mod tidy -diff`, Go 1.23+, or `go mod tidy` then `git diff --exit-code go.mod go.sum`),
  test. Fix the cause of a finding.
- **Lint:** `staticcheck ./...`. A repository that already uses `golangci-lint` keeps it, with
  `staticcheck` and `govet` enabled.
- **Naming:** MixedCaps, never underscores. Initialisms keep one case (`ID`, `URL`, `HTTP`).
  Package names are short, lowercase, and singular — never `util`, `common`, or `helpers`.
  Exported names are not stuttered (`config.Load`, not `config.LoadConfig`).
- Every exported identifier has a doc comment starting with its name.
- `-race` needs cgo, so a C compiler must be on `PATH` (Xcode command-line tools,
  `build-essential`, or MinGW-w64 on Windows). Run tests with cgo on even when release builds
  set `CGO_ENABLED=0`. If `-race` cannot run, drop it from the targeted run and say so in the
  completion digest, along with any gate you could not run on an OS.
- `gofmt` emits LF; a `.gitattributes` keeps Windows checkouts from tripping it.

## Security and data handling

### Safety

- `os/exec` takes an argument list; never pass interpolated input to `sh -c`.
- SQL is parameterized. Templates that render HTML use `html/template`, not `text/template`.
- Secrets come from the environment or a secret store.
- `govulncheck ./...` passes before a dependency upgrade merges.

## Done checklist

### Completion

The same on macOS, Linux, and Windows (PowerShell; `-race` needs a C compiler on `PATH`):

```bash
gofmt -l .                # the format check: prints the files that need it, and nothing when clean
go vet ./...
staticcheck ./...
go build ./...            # the build
go mod tidy -diff         # Go 1.23+; no output means go.mod and go.sum are tidy
go test -race ./...
```

`gofmt` is the formatter, and `-l` lists the files it would rewrite, so an empty list is the pass.
`go build ./...` is the build: it compiles every package in the module. The `go test` line is the
gate the review council and CI run. A dev agent never runs the full suite; it runs the tests
covering its change (`go test -race -run '<TestName>' ./<package>`).
