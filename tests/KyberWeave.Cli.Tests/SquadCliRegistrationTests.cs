using KyberWeave.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The real command tree registers the <c>squad</c> branch: every verb renders its own help with the
/// examples an operator needs, and unrecognized options are refused rather than silently discarded.
/// </summary>
public sealed class SquadCliRegistrationTests
{
    private static readonly string[] Verbs = ["install", "update", "uninstall", "status", "doctor", "pack"];

    [Fact]
    public void ProgramRegistersSquadBranchWithAllVerbsDescriptionsAndExamples()
    {
        CapturedConsoleExecution<int> root = RunInProcess(["squad", "--help"]);
        Assert.Equal(0, root.Result);
        foreach (string verb in Verbs)
        {
            Assert.Contains($"    {verb}", root.Output, StringComparison.Ordinal);
        }

        // Every verb names at least one example an operator can copy.
        foreach (string verb in Verbs)
        {
            CapturedConsoleExecution<int> verbHelp = RunInProcess(["squad", verb, "--help"]);
            Assert.Equal(0, verbHelp.Result);
            Assert.Contains("EXAMPLES:", verbHelp.Output, StringComparison.Ordinal);
            Assert.Contains($"kyber-weave squad {verb}", verbHelp.Output, StringComparison.Ordinal);
        }

        // Strict parsing is the CLI-wide guard against silently discarded unknown options
        // (the squad --path incident): an unrecognized option is refused.
        CapturedConsoleExecution<int> unknownOption = RunInProcess(["squad", "install", "--bogus-option"]);
        Assert.NotEqual(0, unknownOption.Result);
        Assert.Contains("Unknown option", unknownOption.Output, StringComparison.Ordinal);
    }

    private static CapturedConsoleExecution<int> RunInProcess(string[] args) =>
        ProcessConsoleCapture.Run(() => CliApplication.Create().Run(args));
}
