using KyberWeave.Cli.Commands.Review;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Unsafe;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the suppression gate (<c>kyber-weave review suppressions</c>, decision D28): a suppression a
/// change adds is refused unless it says why, and a suppression that was already there is left alone.
/// </summary>
/// <remarks>
/// <para>
/// The three directions are pinned separately on purpose. A suite that only proves an entry appears
/// would pass for a command that rewrote the file, or one that flagged every suppression in the
/// repository including the ones no change touched — and the whole point of scanning added lines is
/// that 21 files already carry <c>#pragma warning disable</c> under a rule that only asks for a reason.
/// </para>
/// <para>
/// The command does not exist yet, so the case finds its type by name in the CLI assembly and
/// registers that type on a <see cref="CommandApp"/>. That keeps the suite compiling against today's
/// tree — a RED proof that does not compile proves nothing about the rule — and it fails with the
/// message below until the command, its registration and its scan ship.
/// </para>
/// </remarks>
public sealed class ReviewSuppressionsCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewSuppressionsCommandTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateSuppressionScanRepository(_repo);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// A suppression added without a reason is the case the gate exists for. The three forms are the
    /// ones decision D28 names: the pragma, the attribute and the project-level <c>NoWarn</c>.
    /// </summary>
    [Theory]
    [InlineData("src/a.cs", "#pragma warning disable CS0168")]
    [InlineData("src/a.cs", "[SuppressMessage(\"Category\", \"CS0168\")]")]
    [InlineData("src/a.csproj", "<NoWarn>CS0168</NoWarn>")]
    public void ASuppressionAddedWithoutAReasonFailsTheGate(string relativePath, string suppression)
    {
        AddLines(relativePath, suppression);

        CliRun run = Cli("review", "suppressions", ".");

        AssertExitCode(1, run);
    }

    /// <summary>D28 accepts the reason on the same added line as the suppression.</summary>
    [Fact]
    public void AReasonOnTheSameLineSatisfiesTheGate()
    {
        AddLines("src/a.cs", "#pragma warning disable CS0168 // reason: the analyzer cannot see generated code");

        CliRun run = Cli("review", "suppressions", ".");

        AssertExitCode(0, run);
    }

    /// <summary>D28 accepts the reason on the added line immediately above the suppression.</summary>
    [Fact]
    public void AReasonOnThePreviousLineSatisfiesTheGate()
    {
        AddLines(
            "src/a.cs",
            "// reason: the analyzer cannot see the generated code it guards",
            "#pragma warning disable CS0168");

        CliRun run = Cli("review", "suppressions", ".");

        AssertExitCode(0, run);
    }

    /// <summary>
    /// The suppression this repository already carries in a committed file is not the change's to
    /// judge. The file is changed — so it is in the scan's set — and the suppression line is context,
    /// not an added line, which is what makes this an assertion about added lines rather than about
    /// files.
    /// </summary>
    [Fact]
    public void ASuppressionThatWasAlreadyThereIsNotScanned()
    {
        AddLines("src/a.cs", "// an unrelated change beside a committed suppression");

        CliRun run = Cli("review", "suppressions", ".");

        AssertExitCode(0, run);
    }

    /// <summary>
    /// The command has to exist and be reachable from the <c>review</c> branch before any of the
    /// behaviour above can be observed. This case names the missing piece on its own so the RED
    /// reason reads as "no command", not as an unexplained failed assertion in the scan.
    /// </summary>
    [Fact]
    public void TheSuppressionGateIsRegisteredOnTheReviewBranch()
    {
        Type command = SuppressionCommandType();

        Assert.Equal("ReviewSuppressionsCommand", command.Name);
        Assert.Equal("KyberWeave.Cli.Commands.Review", command.Namespace);
    }

    private static Type SuppressionCommandType()
    {
        Type? command = typeof(ReviewGatesCommand).Assembly
            .GetTypes()
            .FirstOrDefault(type => string.Equals(type.Name, "ReviewSuppressionsCommand", StringComparison.Ordinal));

        Assert.True(
            command is not null,
            "The CLI assembly declares no 'ReviewSuppressionsCommand'. Decision D28 adds " +
            "'src/KyberWeave.Cli/Commands/Review/ReviewSuppressionsCommand.cs' beside ReviewGatesCommand and " +
            "registers it on the review branch in Program.cs, so every .NET agent's task gate can run it.");
        return command;
    }

    private static CliRun Cli(params string[] arguments)
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.UseStrictParsing();
            config.AddBranch("review", review => review.SafetyOff().AddCommand("suppressions", SuppressionCommandType()));
        });

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => app.Run(arguments));
        return new CliRun(execution.Result, execution.Output);
    }

    /// <summary>Appends lines to a repository file, leaving it modified and unstaged.</summary>
    private void AddLines(string relativePath, params string[] lines)
    {
        string path = Path.Combine(_repo, relativePath);
        string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        File.WriteAllText(path, existing + string.Join("\n", lines) + "\n");
    }

    private static void AssertExitCode(int expected, CliRun run)
    {
        Assert.True(
            run.ExitCode == expected,
            $"Expected exit code {expected} but was {run.ExitCode}. Output:{Environment.NewLine}{run.Output}");
    }

    private sealed record CliRun(int ExitCode, string Output);
}
