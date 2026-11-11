using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The two Arbiter CLI surfaces added for tasks 3.11 and 3.13: <c>kyber-weave arbiter sections --path &lt;doc&gt; --task
/// "&lt;text&gt;"</c> and <c>kyber-weave arbiter comment --path &lt;file&gt; --line &lt;n&gt; --body "&lt;text&gt;"</c>. Both
/// mirror the read-only surfaces in <c>ArbiterCliCommandTests</c>: exit 0 on an answer, exit 1 on a usage or document
/// error, and every value that came from the operator escaped before it reaches the console.
/// </summary>
/// <remarks>
/// <c>arbiter sections</c> contains its path under the repository root it finds from the process's working directory,
/// so a document the test reads from the successful path must sit under that root. The test process runs inside the
/// checkout, so the fixture is written under the repository's gitignored scratch folder, in a directory unique to the
/// test, and removed afterwards. A document outside the root must be refused, which the companion test pins.
/// </remarks>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterTriageCliCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _inRepoScratch = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, ".agent_scratchpad", "arbiter-triage-cli", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _temp.Dispose();
        if (Directory.Exists(_inRepoScratch))
        {
            Directory.Delete(_inRepoScratch, recursive: true);
        }
    }

    private const string GuideText = "# Guide\nIntro.\n## Install\nSteps.\n";

    /// <summary>A document under the root the command resolves, so the containment check admits it.</summary>
    private string WriteDocumentUnderRoot()
    {
        string path = Path.Combine(_inRepoScratch, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, GuideText);
        return path;
    }

    /// <summary>A document in a temp folder, outside the checkout the command resolves its root from.</summary>
    private string WriteDocumentOutsideRoot()
    {
        string path = Path.Combine(_temp.Path, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, GuideText);
        return path;
    }

    private string WriteDocument()
    {
        string path = Path.Combine(_temp.Path, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Guide\nIntro.\n## Install\nSteps.\n");
        return path;
    }

    [Fact]
    public void Sections_PrintsEachSectionIdAndItsLineRange()
    {
        ArbiterSectionsCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSectionsSettings { Path = WriteDocumentUnderRoot(), Task = "Change the install steps." }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("guide/install", execution.Output, StringComparison.Ordinal);
        Assert.Contains("3-4", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A document outside the repository root is refused with a clear message and prints no sections.</summary>
    [Fact]
    public void Sections_PathOutsideTheRoot_ExitsNonZeroWithAMessageAndPrintsNoSections()
    {
        ArbiterSectionsCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSectionsSettings { Path = WriteDocumentOutsideRoot(), Task = "Change the install steps." }));

        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("not found under the repository root", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("guide/install", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_MissingDocument_ExitsOne()
    {
        ArbiterSectionsCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSectionsSettings { Path = Path.Combine(_temp.Path, "absent.md"), Task = "Anything." }));

        Assert.Equal(1, execution.ExitCode);
    }

    /// <summary>With no provider configured the comment prints as real, so the operator is never told a comment is a nitpick on no evidence.</summary>
    [Fact]
    public void Comment_NoProvider_PrintsRealAndTheDecisionId()
    {
        WriteDocument();
        ArbiterCommentCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = _temp.Path,
                Path = "docs/guide.md",
                Line = 2,
                Body = "Shorten this heading.",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("real", execution.Output, StringComparison.Ordinal);
        Assert.Contains("decision", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A comment body with Spectre markup characters does not throw. The body is no longer echoed, so this pins the
    /// exit code and the class and decision lines rather than the body text (see <c>ArbiterCommentBodyCliTests</c>).
    /// </summary>
    [Fact]
    public void Comment_BodyWithMarkupCharacters_DoesNotThrow()
    {
        WriteDocument();
        ArbiterCommentCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = _temp.Path,
                Path = "docs/guide.md",
                Line = 2,
                Body = "Use [red]this[/] and [bogus",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("decision", execution.Output, StringComparison.Ordinal);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
