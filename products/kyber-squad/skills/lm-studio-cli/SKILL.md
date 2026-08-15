---
name: lm-studio-cli
description: use the LM Studio CLI to inspect the local LM Studio instance, review server logs, and inspect downloaded or loaded models.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# LM Studio CLI

Use this skill when the user needs help with a local LM Studio instance, especially to review logs, confirm server status, or inspect model inventory.

## Primary Commands

- `lms status` - check whether the local LM Studio instance is available and responsive.
- `lms ps --json` - list loaded models in machine-readable form.
- `lms ls --json` - list downloaded models in machine-readable form.
- `lms get <model-id>` - fetch detailed information for one model.
- `lms log` - stream server logs for the local instance.
- `lms log stream` - alternate log-streaming form shown in some LM Studio CLI docs.
- `lms server start` - start the local API server.
- `lms server stop` - stop the local API server.

## Recommended Workflow

1. Run `lms status` first to confirm the instance is reachable.
2. Review logs with `lms log` or `lms log stream` when troubleshooting startup, model loading, or request failures.
3. Compare `lms ps --json` against `lms ls --json` to distinguish loaded models from downloaded models.
4. Use `lms get <model-id>` when you need details about one specific model.

## What To Look For

- Server startup failures, port conflicts, runtime errors, and model load errors in the logs.
- Loaded model name, runtime, and availability in `lms ps` output.
- Downloaded model path, publisher, and identifier in `lms ls` output.
- Per-model metadata from `lms get`, especially when diagnosing a mismatch between downloaded and loaded models.

## Practical Notes

- Prefer `--json` when the output will be pasted into another tool or compared programmatically.
- If `lms` is not on the PATH, ask the user to confirm how LM Studio CLI was installed before guessing a binary path.
- If the command shape differs on the installed version, ask for `lms --help` and adjust to the local CLI surface.
