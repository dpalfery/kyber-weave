using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The shipped docs executable wires the draining stdio transport: requests piped in before stdin closes are all
/// answered. The process start is the subject, so it runs for real; the transport's own behaviour is proven over
/// memory streams by <c>McpStdioTransportTests</c> in the MCP test project.
/// </summary>
public sealed class McpStdioTransportExecutableTests
{
    private const string ExecutableRequests =
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"0"}}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}

        """;

    /// <summary>
    /// The same scenario through the shipped executable, so it also pins that <c>Program.cs</c>
    /// wires the draining transport. With the SDK's stdio transport the process exits 0 and
    /// writes nothing to stdout.
    /// </summary>
    [Fact]
    public void TheExecutableAnswersRequestsPipedInBeforeStdinCloses()
    {
        using TempDirectory repository = new TempDirectory();
        DocsTestCommands.Scaffolding.Scaffold(repository.Path);
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository.Path,
        };
        startInfo.ArgumentList.Add(typeof(DocsTools).Assembly.Location);
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(repository.Path);

        ProcessResult result = ProcessRunner.Run(startInfo, ExecutableRequests, TimeSpan.FromSeconds(30));

        List<long> answered = AnsweredIds(result.StandardOutput);
        Assert.True(
            result.ExitCode == 0 && answered.Order().SequenceEqual([1L, 2L]),
            $"Expected exit 0 and responses for ids 1 and 2; got exit {result.ExitCode} and " +
            $"[{string.Join(", ", answered)}]. stdout was:\n{result.StandardOutput}\nstderr was:\n{result.StandardError}");
    }

    private static List<long> AnsweredIds(string written)
    {
        List<long> ids = [];
        foreach (string line in written.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument message = JsonDocument.Parse(line);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("result", out _) && root.TryGetProperty("id", out JsonElement id))
            {
                ids.Add(id.GetInt64());
            }
        }

        return ids;
    }
}
