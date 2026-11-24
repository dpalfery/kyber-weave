---
name: go-gates
description: "Run the Go quality gates in order (gofmt, go vet, staticcheck, go build, go mod tidy, then targeted go test with race detection), check the other OS with GOOS, and run govulncheck before an upgrade. Load when verifying a Go change or before handing a Go change to delivery."
---

# Go gates

Run the gates in order and fix the cause of each finding, never the suppression.

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
