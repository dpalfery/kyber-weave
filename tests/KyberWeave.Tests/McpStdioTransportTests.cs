using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins that a request the server has read is answered even when the client closes its input
/// before the answer is ready.
/// </summary>
/// <remarks>
/// Regression for issue #95: piping requests into <c>kyber-weave-mcp</c> and letting stdin reach
/// end of stream ran every handler to completion and wrote zero bytes to stdout, because the
/// SDK transport drops every send once its read loop has ended.
/// </remarks>
public sealed class McpStdioTransportTests
{
    private const string Requests =
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"0"}}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
        {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"after_input_closed","arguments":{}}}

        """;

    private const string ExecutableRequests =
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"0"}}}
        {"jsonrpc":"2.0","method":"notifications/initialized"}
        {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}

        """;

    [Fact(Timeout = 30_000)]
    public async Task RequestsReadBeforeInputClosesAreAnsweredAfterIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MemoryStream input = new MemoryStream(Encoding.UTF8.GetBytes(Requests));
        using MemoryStream output = new MemoryStream();
        await using DrainingStreamServerTransport transport = new DrainingStreamServerTransport(input, output);

        // The tool finishes only once the transport has seen end of input, so its response is
        // produced after the client stopped writing. That is the case the SDK transport drops.
        McpServerTool afterInputClosed = McpServerTool.Create(
            async (CancellationToken token) =>
            {
                await transport.MessageReader.Completion.WaitAsync(token).ConfigureAwait(false);
                return "answered after input closed";
            },
            new McpServerToolCreateOptions { Name = "after_input_closed" });
        McpServerOptions options = new McpServerOptions { ToolCollection = [afterInputClosed] };

        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken);

        string written = Encoding.UTF8.GetString(output.ToArray());
        List<long> answered = AnsweredIds(written);

        Assert.True(
            answered.Order().SequenceEqual([1L, 2L, 3L]),
            $"Expected responses for ids 1, 2 and 3; got [{string.Join(", ", answered)}]. stdout was:\n{written}");
        Assert.Contains("answered after input closed", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same scenario through the shipped executable, so it also pins that <c>Program.cs</c>
    /// wires the draining transport. With the SDK's stdio transport the process exits 0 and
    /// writes nothing to stdout.
    /// </summary>
    [Fact]
    public void TheExecutableAnswersRequestsPipedInBeforeStdinCloses()
    {
        using TempDirectory repository = new TempDirectory();
        DocsScaffolder.Scaffold(repository.Path);
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
