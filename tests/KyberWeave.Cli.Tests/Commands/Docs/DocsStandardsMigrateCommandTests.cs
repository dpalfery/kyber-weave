using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// What <c>docs standards migrate</c> prints and returns, over the shared in-memory seams. What the migration moves,
/// writes and refuses is <c>StandardsFolderMigrationScenarioTests</c>' subject.
/// </summary>
public class DocsStandardsMigrateCommandTests
{
    private const string LegacyCsharp = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private const string LegacyCsharpWithOwnHeadings = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        How C# is written in this repository.

        ## Stack

        - .NET 10.

        ## Naming

        Types use PascalCase.

        ## Commands

        Run `dotnet build`.
        """;

    private const string DeclaredConfig = "ontology:\n  docs-root: 6-Docs\n  technologies:\n    - csharp\n";

    private readonly MemoryDocsHost _host = new();

    /// <summary>
    /// The operator reads the reshaped headings from the command's own output, not from a
    /// diff, so every moved section is printed with the heading it now sits under.
    /// </summary>
    [Fact]
    public void TheCommandPrintsEachMoveItMade()
    {
        _host.WriteInRepo(".kyber-weave/kyber-weave.yml", DeclaredConfig);
        _host.WriteInRepo("6-Docs/standards/csharp/README.md", LegacyCsharpWithOwnHeadings);

        int exit = Run();

        Assert.Equal(0, exit);
        Assert.Contains("moved 'Stack' under '## Coding standards'", _host.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A standards file the process may not read is a diagnosable refusal, not a stack trace:
    /// the command names the failure and exits 1.
    /// </summary>
    [Fact]
    public void AnUnreadableStandardIsRefusedWithADiagnostic()
    {
        _host.WriteInRepo(".kyber-weave/kyber-weave.yml", DeclaredConfig);
        _host.WriteInRepo("6-Docs/standards/csharp/csharp.standards.md", LegacyCsharp);
        string standard = _host.InRepo("6-Docs/standards/csharp/csharp.standards.md");
        _host.Files.FailWhen(
            operation => string.Equals(operation.Path, standard, StringComparison.Ordinal)
                && (operation.Name.StartsWith("Read", StringComparison.Ordinal) || operation.Name == "OpenRead"),
            new UnauthorizedAccessException($"Access to the path '{standard}' is denied."));

        int exit = Run();

        Assert.Equal(1, exit);
        Assert.Contains("could not be read or written", _host.Output, StringComparison.Ordinal);
    }

    private int Run() =>
        CommandAppFixture
            .DocsStandardsMigrate(_host.Register, _host.Console)
            .Run(["docs", "standards", "migrate", _host.Repo]);
}
