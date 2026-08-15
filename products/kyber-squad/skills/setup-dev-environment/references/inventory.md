# Inventory

Read the project-specific file list in the Preflight Inventory section of the document declared as **Developer Setup Standard** in the root `AGENTS.md` Repository Configuration & Paths registry — repository-wide config files, then each component's dependency manifest. This file only covers the generic discovery mechanics; it holds no project-specific paths so it stays valid if the project's layout changes or this skill is reused elsewhere.

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

For Azure CLI, use `Get-Command az` or `where.exe az` to check install presence. Do not run any `az` command until the active subscription can be verified against the allowlist in the Developer Setup Standard's Guardrails.
