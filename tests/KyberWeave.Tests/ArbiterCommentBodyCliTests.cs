using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Review findings 4538775136 and 4538775160 on <c>arbiter comment</c>. A single run no longer echoes the whole
/// <c>--body</c> to stdout, and a body over the MCP tool's text limit is refused on both the single and the summary
/// path, instead of being classified unbounded.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterCommentBodyCliTests : IDisposable
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
    public void Comment_SingleRun_DoesNotEchoTheBody()
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
                Body = "BODY-MARKER-7f3a review text",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("BODY-MARKER-7f3a", execution.Output, StringComparison.Ordinal);
        Assert.Contains("decision", execution.Output, StringComparison.Ordinal);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
