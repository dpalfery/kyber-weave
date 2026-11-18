using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console;

namespace KyberWeave.Cli.Composition;

/// <summary>Registers what the Arbiter commands take from the host: the console they write to and the console key input.</summary>
internal static class ArbiterCliServices
{
    /// <summary>Registers the host's Arbiter choices with <c>TryAdd</c>, so a test's own registration wins.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>The console is the shared forwarding one from <see cref="CliConsoleServices"/>.</remarks>
    internal static IServiceCollection AddArbiterCliServices(this IServiceCollection services)
    {
        _ = services.AddCliConsole();
        services.TryAddSingleton<IArbiterKeyInput, ConsoleArbiterKeyInput>();
        services.TryAddSingleton<ArbiterRepoRoot>();
        services.TryAddSingleton<PlanArguments>();
        services.TryAddSingleton<IProcessExecutor>(_ => ProcessExecutor.Instance);
        services.TryAddSingleton(sp => new ArbiterProcessProbe(sp.GetRequiredService<IProcessExecutor>(), sp.GetRequiredService<IProcessEnvironment>()));
        services.TryAddSingleton<IArbiterBinaryProbe>(sp => sp.GetRequiredService<ArbiterProcessProbe>());
        return services;
    }
}
