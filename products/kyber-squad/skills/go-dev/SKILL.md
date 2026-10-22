---
name: go-dev
description: Use when writing Go — packages, CLIs, services, error wrapping, context propagation, goroutines and channels, modules, or cross-compiling. Do not use for _test.go authorship or CI/CD.
license: MIT
---

# Go Developer

Toolchain, error-handling, concurrency, module, and cross-compilation decisions live in the
path declared as **<go-coding-standard>** in the repository root `AGENTS.md`. Read that before
the references below — this skill is procedure, not policy.

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Errors and Context | Wrapping and inspecting errors, sentinel and typed errors, threading `context.Context` through calls, timeouts | [Errors and Context](./references/errors-and-context.md) |
| Concurrency | Starting goroutines, channel ownership and closing, `errgroup`, worker pools, graceful shutdown, leaks | [Concurrency](./references/concurrency.md) |
| Build, Vet, and Test Gates | Running `gofmt`, `go vet`, `staticcheck`, build, and `go test` locally on macOS, Linux, or Windows before reporting done | [Gates and Platforms](./references/gates-and-platforms.md) |
| Modules and Cross-Compilation | `go.mod` hygiene, the `toolchain` directive, adding or upgrading a dependency, `GOOS`/`GOARCH` builds, cgo | [Modules and Cross-Compilation](./references/modules-and-cross-compilation.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
