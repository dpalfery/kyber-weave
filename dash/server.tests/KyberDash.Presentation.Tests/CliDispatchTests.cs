using System.Text.Json;
using KyberDash.Cli;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Asserts the <c>kyberdash</c> router only parses arguments and delegates: each command must reach
/// exactly one service call with the parsed values, and the router must add no behaviour of its own.
/// </summary>
/// <remarks>
/// The router resolves services from the container it builds itself, exactly as it does in
/// production. The mocks are consulted only for a contract the container has no registration for,
/// or that a test overrides on purpose.
/// </remarks>
public class CliDispatchTests
{
    [Fact]
    public async Task VersionPrintsTheVersionAndTouchesNoService()
    {
        CliRun run = await CliRun.ExecuteAsync(["--version"]);

        Assert.Equal(0, run.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(run.Output.Trim()));
        run.Builder.Reports.Verify(
            r => r.GenerateReport(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task HelpPrintsUsageAndTouchesNoService()
    {
        CliRun run = await CliRun.ExecuteAsync(["--help"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("report", run.Output, StringComparison.Ordinal);
        Assert.Contains("kyber", run.Output, StringComparison.Ordinal);
        run.Builder.Reports.Verify(
            r => r.GenerateReport(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task NoArgumentsRunsTheDefaultTextReport()
    {
        CliRun run = await CliRun.ExecuteAsync([]);

        Assert.Equal(0, run.ExitCode);
        ReportCall call = Assert.IsType<ReportCall>(run.Builder.LastReport);
        Assert.Equal("text", call.Format);
        Assert.Equal(5, call.Limit);
        Assert.Contains("report body", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportForwardsEveryFilterToTheReportService()
    {
        CliRun run = await CliRun.ExecuteAsync(
            ["report", "--format", "markdown", "--harness", "claude-code", "--session", "s1", "--run", "r1", "--limit", "9"]);

        Assert.Equal(0, run.ExitCode);
        ReportCall call = Assert.IsType<ReportCall>(run.Builder.LastReport);
        Assert.Equal("markdown", call.Format);
        Assert.Equal("claude-code", call.Harness);
        Assert.Equal("s1", call.Session);
        Assert.Equal("r1", call.Run);
        Assert.Equal(9, call.Limit);
        Assert.Contains("report body", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportReturnsTheServiceExitCode()
    {
        CliRun run = await CliRun.ExecuteAsync(
            ["report"],
            b => b.Reports
                .Setup(r => r.GenerateReport(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .Returns((3, "report body")));

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("report body", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorJsonPrintsTheDiagnosticsFromTheService()
    {
        CliRun run = await CliRun.ExecuteAsync(["doctor", "--json"]);

        Assert.Equal(0, run.ExitCode);
        using JsonDocument body = JsonDocument.Parse(run.Output);
        Assert.Equal("canon.db", body.RootElement.GetProperty("databasePath").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("records").GetInt32());
        run.Builder.Doctor.Verify(d => d.RunDiagnostics(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DoctorTextSummarisesTheSameDiagnostics()
    {
        CliRun run = await CliRun.ExecuteAsync(["doctor"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("KyberDash Doctor", run.Output, StringComparison.Ordinal);
        Assert.Contains("Records: 0", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MenubarForwardsItsFlagsAndTheCliVersion()
    {
        CliRun run = await CliRun.ExecuteAsync(["menubar", "--force", "--update", "--version", "0.21.0"]);

        Assert.Equal(0, run.ExitCode);
        run.Builder.Menubar.Verify(
            m => m.InstallOrUpdateAsync(true, true, "0.21.0", Program.ResolveVersion(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains("menubar: unchanged", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineEnsureRequiresTheIfRunningFlag()
    {
        CliRun run = await CliRun.ExecuteAsync(["engine", "ensure"]);

        Assert.Equal(2, run.ExitCode);
        run.Builder.Lifecycle.Verify(
            l => l.EnsureRunningEngineMatchesVersionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EngineEnsureDelegatesToTheLifecycleService()
    {
        CliRun run = await CliRun.ExecuteAsync(["engine", "ensure", "--if-running"]);

        Assert.Equal(0, run.ExitCode);
        run.Builder.Lifecycle.Verify(
            l => l.EnsureRunningEngineMatchesVersionAsync(Program.ResolveVersion(), null, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains("engine: not-running", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task E2eSeedRequiresWorldAndDatabase()
    {
        CliRun run = await CliRun.ExecuteAsync(["e2e-seed", "--world", "harness-collection"]);

        Assert.Equal(2, run.ExitCode);
        run.Builder.Seeder.Verify(s => s.SeedWorld(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task E2eSeedDelegatesToTheSeederWithAnAbsoluteDatabasePath()
    {
        CliRun run = await CliRun.ExecuteAsync(["e2e-seed", "--world", "harness-collection", "--db", "relative.db"]);

        Assert.Equal(0, run.ExitCode);
        run.Builder.Seeder.Verify(
            s => s.SeedWorld("harness-collection", Path.GetFullPath("relative.db"), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanRefusesWithoutYes()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "clean", "--all"]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--yes", run.Error, StringComparison.Ordinal);
        run.Builder.Clean.Verify(
            c => c.CleanDatabase(It.IsAny<CleanScope>(), It.IsAny<Action?>(), It.IsAny<Action?>(), It.IsAny<Func<IReadOnlyList<string>?, int, int>?>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanPassesTheParsedScopeToTheCleanService()
    {
        CleanScope? captured = null;
        CliRun run = await CliRun.ExecuteAsync(
            ["kyber", "clean", "--yes", "--all", "--harness", "claude-code"],
            b => b.Clean
                .Setup(c => c.CleanDatabase(It.IsAny<CleanScope>(), It.IsAny<Action?>(), It.IsAny<Action?>(), It.IsAny<Func<IReadOnlyList<string>?, int, int>?>()))
                .Callback<CleanScope, Action?, Action?, Func<IReadOnlyList<string>?, int, int>?>((scope, _, _, _) => captured = scope)
                .Returns(PresentationTestData.EmptyCleanReport));

        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(captured);
        Assert.True(captured!.All);
        Assert.Equal(["claude-code"], captured.Harnesses);
    }

    [Fact]
    public async Task SettingsJsonPrintsTheServiceView()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "settings", "--json"]);

        Assert.Equal(0, run.ExitCode);
        using JsonDocument body = JsonDocument.Parse(run.Output);
        Assert.Equal(4747, body.RootElement.GetProperty("dashboardPort").GetInt32());
        run.Builder.Settings.Verify(s => s.GetSettings(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RefreshDelegatesToTheProjectionService()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "refresh"]);

        Assert.Equal(0, run.ExitCode);
        run.Builder.Projection.Verify(p => p.ProjectAll(), Times.Once);
        Assert.Contains("Projected:", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VacuumDelegatesToTheDoctorService()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "vacuum"], builder => builder.Doctor.Setup(d => d.RunVacuum()).Returns(true));

        Assert.Equal(0, run.ExitCode);
        run.Builder.Doctor.Verify(d => d.RunVacuum(), Times.Once);
        Assert.Contains("checkpoint and compaction complete", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VacuumOnAStoreItDoesNotApplyToSaysSoInsteadOfReportingCompaction()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "vacuum"], builder => builder.Doctor.Setup(d => d.RunVacuum()).Returns(false));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("vacuum does not apply", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("compaction complete", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturePluginInstallDelegatesToTheCaptureService()
    {
        CliRun run = await CliRun.ExecuteAsync(["kyber", "capture", "plugin", "install", "--harness", "opencode", "--if-installed"]);

        Assert.Equal(0, run.ExitCode);
        run.Builder.Capture.Verify(
            c => c.InstallPlugin("opencode", true, null, It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task WebSpawnsTheApiSiblingWithTheWebVerb()
    {
        CliRun run = await CliRun.ExecuteAsync(["web", "--no-open", "--port", "4747"]);

        Assert.Equal(0, run.ExitCode);
        ProcessInvocation invocation = Assert.Single(run.ProcessMode.Invocations);
        Assert.EndsWith("kyberdash-api", invocation.FileName, StringComparison.Ordinal);
        Assert.Equal(["web", "--no-open", "--port", "4747"], invocation.Arguments);
        Assert.False(invocation.ForwardStandardInput);
    }

    [Fact]
    public async Task McpSpawnsTheMcpSiblingForwardingStandardInput()
    {
        CliRun run = await CliRun.ExecuteAsync(["mcp", "--verbose"]);

        Assert.Equal(0, run.ExitCode);
        ProcessInvocation invocation = Assert.Single(run.ProcessMode.Invocations);
        Assert.EndsWith("kyberdash-mcp", invocation.FileName, StringComparison.Ordinal);
        Assert.Equal(["--verbose"], invocation.Arguments);
        Assert.True(invocation.ForwardStandardInput);
    }

    [Fact]
    public async Task OtelSpawnsTheApiSiblingInOtlpOnlyMode()
    {
        CliRun run = await CliRun.ExecuteAsync(["otel", "--port", "4318"]);

        Assert.Equal(0, run.ExitCode);
        ProcessInvocation invocation = Assert.Single(run.ProcessMode.Invocations);
        Assert.EndsWith("kyberdash-api", invocation.FileName, StringComparison.Ordinal);
        Assert.Equal(["--otlp-only", "--port", "4318"], invocation.Arguments);
    }

    [Fact]
    public async Task SiblingExitCodeIsReturnedUnchanged()
    {
        CliRun run = await CliRun.ExecuteAsync(["web"], processExitCode: 7);

        Assert.Equal(7, run.ExitCode);
    }

    [Fact]
    public async Task UnknownCommandFailsWithAGuidanceMessage()
    {
        CliRun run = await CliRun.ExecuteAsync(["frobnicate"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("unknown command", run.Error, StringComparison.Ordinal);
        Assert.Contains("--help", run.Error, StringComparison.Ordinal);
    }

    /// <summary>One CLI invocation, its streams, and the mocks it touched.</summary>
    private class CliRun
    {
        private CliRun(int exitCode, string output, string error, PresentationBuilder builder, RecordingProcessMode processMode)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
            Builder = builder;
            ProcessMode = processMode;
        }

        public int ExitCode { get; }

        public string Output { get; }

        public string Error { get; }

        public PresentationBuilder Builder { get; }

        public RecordingProcessMode ProcessMode { get; }

        public static async Task<CliRun> ExecuteAsync(
            string[] args,
            Action<PresentationBuilder>? configure = null,
            int processExitCode = 0)
        {
            PresentationBuilder builder = new();
            builder.ApplyCannedResults();
            configure?.Invoke(builder);

            using ServiceProvider container = Program.BuildProvider(args);
            IServiceProvider services = PresentationBuilder.Overlay(container, builder);

            RecordingProcessMode processMode = new(processExitCode);
            using StringWriter output = new();
            using StringWriter error = new();

            int exitCode = await Program.RunAsync(
                args,
                output,
                error,
                services,
                processMode,
                TestContext.Current.CancellationToken);

            return new CliRun(exitCode, output.ToString(), error.ToString(), builder, processMode);
        }
    }

    /// <summary>Records what the router asked to run and returns a fixed exit code.</summary>
    private class RecordingProcessMode : IProcessMode
    {
        private readonly int _exitCode;

        public RecordingProcessMode(int exitCode) => _exitCode = exitCode;

        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<int> RunAsync(ProcessInvocation invocation, TextWriter output, TextWriter standardError, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(_exitCode);
        }
    }
}
