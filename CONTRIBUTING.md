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
2. Create your branch with
   `kyber-weave run start --harness <token> --type <type> --slug <slug>`. It fetches `origin`,
   starts from the branch the repository's `run.base-branch` setting names or from `origin/HEAD`
   when that setting is absent, and names the branch `[<owner>.<agent>.]<harness>/<type>/<slug>`.
3. Make focused commits with clear messages
4. Open a pull request against the base your branch was started from — the `run.base-branch`
   setting, or `origin/HEAD` when no setting names one — and end its title with the harness token
   in square brackets (`[<harness>]`), the one the branch already carries.
5. Ensure the required `CI Summary` check passes. It aggregates build and test, publish smoke, CodeQL, Trivy, Semgrep, gitleaks, and the skill/docs gate.
6. Address review feedback

Pull requests require at least one approving review, and that approval must include a
code-owner review. New commits dismiss stale approvals, and all review conversations
must be resolved before merging. Protected branches require a pull request, so ordinary
contributors cannot push directly to `main`; the branch must also be
up to date with its base before merging.

This repository currently has one maintainer and the repository-wide owner in
`.github/CODEOWNERS` is `@dpalfery`. GitHub does not count a pull request author's own
approval, so a solo-maintainer pull request cannot satisfy the code-owner rule through
review alone. Until an independent maintainer or reviewer is added, the repository
administrator may use the protected-branch bypass only for a necessary hotfix or
emergency, and must record the reason in the pull request. This is an exception to the
normal review policy, not a substitute for adding a second reviewer.

Default merge method is **squash**.

## Develop from source

Requires .NET SDK 10 (see `global.json`).

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClass>"
dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
```

The review council and CI run the full suite. A person may still run it from a terminal, but a dev agent never does.

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

## Contributing a technology template

A template is one folder under `products/kyber-squad/standards/<name>/` holding four files,
every one named after the folder:

| File | What it holds |
|---|---|
| `<name>.standards.md` | The standard's decisions, under exactly these six headings in this order: Naming and layout; Coding standards; Testing; Commands; Security and data handling; Done checklist. |
| `<name>.rules.md` | Prose rules a model reads. They outrank the standard. |
| `<name>.skills.md` | How to do the work: sign-in, tools and tool servers, query patterns, output shape, and the hand-off. |
| `<name>.arbiter-rules.yml` | Machine rules the Arbiter evaluates. An empty list (`rules: []`) is valid. |

Scope is one key: `technology: <name>` for a technology or platform, `role: <name>` for a
role. A standard may name one parent with `builds-on: <technology>`; the parent's rules and
skills then reach every task in the child, and a chain longer than one level is an error.

**The one completeness check** is `docs validate`, which reports a folder missing one of the
four files (`KW-DOC-SPEC-012`) and a standard whose headings are missing or out of order
(`KW-DOC-SPEC-013`). The shipped template shape test is the same check over the embedded
copies. To add a template:

1. Add the four files; use an existing folder as the shape, not as the content.
2. Register the name in `KyberStandardsTemplates` (and `Docs.props` for the embedded files).
3. Run `dotnet test tests/KyberWeave.Content.Tests --filter "FullyQualifiedName~KyberStandardsTemplateShapeTests"`.
4. Run `dotnet run --project src/KyberWeave.Cli -- docs validate .`.

A contribution that restates a language's own documentation is noise. Write the decisions a
repository has to make and would otherwise re-argue.

## Pull request checklist

- [ ] Change is focused and explained
- [ ] Tests added or updated when behavior changes
- [ ] `dotnet build` and the tests targeting your change pass locally (the review council and CI run the full suite; agents never run it locally, even for cross-cutting changes)
- [ ] Docs or samples updated when user-facing behavior changes
- [ ] Linked issue (if applicable)

## Licence

Contributions are accepted under the MIT licence. See [LICENSE](LICENSE) and
[NOTICE](NOTICE).
