---
name: kw-ci-gates
description: "Lint Kyber-Weave workflows and scripts and know which gates CI runs. Load when changing a GitHub Actions workflow or shell script in this repository, or when deciding which CI gate covers a change. For a failing .NET build in CI, load dotnet-build-diagnostics."
---

# Kyber-Weave CI gates

## Commands

```bash
actionlint <changed workflow files>
shellcheck <changed shell scripts>
kyber-weave review gates . --council    # the council's command, not a dev agent's
```

## What CI runs

- The `test` gate runs every unit, content, policy and architecture project; `test-policy` runs the repository-policy suite.
- The KyberDash and tray gates are `npm --prefix dash …` and `cargo --manifest-path
  dash/tray/src-tauri/Cargo.toml …`. CI and the council run them; a dev agent runs the
  targeted subset.

## A failed .NET build in CI

CI uploads no binlog. Reproduce by building the failing project alone with `/bl:app.binlog`, then
follow the `dotnet-build-diagnostics` skill to read it.
