---
id: standards/go/rules
title: "go rules"
doc-type: rule
status: current
technology: go
owner: dpalfery
last-reviewed: 2026-11-16
---

# go rules

## Status

There is no Go code in this repository yet. The standard and these skills exist so that the
first Go change follows the same gates as every other stack rather than inventing them.

## When Go arrives

- `gofmt` and `go vet` are the format and lint gates; a finding is a change defect.
- Run tests with `-race` where cgo is available.
- Add the package's gate to `review.task-gates` under the technology the task names, so the
  task gate and the council see it.
