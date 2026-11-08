using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the optional <c>requires-paths</c> gate key: a gate whose declared path is missing does not start, and says
/// so instead of running and failing on whatever the missing directory would have provided.
/// </summary>
/// <remarks>
/// <para>
/// The dash gates need <c>dash/node_modules</c>. A contributor without it should be told to install the dependencies,
/// not handed a wall of module-not-found output from a tool that never had what it needed. The missing path is the
/// same class of fact as a gate whose program could not start, so it stays <c>KW-REVIEW-033</c>: the gate never
/// started, which is not the same as a check that ran and failed (<c>KW-REVIEW-022</c>), and it is never a pass.
/// </para>
/// <para>
/// Every gate here is a marker write into the repository's git configuration, and the test reads that file back as
/// text. "The gate did not start" is therefore a fact about the repository rather than an absent log line, and the
/// test starts no process of its own.
/// </para>
/// </remarks>
public sealed class ReviewGatePrerequisiteTests : IDisposable
{
    private const string GateNotStarted = "KW-REVIEW-033";
    private const string GateFailed = "KW-REVIEW-022";
    private const string RequiredPath = "dash/node_modules";
    private const string MarkerValue = "t32-prerequisite-ran";

    private readonly string _repo;
    private readonly TempDirectory _temp = new();

    public ReviewGatePrerequisiteTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_repo);
    }

    private string ReportPath => Path.Combine(_temp.Path, "gates.json");

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// A blocking gate whose required path is absent never starts, its refusal is <c>KW-REVIEW-033</c> at Error, and
    /// the message names the path and how to provide it.
    /// </summary>
    /// <remarks>
    /// A gate that started anyway would report the missing directory through whatever tool it ran, which reads as a
    /// code failure in the change under review. The hint is what turns the refusal into an action: the path is named,
    /// and it says to install.
    /// </remarks>
    [Fact]
    public void AGateWhoseRequiredPathIsMissingIsReportedAsNotStartedWithTheInstallHint()
    {
        WriteGateConfiguration(true);

        CliRun run = Cli(GatesCommand());

        AssertExitCode(1, run);
        Assert.Equal("ERROR", SeverityOf(run.Output, GateNotStarted));
        Assert.Contains(RequiredPath, run.Output, StringComparison.Ordinal);
        Assert.Contains("install", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("npm", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
        Assert.False(GateStarted(), $"Gate 'dash-typecheck' started even though '{RequiredPath}' is absent.");
        using JsonDocument report = ReadReport();
        Assert.False(
            GatePassed(report, "dash-typecheck"),
            "A gate that never started must not read as a pass.");
        Assert.Equal("notStarted", Outcome(report, "dash-typecheck"));
    }

    /// <summary>
    /// The same missing path in a non-blocking gate is a warning, the run does not fail, and the gate still never
    /// reads as a pass.
    /// </summary>
    [Fact]
    public void AMissingRequiredPathInANonBlockingGateIsAWarningAndNeverReadsAsAPass()
    {
        WriteGateConfiguration(false);

        CliRun run = Cli(GatesCommand());

        AssertExitCode(0, run);
        Assert.Equal("WARNING", SeverityOf(run.Output, GateNotStarted));
        Assert.DoesNotContain(GateFailed, run.Output, StringComparison.Ordinal);
        Assert.False(GateStarted(), $"Gate 'dash-typecheck' started even though '{RequiredPath}' is absent.");
        using JsonDocument report = ReadReport();
        Assert.False(
            GatePassed(report, "dash-typecheck"),
            "A gate that never started must not read as a pass.");
    }

    /// <summary>
    /// A gate whose required path is present runs, so declaring a prerequisite cannot quietly disable a gate.
    /// </summary>
    [Fact]
    public void AGateWhoseRequiredPathIsPresentStarts()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "dash", "node_modules"));
        WriteGateConfiguration(true);

        CliRun run = Cli(GatesCommand());

        AssertExitCode(0, run);
        Assert.DoesNotContain(GateNotStarted, run.Output, StringComparison.Ordinal);
        Assert.True(GateStarted(), $"Gate 'dash-typecheck' did not start even though '{RequiredPath}' exists.");
        using JsonDocument report = ReadReport();
        Assert.True(GatePassed(report, "dash-typecheck"));
    }

    /// <summary>
    /// The configuration reader knows the key, so a host that declares it is not told the key does nothing.
    /// </summary>
    /// <remarks>
    /// An unknown key is reported and ignored, which would leave the gate running against a missing directory while
    /// the operator is told their declaration had no effect. This is the same rule that keeps <c>full-suite</c>
    /// working.
    /// </remarks>
    [Fact]
    public void TheLoaderDoesNotReportTheRequiresPathsKeyAsUnknown()
    {
        const string yaml = """
                            review:
                              gates:
                                - id: dash-typecheck
                                  requires-paths: [dash/node_modules]
                                  run: [npm, run, --prefix, dash, typecheck]
                            """;

        IReadOnlyList<string> warnings = ReviewConfigLoader.UnknownKeyWarnings(yaml);

        Assert.DoesNotContain(
            warnings,
            warning => warning.Contains("requires-paths", StringComparison.Ordinal));
    }

    [Fact]
    public void CouncilGateRequiresPaths_NormalizesWhitespaceAndDotAndTrailingSlashes()
    {
        const string yaml = """
                            review:
                              gates:
                                - id: dash-typecheck
                                  requires-paths: ["  ./dash/ node_modules/  "]
                                  run: [npm, run, --prefix, dash, typecheck]
                            """;

        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml(yaml).Review;
        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal(["dash/node_modules"], gate.RequiresPaths);
    }

    [Fact]
    public void CouncilGateRequiresPaths_AllowsTildeInRelativeDirectoryName()
    {
        const string yaml = """
                            review:
                              gates:
                                - id: dash-typecheck
                                  requires-paths: ["~cache/deps"]
                                  run: [npm, run, --prefix, dash, typecheck]
                            """;

        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml(yaml).Review;
        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal(["~cache/deps"], gate.RequiresPaths);
    }

    [Theory]
    [InlineData("/dash/node_modules")]
    [InlineData("~/dash/node_modules")]
    [InlineData("~")]
    [InlineData("C:/dash/node_modules")]
    [InlineData("c:dash/node_modules")]
    [InlineData("dash/../../node_modules")]
    [InlineData(".")]
    [InlineData("./")]
    public void CouncilGateRequiresPaths_RefusesAbsoluteEscapingOrRootPaths(string invalidPath)
    {
        string yaml = $"""
                      review:
                        gates:
                          - id: dash-typecheck
                            requires-paths: ["{invalidPath}"]
                            run: [npm, run, --prefix, dash, typecheck]
                      """;

        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));
    }

    [Fact]
    public void AGateWithMalformedRequiresPathSafelyReportsNotStarted()
    {
        ReviewGate gate = new("dash-typecheck", ["git", "status"], true, 900)
        {
            RequiresPaths = ["invalid\0path"]
        };

        GateReport report = GateRunner.Run(new ReviewConfig { Gates = [gate] }, _repo);
        GateResult result = Assert.Single(report.Gates);
        Assert.Equal(GateOutcome.NotStarted, result.Outcome);
        Assert.False(result.Passed);
        Assert.Contains("could not be resolved", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One gate that needs <c>dash/node_modules</c> and writes a marker when it starts, so the test can tell a gate
    /// that never started from one that ran.
    /// </summary>
    private void WriteGateConfiguration(bool blocking)
    {
        string blocked = blocking ? "true" : "false";
        string path = Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
                                 review:
                                   gates:
                                     - id: dash-typecheck
                                       requires-paths: ["{RequiredPath}"]
                                       blocking: {blocked}
                                       run: [git, config, --local, gate.ran, {MarkerValue}]
                                 """);
    }

    /// <summary>Whether the marker gate started, read from the repository's git configuration as text.</summary>
    private bool GateStarted()
    {
        string gitConfig = Path.Combine(_repo, ".git", "config");
        return File.Exists(gitConfig)
               && File.ReadAllText(gitConfig).Contains(MarkerValue, StringComparison.Ordinal);
    }

    private string[] GatesCommand()
    {
        return ["review", "gates", _repo, "--format", "json", "--out", ReportPath];
    }

    private static CliRun Cli(string[] arguments)
    {
        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => CommandAppFixture.ReviewGates().Run(arguments));
        return new CliRun(execution.Result, execution.Output);
    }

    private JsonDocument ReadReport()
    {
        return JsonDocument.Parse(File.ReadAllText(ReportPath));
    }

    private static bool GatePassed(JsonDocument report, string id)
    {
        return GateEntry(report, id).GetProperty("passed").GetBoolean();
    }

    private static string? Outcome(JsonDocument report, string id)
    {
        return GateEntry(report, id).GetProperty("outcome").GetString();
    }

    private static JsonElement GateEntry(JsonDocument report, string id)
    {
        return report.RootElement.GetProperty("gates").EnumerateArray()
            .Single(gate => gate.GetProperty("id").GetString() == id);
    }

    /// <summary>The severity of the named diagnostic in the <c>--format json</c> payload, or null when it is absent.</summary>
    private static string? SeverityOf(string output, string code)
    {
        char[] opening = ['{', '['];
        char[] closing = ['}', ']'];
        int first = output.IndexOfAny(opening);
        int last = output.LastIndexOfAny(closing);
        Assert.True(first >= 0 && last > first, $"No JSON payload in the output:{Environment.NewLine}{output}");

        using JsonDocument document = JsonDocument.Parse(output[first..(last + 1)]);
        string? severity = FindSeverity(document.RootElement, code);
        Assert.True(
            severity is not null,
            $"Expected {code} in the diagnostics. Output:{Environment.NewLine}{output}");
        return severity;
    }

    private static string? FindSeverity(JsonElement element, string code)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (FindSeverity(item, code) is { } found)
                    return found;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("code", out JsonElement foundCode)
            && foundCode.GetString() == code
            && element.TryGetProperty("severity", out JsonElement severity))
            return severity.GetString();

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (FindSeverity(property.Value, code) is { } nested)
                return nested;
        }

        return null;
    }

    private static void AssertExitCode(int expected, CliRun run)
    {
        Assert.True(
            run.ExitCode == expected,
            $"Expected exit code {expected} but was {run.ExitCode}. Output:{Environment.NewLine}{run.Output}");
    }

    private sealed record CliRun(int ExitCode, string Output);
}
