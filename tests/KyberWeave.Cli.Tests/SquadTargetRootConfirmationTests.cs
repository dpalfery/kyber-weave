using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What the target-root confirmation echoes and asks, over a test console and the shared console fake.</summary>
public sealed class SquadTargetRootConfirmationTests : IDisposable
{
    private const string Root = "/kw/work/project";
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();

    public void Dispose()
    {
        _ansi.Dispose();
        _streams.Dispose();
    }

    [Fact]
    public void Confirm_InteractiveAccepted_ProceedsAfterAskingOnce()
    {
        List<string> questions = [];

        bool proceed = Confirmation().Confirm(
            Root, SquadDeploymentScope.Project, "install", isInteractive: true, yes: false, readAnswer: question =>
            {
                questions.Add(question);
                return true;
            });

        Assert.True(proceed);
        Assert.Equal(["Proceed with 'install' at '/kw/work/project' (project scope)? (y/n)"], questions);
        Assert.Equal("kyber-weave squad install: target root /kw/work/project (project scope).\n", Output());
    }

    [Fact]
    public void Confirm_InteractiveDeclined_StopsAfterEchoingTheRoot()
    {
        bool proceed = Confirmation().Confirm(
            Root, SquadDeploymentScope.Project, "install", isInteractive: true, yes: false, readAnswer: _ => false);

        Assert.False(proceed);
        Assert.Equal("kyber-weave squad install: target root /kw/work/project (project scope).\n", Output());
    }

    [Fact]
    public void Confirm_YesFlag_ProceedsWithoutAsking()
    {
        bool proceed = Confirmation().Confirm(
            Root, SquadDeploymentScope.Global, "update", isInteractive: true, yes: true, readAnswer: NeverAsked);

        Assert.True(proceed);
        Assert.Equal("kyber-weave squad update: target root /kw/work/project (global scope).\n", Output());
    }

    [Fact]
    public void Confirm_NonInteractive_ProceedsWithoutAsking()
    {
        bool proceed = Confirmation().Confirm(
            Root, SquadDeploymentScope.Project, "uninstall", isInteractive: false, yes: false, readAnswer: NeverAsked);

        Assert.True(proceed);
        Assert.Equal("kyber-weave squad uninstall: target root /kw/work/project (project scope).\n", Output());
    }

    [Fact]
    public void Confirm_GlobalRoots_AreEchoedAndCarriedIntoTheQuestion()
    {
        string? asked = null;

        bool proceed = Confirmation().Confirm(
            Root,
            SquadDeploymentScope.Global,
            "install",
            isInteractive: true,
            yes: false,
            readAnswer: question =>
            {
                asked = question;
                return true;
            },
            globalTargetRoots: [(SquadTarget.Claude, "/home/u/.claude"), (SquadTarget.OpenCode, "/home/u/.config/opencode")]);

        Assert.True(proceed);
        Assert.Equal(
            "kyber-weave squad install: target root /kw/work/project (global scope).\n" +
            "  claude writes beneath /home/u/.claude.\n" +
            "  opencode writes beneath /home/u/.config/opencode.\n",
            Output());
        Assert.Equal(
            "Proceed with 'install' at '/kw/work/project' (global scope), writing beneath: " +
            "claude '/home/u/.claude', opencode '/home/u/.config/opencode'? (y/n)",
            asked);
    }

    [Fact]
    public void Confirm_LongRoot_IsNotFolded()
    {
        _ansi.Profile.Width = 20;
        string longRoot = "/kw/" + new string('x', 70);

        Confirmation().Confirm(longRoot, SquadDeploymentScope.Project, "install", isInteractive: false, yes: false, readAnswer: null);

        Assert.Equal($"kyber-weave squad install: target root {longRoot} (project scope).\n", Output());
        Assert.Equal(20, _ansi.Profile.Width);
    }

    [Theory]
    [InlineData("y\n", true)]
    [InlineData("YES\n", true)]
    [InlineData("n\n", false)]
    [InlineData("maybe\n", false)]
    [InlineData("", false)]
    public void Confirm_WithoutAnAnswerSeam_AsksOnTheConsoleAndOnlyYesProceeds(string typed, bool expected)
    {
        _streams.ScriptInput(typed);

        bool proceed = Confirmation().Confirm(
            Root, SquadDeploymentScope.Project, "install", isInteractive: true, yes: false, readAnswer: null);

        Assert.Equal(expected, proceed);
        Assert.EndsWith("Proceed with 'install' at '/kw/work/project' (project scope)? (y/n) ", Output(), StringComparison.Ordinal);
    }

    private static bool NeverAsked(string question) =>
        throw new InvalidOperationException($"The prompt must not be shown: {question}");

    private SquadTargetRootConfirmation Confirmation() => new(new SquadConsole(_ansi, _streams));

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
