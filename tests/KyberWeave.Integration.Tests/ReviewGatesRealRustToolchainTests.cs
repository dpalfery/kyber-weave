using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     The one <c>docs init</c> review-gates case that needs a real repository and a real toolchain: after scaffolding
///     the rust fixture, <c>review gates --agent rust-dev</c> runs cargo against a real git work tree. The rest of the
///     review-gates scaffolding is proven in memory by <c>DocsInitReviewGatesTests</c> and
///     <c>DocsInitReviewGatesReportTests</c>.
/// </summary>
public sealed class ReviewGatesRealRustToolchainTests : IDisposable
{
    private const string NoGatesForAgent = "KW-REVIEW-027";

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     After <c>docs init</c> on the rust fixture, <c>review gates --agent rust-dev</c>
    ///     runs that agent's gates instead of reporting that no gates are declared. Toolchain
    ///     runs need cargo; the selection itself is asserted from the written configuration
    ///     when cargo is missing, so the test holds on hosts without the toolchain.
    ///     The R6.4 trigger is the task-gate skill, which always names a task, so the run
    ///     passes <c>--task r6-4-probe</c>: rust-clippy uses
    ///     <c>--target-dir .agent_scratchpad/{task}/target</c>, which is refused without one.
    /// </summary>
    [Fact]
    public async Task ReviewGatesRunsTheWrittenRustGatesAndReportsGatesDeclared()
    {
        string repo = Path.Combine(_temp.Path, "rust-" + Guid.NewGuid().ToString("N"));
        TestGitRepositoryTemplate.CreateDocsInitFixtureRepository("rust", ["rust"], repo);
        await DocsTestCommands.Scaffolding.ScaffoldAsync(repo);
        await File.AppendAllTextAsync(Path.Combine(repo, "src", "lib.rs"), "\n// review probe\n");
        string reportPath = Path.Combine(_temp.Path, "rust-gates-" + Guid.NewGuid().ToString("N") + ".json");

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => CommandAppFixture.ReviewGates().Run(
        [
            "review", "gates", repo, "--agent", "rust-dev", "--task", "r6-4-probe",
            "--paths", "src/lib.rs", "--out", reportPath
        ]));

        Assert.DoesNotContain(NoGatesForAgent, execution.Output, StringComparison.Ordinal);
        if (await CargoAvailable())
        {
            Assert.True(
                File.Exists(reportPath),
                $"review gates wrote no report. Output:{Environment.NewLine}{execution.Output}");
            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.NotEmpty(report.RootElement.GetProperty("gates").EnumerateArray());
        }
        else
        {
            KyberWeaveConfig config = ConfigFileReader.Load(repo);
            Assert.True(
                config.Review.TaskGates.ContainsKey("rust-dev"),
                "docs init wrote no rust-dev task gates, so there was nothing to select.");
            Assert.NotEmpty(config.Review.TaskGates["rust-dev"]);
        }
    }

    private static async Task<bool> CargoAvailable()
    {
        try
        {
            ProcessStartInfo startInfo = new("cargo")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--version");

            // Bounded: a rustup proxy can stall on a toolchain download, and that stall must not hang the suite.
            return (await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(30))).ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
