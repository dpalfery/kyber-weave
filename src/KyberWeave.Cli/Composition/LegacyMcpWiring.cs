using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Cli.Composition;

/// <summary>
/// The MCP wiring service for the two callers that are not built by the command container yet: the squad doctor's
/// retired-key note and the post-update hook refresher. Both only scan, read only. Each retires this holder when it
/// takes <see cref="IMcpWiringService"/> from the container like the docs commands do.
/// </summary>
internal static class LegacyMcpWiring
{
    private static readonly Lazy<IMcpWiringService> Default = new(() =>
    {
        ServiceCollection services = [];
        services.TryAddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider().GetRequiredService<IMcpWiringService>();
    });

    /// <summary>The service, built on first use.</summary>
    public static IMcpWiringService Service => Default.Value;
}
