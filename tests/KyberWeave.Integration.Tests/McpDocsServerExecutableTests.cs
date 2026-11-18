using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The shipped docs executable answers a real <c>tools/call</c> over its own stdio with an answer rather than an
/// error result. The process start is the subject, so it runs for real; the container registration it guards is
/// proven in memory by <c>McpCompositionRootTests</c> in the MCP test project.
/// </summary>
public sealed class McpDocsServerExecutableTests : IDisposable
{
    private readonly TempDirectory _repository = new();

    public void Dispose() => _repository.Dispose();

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
}
