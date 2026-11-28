using KyberWeave.Cli.Commands.Review;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests;

/// <summary>
/// The review commands write the plain lines their use case produces through the standard-stream console they were
/// given and render markup and reports on the Spectre console they were given, so a unit test reads both without a
/// terminal and tells which stream a line went to.
/// </summary>
public sealed class ReviewCommandConsoleSeamTests
{
    private static ReviewCommandResult WithFinding() => new(
        0,
        Report: ReportOf("KW-TEST-1"),
        Epilogue: ["epilogue one", "epilogue two"]);

    private static DiagnosticReport ReportOf(string code)
    {
        DiagnosticReport report = new();
        report.Add(new Diagnostic(code, Severity.Warning, "a finding", "subject"));
        return report;
    }

    private static TestConsole Ansi()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        return console;
    }

    // ------------------------------------------------------------------------------------ catalogue

    [Fact]
    public void Catalogue_HandsTheUseCaseTheStandardOutputWriterAndRendersItsReportOnTheAnsiConsole()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ScriptedService service = new(WithFinding());
        ReviewCatalogueCommand command = new(service, standard, ansi);

        int exit = command.Execute(null!, new ReviewCatalogueSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Same(standard.Out, service.Output);
        Assert.Contains("KW-TEST-1", ansi.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-TEST-1", standard.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalogue_AClientError_IsWrittenPlainToTheStandardOutputAndNothingIsRendered()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ReviewCatalogueCommand command = new(
            new ScriptedService(new ReviewCommandResult(2, ClientError: "unknown technology [x]")), standard, ansi);

        int exit = command.Execute(null!, new ReviewCatalogueSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave review catalogue: error: unknown technology [x]" + Environment.NewLine,
            standard.Output);
        Assert.Equal(string.Empty, ansi.Output);
    }

    // ------------------------------------------------------------------------------------ cycle

    [Fact]
    public void Cycle_WritesItsEpilogueToTheStandardOutputAndItsReportToTheAnsiConsole()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ScriptedService service = new(WithFinding());
        ReviewCycleCommand command = new(service, standard, ansi);

        int exit = command.Execute(null!, new ReviewCycleSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Same(standard.Out, service.Output);
        Assert.Contains("KW-TEST-1", ansi.Output, StringComparison.Ordinal);
        Assert.Equal(
            "epilogue one" + Environment.NewLine + "epilogue two" + Environment.NewLine,
            standard.Output);
    }

    [Fact]
    public void Cycle_AClientError_IsWrittenPlainToTheStandardOutput()
    {
        FakeConsole standard = new();
        ReviewCycleCommand command = new(
            new ScriptedService(new ReviewCommandResult(2, ClientError: "no cycle")), standard, Ansi());

        int exit = command.Execute(null!, new ReviewCycleSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave review cycle: error: no cycle" + Environment.NewLine, standard.Output);
    }

    // ------------------------------------------------------------------------------------ suppressions

    [Fact]
    public void Suppressions_RendersItsReportOnTheAnsiConsole()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ReviewSuppressionsCommand command = new(
            new ScriptedService(new ReviewCommandResult(1, Report: ReportOf("KW-TEST-2"))), standard, ansi);

        int exit = command.Execute(null!, new ReviewSuppressionsSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-TEST-2", ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standard.Output);
    }

    [Fact]
    public void Suppressions_AClientError_IsWrittenPlainSoBracketsAreNotReadAsMarkup()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ReviewSuppressionsCommand command = new(
            new ScriptedService(new ReviewCommandResult(2, ClientError: "bad ref [oops]")), standard, ansi);

        int exit = command.Execute(null!, new ReviewSuppressionsSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave review suppressions: error: bad ref [oops]" + Environment.NewLine,
            standard.Output);
        Assert.Equal(string.Empty, ansi.Output);
    }

    // ------------------------------------------------------------------------------------ seats

    [Fact]
    public void Seats_WritesItsEpilogueAndAnErrorToTheStandardOutputAndItsReportToTheAnsiConsole()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ScriptedService service = new(WithFinding());
        ReviewSeatsCommand command = new(service, new NoDecisionModels(), standard, ansi, new ScriptedProcessExecutor(_ => throw new InvalidOperationException()));

        int exit = command.Execute(null!, new ReviewSeatsSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Same(standard.Out, service.Output);
        Assert.Contains("KW-TEST-1", ansi.Output, StringComparison.Ordinal);
        Assert.Equal(
            "epilogue one" + Environment.NewLine + "epilogue two" + Environment.NewLine,
            standard.Output);
    }

    [Fact]
    public void Seats_AClientError_IsWrittenPlainToTheStandardOutput()
    {
        FakeConsole standard = new();
        ReviewSeatsCommand command = new(
            new ScriptedService(new ReviewCommandResult(2, ClientError: "no folder")),
            new NoDecisionModels(),
            standard,
            Ansi(),
            new ScriptedProcessExecutor(_ => throw new InvalidOperationException()));

        int exit = command.Execute(null!, new ReviewSeatsSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave review seats: error: no folder" + Environment.NewLine, standard.Output);
    }

    // ------------------------------------------------------------------------------------ verdict

    [Fact]
    public void Verdict_ThePrintedNextStep_GoesToTheStandardOutputForAHumanFormatOnly()
    {
        FakeConsole humanStandard = new();
        ReviewVerdictCommand human = new(
            new ScriptedVerdicts(new ReviewVerdictResult(ReportOf("KW-TEST-3"), 0, "run this next")),
            humanStandard,
            Ansi());
        FakeConsole jsonStandard = new();
        ReviewVerdictCommand json = new(
            new ScriptedVerdicts(new ReviewVerdictResult(ReportOf("KW-TEST-3"), 0, "run this next")),
            jsonStandard,
            Ansi());

        human.Execute(null!, new ReviewVerdictSettings { Format = "table" }, CancellationToken.None);
        json.Execute(null!, new ReviewVerdictSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal("run this next" + Environment.NewLine, humanStandard.Output);
        Assert.Equal(string.Empty, jsonStandard.Output);
    }

    [Fact]
    public void Verdict_RendersItsReportOnTheAnsiConsole()
    {
        TestConsole ansi = Ansi();
        ReviewVerdictCommand command = new(
            new ScriptedVerdicts(new ReviewVerdictResult(ReportOf("KW-TEST-3"), 1, null)), new FakeConsole(), ansi);

        int exit = command.Execute(null!, new ReviewVerdictSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-TEST-3", ansi.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ gates

    [Fact]
    public void Gates_ARefusedOption_IsWrittenAsMarkupOnTheAnsiConsole()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ScriptedGates service = new(new ReviewCommandResult(0));
        ReviewGatesCommand command = new(service, standard, ansi, new ScriptedProcessExecutor(_ => throw new InvalidOperationException()));

        int exit = command.Execute(
            null!, new ReviewGatesSettings { Config = " " }, CancellationToken.None);

        Assert.Equal(ReviewCommandResult.ClientInputExitCode, exit);
        Assert.Contains("kyber-weave review gates: error:", ansi.Output, StringComparison.Ordinal);
        Assert.Contains("--config was given an empty value", ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standard.Output);
        Assert.Null(service.Request);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Gates_TheRunIsInteractiveOnlyWhenTheStandardInputIsNotRedirected(bool redirected, bool interactive)
    {
        FakeConsole standard = new(isInputRedirected: redirected);
        ScriptedGates service = new(new ReviewCommandResult(0));
        ReviewGatesCommand command = new(service, standard, Ansi(), new ScriptedProcessExecutor(_ => throw new InvalidOperationException()));

        command.Execute(null!, new ReviewGatesSettings(), CancellationToken.None);

        Assert.NotNull(service.Request);
        Assert.Equal(interactive, service.Request.Interactive);
    }

    [Fact]
    public void Gates_HandsTheUseCaseTheStandardOutputWriterAndWritesItsEpilogueThere()
    {
        FakeConsole standard = new();
        TestConsole ansi = Ansi();
        ScriptedGates service = new(WithFinding());
        ReviewGatesCommand command = new(service, standard, ansi, new ScriptedProcessExecutor(_ => throw new InvalidOperationException()));

        int exit = command.Execute(null!, new ReviewGatesSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Same(standard.Out, service.Output);
        Assert.Contains("KW-TEST-1", ansi.Output, StringComparison.Ordinal);
        Assert.Equal(
            "epilogue one" + Environment.NewLine + "epilogue two" + Environment.NewLine,
            standard.Output);
    }

    // ------------------------------------------------------------------------------------ fakes

    /// <summary>One scripted use case behind every review service the commands call, remembering the writer it was given.</summary>
    private sealed class ScriptedService(ReviewCommandResult result)
        : IReviewCatalogueService, IReviewCycleService, IReviewSuppressionsService, IReviewSeatsService
    {
        public TextWriter? Output { get; private set; }

        public ReviewCommandResult Run(ReviewCatalogueRequest request, TextWriter output, CancellationToken cancellationToken)
        {
            Output = output;
            return result;
        }

        public Task<ReviewCommandResult> RunAsync(
            ReviewCatalogueRequest request,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            Output = output;
            return Task.FromResult(result);
        }

        public ReviewCommandResult Run(ReviewCycleRequest request, TextWriter output, CancellationToken cancellationToken)
        {
            Output = output;
            return result;
        }

        public ReviewCommandResult Run(ReviewSuppressionsRequest request, CancellationToken cancellationToken) => result;

        public Task<ReviewCommandResult> RunAsync(
            ReviewSuppressionsRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public ReviewCommandResult Prepare(
            ReviewSeatsRequest request,
            Func<string, KyberWeaveConfig, IReviewDecisionModel> decisionModel,
            IProcessExecutor indexExecutor,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Output = output;
            return result;
        }

        public Task<ReviewCommandResult> PrepareAsync(
            ReviewSeatsRequest request,
            Func<string, KyberWeaveConfig, IReviewDecisionModel> decisionModel,
            IProcessRunner indexExecutor,
            TextWriter output,
            CancellationToken cancellationToken = default)
        {
            Output = output;
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedGates(ReviewCommandResult result) : IReviewGatesService
    {
        public ReviewGatesRequest? Request { get; private set; }

        public TextWriter? Output { get; private set; }

        public ReviewCommandResult Run(
            ReviewGatesRequest request, IProcessExecutor indexExecutor, TextWriter output, CancellationToken cancellationToken)
        {
            Request = request;
            Output = output;
            return result;
        }

        public Task<ReviewCommandResult> RunAsync(
            ReviewGatesRequest request, IProcessRunner indexExecutor, TextWriter output, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class ScriptedVerdicts(ReviewVerdictResult result) : IReviewVerdictService
    {
        public ReviewVerdictResult Decide(ReviewVerdictRequest request, CancellationToken cancellationToken) => result;

        public Task<ReviewVerdictResult> DecideAsync(ReviewVerdictRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class NoDecisionModels : IReviewDecisionModelFactory
    {
        public IReviewDecisionModel Create(
            IArbiterProvider? provider,
            IReadOnlyList<ArbiterRule> rules,
            string? logDirectory,
            TimeProvider clock,
            string? unavailableReason = null,
            string? cachePath = null) => throw new NotSupportedException();

        public IReviewDecisionModel CreateForRepository(
            string repositoryRoot, KyberWeaveConfig config, bool disabled, string? cachePath) =>
            throw new NotSupportedException();

        public Task<IReviewDecisionModel> CreateForRepositoryAsync(
            string repositoryRoot,
            KyberWeaveConfig config,
            bool disabled,
            string? cachePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
