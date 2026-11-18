using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Tests;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Cli.Tests.Docs;

/// <summary>The <c>docs mcp-migrate</c> command over an in-memory tree whose configuration cannot be inspected.</summary>
public sealed class McpMigrateDenialTests
{
    private readonly InMemoryMcpWiring _wiring = McpWiringTestServices.InMemory();

    /// <summary>
    /// A migrate over a configuration this process may not inspect is a refusal, not a silent skip: it exits 1
    /// with KW-MCP-WIRE-009 and, once traversal is restored, the file is byte-identical. The file holds a proven
    /// old key, so the only reason the rename does not happen is the denial.
    /// </summary>
    [Fact]
    public void AMigrateRefusesAConfigurationItMayNotInspectAndLeavesItByteIdentical()
    {
        string directory = Path.Combine(_wiring.Root, ".vscode");
        string target = Path.Combine(directory, "mcp.json");
        _wiring.Files.CreateDirectory(directory);
        _wiring.Files.WriteAllText(
            target,
            """{"servers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", "."]}}}""");
        byte[] before = _wiring.Files.ReadAllBytes(target);

        try
        {
            _wiring.Files.FailWhen(
                operation => operation.Name == nameof(InMemoryFileSystem.GetLinkTarget) && operation.Path == target,
                new UnauthorizedAccessException($"Access to the path '{target}' is denied."));

            TestConsole console = new() { EmitAnsiSequences = false };
            int exit = new DocsMcpMigrateCommand(
                _wiring.Wiring,
                _wiring.Paths,
                _wiring.Environment,
                console).Execute(
                null!,
                new DocsMcpMigrateSettings { Path = _wiring.Root, Target = "vscode" });
            CapturedConsoleExecution<int> execution = new(exit, console.Output);

            Assert.Equal(1, execution.Result);
            Assert.Contains("KW-MCP-WIRE-009", execution.Output, StringComparison.Ordinal);
        }
        finally
        {
            _wiring.Files.ClearFaults();
        }

        Assert.Equal(before, _wiring.Files.ReadAllBytes(target));
    }
}
