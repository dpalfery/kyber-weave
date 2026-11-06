using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>squad doctor</c> notices a leftover old MCP server key in a project and says how to migrate it,
/// without changing its exit code. Modelled on <see cref="SquadDoctorRetiredSkillTests"/>.
/// </summary>
/// <remarks>
/// The notice is read-only. Doctor is the place an operator looks when something is off, so it must
/// name the leftover; but a leftover is a warning about configuration, not a failed prerequisite, and
/// an exit code that moved on it would turn a healthy install into a red one for a rename it has not
/// yet been asked to make.
/// </remarks>
public sealed class SquadDoctorMcpRenameTests : IDisposable
{
    private const string WarningCode = "KW-MCP-WIRE-007";

    private const string MigrateVerb = "docs mcp-migrate";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A project whose <c>.mcp.json</c> holds a proven old key is warned about: doctor names the rule id,
    /// the file, and the migrate verb, and exits as it would have without the leftover.
    /// </summary>
    [Fact]
    public void Doctor_ProjectWithProvenOldKey_WarnsWithTheMigrateVerbAndKeepsItsExitCode()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-old-key");
        WriteProvenOldKey(workingDir);

        CommandExecution leftover = RunProjectDoctor(workingDir);

        Assert.Contains(WarningCode, leftover.Output, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", leftover.Output, StringComparison.Ordinal);
        Assert.Contains(MigrateVerb, leftover.Output, StringComparison.Ordinal);

        string cleanDir = Path.Combine(_temp.Path, "doctor-clean");
        Directory.CreateDirectory(cleanDir);
        CommandExecution clean = RunProjectDoctor(cleanDir);

        Assert.Equal(clean.ExitCode, leftover.ExitCode);
    }

    /// <summary>
    /// A project with no old key says nothing about renaming, so a healthy project stays quiet.
    /// </summary>
    [Fact]
    public void Doctor_ProjectWithoutAnOldKey_SaysNothingAboutRenaming()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-no-old-key");
        Directory.CreateDirectory(workingDir);
        McpWiringFixture.WriteText(
            workingDir,
            ".mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["kw-docs"] = new JsonObject { ["command"] = "kyber-weave-mcp", ["args"] = new JsonArray("--repo-root", ".") }
                }
            }.ToJsonString());

        CommandExecution execution = RunProjectDoctor(workingDir);

        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(MigrateVerb, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice is read-only: the leftover file keeps every byte after doctor has named it.
    /// </summary>
    [Fact]
    public void Doctor_ProjectWithProvenOldKey_LeavesTheFileByteIdentical()
    {
        string workingDir = Path.Combine(_temp.Path, "doctor-read-only");
        WriteProvenOldKey(workingDir);
        byte[] before = File.ReadAllBytes(Path.Combine(workingDir, ".mcp.json"));

        RunProjectDoctor(workingDir);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(workingDir, ".mcp.json")));
    }

    private void WriteProvenOldKey(string workingDir)
    {
        Directory.CreateDirectory(workingDir);
        string bin = Path.Combine(_temp.Path, "bin");
        Directory.CreateDirectory(bin);
        string binary = McpWiringFixture.WriteExecutable(bin, "kyber-weave-mcp");
        McpWiringFixture.WriteText(
            workingDir,
            ".mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["kyber-weave"] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = binary,
                        ["args"] = new JsonArray("--repo-root", ".")
                    }
                }
            }.ToJsonString());
    }

    private CommandExecution RunProjectDoctor(string workingDir)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        FakeSquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_temp.Path, "user-home"));
        SquadDoctorCommand command = new SquadDoctorCommand(
            executor,
            userPaths,
            workingDirectory: workingDir);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false }));

        return new CommandExecution(execution.Result, Normalize(execution.Output));
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record CommandExecution(int ExitCode, string Output);
}
