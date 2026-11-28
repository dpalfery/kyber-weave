using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 4538775160: <c>arbiter comment</c> refuses a body over the MCP tool's text limit on both the single
/// and the summary path. The summary path used to parse and classify an unbounded body.
/// </summary>
public sealed class ArbiterCommentBodyLimitCliTests : IDisposable
{
    /// <summary>The MCP tool's text limit, written out here so the test pins the value, not a shared name.</summary>
    private const int TextLimit = 256 * 1024;

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string WriteDocument() =>
        _host.Write(_host.InWork("docs", "guide.md"), "# Guide\nIntro.\n## Install\nSteps.\n");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Comment_BodyOverTheTextLimit_IsRefused(bool summary)
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
                Summary = summary,
                Author = "dave",
                Body = new string('x', TextLimit + 1),
            },
            token));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("larger than", execution.Output, StringComparison.Ordinal);
    }
}
