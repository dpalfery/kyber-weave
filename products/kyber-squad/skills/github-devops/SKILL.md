---
name: github-devops
description: Use when working on GitHub Actions workflows, CI/CD pipelines, Docker builds, MSBuild configuration, build diagnostics, project modernization, or build performance in the CI context.
license: MIT
---

# GitHub DevOps

Identify your sub-task and read ONLY the relevant reference document before proceeding. All references are self-contained and located in the local `references/` directory.

| Sub-Task | When to Use | Reference |
|---|---|---|
| CI Build Diagnostics | Build step fails in GitHub Actions; capture and analyze binary logs (binlog); replay error/warning logs | [CI Build Diagnostics](./references/ci-build-diagnostics.md) |
| Build Performance | CI builds are slow; enable parallelism (`-m`); identify bottlenecks; MSBuild Server warm-up for sequential CI builds | [Build Performance](./references/build-performance.md) |
| Incremental Build & Caching | GitHub Actions cache for `obj/`; incremental build never skipping targets; always rebuilding after first run | [Incremental Build](./references/incremental-build.md) |
| Directory.Build Organization | Centralize build settings across projects; `Directory.Build.props`/`.targets`/`Directory.Packages.props`; multi-level hierarchy | [Directory.Build Organization](./references/directory-build-organization.md) |
| MSBuild Modernization | Legacy `.csproj` with `ToolsVersion`; explicit `<Compile>` lists; `packages.config`; migrate to SDK-style | [MSBuild Modernization](./references/msbuild-modernization.md) |
| MSBuild Anti-patterns | Cross-platform build failures; hardcoded paths; `<Exec>` shell commands; `<Reference>` with `HintPath`; unquoted conditions | [MSBuild Anti-patterns](./references/msbuild-anti-patterns.md) |

**Rule:** Read ONLY the reference(s) relevant to your current task. Do not pre-load all references.
