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

When a **Developer Setup Standard** (`<developer-setup-standard>`) is declared in the root `AGENTS.md` Repository Configuration & Paths registry, read that document as the authoritative host override for required tooling, install approach, guardrails, and validation criteria. When `<developer-setup-standard>` is not declared in root `AGENTS.md` Config Reg, autonomously inspect repository manifests and onboarding files (such as `global.json`, `.editorconfig`, `*.csproj`, `package.json`, `requirements.txt`, Dockerfiles, `docs/install.md`, `CONTRIBUTING.md`, `README.md`) against the discovery inventory in [inventory.md](references/inventory.md) to deduce the required toolchains, runtimes, and validation steps. This file covers the session mechanics: how to run the setup conversation.

## Required Behavior

- Start every response with `[******Working Agreement: Active******]`.
- Detect OS first: Windows native/WSL, macOS Intel/Apple Silicon, shell, package managers, VS Code or VS Code Insiders.
- Inventory before installing. Read the repo files listed in [inventory.md](references/inventory.md), then run read-only version checks against discovered prerequisites or the Developer Setup Standard's Required Tooling list if provided.
- Present a concise install plan with missing tools, install commands, and trade-offs. Follow standard approval gates before acting (or host setup standard overrides).
- Install with the platform-native path (e.g. winget on Windows, Homebrew on macOS, or per host setup standard).
- Configure local services and data only after approval (per repo documentation or host setup standard).
- Validate before reporting success against discovered build/test commands or the Developer Setup Standard's Validation Criteria.

## Workflow

1. **Preflight**
   - Confirm repo root and git state with read-only commands.
   - Detect OS, architecture, shell, package manager, and editor command (`code` or `code-insiders`).
   - Check if `<developer-setup-standard>` is declared in root `AGENTS.md` Config Reg. If present, load its requirements and guardrails. If absent, autonomously inspect repository manifests and onboarding files (`global.json`, `.editorconfig`, `*.csproj`, `package.json`, `requirements.txt`, Dockerfiles, `docs/install.md`, `CONTRIBUTING.md`, `README.md`, `.vscode/extensions.json`, `.mcp.json`, `.codex/config.toml`).

2. **Inventory**
   - Run read-only discovery checks for all detected or declared toolchains against [inventory.md](references/inventory.md), including platform-specific additions for the detected OS.

3. **Plan and Approval**
   - Group missing items by required, recommended, and optional.
   - Show exact install commands per platform.
   - Ask for one explicit approval to install required/recommended tools.
   - Ask separately before optional tools, Docker container startup, database provisioning, and machine/account-wide environment changes (following guardrails or host standard).

4. **Install and Configure**
   - Follow the platform-native package manager install approach or the host Developer Setup Standard's Install Approach.

5. **Validate**
   - Run focused validation for each stack (builds, tests, toolchain checks).
   - Report installed versions, remaining manual steps, and any blocked items.
   - Do not claim the environment is ready unless validation criteria and smoke checks pass.
