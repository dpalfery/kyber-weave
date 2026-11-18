using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>The MCP wiring service built the way the CLI container builds it, over the physical file system.</summary>
internal static class McpWiringTestServices
{
    private static readonly Lazy<IMcpWiringService> Service = new(() =>
        new ServiceCollection()
            .AddSingleton<IMcpDeclarationReader, McpDeclarationReader>()
            .AddWeaveApplication()
            .BuildServiceProvider()
            .GetRequiredService<IMcpWiringService>());

    /// <summary>The service.</summary>
    public static IMcpWiringService Wiring => Service.Value;
}
