using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Services;
using KyberWeave.Testing.Squad;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class SquadDoctorMcpRenameTests
{
    private const string WarningCode = "KW-MCP-WIRE-007";

    private const string InfoCode = "KW-MCP-WIRE-008";

    private const string MigrateVerb = "docs mcp-migrate";

    private readonly InMemorySquadState _state = new();
    private readonly MemoryDocsHost _host = new();

    /// <summary>
    /// A project whose <c>.mcp.json</c> holds a proven old key is warned about: doctor names the rule id,
    /// the file, and the migrate verb, and exits as it would have without the leftover.
    /// </summary>
    [Fact]
    public async Task Doctor_ProjectWithProvenOldKey_WarnsWithTheMigrateVerbAndKeepsItsExitCode()
    {
        string workingDir = NewRoot("doctor-old-key");
        WriteProvenOldKey(workingDir);

        CommandExecution leftover = await RunProjectDoctor(workingDir);

        Assert.Contains("warn", leftover.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WarningCode, leftover.Output, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", leftover.Output, StringComparison.Ordinal);
        Assert.Contains(MigrateVerb, leftover.Output, StringComparison.Ordinal);

        string cleanDir = NewRoot("doctor-clean");
        _state.Files.CreateDirectory(cleanDir);
        CommandExecution clean = await RunProjectDoctor(cleanDir);

        Assert.Equal(clean.ExitCode, leftover.ExitCode);
    }

    /// <summary>
    /// A project with no old key says nothing about renaming, so a healthy project stays quiet.
    /// </summary>
    /// <remarks>
    /// The control project holds a proven old key and must be warned about: silence over the clean tree
    /// is then doctor having read it and found nothing, not doctor having no rename check at all.
    /// </remarks>
    [Fact]
    public async Task Doctor_ProjectWithoutAnOldKey_SaysNothingAboutRenaming()
    {
        string workingDir = NewRoot("doctor-no-old-key");
        _host.Files.WriteAllText(
            Path.Combine(workingDir, ".mcp.json"),
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["kw-docs"] = new JsonObject { ["command"] = "kyber-weave-mcp", ["args"] = new JsonArray("--repo-root", ".") }
                }
            }.ToJsonString());

        CommandExecution execution = await RunProjectDoctor(workingDir);

        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(MigrateVerb, execution.Output, StringComparison.Ordinal);

        string controlDir = NewRoot("doctor-no-old-key-control");
        WriteProvenOldKey(controlDir);

        CommandExecution control = await RunProjectDoctor(controlDir);

        Assert.Contains(WarningCode, control.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An old key that is present but not provably ours is still a leftover doctor reports, with the
    /// hand edit as its remedy rather than a command that would refuse it.
    /// </summary>
    [Fact]
    public async Task Doctor_ProjectWithUnprovenOldKey_ReportsItAsLeftAloneWithAHandEdit()
    {
        string workingDir = NewRoot("doctor-unproven");
        _host.Files.WriteAllText(
            Path.Combine(workingDir, ".mcp.json"),
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["kyber-weave"] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "npx",
                        ["args"] = new JsonArray("kyber-weave-mcp")
                    }
                }
            }.ToJsonString());

        CommandExecution execution = await RunProjectDoctor(workingDir);

        Assert.Contains("warn", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(MigrateVerb, execution.Output, StringComparison.Ordinal);
        Assert.Contains("by hand", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The notice is read-only: the leftover file keeps every byte after doctor has named it.
    /// </summary>
    [Fact]
    public async Task Doctor_ProjectWithProvenOldKey_LeavesTheFileByteIdentical()
    {
        string workingDir = NewRoot("doctor-read-only");
        WriteProvenOldKey(workingDir);
        byte[] before = _host.Files.ReadAllBytes(Path.Combine(workingDir, ".mcp.json"));

        CommandExecution execution = await RunProjectDoctor(workingDir);

        Assert.Contains(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, _host.Files.ReadAllBytes(Path.Combine(workingDir, ".mcp.json")));
    }

    private string NewRoot(string name)
    {
        string root = Path.Combine(_host.Files.Root, name);
        _host.Files.CreateDirectory(root);
        _state.Files.CreateDirectory(root);
        return root;
    }

    private void WriteProvenOldKey(string workingDir)
    {
        string bin = Path.Combine(_host.Files.Root, "bin");
        string binary = _host.WriteExecutable(bin, "kyber-weave-mcp");
        _host.Files.WriteAllText(
            Path.Combine(workingDir, ".mcp.json"),
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

    private async Task<CommandExecution> RunProjectDoctor(string workingDir)
    {
        FakeProcessExecutor executor = new FakeProcessExecutor()
            .WithProbeOutput("kyber-weave-mcp", "kyber-weave-mcp 1.2.3\n");
        SquadDoctorCommand command = SquadTestCommands.InMemoryDoctor(
            _state,
            executor,
            workingDirectory: workingDir,
            wiring: _host.Services().GetRequiredService<IMcpWiringService>());

        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(() => command.ExecuteAsync(
            null!,
            new SquadDoctorSettings { Path = workingDir, Global = false },
            CancellationToken.None));

        return new CommandExecution(execution.Result, Normalize(execution.Output));
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record CommandExecution(int ExitCode, string Output);
}
