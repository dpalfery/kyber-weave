using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Doctor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console.Cli;

namespace KyberWeave.Tests;

/// <summary>The review services as the application registers them, for tests that drive them directly.</summary>
internal static class ReviewTestServices
{
    private static readonly ServiceProvider Provider = BuildProvider();

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        _ = services.AddWeaveApplication();

        // The command line registers the repository settings reader over the configuration loader; the guard needs it.
        services.TryAddSingleton<IRepositoryToolSettings, ConfigFileToolSettings>();
        return services.BuildServiceProvider();
    }

    /// <summary>Reads and writes review cycle folders.</summary>
    public static IReviewCycleStore Cycles { get; } = Provider.GetRequiredService<IReviewCycleStore>();

    /// <summary>Keeps the not-seated record of a cycle.</summary>
    public static INotSeatedStore NotSeated { get; } = Provider.GetRequiredService<INotSeatedStore>();

    /// <summary>Finds the work tree a review belongs to and the change checked out in it.</summary>
    public static IReviewWorkTreeService WorkTrees { get; } = Provider.GetRequiredService<IReviewWorkTreeService>();

    /// <summary>Finds the review folder and the recorded findings a command works from.</summary>
    public static IReviewCycleSelector CycleSelector { get; } = Provider.GetRequiredService<IReviewCycleSelector>();

    /// <summary>Names the change and produces its diff.</summary>
    public static IGitChangeReader Git { get; } = Provider.GetRequiredService<IGitChangeReader>();

    /// <summary>Writes the diff and the evidence file of a review cycle.</summary>
    public static IReviewEvidenceService Evidence { get; } = Provider.GetRequiredService<IReviewEvidenceService>();

    /// <summary>A type registrar over the application's registered services, so commands that take services are built as they are on the command line.</summary>
    public static ITypeRegistrar Registrar()
    {
        ServiceCollection services = new();
        _ = services.AddWeaveApplication();
        services.TryAddSingleton<IRepositoryToolSettings, ConfigFileToolSettings>();
        return new ServiceCollectionTypeRegistrar(services);
    }

    /// <summary>Runs the gates a review executes, repository-wide or for one agent.</summary>
    public static IReviewGatesService ReviewGates { get; } = Provider.GetRequiredService<IReviewGatesService>();

    /// <summary>Finds the project file a source file belongs to.</summary>
    public static IProjectFileLocator ProjectFiles { get; } = Provider.GetRequiredService<IProjectFileLocator>();

    /// <summary>Runs the gates a host declares.</summary>
    public static IGateRunService Gates { get; } = Provider.GetRequiredService<IGateRunService>();

    /// <summary>Reads the coverage reports a test gate wrote.</summary>
    public static ICoverageReportSource Coverage { get; } = Provider.GetRequiredService<ICoverageReportSource>();

    /// <summary>Repairs the lines findings cite.</summary>
    public static ICitedLineChecker CitedLines { get; } = Provider.GetRequiredService<ICitedLineChecker>();

    /// <summary>Reports the declarations whose bodies repeat verbatim.</summary>
    public static IDuplicateDetectionService Duplicates { get; } = Provider.GetRequiredService<IDuplicateDetectionService>();

    /// <summary>Builds the CodeGraph ports over a repository's index.</summary>
    public static ICodeGraphPortsFactory CodeGraphs { get; } = Provider.GetRequiredService<ICodeGraphPortsFactory>();

    /// <summary>Makes sure the tree a review runs in has a CodeGraph index.</summary>
    public static IWorktreeIndexGuard WorktreeIndex { get; } = Provider.GetRequiredService<IWorktreeIndexGuard>();

    /// <summary>The file system the review commands write their evidence through.</summary>
    public static IWritableFileSystem Files { get; } = Provider.GetRequiredService<IWritableFileSystem>();

    /// <summary>The process state the review commands read the temporary directory from.</summary>
    public static IProcessEnvironment Environment { get; } = Provider.GetRequiredService<IProcessEnvironment>();

    /// <summary>Builds a command the way the command line does, from the registered services.</summary>
    public static T Create<T>()
        where T : class => ActivatorUtilities.CreateInstance<T>(Provider);
}
