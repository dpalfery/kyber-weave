using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review findings 4538775136 and 4538775160 on <c>arbiter comment</c>. A single run no longer echoes the whole
/// <c>--body</c> to stdout, and a body over the MCP tool's text limit is refused on both the single and the summary
/// path, instead of being classified unbounded.
/// </summary>
public sealed class ArbiterCommentBodyCliTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string WriteDocument() =>
        _host.Write(_host.InWork("docs", "guide.md"), "# Guide\nIntro.\n## Install\nSteps.\n");

    [Fact]
    public async Task Comment_SingleRun_DoesNotEchoTheBody()
    {
        WriteDocument();
        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();
        ArbiterCommandRun execution = await _host.RunAsync(token => command.ExecuteAsync(
            null!,
            new ArbiterCommentSettings
            {
                Root = _host.WorkingDirectory,
                Path = "docs/guide.md",
                Line = 2,
                Body = "BODY-MARKER-7f3a review text",
            },
            token));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("BODY-MARKER-7f3a", execution.Output, StringComparison.Ordinal);
        Assert.Contains("decision", execution.Output, StringComparison.Ordinal);
    }
}
