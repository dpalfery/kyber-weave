using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Usage errors on <c>arbiter comment</c>. A missing <c>--path</c> is refused with the sibling commands' usage error
/// (exit 2, one line) rather than an exception that escapes the command.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterCommentUsageCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ACommentWithoutAPathIsAUsageErrorNotAnException()
    {
        ArbiterCommentCommand command = new();

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = _temp.Path,
                Line = 2,
                Body = "Shorten this heading.",
            }));

        Assert.Equal(2, execution.Result);
        Assert.Contains("kyber-weave arbiter: error: --path is required.", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A comment with no body is a usage error on one line, as the MCP tool answers it with an error.</summary>
    [Fact]
    public void ACommentWithoutABodyIsAUsageErrorNotAnException()
    {
        ArbiterCommentCommand command = new();

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = _temp.Path,
                Path = "docs/guide.md",
                Line = 2,
                Body = null!,
            }));

        Assert.Equal(2, execution.Result);
        Assert.Contains("kyber-weave arbiter: error: --body is required.", execution.Output, StringComparison.Ordinal);
    }
}
