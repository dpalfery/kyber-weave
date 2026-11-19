using System.Runtime.Versioning;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The one <c>review verdict</c> fact that needs the real file system: a seat folder the process is not allowed to
/// list. Windows and root ignore the directory mode, so the denied listing cannot be produced anywhere else, and the
/// in-memory file system has no permission bits to refuse it. The behaviour itself — a folder the seat seam cannot list
/// is refused under KW-REVIEW-040, naming the folder — also has a unit test over a fake seat folder:
/// <c>ReviewVerdictSeatsCommandTests.ASeatFolderTheSeamCannotListIsRefusedUnderKw040NamingTheFolder</c>.
/// </summary>
public sealed class ReviewVerdictSeatsCommandTests : IDisposable
{
    private const string SeatUnparseable = "KW-REVIEW-040";

    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewVerdictSeatsCommandTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");
    }

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// A seats folder the process cannot list is a seat folder that cannot be read: KW-REVIEW-040 naming the folder, not
    /// an unhandled UnauthorizedAccessException out of the command.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ASeatFolderThatCannotBeListedIsRefusedUnderKw040NamingTheFolder()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            // Windows and root ignore the directory mode, so the denied listing cannot be produced here.
            Assert.Skip("Directory listing permission is not enforced for this user on this platform.");
        }

        string locked = Path.Combine(_temp.Path, "locked-seats");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            CapturedConsoleExecution<int> run = Verdict("--seats", locked);

            Assert.Equal(1, run.Result);
            AssertDiagnostic(run.Output, SeatUnparseable, "Error", locked);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        List<string> arguments = ["review", "verdict", _repo];
        arguments.AddRange(options);
        arguments.AddRange(["--format", "json"]);
        return ProcessConsoleCapture.Run(() => CreateApp().Run([.. arguments]));
    }

    private static CommandApp CreateApp()
    {
        CommandApp app = new(ReviewTestServices.Registrar());
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("review", review =>
            {
                review.SetDescription("Run the declared review gates and compute the review verdict.");
                review.AddCommand<ReviewVerdictCommand>("verdict")
                    .WithDescription(ReviewVerdictCommand.Description);
            });
        });
        return app;
    }
}
