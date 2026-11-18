---
id: standards/go/skills
title: "go skills"
doc-type: skill
status: draft
technology: go
owner: unassigned
last-reviewed: 2026-11-16
---

# go skills

## What this folder covers

- Errors and context propagation through the call chain.
- Goroutine and channel lifecycle, including cancellation.
- Module hygiene and cross-compilation.
- The build, vet, lint and test commands, per platform.

## Hand-off

When ceding work to delivery, provide the `GOOS`/`GOARCH` targets, the build, vet and test
commands, and whether cgo is needed.
