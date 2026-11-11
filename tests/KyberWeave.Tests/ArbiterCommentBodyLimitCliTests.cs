using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Review finding 4538775160: <c>arbiter comment</c> refuses a body over the MCP tool's text limit on both the single
/// and the summary path. The summary path used to parse and classify an unbounded body.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterCommentBodyLimitCliTests : IDisposable
{
    /// <summary>The MCP tool's text limit, written out here so the test pins the value, not a shared name.</summary>
    private const int TextLimit = 256 * 1024;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string WriteDocument()
    {
        string path = Path.Combine(_temp.Path, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Guide\nIntro.\n## Install\nSteps.\n");
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Comment_BodyOverTheTextLimit_IsRefused(bool summary)
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
                Summary = summary,
                Author = "dave",
                Body = new string('x', TextLimit + 1),
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("larger than", execution.Output, StringComparison.Ordinal);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
