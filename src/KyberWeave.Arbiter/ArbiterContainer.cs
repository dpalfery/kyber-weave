using KyberWeave.Application;
using KyberWeave.Application.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Arbiter;

/// <summary>The hook host's container: a plain service collection, never a Host builder.</summary>
/// <remarks>
/// The hook path must start fast and must not write to stdout, so nothing here starts a host, a logger or a
/// background service. Each path builds its own provider inside its own fault handler, so a registration fault is
/// logged and answered the way that path answers any fault: the hook and the worker guard allow, the conductor guard denies.
/// </remarks>
internal static class ArbiterContainer
{
    /// <summary>Registers the application layer and the one port the entry app supplies: the host configuration loader.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddArbiterHost(this IServiceCollection services)
    {
        _ = services.AddWeaveApplication();
        services.TryAddSingleton<IHostConfigLoader, CoreHostConfigLoader>();
        return services;
    }

    /// <summary>Registers the hook host's services, plus the serve context built from this process's own argv.</summary>
    /// <remarks>
    /// The serve surface's tools resolve their collaborators from this collection, so the composition-root test
    /// builds it too: a tool that asks for a service this root does not register fails the test rather than the
    /// first tool call a client makes.
    /// </remarks>
    /// <param name="services">The collection to populate.</param>
    /// <param name="args">The process arguments that name the root the serve surface answers for.</param>
    /// <param name="log">Where the context factory reports a root it refuses.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddArbiterServe(this IServiceCollection services, string[] args, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);

        _ = services.AddArbiterHost();
        services.AddSingleton(sp => sp.GetRequiredService<IArbiterServeContextFactory>().Create(args, log));
        return services;
    }

    /// <summary>Registers only the guards and the adapters they read through, which is all a guard process needs.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddArbiterGuards(this IServiceCollection services)
    {
        _ = services.AddWeaveArbiterGuards();
        services.TryAddSingleton<IHostConfigLoader, CoreHostConfigLoader>();
        return services;
    }

    /// <summary>Builds a provider over <see cref="AddArbiterGuards"/>.</summary>
    /// <returns>A provider that resolves lazily.</returns>
    internal static IServiceProvider CreateGuards()
    {
        ServiceCollection services = new();
        _ = services.AddArbiterGuards();
        return services.BuildServiceProvider();
    }

    /// <summary>Builds a provider over <see cref="AddArbiterHost"/>.</summary>
    /// <returns>A provider that resolves lazily.</returns>
    internal static IServiceProvider Create()
    {
        ServiceCollection services = new();
        _ = services.AddArbiterHost();
        return services.BuildServiceProvider();
    }
}

/// <summary>Loads the host configuration through the legacy loader, which has no layer port yet.</summary>
internal sealed class CoreHostConfigLoader : IHostConfigLoader
{
    /// <inheritdoc/>
    public HostConfigLoadResult TryLoad(string repoRoot)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
        return new HostConfigLoadResult(loaded.Success, loaded.Config, loaded.ConfigPath, loaded.Error);
    }
}
