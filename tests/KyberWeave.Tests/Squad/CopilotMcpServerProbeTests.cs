using System.Diagnostics;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Pins <see cref="CopilotMcpServerProbe.HasServer"/>: the probe answers whether the Copilot CLI MCP config
/// declares a server, and it must never block on a file that is not a regular file.
/// </summary>
/// <remarks>
/// A FIFO at the config path makes an <c>open</c> block until a writer appears, so a probe that opens it
/// hangs a render forever. Each non-regular case runs under a deadline, so a regression fails in seconds
/// rather than stalling the run. A symlink is followed, and its target must itself be a regular file.
/// </remarks>
public sealed class CopilotMcpServerProbeTests : IDisposable
{
    private const string ServerName = "ops-relay";
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(5);
    private const string ConfigWithServer = "{\"mcpServers\":{\"ops-relay\":{\"command\":\"x\"}}}";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string NewRoot()
    {
        string root = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Runs the probe on a worker and fails if it has not returned within the deadline.</summary>
    private static async Task<bool> HasServerWithinDeadlineAsync(string root)
    {
        Task<bool> probe = Task.Run(() => CopilotMcpServerProbe.HasServer(root, ServerName));
        Task finished = await Task.WhenAny(probe, Task.Delay(ProbeDeadline));
        Assert.True(
            ReferenceEquals(finished, probe),
            $"HasServer did not return within {ProbeDeadline.TotalSeconds}s, so it is blocked reading a non-regular file.");
        return await probe;
    }

    private static void MakeFifo(string path)
    {
        ProcessStartInfo info = new("mkfifo");
        info.ArgumentList.Add(path);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("mkfifo did not start.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task RegularConfigDeclaringTheServer_ReturnsTrue()
    {
        string root = NewRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "mcp-config.json"), ConfigWithServer);

        Assert.True(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task RegularConfigWithoutTheServer_ReturnsFalse()
    {
        string root = NewRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "mcp-config.json"), "{\"mcpServers\":{\"other\":{}}}");

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task ServerNameMatchesOnlyAnExactKey()
    {
        string root = NewRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "mcp-config.json"), "{\"mcpServers\":{\"ops-relay-old\":{}}}");

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task MissingConfig_ReturnsFalse()
    {
        Assert.False(await HasServerWithinDeadlineAsync(NewRoot()));
    }

    [Fact]
    public async Task DirectoryAtConfigPath_ReturnsFalseWithoutReading()
    {
        string root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "mcp-config.json"));

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task FifoAtConfigPath_ReturnsFalseWithoutOpeningIt()
    {
        string root = NewRoot();
        MakeFifo(Path.Combine(root, "mcp-config.json"));

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task SymlinkToRegularConfigDeclaringTheServer_IsFollowedAndReturnsTrue()
    {
        string root = NewRoot();
        string target = Path.Combine(root, "real-config.json");
        await File.WriteAllTextAsync(target, ConfigWithServer);
        File.CreateSymbolicLink(Path.Combine(root, "mcp-config.json"), target);

        Assert.True(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task SymlinkToFifo_ReturnsFalseWithoutOpeningIt()
    {
        string root = NewRoot();
        string fifo = Path.Combine(root, "real-fifo");
        MakeFifo(fifo);
        File.CreateSymbolicLink(Path.Combine(root, "mcp-config.json"), fifo);

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }

    [Fact]
    public async Task SymlinkToDeviceNode_ReturnsFalse()
    {
        string root = NewRoot();
        File.CreateSymbolicLink(Path.Combine(root, "mcp-config.json"), "/dev/null");

        Assert.False(await HasServerWithinDeadlineAsync(root));
    }
}
