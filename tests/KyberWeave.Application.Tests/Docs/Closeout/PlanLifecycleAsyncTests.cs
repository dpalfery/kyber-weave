using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static KyberWeave.Tests.PlanFinalizeFixture;

namespace KyberWeave.Application.Tests.Docs.Closeout;

/// <summary>
/// The asynchronous closeout, finalize and MCP plan-lifecycle path: each step awaits the async picker, task-file
/// generator and git runner without blocking a thread, and forwards cancellation.
/// </summary>
public sealed class PlanLifecycleAsyncTests
{
    private static readonly DateOnly CloseoutDate = DateOnly.Parse(DocsCloseoutRepository.Date, CultureInfo.InvariantCulture);
    private static readonly DateOnly FinalizeDate = DateOnly.Parse(Date, CultureInfo.InvariantCulture);

    [Fact]
    public async Task PlanCloseoutRunAsyncArchivesMovesAndStagesThroughAsyncProcessRunner()
    {
        using DocFixture fixture = DocsCloseoutRepository.Create(inMemory: true).EnsureDirectory(".git");
        ScriptedProcessExecutor process = new(_ => new ProcessResult(0, string.Empty, string.Empty));
        IServiceProvider services = DocsMemoryServicesD.Services(fixture);
        PlanCloseoutService service = new(
            fixture.Store,
            process,
            new InMemoryExclusiveFileWriter(fixture.Memory ?? throw new InvalidOperationException("In-memory fixture required.")),
            services.GetRequiredService<IDocumentSourceFactory>(),
            fixture.Paths,
            fixture.Environment);

        PlanCloseoutOutcome outcome = await service.RunAsync(
            fixture.Root,
            KyberWeaveConfigDefaults.Product,
            DocsCloseoutRepository.Plan,
            CloseoutDate,
            archive: true);

        Assert.True(outcome.Archived);
        Assert.False(fixture.Exists(DocsCloseoutRepository.Plan));
        Assert.True(fixture.Exists(DocsCloseoutRepository.Archived));
        Assert.Contains(process.Calls, call => call.FileName == "git" && call.ArgumentList.Contains("add"));
    }

