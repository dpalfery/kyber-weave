using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Data.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The physical MCP configuration file adapter reports a directory this process may not traverse as a denial rather
/// than an absence, read through a real permission bit. The same question over an in-memory file system is answered
/// by <c>McpWiringHardeningTests</c>; this is the adapter contract.
/// </summary>
public sealed class PhysicalMcpConfigFilesDenialTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A directory this process may not traverse makes the guard report a denial, not an absence: "cannot inspect"
    /// and "does not exist" lead to opposite decisions, the first must refuse and the second may create.
    /// </summary>
    [Fact]
    public void AnUnreadableConfigurationIsReportedAsDeniedRatherThanAbsent()
    {
        string repo = Path.Combine(_root.Path, "repo");
        string directory = Path.Combine(repo, ".vscode");
        string target = Path.Combine(directory, "mcp.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(target, """{"servers": {}}""");

        try
        {
            DenyTraversal(directory, target);

            Assert.Equal(McpLinkObstruction.Denied, McpConfigurationLink.Probe(repo, target, new PhysicalMcpConfigFiles().InspectLink).Obstruction);
            Assert.Equal(".vscode/mcp.json", McpConfigurationLink.Probe(repo, target, new PhysicalMcpConfigFiles().InspectLink).Segment);
        }
        finally
        {
            AllowTraversal(directory);
        }
    }

    /// <summary>
    /// Removes this process's permission to traverse <paramref name="directory"/>, or skips the calling test where
    /// that cannot be arranged: Windows has no permission bits, and an account that bypasses them reads the directory
    /// anyway. <paramref name="probe"/> is a file inside the denied directory: if it is still visible, this process
    /// was not denied and the case would prove nothing.
    /// </summary>
    private static void DenyTraversal(string directory, string probe)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This platform has no Unix permission bits, so a directory this process may not traverse cannot be arranged here.");
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
        }

        if (File.Exists(probe) || Directory.Exists(probe))
        {
            AllowTraversal(directory);
            Assert.Skip("This account can traverse a directory whose permission bits deny it, so the denial cannot be arranged here.");
        }
    }

    private static void AllowTraversal(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
