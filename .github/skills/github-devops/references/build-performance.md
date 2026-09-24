---
name: github-devops/build-performance
description: Optimize MSBuild and .NET build performance in local development and GitHub Actions CI pipelines — parallelism, node reuse, analyzer tuning, and caching strategies.
---

# Build Performance & Optimization

Use this reference when CI builds are slow, MSBuild bottlenecks need profiling, or build parallelism and caching require optimization.

---

## 1. Parallel Build Configuration

MSBuild can execute project evaluations and target executions in parallel across multiple worker nodes (processes).

### Enabling Multi-Core Parallelism

By default, the .NET CLI enables multiprocessor builds (`-m` or `-maxcpucount`). You can tune the maximum number of worker nodes explicitly:

```bash
# Use all available logical CPU cores (default)
dotnet build -m

# Explicitly limit worker nodes (e.g. 2 cores on standard GitHub Actions Linux runners)
dotnet build -m:2

# Enable parallel target execution across referenced projects
dotnet build -m -p:BuildInParallel=true
```

> [!TIP]
> Standard GitHub-hosted `ubuntu-latest` and `windows-latest` runners typically provide **2 vCPUs** and **7 GB RAM** (or 4 vCPUs on large runners). Over-allocating worker nodes (`-m:8`) on a 2-vCPU runner causes CPU thrashing and context switching overhead. Use `-m:2` or leave default `-m`.

---

## 2. Process Reuse and MSBuild Server

### MSBuild Server (.NET 7+)

The MSBuild Server keeps a long-running background process warm across command invocations, avoiding JIT compilation overhead and assembly load times on sequential builds:

```bash
# Enable MSBuild Server for faster consecutive CLI commands
export DOTNET_CLI_USE_MSBUILD_SERVER=1
dotnet build
dotnet test --no-build
```

In short-lived CI runners (where each job runs in a fresh disposable container/VM), MSBuild Server provides the most value when multiple .NET CLI commands run in the same step or job.

### Node Reuse

Worker nodes can stay active in memory after a build completes to service subsequent builds:

```bash
# Enable node reuse (default in Visual Studio and some CLI scenarios)
dotnet build -p:NodeReuse=true

# Disable node reuse (recommended at the end of CI jobs to prevent locked file handles)
dotnet build -p:NodeReuse=false
```

In CI environments, set `-p:NodeReuse=false` or ensure runner cleanup kills lingering worker nodes before archiving artifacts or cleaning workspace directories.

---

## 3. Profiling and Identifying Bottlenecks

### Performance Summary Logging

Generate structured timing metrics to pinpoint slow targets and tasks:

```bash
# Text log performance summary
dotnet build -fl -flp:logfile=build-perf.log;performancesummary

# View slowest targets
grep -A 25 "Target Performance Summary:" build-perf.log

# View slowest tasks
grep -A 25 "Task Performance Summary:" build-perf.log
```

### Binary Log (binlog) Profiling

Binary logs capture millisecond-accurate timeline traces of every target, task, and evaluation:

```bash
# Generate binlog
dotnet build /bl:build.binlog
```

Analyze the `.binlog` with:
- **MSBuild Structured Log Viewer (`msbuildlog.com`)**: Open the binlog and inspect the "Timeline" tab to visualize thread execution waterfalls and identify sequential bottleneck paths.
- **`Microsoft.AITools.BinlogMcp` MCP Server**: Query task duration, target dependencies, and project evaluation overhead directly.

---

## 4. Compiler and Analyzer Tuning

Roslyn analyzers and source generators run during compilation and can dominate build times if unoptimized.

### Analyzer Execution Strategy

| Goal | Configuration | Notes |
|---|---|---|
| Fast dev / PR loop | `-p:RunAnalyzersDuringBuild=false` | Compiles code without running Roslyn analyzers |
| Dedicated CI check | `dotnet format --verify-no-changes` | Run analyzers or linters as a separate parallel CI job |
| Full validation | `-p:RunAnalyzers=true` | Enforces all analyzers during compilation |

### Concurrent Roslyn Compilation

Ensure concurrent compilation is enabled in project or directory settings:

```xml
<PropertyGroup>
  <!-- Allows Roslyn to compile syntax trees concurrently across threads -->
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
</PropertyGroup>
```

---

## 5. Caching and CI Pipeline Optimization

### NuGet Package Caching in GitHub Actions

Avoid re-downloading packages on every CI run:

```yaml
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '8.0.x'
    cache: true
    cache-dependency-path: '**/packages.lock.json' # or '**/*.csproj'
```

### Avoiding Redundant Restore and Build Steps

Chain commands using `--no-restore` and `--no-build` in multi-step CI workflows:

```bash
# 1. Restore dependencies once
dotnet restore MySolution.sln

# 2. Build binaries once without re-restoring
dotnet build MySolution.sln -c Release --no-restore

# 3. Run unit tests against pre-built binaries
dotnet test tests/UnitTests/UnitTests.csproj -c Release --no-build

# 4. Run integration tests against pre-built binaries
dotnet test tests/IntegrationTests/IntegrationTests.csproj -c Release --no-build
```

---

## 6. Performance Checklist

- [ ] Multi-core parallelism (`-m`) enabled on multi-core runners.
- [ ] Worker node count aligned with CI runner vCPUs (e.g. 2–4).
- [ ] NuGet package caches configured with `actions/setup-dotnet` or `actions/cache`.
- [ ] Sequential CI steps use `--no-restore` and `--no-build`.
- [ ] Slow targets identified via `/flp:performancesummary` or `.binlog` timeline.
- [ ] Heavy analyzers isolated to dedicated CI jobs or optimized.
