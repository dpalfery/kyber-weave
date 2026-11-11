using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for the CLI <c>kyber-weave arbiter comment</c> while the comment rule is in shadow. The printed class is
/// <c>real</c>, and a line beside it says what the model would have given: <c>shadow: would have been nitpick</c>. A host
/// override that switches shadow off prints the model's class with no shadow line. The text output is the only output mode.
/// </summary>
/// <remarks>
/// The provider is substituted through <c>ArbiterCommentCommand.ProviderFactory</c>, so the printed answer does not depend on
/// a live provider. Host configuration is written to <c>.kyber-weave/kyber-weave.yml</c> under the temp root the command reads.
/// </remarks>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterCommentShadowCliTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    /// <remarks>The issue is short so the printed row stays under the 80-column capture width and the note is not wrapped.</remarks>
    private const string Summary =
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| nit | `src/Widget.cs` | L12 | Rename this. |\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Answers every comment question with a confident nitpick.</summary>
    private sealed class NitpickProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null && rule.Id == CommentTriageService.CommentRuleId)
                .ToDictionary(rule => rule.Id, _ => "nitpick", StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, 0.95, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }

    private void WriteCitedFile()
    {
        string full = Path.Combine(_temp.Path, "src", "Widget.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
    }

    /// <summary>Writes the host configuration the command reads for this root.</summary>
    private void WriteHostConfig(string yaml)
    {
        string directory = Path.Combine(_temp.Path, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml);
    }

    private ArbiterCommentCommand Command(IArbiterProvider provider) =>
        new() { ProviderFactory = (_, _) => provider };

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    /// <summary>An inline answer in shadow prints the real class and the line saying what the model would have given.</summary>
    [Fact]
    public void Inline_InShadow_PrintsRealAndTheLineSayingWhatTheModelWouldHaveGiven()
    {
        WriteCitedFile();
        ArbiterCommentCommand command = Command(new NitpickProvider());

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings { Root = _temp.Path, Path = "src/Widget.cs", Line = 12, Body = "Rename this." }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("class real", execution.Output, StringComparison.Ordinal);
        Assert.Contains("shadow: would have been nitpick", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("class nitpick", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>With no provider the shadow line says nothing was answered, so an empty answer is not read as a nitpick.</summary>
    [Fact]
    public void Inline_NoProvider_InShadow_SaysNothingWasAnswered()
    {
        WriteCitedFile();
        ArbiterCommentCommand command = Command(new NoneProvider());

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings { Root = _temp.Path, Path = "src/Widget.cs", Line = 12, Body = "Rename this." }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("class real", execution.Output, StringComparison.Ordinal);
        Assert.Contains("shadow: nothing answered", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A host override that switches shadow off prints the model's class and no shadow line.</summary>
    [Fact]
    public void Inline_HostOverrideSwitchingShadowOff_PrintsTheModelsClassWithNoShadowLine()
    {
        WriteCitedFile();
        WriteHostConfig("arbiter:\n  enabled: true\n  rules:\n    - id: KW-ARB-COMMENT-001\n      shadow: false\n");
        ArbiterCommentCommand command = Command(new NitpickProvider());

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings { Root = _temp.Path, Path = "src/Widget.cs", Line = 12, Body = "Rename this." }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("class nitpick", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("shadow:", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>Each summary row in shadow prints its real class and the would-have-been note beside it.</summary>
    [Fact]
    public void Summary_InShadow_PrintsTheWouldHaveBeenNoteOnEachRow()
    {
        WriteCitedFile();
        WriteHostConfig("arbiter:\n  enabled: true\n  comment-triage:\n    bot-logins:\n      - " + BotLogin + "\n");
        ArbiterCommentCommand command = Command(new NitpickProvider());

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings { Root = _temp.Path, Summary = true, Author = BotLogin, Body = Summary }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("src/Widget.cs:12 real", execution.Output, StringComparison.Ordinal);
        Assert.Contains("(shadow: would have been nitpick)", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Widget.cs:12 nitpick", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>With shadow switched off, each summary row prints the model's class and no shadow note.</summary>
    [Fact]
    public void Summary_HostOverrideSwitchingShadowOff_PrintsTheModelsClassWithNoShadowNote()
    {
        WriteCitedFile();
        WriteHostConfig(
            "arbiter:\n  enabled: true\n  comment-triage:\n    bot-logins:\n      - " + BotLogin +
            "\n  rules:\n    - id: KW-ARB-COMMENT-001\n      shadow: false\n");
        ArbiterCommentCommand command = Command(new NitpickProvider());

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterCommentSettings { Root = _temp.Path, Summary = true, Author = BotLogin, Body = Summary }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("src/Widget.cs:12 nitpick", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("shadow:", execution.Output, StringComparison.Ordinal);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
