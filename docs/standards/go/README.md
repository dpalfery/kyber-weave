---
id: standards/go
title: Go coding standard
doc-type: coding-standard
status: current
technology: go
owner: dpalfery
last-reviewed: 2026-11-11
---

# Go coding standard

How Go is written in this repository, once it is. Agents and skills resolve this document as
`<go-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md).

## Status: no Go code yet

This repository has no Go code today. There is no `go.mod`, no `*.go` file, and no Go gate in
the repository's gate configuration. This standard applies when Go is introduced. It states the
language-level rules that change must follow, and it does not describe any Go code that exists.

The change that introduces the first Go file must also add the Go gates: `gofmt`, `go vet`,
`go build` and `go test`, declared as `review.task-gates` entries for `go-dev` and `test-dev` in
[`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) (plus `review.gates` for
the council run) and in the root `AGENTS.md` loop commands, and run before that change is reported done. Until then, no Go change has a
mechanical gate behind it, and a reviewer must say so rather than report it clean.

The Go layout, the toolchain version, and the cross-platform targets are not decided here. The
change that introduces Go records them in its own design, and this standard is updated to match.

## Modules and build

- `go.mod` and `go.sum` are committed. No `replace` directive pointing at a local path is
  committed.
- Shipped binaries are built with `CGO_ENABLED=0`. A cgo dependency needs a reason in the change
  description. Tests and `go test -race` run with cgo on, because the race detector needs it.

## Formatting and vet

`gofmt` and `go vet` are the first reviewers for formatting and mechanical rules. Do not restate
their rules here, and do not merge code that fails them. Naming and documentation rules that
the tools do not check are below.

- Names use MixedCaps, never underscores. Initialisms keep one case: `ID`, `URL`, `HTTP`.
- Package names are short, lowercase, and singular. `util`, `common`, and `helpers` are not used.
- An exported name is not stuttered: `config.Load`, not `config.LoadConfig`.
- Every exported identifier has a doc comment that starts with its name.

## Errors

Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.

- Wrap with context: `fmt.Errorf("<operation> <input>: %w", err)`. Use `%v` only to cut the
  chain on purpose at an API boundary.
- Callers branch with `errors.Is` on exported sentinel values or `errors.As` on exported error
  types. Error strings are not compared.
- An error is handled once: returned, or logged, not both.
- `panic` is not used for an expected failure. Libraries return errors. `log.Fatal` and
  `os.Exit` appear only in `main`.

## Context

- `ctx context.Context` is the first parameter of every function that does I/O or blocks.
- `context.Background()` appears only in `main`, in tests, and at the root of a background job.
  `context.TODO()` is not committed.
- A context is not stored in a struct. Every `WithTimeout` or `WithCancel` is followed by
  `defer cancel()`.

## Concurrency

- Every goroutine has a defined exit: it returns when its context is cancelled or its input
  channel closes. The code that starts it waits for it, with a `sync.WaitGroup` or
  `golang.org/x/sync/errgroup`.
- The sender closes a channel; a receiver never does. Channels are unbuffered unless the buffer
  size has a stated reason. Function signatures use directional channel types.
- Shared mutable state is guarded by a `sync.Mutex` or confined to one goroutine. Pick one per
  piece of state.
- `go test -race` passes. A data race is a bug even when the test is green.
- A service shuts down gracefully on `os.Interrupt` and `SIGTERM`, using `signal.NotifyContext`.

## Safety

- `os/exec` takes an argument list. Interpolated input is never passed to `sh -c`.
- SQL is parameterized. Templates that render HTML use `html/template`, not `text/template`.
- Secrets come from the environment or a secret store, never a literal, and are not logged.

## Dependencies and structure

- Application code takes its collaborators as parameters, accepts interfaces, and returns
  concrete types. It does not construct clients, connections, or clocks internally, and does
  not read a package-level variable for something a caller could pass.
- Package-level mutable state is avoided, so tests can construct the code under test directly.
- `govulncheck` is run before a dependency upgrade merges, once Go is in the repository.

## Tests

Test authorship follows the [test coding standard](../test/README.md) and belongs to `test-dev`.
Go tests are `_test.go` files beside the code they exercise, run by `go test`.

## Completion

No Go gate exists today. When Go is introduced, its change adds these gates, and this section is
updated to name them exactly:

```bash
gofmt -l .        # the format check: must print nothing
go vet ./...
go build ./...    # the build
go test -race ./...
```

`gofmt` is the formatter, and `-l` lists the files it would rewrite, so an empty list is the pass.
`go build ./...` is the build — it compiles every package, which `go vet` alone does not. The
`go test` line is the gate the review council and CI run. A dev agent never runs the full
suite; it runs the tests covering its change (`go test -race -run '<TestName>' ./<package>`).
