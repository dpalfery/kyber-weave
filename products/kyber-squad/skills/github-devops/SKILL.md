---
name: github-devops
description: Use when working on GitHub Actions workflows, CI/CD pipelines, Docker builds, MSBuild configuration, build diagnostics, project modernization, or build performance in the CI context.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# GitHub DevOps

Identify your sub-task and read ONLY the relevant reference before proceeding. The project-specific build references live in `6-Docs/DevOps/` and are looked up by name from the root `AGENTS.md` Repository Configuration & Paths registry, not by a path embedded here — this keeps the skill valid if that documentation moves.

| Sub-Task | When to Use | Registry Key |
|---|---|---|
| CI Build Diagnostics | Build step fails in GitHub Actions; capture and analyze binary logs (binlog); replay error/warning logs | [CI Build Diagnostics](./references/ci-build-diagnostics.md) (skill-local, not registered) |
| Build Performance | CI builds are slow; enable parallelism (`-m`); identify bottlenecks; MSBuild Server warm-up for sequential CI builds | **Build Performance** |
| Incremental Build & Caching | GitHub Actions cache for `obj/`; incremental build never skipping targets; always rebuilding after first run | **Incremental Build** |
| Directory.Build Organization | Centralize build settings across projects; `Directory.Build.props`/`.targets`/`Directory.Packages.props`; multi-level hierarchy | **Directory.Build Organization** |
| MSBuild Modernization | Legacy `.csproj` with `ToolsVersion`; explicit `<Compile>` lists; `packages.config`; migrate to SDK-style | **MSBuild Modernization** |
| MSBuild Anti-patterns | Cross-platform build failures; hardcoded paths; `<Exec>` shell commands; `<Reference>` with `HintPath`; unquoted conditions | **MSBuild Anti-patterns** |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
