---
id: standards/go/rules
title: "go rules"
doc-type: rule
status: draft
technology: go
owner: unassigned
last-reviewed: 2026-11-22
---

# go rules

- Never compare `err.Error()` strings; branch with `errors.Is` / `errors.As`.
- Never store a `context.Context` in a struct, and never ship `context.TODO()`.
- Every `WithTimeout` / `WithCancel` is followed by `defer cancel()`.
- Never start a goroutine without a defined exit and an owner that waits for it.
- Only the sender closes a channel, exactly once.
- Never bound a graceful shutdown with an unbounded context.
- Never copy a struct that contains a `sync.Mutex`.
- Never commit a `replace` directive pointing at a local path, hand-edit `go.sum`, or add a
  dependency at `@latest`.
- Never raise the `go` or `toolchain` directive as a side effect of another change.
- No `//nolint` or `//lint:ignore` without the check name and a reason on the same line.

The rules that hold for every technology, including secrets and input validation, are
in the shared rules file beside this folder.
