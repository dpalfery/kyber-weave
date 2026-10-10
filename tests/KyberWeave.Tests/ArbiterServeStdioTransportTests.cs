using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins that the <c>serve</c> process answers every request it has read even when the
/// client closes stdin first: a scripted session — initialize, tools/list, a tool call,
/// then end of input — must come back with a response per request.
/// </summary>
/// <remarks>
/// Same defect the docs server had (see <see cref="McpStdioTransportTests"/>): the SDK's
/// stdio transport marks itself disconnected the moment its read loop sees end of stream
/// and drops every send after that, so against the stock transport this test fails with
/// exit 0 and zero responses on stdout.
/// </remarks>
public sealed class ArbiterServeStdioTransportTests
{
    private const string Requests =
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"0"}}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
        {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"arbiter_status","arguments":{}}}

        """;

    [Fact]
    public void TheExecutableAnswersRequestsPipedInBeforeStdinCloses()
    {
        using TempDirectory repository = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repository.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(repository.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: true\n  provider:\n    kind: none\n");

        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository.Path,
        };
        startInfo.ArgumentList.Add(typeof(HookCommand).Assembly.Location);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(repository.Path);

        ProcessResult result = ProcessRunner.Run(startInfo, Requests, TimeSpan.FromSeconds(30));

        List<long> answered = AnsweredIds(result.StandardOutput);
        Assert.True(
            result.ExitCode == 0 && answered.Order().SequenceEqual([1L, 2L, 3L]),
            $"Expected exit 0 and responses for ids 1, 2 and 3; got exit {result.ExitCode} and " +
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
