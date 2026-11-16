using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Doctor;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.RunStart;
using KyberWeave.Data.Squad;
using KyberWeave.Kernel.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Data;

/// <summary>Composition root for the data layer.</summary>
/// <remarks>Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation first keeps it.</remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the adapters that implement the contracts.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWeaveDataServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWritableFileSystem>(_ => new PhysicalFileSystem());
        services.TryAddSingleton<IFileSystem>(sp => sp.GetRequiredService<IWritableFileSystem>());
        services.TryAddSingleton<IProcessEnvironment>(_ => new ProcessEnvironment());
        services.TryAddSingleton<IProductRootLocator>(sp => new CheckoutProductRootLocator(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IDoctorFileSystem>(_ => new PhysicalDoctorFileSystem());
        services.TryAddSingleton<IProcessRunner>(_ => new KernelProcessRunner());
        services.TryAddSingleton<IGitCommandRunner>(_ => new ProcessGitCommandRunner(ProcessGitCommandRunner.DefaultTimeout));
        services.TryAddSingleton<ICheckedOutBranchReader>(sp => new GitCheckedOutBranchReader(sp.GetRequiredService<IGitCommandRunner>()));
        services.TryAddSingleton<IRunStateStore>(sp => new FileRunStateStore(sp.GetRequiredService<IWritableFileSystem>(), sp.GetRequiredService<ICheckedOutBranchReader>()));
        services.TryAddSingleton<IRunCheckout>(sp => new RunCheckoutGuard(sp.GetRequiredService<IWritableFileSystem>(), sp.GetRequiredService<IRunStateStore>()));
        services.TryAddSingleton<IHooksConfigSource>(sp => new YamlHooksConfigSource(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ITestClassResolver>(sp => new ConventionTestClassResolver(sp.GetRequiredService<IFileSystem>()));
        _ = services.AddWeaveSquadData();
        _ = services.AddWeaveArbiterData();
        return services;
    }
}
