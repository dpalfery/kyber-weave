using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Doctor;
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

        services.TryAddSingleton<IFileSystem>(_ => new PhysicalFileSystem());
        services.TryAddSingleton<IDoctorFileSystem>(_ => new PhysicalDoctorFileSystem());
        services.TryAddSingleton<IProcessRunner>(_ => new KernelProcessRunner());
        services.TryAddSingleton<IHooksConfigSource>(sp => new YamlHooksConfigSource(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ITestClassResolver>(sp => new ConventionTestClassResolver(sp.GetRequiredService<IFileSystem>()));
        return services;
    }
}
