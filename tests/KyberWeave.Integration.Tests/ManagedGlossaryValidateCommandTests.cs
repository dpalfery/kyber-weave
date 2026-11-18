using KyberWeave.Cli.Commands.Docs;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>docs validate</c> command run end to end over a real checkout on disk: an invalid managed glossary makes
/// the whole command exit 1. The glossary's own rules are unit-tested over the in-memory file system in
/// <c>ManagedGlossaryTests</c>; this smoke test proves the command, the container and the disk wire together.
/// </summary>
public sealed class ManagedGlossaryValidateCommandTests
{
    [Fact]
    public void DocsValidateInvalidManagedGlossaryReturnsGlossaryOperationalError()
    {
        using TempDirectory repository = new();
        Write(repository.Path, "docs/catalog.md", """
            ---
            id: system/catalog
            title: Catalog
            doc-type: index
            status: current
            owner: Maintainers
            last-reviewed: 2026-08-01
            ---

            # Catalog

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | Gameplay | Application | `src/Game` | [README](x) | [docs](y) | Gameplay maintainers | 2026-08-01 | Current |
            | System | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-08-01 | Current |
            """);
        Write(repository.Path, "docs/glossary.md", """
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: needs-review
            owner: Gameplay maintainers
            last-reviewed: 2026-07-01
            ---

            # Glossary

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-a | accepted | Definition. | component:Gameplay | gameplay loop |
            """);
        Write(repository.Path, ".kyber-weave/kyber-weave.yml", """
            ontology:
              docs-root: docs
            docs-analysis:
              glossary-path: docs/glossary.md
            """);
        DocsValidateSettings settings = new DocsValidateSettings { Path = repository.Path, Format = "json" };

        int exitCode = ProcessConsoleCapture.Run(() => DocsTestCommands.Validate().Execute(null!, settings)).Result;

        Assert.Equal(1, exitCode);
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
