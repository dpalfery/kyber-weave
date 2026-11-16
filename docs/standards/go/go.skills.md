---
id: standards/go/skills
title: "go skills"
doc-type: skill
status: current
technology: go
owner: dpalfery
last-reviewed: 2026-11-16
---

# go skills

## Commands

```bash
gofmt -l .
go vet ./...
go test -race ./...
```

A package whose tests need a toolchain the CI image lacks is declared in its gate, not skipped
silently.
