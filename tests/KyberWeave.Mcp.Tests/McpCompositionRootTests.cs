using System.Reflection;
using KyberWeave.Application;
using KyberWeave.Mcp;
using KyberWeave.Contracts.Repositories;
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
    public async Task EveryDocsToolTypeResolvesFromTheServerContainer()
    {
        _ = await _host.Services().GetRequiredService<IDocsScaffoldingService>().ScaffoldAsync(_host.Repo);

        ServiceCollection services = new();
        _host.Register(services);
        services.AddWeaveApplication();
        services.AddDocsHostServices(_host.Repo, codeGraphRefreshEnabled: false);
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertEveryToolTypeResolves(provider, typeof(DocsTools).Assembly);
    }

    /// <summary>
    /// The bound container picks the root-bound constructor: a tool called without <c>projectPath</c> answers from the
    /// server's own root, and the instructions carry no any-repo paragraph.
    /// </summary>
    [Fact]
    public async Task TheBoundContainerAnswersFromItsOwnRoot()
    {
        _ = await _host.Services().GetRequiredService<IDocsScaffoldingService>().ScaffoldAsync(_host.Repo);

        ServiceCollection services = new();
        _host.Register(services);
        services.AddWeaveApplication();
        services.AddDocsHostServices(_host.Repo, codeGraphRefreshEnabled: false);
        using ServiceProvider provider = services.BuildServiceProvider();

        DocsTools tools = ActivatorUtilities.CreateInstance<DocsTools>(provider);

        Assert.StartsWith("provenance: root=" + _host.Repo, await tools.DocsStatusAsync(), StringComparison.Ordinal);
        McpServerOptions options = new();
        DocsServerInstructions.Configure(options, anyRepo: false);
        Assert.Equal(DocsServerInstructions.Text, options.ServerInstructions);
        Assert.DoesNotContain(DocsServerInstructions.AnyRepoParagraph, options.ServerInstructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// The any-repo container builds with no root at all, which is what lets <c>kyber-weave-mcp --any-repo</c> start from
    /// a directory with no host configuration, and every tool it constructs refuses a call without <c>projectPath</c>.
    /// </summary>
    [Fact]
    public async Task TheAnyRepoContainerBuildsWithoutARootAndEveryToolRefusesAMissingProjectPath()
    {
        using ServiceProvider provider = AnyRepoContainer();

        AssertEveryToolTypeResolves(provider, typeof(DocsTools).Assembly);
        DocsTools tools = ActivatorUtilities.CreateInstance<DocsTools>(provider);
        MethodInfo[] toolMethods = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        Assert.NotEmpty(toolMethods);
        foreach (MethodInfo method in toolMethods)
        {
            Task<string> call = method.GetParameters().Any(parameter => parameter.ParameterType == typeof(McpServer))
                ? tools.BuildTaskPromptForClientAsync("claude-code", taskId: "T1")
                : (Task<string>)method.Invoke(tools, method.GetParameters().Select(ArgumentFor).ToArray())!;
            string answer = await call;

            Assert.Equal(MissingProjectPathRefusal, answer);
        }
    }

    /// <summary>A call naming a directory inside an initialized repository answers with that repository's provenance.</summary>
    [Fact]
    public async Task TheAnyRepoContainerAnswersForTheRepositoryAProjectPathNames()
    {
        _ = await _host.Services().GetRequiredService<IDocsScaffoldingService>().ScaffoldAsync(_host.Repo);
        using ServiceProvider provider = AnyRepoContainer();

        DocsTools tools = ActivatorUtilities.CreateInstance<DocsTools>(provider);

        Assert.StartsWith("provenance: root=" + _host.Repo, await tools.DocsStatusAsync(_host.Repo), StringComparison.Ordinal);
    }

    /// <summary>A directory that is no initialized repository is refused with the any-repo provenance line.</summary>
    [Fact]
    public async Task TheAnyRepoContainerRefusesAProjectPathWithItsReason()
    {
        using ServiceProvider provider = AnyRepoContainer();

        DocsTools tools = ActivatorUtilities.CreateInstance<DocsTools>(provider);
        string answer = await tools.DocsStatusAsync(_host.Repo);

        Assert.StartsWith("provenance: root=(none) mode=any-repo\nprojectPath was refused: ", answer, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs init", answer, StringComparison.Ordinal);
    }

    /// <summary>The any-repo instructions append the paragraph that says every tool needs <c>projectPath</c>.</summary>
    [Fact]
    public async Task TheAnyRepoInstructionsSayEveryToolRequiresAProjectPath()
    {
        McpServerOptions options = new();

        DocsServerInstructions.Configure(options, anyRepo: true);

        Assert.StartsWith(DocsServerInstructions.Text, options.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains(DocsServerInstructions.AnyRepoParagraph, options.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("requires projectPath", DocsServerInstructions.AnyRepoParagraph, StringComparison.Ordinal);
        Assert.Contains("absolute path of a directory inside the repository", DocsServerInstructions.AnyRepoParagraph, StringComparison.Ordinal);
    }

    private const string MissingProjectPathRefusal =
        "provenance: root=(none) mode=any-repo\n"
        + "projectPath is required: this server runs in any-repo mode. "
        + "Pass the absolute path of a directory inside the repository you are working in.";

    private ServiceProvider AnyRepoContainer()
    {
        ServiceCollection services = new();
        _host.Register(services);

        // The in-memory host has no git, so the one repository it holds is named by a scripted identity. Registered
        // before the application's own, which only adds the real adapter when none is registered.
        services.AddSingleton<IGitWorktreeIdentity>(new OneRepository(_host.Repo));
        services.AddWeaveApplication();
        services.AddAnyRepoDocsHostServices(codeGraphRefreshEnabled: false);
        return services.BuildServiceProvider();
    }

    private static object? ArgumentFor(ParameterInfo parameter)
    {
        if (parameter.Name == "projectPath") return null;
        if (parameter.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        return parameter.ParameterType == typeof(string) ? "value" : null;
    }

    /// <summary>Every directory is the top level of one repository, whose git directory is under <c>root</c>.</summary>
    private sealed class OneRepository(string root) : IGitWorktreeIdentity
    {
        public Task<GitWorktreeRoots> TryRootsAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitWorktreeRoots(root, Path.Combine(root, ".git"), null));
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
