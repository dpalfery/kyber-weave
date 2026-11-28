using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.20 (requirement 20 amendment), CLI: <c>kyber-weave arbiter comment --summary --author &lt;login&gt;
/// --body "&lt;summary&gt;"</c> classifies every row of a bot summary's findings table. It exits 0 on an answer, prints one
/// line per item with its path, line and class, and escapes operator text before it reaches the console. A human author, or
/// no author, is printed real.
/// RED: <c>ArbiterCommentSettings</c> has no <c>Summary</c> or <c>Author</c> property yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>ArbiterCommentSettings.Summary</c> (bool, <c>--summary</c>) and
/// <c>ArbiterCommentSettings.Author</c> (string?, <c>--author &lt;LOGIN&gt;</c>). With no provider configured every item is
/// real, which is the fail-safe answer, so these tests pin the shape and the fail-safe, not a nitpick answer.
/// </remarks>
public sealed class ArbiterSummaryCommentTriageCliTests : IDisposable
{
    private const string Summary =
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Use [red]this[/] name. |\n" +
        "| nit | `src/Gadget.cs` | 8-10 | Prefer a constant. |\n" +
        "| warning | `src/Widget.cs:20` | 20 | Null check missing. |\n";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private void WriteCitedFiles()
    {
        foreach (string relative in new[] { "src/Widget.cs", "src/Gadget.cs" })
        {
            _ = _host.Write(_host.InWork(relative.Split('/')), string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
        }
    }

    /// <summary>A bot summary prints one line per table row, each with its path and line, and exits 0.</summary>
    [Fact]
    public async Task Summary_PrintsOneLinePerTableRow()
    {
        WriteCitedFiles();
        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();

        ArbiterCommandRun execution = await _host.RunAsync(token => command.ExecuteAsync(
            null!,
            new ArbiterCommentSettings { Root = _host.WorkingDirectory, Summary = true, Author = "kilo-code-bot", Body = Summary },
            token));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("src/Widget.cs", execution.Output, StringComparison.Ordinal);
        Assert.Contains("src/Gadget.cs", execution.Output, StringComparison.Ordinal);
        Assert.Contains("12", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A human author's summary prints every item as real, so no operator is told a human's comment is a nitpick.</summary>
    [Fact]
    public async Task Summary_HumanAuthor_PrintsEveryItemReal()
    {
        WriteCitedFiles();
        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();

        ArbiterCommandRun execution = await _host.RunAsync(token => command.ExecuteAsync(
            null!,
            new ArbiterCommentSettings { Root = _host.WorkingDirectory, Summary = true, Author = "dave", Body = Summary },
            token));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("nitpick", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("already-fixed", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A summary with no author is fail-safe real and still exits 0.</summary>
    [Fact]
    public async Task Summary_NoAuthor_ExitsZeroWithRealItems()
    {
        WriteCitedFiles();
        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();

        ArbiterCommandRun execution = await _host.RunAsync(token => command.ExecuteAsync(
            null!,
            new ArbiterCommentSettings { Root = _host.WorkingDirectory, Summary = true, Body = Summary },
            token));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("nitpick", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>Operator text in a summary's issue cell renders literally and does not throw.</summary>
    [Fact]
    public async Task Summary_IssueWithMarkupCharacters_RendersLiterally()
    {
        WriteCitedFiles();
        ArbiterCommentCommand command = _host.Command<ArbiterCommentCommand>();

        ArbiterCommandRun execution = await _host.RunAsync(token => command.ExecuteAsync(
            null!,
            new ArbiterCommentSettings { Root = _host.WorkingDirectory, Summary = true, Author = "dave", Body = Summary },
            token));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Use [red]this[/] name.", execution.Output, StringComparison.Ordinal);
    }
}