    [Fact]
    public async Task PlanCloseoutRunAsyncHonorsCancellationBeforeStaging()
    {
        using DocFixture fixture = DocsCloseoutRepository.Create(inMemory: true).EnsureDirectory(".git");
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        PlanCloseout closer = new(
            fixture.Root,
            KyberWeaveConfigDefaults.Product,
            () => fixture.Loader(KyberWeaveConfigDefaults.Product.Ontology),
            fixture.Store,
            new ScriptedProcessExecutor(_ => throw new InvalidOperationException("Should not run when cancelled.")),
            new InMemoryExclusiveFileWriter(fixture.Memory ?? throw new InvalidOperationException("In-memory fixture required.")),
            fixture.Paths,
            fixture.Environment);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => closer.RunAsync(DocsCloseoutRepository.Plan, CloseoutDate, archive: true, cancelled.Token));
    }

    [Fact]
    public async Task PlanFinalizeServiceRunAsyncAwaitsAsyncPickerFactoryAndPickerAndCommitsViaAsyncRunner()
    {
        using DocFixture fixture = RepositoryInMemory();
        ScriptedProcessExecutor process = new(call => call.ArgumentList.Contains("status")
            ? new ProcessResult(0, "M  " + PlanTaskFileProbe.PlanPath + "\n", string.Empty)
            : new ProcessResult(0, string.Empty, string.Empty));
        AsyncOnlyPicker picker = new();
        AsyncOnlyPickerFactory pickerFactory = new(picker);
        IServiceProvider services = DocsMemoryServicesD.Services(fixture);
        KyberWeaveConfig config = services.GetRequiredService<IKyberWeaveConfigRepository>().Load(fixture.Root);
        IDocsSchemaGate gate = services.GetRequiredService<IDocsSchemaGate>();
        PlanFinalizeService service = new(
            fixture.Store,
            process,
            gate,
            services.GetRequiredService<IDocumentSourceFactory>(),
            pickerFactory,
            fixture.Paths,
            fixture.Environment);

        PlanFinalizeOutcome outcome = await service.RunAsync(
            fixture.Root,
            config,
            PlanTaskFileProbe.PlanPath,
            PlanApprovalMode.Autonomous,
            Approval,
            FinalizeDate);

        Assert.True(outcome.Finalized, string.Join("; ", outcome.Report.Items.Select(item => $"{item.Code}: {item.Message}")));
        Assert.True(picker.PickedAsync);
        Assert.True(picker.Disposed);
        Assert.Contains(process.Calls, call => call.ArgumentList.Contains("diff"));
        Assert.Contains(process.Calls, call => call.ArgumentList.Contains("add"));
        Assert.Contains(process.Calls, call => call.ArgumentList.Contains("commit"));

        // Running again on the now-Ready plan takes the AlreadyReadyAsync path without blocking.
        PlanFinalizeOutcome second = await service.RunAsync(
            fixture.Root,
            config,
            PlanTaskFileProbe.PlanPath,
            PlanApprovalMode.Autonomous,
            Approval,
            FinalizeDate);

        Assert.False(second.Finalized);
        Assert.True(second.AlreadyReady, string.Join("; ", second.Report.Items.Select(item => $"{item.Code}: {item.Message}")));
    }

    [Fact]
    public async Task PlanLifecycleServiceAwaitsFinalizeAndCloseoutRunAsync()
    {
        using DocFixture fixture = RepositoryInMemory();
        AsyncOnlyFinalizeService finalize = new();
        AsyncOnlyCloseoutService closeout = new();
        PlanLifecycleService service = new(
            fixture.Root,
            new StubDocsQueryService(),
            new StubHostConfiguration(),
            finalize,
            closeout,
            fixture.Store,
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)),
            fixture.Paths,
            fixture.Environment);

        string finalized = await service.FinalizePlanAsync(PlanTaskFileProbe.PlanPath, "autonomous", Approval);
        string archived = await service.ArchivePlanAsync(PlanTaskFileProbe.PlanPath);

        Assert.True(finalize.CalledAsync);
        Assert.True(closeout.CalledAsync);
        Assert.Contains("result: finalized", finalized, StringComparison.Ordinal);
        Assert.Contains("result: archived", archived, StringComparison.Ordinal);
    }

    private sealed class AsyncOnlyPickerFactory(AsyncOnlyPicker picker) : IPlanTaskAgentPickerFactory
    {
        public IPlanTaskAgentPicker? ForConfig(KyberWeaveConfig config) =>
            throw new InvalidOperationException("RunAsync must call ForConfigAsync, never ForConfig.");

        public Task<IPlanTaskAgentPicker?> ForConfigAsync(
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IPlanTaskAgentPicker?>(picker);
    }

    private sealed class AsyncOnlyPicker : IPlanTaskAgentPicker
    {
        public bool PickedAsync { get; private set; }
        public bool Disposed { get; private set; }

        public IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks) =>
            throw new InvalidOperationException("RunAsync must call PickAsync, never Pick.");

        public Task<IReadOnlyDictionary<string, PlanTaskAgentChoice>> PickAsync(
            IReadOnlyList<PlanTaskFileEntry> tasks,
            CancellationToken cancellationToken = default)
        {
            PickedAsync = true;
            Dictionary<string, PlanTaskAgentChoice> picks = new(StringComparer.Ordinal);
            foreach (PlanTaskFileEntry task in tasks)
            {
                if (task.Id is { } id)
                    picks[id] = new PlanTaskAgentChoice("dev-medium", 0.95);
            }
            return Task.FromResult<IReadOnlyDictionary<string, PlanTaskAgentChoice>>(picks);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class AsyncOnlyFinalizeService : IPlanFinalizeService
    {
        public bool CalledAsync { get; private set; }

        public PlanFinalizeOutcome Run(
            string repoRoot,
            KyberWeaveConfig config,
            string planPath,
            PlanApprovalMode mode,
            string? approval,
            DateOnly approvalDate) =>
            throw new InvalidOperationException("PlanLifecycleService must call RunAsync, never Run.");

        public Task<PlanFinalizeOutcome> RunAsync(
            string repoRoot,
            KyberWeaveConfig config,
            string planPath,
            PlanApprovalMode mode,
            string? approval,
            DateOnly approvalDate,
            CancellationToken cancellationToken = default)
        {
            CalledAsync = true;
            return Task.FromResult(new PlanFinalizeOutcome(new DiagnosticReport(), true, false, PlanTaskFileProbe.TaskFilePath, "T1: ready"));
        }
    }

    private sealed class AsyncOnlyCloseoutService : IPlanCloseoutService
    {
        public bool CalledAsync { get; private set; }

        public PlanCloseoutOutcome Run(
            string repoRoot,
            KyberWeaveConfig config,
            string planPath,
            DateOnly archiveDate,
            bool archive) =>
            throw new InvalidOperationException("PlanLifecycleService must call RunAsync, never Run.");

        public Task<PlanCloseoutOutcome> RunAsync(
            string repoRoot,
            KyberWeaveConfig config,
            string planPath,
            DateOnly archiveDate,
            bool archive,
            CancellationToken cancellationToken = default)
        {
            CalledAsync = true;
            return Task.FromResult(new PlanCloseoutOutcome(new DiagnosticReport(), true));
        }
    }

    private sealed class StubHostConfiguration : IDocsHostConfiguration
    {
        public bool TryLoad(string repositoryRoot, DiagnosticReport report, out KyberWeaveConfig config)
        {
            config = KyberWeaveConfigDefaults.Product;
            return true;
        }
    }

    private sealed class StubDocsQueryService : IDocsQueryService
    {
        public Task<string> ProvenanceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("provenance: root=/repo branch=main");

        public Task<string> ExploreAsync(string query, string? cursor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ForSymbolAsync(string symbol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AnalysisCandidatesAsync(string? kind, string? cursor, int limit, int charBudget, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GlossaryAsync(string term, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DocsValidateAsync(bool mergeReady, string baseRef, int charBudget, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DocsDriftAsync(int charBudget, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> DocsStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AgentCodingStandardsAsync(string technology, string? topic, int charBudget, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadSkillAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
