using System.Reflection;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Arbiter.Tests;

/// <summary>
/// Every MCP tool type the Arbiter's serve command advertises can be constructed from the container that command
/// builds, the same way the SDK builds one per call. The repository and its configuration are in memory.
/// </summary>
/// <remarks>
/// The docs server's equivalent is <c>McpCompositionRootTests</c> in the MCP test project; the shipped executable's
/// real tool call is in the integration project.
/// </remarks>
public sealed class ArbiterServeCompositionRootTests
{
    private readonly MemoryDocsHost _host = new();

    /// <summary>The Arbiter's serve tools resolve from the container <c>kyber-weave-arbiter serve</c> builds.</summary>
    [Fact]
    public void EveryArbiterToolTypeResolvesFromTheServeContainer()
    {
        _host.WriteInRepo(".kyber-weave/kyber-weave.yml", "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");

        ServiceCollection services = new();
        _host.Register(services);
        services.AddArbiterServe(["--repo-root", _host.Repo], TextWriter.Null);
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertEveryToolTypeResolves(provider, typeof(ArbiterTools).Assembly);
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
