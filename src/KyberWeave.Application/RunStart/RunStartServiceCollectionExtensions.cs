using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.RunStart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Application.RunStart;

/// <summary>Registers the run records and the canonical-checkout guard over the file-system and git seams.</summary>
internal static class RunStartServiceCollectionExtensions
{
    /// <summary>
    /// Registers the run-record store and the checkout guard. The seams they read through (the file system and the
    /// checked-out-branch reader) come from the data layer's run registration, which the caller adds first.
    /// </summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveRunStartState(this IServiceCollection services)
    {
        services.TryAddSingleton<IRunStateStore>(sp => new FileRunStateStore(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<ICheckedOutBranchReader>()));
        services.TryAddSingleton<IRunCheckout>(sp => new RunCheckoutGuard(
            sp.GetRequiredService<IWritableFileSystem>(),
            sp.GetRequiredService<IRunStateStore>()));
        return services;
    }
}
