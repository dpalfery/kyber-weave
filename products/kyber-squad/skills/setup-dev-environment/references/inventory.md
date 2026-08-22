# Inventory

This document defines the generic discovery commands and patterns for inspecting a development machine and repository prerequisites. If a **Developer Setup Standard** (`<developer-setup-standard>`) is declared in the root `AGENTS.md` Repository Configuration & Paths registry, treat it as an optional host overlay that supplements or customizes this discovery inventory and project manifest list. When no host setup standard is declared, discover prerequisites autonomously by inspecting repository manifests (such as `global.json`, `.editorconfig`, `*.csproj`, `package.json`, `requirements.txt`, Dockerfiles, `docs/install.md`, `CONTRIBUTING.md`, `README.md`) and running the discovery checks below.

Use these discovery commands when available:

```sh
dotnet --info
dotnet workload list
node --version
npm --version
python --version
python3 --version
poetry --version
rustc --version
cargo --version
docker --version
docker compose version
gh --version
ollama --version
```

Editor checks:

```sh
code --version
code-insiders --version
code --list-extensions
code-insiders --list-extensions
```

macOS-specific checks:

```sh
sw_vers
uname -m
xcode-select -p
xcodebuild -version
launchctl getenv DEVELOPER_DIR
```

Windows-specific checks:

```powershell
winget --version
Get-Command git
Get-Command dotnet
Get-Command node
Get-Command npm
Get-Command python
Get-Command py
Get-Command cargo
Get-Command docker
Get-Command gh
Get-Command az
```

For Azure CLI, use `Get-Command az` or `where.exe az` to check install presence. Do not run any `az` command until the active subscription can be verified against the allowlist in the host standard's guardrails or repository documentation.
