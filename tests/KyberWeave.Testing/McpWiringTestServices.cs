using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests;

/// <summary>The MCP wiring service built the way the CLI container builds it, over the physical file system.</summary>
internal static class McpWiringTestServices
{
    private static readonly Lazy<ServiceProvider> Provider = new(() =>
        new ServiceCollection()
            .AddSingleton<IMcpDeclarationReader, McpDeclarationReader>()
            .AddWeaveApplication()
            .BuildServiceProvider());

    /// <summary>The service.</summary>
    public static IMcpWiringService Wiring => Provider.Value.GetRequiredService<IMcpWiringService>();

    /// <summary>The repair, built by the container.</summary>
    public static McpWiringRepairer Repairer => Provider.Value.GetRequiredService<McpWiringRepairer>();

    /// <summary>The repository-containment rule, over the physical path resolver.</summary>
    public static McpRepositoryPath RepositoryPath => Provider.Value.GetRequiredService<McpRepositoryPath>();

    /// <summary>The receipt store, over the physical file system.</summary>
    public static IMcpWiringReceipts Receipts => Provider.Value.GetRequiredService<IMcpWiringReceipts>();
}
