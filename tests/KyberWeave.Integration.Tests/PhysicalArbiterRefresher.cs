using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Tests.Fakes;

/// <summary>The production Arbiter hook refresher, built over the container, for a test that renders the real files into its own temporary tree.</summary>
internal static class PhysicalArbiterRefresher
{
    /// <summary>
    /// The production refresher, resolved from the container over <paramref name="readEnvironment"/>'s home, so a test
    /// exercises the real renderers and receipt rewrites against its own temporary tree.
    /// </summary>
    /// <param name="readEnvironment">Reads one environment variable.</param>
    /// <param name="log">Receives one line per step or warning.</param>
    /// <returns>The refresher.</returns>
    internal static ArbiterHookRefresher Create(Func<string, string?> readEnvironment, Action<string>? log = null) =>
        (ArbiterHookRefresher)Container(readEnvironment).GetRequiredService<IArbiterHookRefresherFactory>().Create(log ?? (_ => { }));

    private static ServiceProvider Container(Func<string, string?> readEnvironment)
    {
        ServiceCollection services = [];
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            () => readEnvironment("HOME") ?? readEnvironment("USERPROFILE") ?? string.Empty,
            readEnvironment));
        services.TryAddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddSquadCliServices();
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider();
    }
}
