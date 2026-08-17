---
name: setup-dev-environment
description: Set up the host repository development environment on a brand new Windows or macOS machine. Use when the user asks to setup/install/bootstrap/configure the repo or dev environment, including .NET, MAUI, Node/npm, Tauri/Rust, Python/Poetry, Docker/SQL Server database setup, VS Code extensions, MCP servers, Azure CLI read-only tooling, GitHub CLI, Ollama, and validation.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Setup Dev Environment

Use this skill to turn a new Windows or macOS machine into a working development machine for the host repository.

Required tooling, install approach, guardrails, and validation criteria are the path declared as **Developer Setup Standard** in the root `AGENTS.md` Repository Configuration & Paths registry. Read that document before planning or running any install — it is the single source of truth; do not restate or fork its rules here. This file covers only the session mechanics: how to run the setup conversation.

## Required Behavior

- Start every response with `[******Working Agreement: Active******]`.
- Detect OS first: Windows native/WSL, macOS Intel/Apple Silicon, shell, package managers, VS Code or VS Code Insiders.
- Inventory before installing. Read the repo files listed in [inventory.md](references/inventory.md), then run read-only version checks against the Developer Setup Standard's Required Tooling list.
- Present a concise install plan with missing tools, install commands, and trade-offs. Follow the Developer Setup Standard's approval gates before acting.
- Install with the platform-native path, per the Developer Setup Standard's Platform Setup section (Windows / macOS).
- Configure local services and data only after approval, per the Developer Setup Standard's Local Database and Editor and MCP Configuration sections.
- Validate before reporting success, against the Developer Setup Standard's Validation Criteria.

## Workflow

1. **Preflight**
   - Confirm repo root and git state with read-only commands.
   - Detect OS, architecture, shell, package manager, and editor command (`code` or `code-insiders`).
   - Read `global.json`, `.vscode/extensions.json`, `.mcp.json`, `.codex/config.toml`, package manifests, Python config, and database setup docs.

2. **Inventory**
   - Check every item in the Developer Setup Standard's Required Tooling section, including its platform-specific additions for the detected OS.

3. **Plan and Approval**
   - Group missing items by required, recommended, and optional.
   - Show exact install commands per platform.
   - Ask for one explicit approval to install required/recommended tools.
   - Ask separately before optional tools, Docker container startup, database provisioning, and machine/account-wide environment changes, per the Developer Setup Standard's Guardrails.

4. **Install and Configure**
   - Follow the Developer Setup Standard's Install Approach exactly.

5. **Validate**
   - Run focused validation for each stack.
   - Report installed versions, remaining manual steps, and any blocked items.
   - Do not claim the environment is ready unless the Developer Setup Standard's Validation Criteria are met.
