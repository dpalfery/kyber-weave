using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The two Arbiter CLI surfaces added for tasks 3.11 and 3.13: <c>kyber-weave arbiter sections --path &lt;doc&gt; --task
/// "&lt;text&gt;"</c> and <c>kyber-weave arbiter comment --path &lt;file&gt; --line &lt;n&gt; --body "&lt;text&gt;"</c>. Both
/// mirror the read-only surfaces in <c>ArbiterCliCommandTests</c>: exit 0 on an answer, exit 1 on a usage or document
/// error, and every value that came from the operator escaped before it reaches the console.
/// RED: <c>ArbiterSectionsCommand</c>, <c>ArbiterSectionsSettings</c>, <c>ArbiterCommentCommand</c> and
/// <c>ArbiterCommentSettings</c> do not exist yet.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterTriageCliCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

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
            new ArbiterSectionsSettings { Path = WriteDocument(), Task = "Change the install steps." }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("guide/install", execution.Output, StringComparison.Ordinal);
        Assert.Contains("3-4", execution.Output, StringComparison.Ordinal);
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

    /// <summary>A comment body with Spectre markup characters renders literally and does not throw; it is operator text, so it is escaped.</summary>
    [Fact]
    public void Comment_BodyWithMarkupCharacters_RendersWithoutThrowing()
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
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
