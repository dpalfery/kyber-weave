using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using KyberWeave.Application;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Integration.Tests;

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
public sealed class McpCompositionRootTests : IDisposable
{
    private readonly TempDirectory _repository = new();

    public void Dispose() => _repository.Dispose();

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
        DocsTestCommands.Scaffolding.Scaffold(_repository.Path);

        ServiceCollection services = new();
        services.AddWeaveApplication();
        services.AddDocsHostServices(_repository.Path, codeGraphRefreshEnabled: false);
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertEveryToolTypeResolves(provider, typeof(DocsTools).Assembly);
    }

    /// <summary>The Arbiter's serve tools resolve from the container <c>kyber-weave-arbiter serve</c> builds.</summary>
    [Fact]
    public void EveryArbiterToolTypeResolvesFromTheServeContainer()
    {
        Directory.CreateDirectory(Path.Combine(_repository.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repository.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");

        ServiceCollection services = new();
        services.AddArbiterServe(["--repo-root", _repository.Path], TextWriter.Null);
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertEveryToolTypeResolves(provider, typeof(ArbiterTools).Assembly);
    }

    /// <summary>
    /// The shipped docs executable answers a real <c>tools/call</c> with an answer rather than an error result.
    /// </summary>
    /// <remarks>
    /// This is the failing-first evidence: the process starts, lists its tools, and only the calls that need the
    /// tool instance fail. The second call names the prompts collaborator's own tool, so the parameter that went
    /// unregistered is exercised rather than assumed.
    /// </remarks>
    [Fact]
    public void TheDocsServerExecutableAnswersRealToolCalls()
    {
        DocsTestCommands.Scaffolding.Scaffold(_repository.Path);

        const string Requests =
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"docs_answer_question_from_repo_documentation","arguments":{"query":"documentation ontology"}}}
            {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"docs_build_task_prompt_for_agent_dispatch","arguments":{}}}

            """;

        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _repository.Path,
        };
        startInfo.ArgumentList.Add(typeof(DocsTools).Assembly.Location);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(_repository.Path);
        startInfo.ArgumentList.Add("--no-codegraph-refresh");

        ProcessResult result = ProcessRunner.Run(startInfo, Requests, TimeSpan.FromSeconds(60));

        string answered = ToolCallText(result.StandardOutput, 3);
        Assert.True(
            result.ExitCode == 0 && answered.Contains("provenance:", StringComparison.Ordinal)
                && answered.Contains("confidence:", StringComparison.Ordinal),
            "Expected the docs question to answer with provenance and a confidence line. "
            + $"stdout was:\n{result.StandardOutput}\nstderr was:\n{result.StandardError}");

        string prompt = ToolCallText(result.StandardOutput, 4);
        Assert.Contains("prompt: none", prompt, StringComparison.Ordinal);
        Assert.Contains("name a task id", prompt, StringComparison.Ordinal);
    }

    /// <summary>The answer text of the tool call carrying <paramref name="id"/>, asserting the call is not an error.</summary>
    private static string ToolCallText(string stdout, long id)
    {
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument message = JsonDocument.Parse(line);
            JsonElement root = message.RootElement;
            if (!root.TryGetProperty("id", out JsonElement responseId) || responseId.GetInt64() != id) continue;

            Assert.True(
                root.TryGetProperty("result", out JsonElement callResult),
                $"Expected a JSON-RPC result for id {id}; the line was:\n{line}");
            Assert.False(
                callResult.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(),
                $"The tool call for id {id} returned an error result:\n{line}");

            return string.Concat(callResult.GetProperty("content").EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()));
        }

        Assert.Fail($"No response carried id {id}. stdout was:\n{stdout}");
        return string.Empty;
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
