using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// The <c>docs standards diff</c> command turns a refused read into a diagnostic and exit code 1, over the shared
/// in-memory seams. What the diff compares is <c>StandardsTemplateDiffComparisonTests</c>' subject.
/// </summary>
public class DocsStandardsDiffCommandTests
{
    private readonly MemoryDocsHost _host = new();

    /// <summary>
    /// A standards file the process may not read is a diagnosable refusal, not a stack trace:
    /// the command names the failure and exits 1.
    /// </summary>
    [Fact]
    public void AnUnreadableStandardIsRefusedWithADiagnostic()
    {
        _host.WriteInRepo(
            ".kyber-weave/kyber-weave.yml",
            "ontology:\n  docs-root: 6-Docs\n  technologies:\n    - csharp\n");
        _host.WriteInRepo("6-Docs/standards/csharp/csharp.standards.md", "---\nid: standards/csharp\n---\n");
        string standard = _host.InRepo("6-Docs/standards/csharp/csharp.standards.md");
        _host.Files.FailWhen(
            operation => string.Equals(operation.Path, standard, StringComparison.Ordinal)
                && (operation.Name.StartsWith("Read", StringComparison.Ordinal) || operation.Name == "OpenRead"),
            new UnauthorizedAccessException($"Access to the path '{standard}' is denied."));

        int exit = CommandAppFixture
            .DocsStandardsDiff(_host.Register, _host.Console)
            .Run(["docs", "standards", "diff", _host.Repo]);

        Assert.Equal(1, exit);
        Assert.Contains("could not be read", _host.Output, StringComparison.Ordinal);
    }
}
