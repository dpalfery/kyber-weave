# Contributing to Kyber-Weave

Thanks for contributing. By participating you agree to follow our
[Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- Bug reports and feature ideas (use the issue templates)
- Documentation improvements
- Code changes via pull request

Security vulnerabilities: see [SECURITY.md](SECURITY.md) — do not file a public issue.

## Workflow

1. Fork the repository (or use a branch if you have write access)
2. Create a branch from `develop` for work intended for the next integration line, or from `main` for hotfixes
3. Make focused commits with clear messages
4. Open a pull request against `develop` (or `main` for hotfixes)
5. Ensure CI passes (`Build and test`)
6. Address review feedback

Pull requests require one approving review, including a code owner review.
Maintainers may override protection on a PR when needed.

Default merge method is **squash**.

## Develop from source

Requires .NET SDK 10 (see `global.json`).

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

Run the CLI from source:

```bash
dotnet run --project src/KyberWeave.Cli -- <branch> <command> [args]
```

Run the MCP server from source:

```bash
dotnet run --project src/KyberWeave.Mcp
```

Self-contained publish (same flags the Release workflow uses):

```bash
dotnet publish src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release \
  -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./artifacts/cli-linux-x64
```

More detail: [README.md](README.md) and [docs/](docs/).

## Pull request checklist

- [ ] Change is focused and explained
- [ ] Tests added or updated when behavior changes
- [ ] `dotnet build` / `dotnet test` pass locally
- [ ] Docs or samples updated when user-facing behavior changes
- [ ] Linked issue (if applicable)

## Licence

Contributions are accepted under the MIT licence. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
