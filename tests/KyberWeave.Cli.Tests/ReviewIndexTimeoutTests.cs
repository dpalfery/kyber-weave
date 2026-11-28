using System.Diagnostics;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The index (code-graph) child that <c>review gates</c> and <c>review seats</c> start must run under the ten-minute
/// limit, both when the command is built by hand and when the container builds it.
/// </summary>
public sealed class ReviewIndexTimeoutTests
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private static ScriptedProcessExecutor Bounded() => new(_ => new ProcessResult(0, string.Empty, string.Empty));

    private static void RunIndexChild(IProcessExecutor? executor)
    {
        Assert.NotNull(executor);
        _ = executor.Run(new ProcessStartInfo("codegraph"), string.Empty);
    }

    [Fact]
    public void Gates_TheIndexChildRunsUnderTheTenMinuteLimit()
    {
        ScriptedProcessExecutor bounded = Bounded();
        RecordingGates service = new();
        ReviewGatesCommand command = new(service, new FakeConsole(), new TestConsole(), bounded);

        _ = command.Execute(null!, new ReviewGatesSettings(), CancellationToken.None);

        RunIndexChild(service.Executor);
        Assert.Equal([TenMinutes], bounded.Timeouts);
    }

    [Fact]
    public void Seats_TheIndexChildRunsUnderTheTenMinuteLimit()
    {
        ScriptedProcessExecutor bounded = Bounded();
        RecordingSeats service = new();
        ReviewSeatsCommand command = new(service, new NoDecisionModels(), new FakeConsole(), new TestConsole(), bounded);

        _ = command.Execute(null!, new ReviewSeatsSettings(), CancellationToken.None);

        RunIndexChild(service.Executor);
        Assert.Equal([TenMinutes], bounded.Timeouts);
    }

    [Fact]
    public void TheContainerBuildsTheGatesCommandOverTheBoundedExecutor()
    {
        ScriptedProcessExecutor bounded = Bounded();
        RecordingGates service = new();
        using ArbiterTestHost host = ArbiterTestHost.Create(services =>
        {
            _ = services.AddSingleton<IBoundedProcessExecutor>(bounded);
            _ = services.AddSingleton<IReviewGatesService>(service);
        });

        _ = host.Command<ReviewGatesCommand>().Execute(null!, new ReviewGatesSettings(), CancellationToken.None);

        RunIndexChild(service.Executor);
        Assert.Equal([TenMinutes], bounded.Timeouts);
    }

    [Fact]
    public void TheContainerBuildsTheSeatsCommandOverTheBoundedExecutor()
    {
        ScriptedProcessExecutor bounded = Bounded();
        RecordingSeats service = new();
        using ArbiterTestHost host = ArbiterTestHost.Create(services =>
        {
            _ = services.AddSingleton<IBoundedProcessExecutor>(bounded);
            _ = services.AddSingleton<IReviewSeatsService>(service);
        });

        _ = host.Command<ReviewSeatsCommand>().Execute(null!, new ReviewSeatsSettings(), CancellationToken.None);

        RunIndexChild(service.Executor);
        Assert.Equal([TenMinutes], bounded.Timeouts);
    }

    private sealed class RecordingGates : IReviewGatesService
    {
        public IProcessExecutor? Executor { get; private set; }

        public ReviewCommandResult Run(
            ReviewGatesRequest request, IProcessExecutor indexExecutor, TextWriter output, CancellationToken cancellationToken)
        {
            Executor = indexExecutor;
            return new ReviewCommandResult(0);
        }
    }

    private sealed class RecordingSeats : IReviewSeatsService
    {
        public IProcessExecutor? Executor { get; private set; }

        public ReviewCommandResult Prepare(
            ReviewSeatsRequest request,
            Func<string, KyberWeaveConfig, IReviewDecisionModel> decisionModel,
            IProcessExecutor indexExecutor,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Executor = indexExecutor;
            return new ReviewCommandResult(0);
        }

        public Task<ReviewCommandResult> PrepareAsync(
            ReviewSeatsRequest request,
            Func<string, KyberWeaveConfig, IReviewDecisionModel> decisionModel,
            IProcessRunner indexExecutor,
            TextWriter output,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewCommandResult(0));
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
    }
}
