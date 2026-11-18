using System.Reflection;
using KyberWeave.Application;
using KyberWeave.Mcp;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// Pins that every MCP tool type each server advertises can actually be constructed from the container that server
/// builds, and that the docs server completes a real <c>tools/call</c> end to end.
/// </summary>
/// <remarks>
/// <para>
/// Regression for the branch that added two constructor parameters to <see cref="DocsTools"/> — an
/// <c>ITaskPromptService</c> and a bare <c>string</c> root — and registered neither in the composition root.
/// <c>tools/list</c> still answered, because it reads attributes only, while every <c>tools/call</c> failed inside the
/// container and the client saw "An error occurred invoking ...". The SDK constructs each tool type from the container
/// on every call, so nothing in the suite that built the tools by hand exercised the registration, and ten thousand
/// tests passed. A primitive is never resolvable; the root now travels as <see cref="DocsHostRoot"/>.
/// </para>
/// <para>
/// Both servers' tool types are constructed the same way, so the same rule covers the Arbiter's five tools: construct
/// each type from the container the server's own composition method builds, then run a real call through the shipped
/// docs executable.
/// </para>
/// </remarks>
public sealed class McpCompositionRootTests
{
    private readonly MemoryDocsHost _host = new();

    /// <summary>
    /// Every tool type the docs server advertises can be constructed from the container <c>Program.cs</c> builds.
    /// </summary>
    /// <remarks>
    /// A missing registration throws <see cref="InvalidOperationException"/> here, naming the service and the tool
    /// type, instead of surfacing as an opaque invocation error on a client's first tool call.
    /// </remarks>
    [Fact]
    public void EveryDocsToolTypeResolvesFromTheServerContainer()
    {
        _host.Services().GetRequiredService<IDocsScaffoldingService>().Scaffold(_host.Repo);

        ServiceCollection services = new();
        _host.Register(services);
        services.AddWeaveApplication();
        services.AddDocsHostServices(_host.Repo, codeGraphRefreshEnabled: false);
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertEveryToolTypeResolves(provider, typeof(DocsTools).Assembly);
    }

    /// <summary>Every advertised tool type in <paramref name="assembly"/> resolves, and there is at least one.</summary>
    private static void AssertEveryToolTypeResolves(IServiceProvider provider, Assembly assembly)
    {
        Type[] toolTypes = assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(toolTypes);
        foreach (Type toolType in toolTypes)
        {
            // The SDK's own construction path (`McpServerBuilderExtensions.CreateTarget`): resolving the type itself
            // is not enough, because a tool type is never registered — it is built per call, from this container.
            Assert.NotNull(ActivatorUtilities.CreateInstance(provider, toolType));
        }
    }
}
