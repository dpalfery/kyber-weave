---
id: standards/github-actions/skills
title: "github-actions skills"
doc-type: skill
status: current
technology: github-actions
owner: dpalfery
last-reviewed: 2026-11-16
---

# github-actions skills

## Commands

```bash
actionlint <changed workflow files>
shellcheck <changed shell scripts>
kyber-weave review gates . --council    # the council's command, not a dev agent's
```

## What CI runs

- The `test` gate runs the full product suite; `test-policy` runs the repository-policy suite.
- The KyberDash and tray gates are `npm --prefix dash …` and `cargo --manifest-path
  dash/tray/src-tauri/Cargo.toml …`. CI and the council run them; a dev agent runs the
  targeted subset.
