---
id: standards/go/skills
title: "go skills"
doc-type: skill
status: draft
technology: go
owner: unassigned
last-reviewed: 2026-11-22
---

# go skills

## What this folder covers

- Errors, context propagation, goroutine and channel lifecycle, graceful shutdown, module
  hygiene and cross-compilation: the standard's Naming and layout, Coding standards and
  Security and data handling sections.
- The format, vet, lint, build, modules and test gates and their per-platform caveats: the
  standard's Commands and Done checklist sections.
- Procedure, below: run the gates in order and fix the cause of each finding, never the
  suppression.

## Running the gates

- Run the command the standard names; the usual shape is `gofmt -l .` (passes when it prints
  nothing), `go vet ./...`, `staticcheck ./...`, `go build ./...`, `go mod tidy -diff`, then
  `go test -race -run '<TestName>' ./<package>` for the tests covering the change.
- A passing test run has a non-zero test count. Never run the full suite; the review council
  and CI do.
- On Windows run the same commands in PowerShell. To check the other OS without a second
  machine, run `GOOS=windows go vet ./...` and `GOOS=linux go build ./...` (PowerShell:
  `$env:GOOS='windows'; go vet ./...`, then `$env:GOOS='linux'; go build ./...`, then
  `Remove-Item Env:GOOS`).
- Before an upgrade, run `govulncheck ./...` to see whether a known vulnerability is reachable.

## Hand-off

When ceding work to delivery, provide the `GOOS`/`GOARCH` targets, the build, vet and test
commands, and whether cgo is needed.
