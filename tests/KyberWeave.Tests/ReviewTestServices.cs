using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace KyberWeave.Tests;

/// <summary>The review services as the application registers them, for tests that drive them directly.</summary>
internal static class ReviewTestServices
{
    private static readonly ServiceProvider Provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

    /// <summary>Reads and writes review cycle folders.</summary>
    public static IReviewCycleStore Cycles { get; } = Provider.GetRequiredService<IReviewCycleStore>();

    /// <summary>Keeps the not-seated record of a cycle.</summary>
    public static INotSeatedStore NotSeated { get; } = Provider.GetRequiredService<INotSeatedStore>();

    /// <summary>Finds the work tree a review belongs to and the change checked out in it.</summary>
    public static IReviewWorkTreeService WorkTrees { get; } = Provider.GetRequiredService<IReviewWorkTreeService>();

    /// <summary>Names the change and produces its diff.</summary>
    public static IGitChangeReader Git { get; } = Provider.GetRequiredService<IGitChangeReader>();

    /// <summary>Writes the diff and the evidence file of a review cycle.</summary>
    public static IReviewEvidenceService Evidence { get; } = Provider.GetRequiredService<IReviewEvidenceService>();

    /// <summary>A type registrar over the application's registered services, so commands that take services are built as they are on the command line.</summary>
    public static ITypeRegistrar Registrar() => new ServiceCollectionTypeRegistrar(new ServiceCollection().AddWeaveApplication());

    /// <summary>Finds the project file a source file belongs to.</summary>
    public static IProjectFileLocator ProjectFiles { get; } = Provider.GetRequiredService<IProjectFileLocator>();

    /// <summary>Runs the gates a host declares.</summary>
    public static IGateRunService Gates { get; } = Provider.GetRequiredService<IGateRunService>();

    /// <summary>Reads the coverage reports a test gate wrote.</summary>
    public static ICoverageReportSource Coverage { get; } = Provider.GetRequiredService<ICoverageReportSource>();

    /// <summary>Builds a command the way the command line does, from the registered services.</summary>
    public static T Create<T>()
        where T : class => ActivatorUtilities.CreateInstance<T>(Provider);
}
