using KyberWeave.Application;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Application.Review;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Doctor;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// The review services as the application registers them, over the shared fakes: an in-memory file system, a fake
/// environment, clock, identifiers, process runner and decision log. Nothing here reads the disk, the process
/// environment, the clock or a program, so a unit test states the tree it needs and gets it back.
/// </summary>
public sealed class InMemoryReviewServices : IDisposable
{
    private readonly ServiceProvider _provider;

    /// <summary>Creates the services over a fresh in-memory tree.</summary>
    /// <param name="files">The file system; a new empty one when null.</param>
    /// <param name="caseSensitive">True for Linux name rules when the file system is created here.</param>
    public InMemoryReviewServices(InMemoryFileSystem? files = null, bool caseSensitive = false)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Files = files ?? new InMemoryFileSystem(Time, caseSensitive);
        Root = Path.Combine(Files.RootPath, "work");
        Files.CreateDirectory(Root);
        Paths = new FakePathResolver(Files);
        Environment = new FakeProcessEnvironment(
            currentDirectory: Root,
            homeDirectory: Path.Combine(Files.RootPath, "home"),
            tempDirectory: Path.Combine(Files.RootPath, "temp"));
        Ids = new FakeIdGenerator();
        Processes = new ScriptedProcessRunner();
        Log = new FakeArbiterLogStore();

        ServiceCollection services = [];
        services.AddSingleton<IWritableFileSystem>(Files);
        services.AddSingleton<IFileSystem>(Files);
        services.AddSingleton<IPathResolver>(Paths);
        services.AddSingleton<IProcessEnvironment>(Environment);
        services.AddSingleton<IIdGenerator>(Ids);
        services.AddSingleton<IProcessRunner>(Processes);
        services.AddSingleton<IProgramRunner>(Processes);
        services.AddSingleton<IBoundedProcessExecutor>(Processes);
        services.AddSingleton<IRepositoryFileStore>(new InMemoryRepositoryFileStore(Files));
        services.AddSingleton<IArbiterLogStore>(Log);
        services.AddSingleton<TimeProvider>(Time);
        services.TryAddSingleton<IRepositoryToolSettings, ConfigFileToolSettings>();
        _ = services.AddWeaveApplication();
        _provider = services.BuildServiceProvider();
    }

    /// <summary>Gets the file system every service reads and writes.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    public FakePathResolver Paths { get; }

    /// <summary>Gets the environment, whose current directory is <see cref="Root"/>.</summary>
    public FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the clock.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Gets the identifier generator.</summary>
    public FakeIdGenerator Ids { get; }

    /// <summary>Gets the process runner, which refuses any run a test did not script.</summary>
    public ScriptedProcessRunner Processes { get; }

    /// <summary>Gets the Arbiter append log the decision log writes through.</summary>
    public FakeArbiterLogStore Log { get; }

    /// <summary>Gets a directory under the file system's root that is the environment's current directory.</summary>
    public string Root { get; }

    /// <summary>Gets the cited-line checker.</summary>
    public ICitedLineChecker CitedLines => Get<ICitedLineChecker>();

    /// <summary>Gets the coverage report reader.</summary>
    public ICoverageReportSource Coverage => Get<ICoverageReportSource>();

    /// <summary>Gets the not-seated record store.</summary>
    public INotSeatedStore NotSeated => Get<INotSeatedStore>();

    /// <summary>Gets the decision-model steps.</summary>
    public IReviewDecisionSteps DecisionSteps => Get<IReviewDecisionSteps>();

    /// <summary>Gets the duplicate detector.</summary>
    public IDuplicateDetectionService Duplicates => Get<IDuplicateDetectionService>();

    /// <summary>Gets the gate runner.</summary>
    public IGateRunService Gates => Get<IGateRunService>();

    /// <summary>Gets the review gates service.</summary>
    public IReviewGatesService ReviewGates => Get<IReviewGatesService>();

    /// <summary>Gets the project file locator.</summary>
    public IProjectFileLocator ProjectFiles => Get<IProjectFileLocator>();

    /// <summary>Gets the review cycle store.</summary>
    public IReviewCycleStore Cycles => Get<IReviewCycleStore>();

    /// <summary>Resolves a registered service.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The service.</returns>
    public T Get<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>Builds a type the way the command line does, from the registered services.</summary>
    /// <typeparam name="T">The type to build.</typeparam>
    /// <returns>The instance.</returns>
    public T Create<T>()
        where T : class => ActivatorUtilities.CreateInstance<T>(_provider);

    /// <summary>Builds a decision model whose decision log is the fake append log under <paramref name="logDirectory"/>.</summary>
    /// <param name="provider">The provider that answers; null is the model switched off.</param>
    /// <param name="rules">The effective Arbiter rules.</param>
    /// <param name="logDirectory">The directory the decision log names; null logs nothing.</param>
    /// <param name="unavailableReason">Why a configured model cannot be used; null otherwise.</param>
    /// <param name="cacheStore">The answer cache; an in-memory fake when null.</param>
    /// <param name="cachePath">The cache file's name; null keeps no answers.</param>
    /// <returns>The session.</returns>
    public ReviewDecisionSession DecisionModel(
        IArbiterProvider? provider,
        IReadOnlyList<ArbiterRule> rules,
        string? logDirectory,
        string? unavailableReason = null,
        IReviewDecisionCacheStore? cacheStore = null,
        string? cachePath = null) =>
        new(
            provider,
            rules,
            logDirectory is null ? null : new DecisionLog(logDirectory, Log),
            Time,
            Ids,
            unavailableReason,
            cacheStore ?? new FakeReviewDecisionCacheStore(),
            cachePath);

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();
}
