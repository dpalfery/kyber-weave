using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>squad status</c> reads and prints over an in-memory Squad state: a missing receipt, a matching one,
/// a drifted file, a missing file, the global scope, and a legacy global receipt. No test here touches the disk.
/// </summary>
public sealed class SquadStatusCommandInMemoryTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly InMemorySquadState _state = new();
    private readonly SquadConsole _console;

    public SquadStatusCommandInMemoryTests()
    {
        _ansi.Profile.Width = 200;
        _console = new SquadConsole(_ansi, new FakeConsole());
    }

    public void Dispose() => _ansi.Dispose();

    [Fact]
    public void Status_MissingReceipt_ExitsOneWithGuidanceToInstall()
    {
        string targetDir = CreateRoot("no-receipt-project");

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetDir,
            Global = false
        });

        Assert.Equal(1, exitCode);
        Assert.Contains("squad install", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ValidReceiptWithMatchingHashes_ExitsZero()
    {
        string targetDir = CreateRoot("valid-project");

        SeedDeployment(targetDir, SquadDeploymentScope.Project,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetDir,
            Global = false
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("architect.md", Output(), StringComparison.Ordinal);
        Assert.Contains("test-dev", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ModifiedFile_ExitsOneAndIdentifiesDrift()
    {
        string targetDir = CreateRoot("drifted-project");

        SeedDeployment(targetDir, SquadDeploymentScope.Project,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        string driftedFilePath = Path.Combine(targetDir, "agents", "architect.md");
        _state.Files.WriteAllText(driftedFilePath, "Locally modified content.");

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetDir,
            Global = false
        });

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("agents/architect.md", output, StringComparison.Ordinal);
        Assert.Contains("modified locally", output, StringComparison.Ordinal);
        Assert.Contains("--replace-managed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_MissingFile_ExitsOneAndIdentifiesMissingFile()
    {
        string targetDir = CreateRoot("missing-file-project");

        SeedDeployment(targetDir, SquadDeploymentScope.Project,
            ("agents/architect.md", "You are architect.\nPlan first.\n"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n"));

        _state.Files.DeleteFile(Path.Combine(targetDir, "skills", "test-dev", "SKILL.md"));

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetDir,
            Global = false
        });

        Assert.Equal(1, exitCode);
        Assert.Contains("skills/test-dev/SKILL.md", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_GlobalFlag_RoutesToGlobalStateStore()
    {
        string targetDir = CreateRoot("project-without-receipt");
        string globalRoot = Path.Combine(_state.UserPaths.HomeDirectory, ".codex");

        SeedDeployment(globalRoot, SquadDeploymentScope.Global,
            ("agents/architect.md", "You are architect.\nPlan first.\n"));

        // A global receipt's paths resolve beneath each target's own global root, so the
        // seeded deployment is only verifiable through a resolver that maps codex there.
        SquadStatusCommand command = SquadTestCommands.InMemoryStatus(
            _state,
            new FakeSquadGlobalRootResolver(_state.UserPaths.HomeDirectory),
            _console);

        int projectExitCode = command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = targetDir,
                Global = false
            });
        Assert.Equal(1, projectExitCode);

        int globalExitCode = command.Execute(
            null!,
            new SquadStatusSettings
            {
                Path = globalRoot,
                Global = true
            });
        Assert.Equal(0, globalExitCode);
        Assert.Contains("architect.md", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void StatusReceiptLayout_CleanLegacyGlobalReceipt_PrintsOkAndExitsZero()
    {
        string targetRoot = CreateRoot("legacy-clean");
        SeedLegacyGlobalDeployment(targetRoot,
            (".codex/agents/conductor.toml", "conductor content"));

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetRoot,
            Global = true
        });

        string output = Output();
        Assert.Equal(0, exitCode);
        Assert.Contains("ok", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusReceiptLayout_DriftEditedFileLegacyReceipt_PrintsDriftAndExitsOne()
    {
        string targetRoot = CreateRoot("legacy-drift");
        SeedLegacyGlobalDeployment(targetRoot,
            (".codex/agents/conductor.toml", "conductor content"));

        _state.Files.WriteAllText(
            Path.Combine(targetRoot, ".codex", "agents", "conductor.toml"),
            "edited conductor content");

        int exitCode = Run(new SquadStatusSettings
        {
            Path = targetRoot,
            Global = true
        });

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("modified locally", output, StringComparison.Ordinal);
        Assert.Contains(".codex/agents/conductor.toml", output, StringComparison.Ordinal);
        Assert.Contains("--replace-managed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", output, StringComparison.OrdinalIgnoreCase);
    }

    private int Run(SquadStatusSettings settings) =>
        SquadTestCommands.InMemoryStatus(_state, output: _console).Execute(null!, settings);

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string CreateRoot(string name)
    {
        string root = Path.Combine(_state.Files.Root, "projects", name);
        _state.Files.CreateDirectory(root);
        return _state.Layout.PhysicalOf(root);
    }

    private void SeedDeployment(
        string targetRoot,
        SquadDeploymentScope scope,
        params (string RelativePath, string Content)[] files)
    {
        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _state.Files.WriteAllText(fullPath, content);
            ownedFiles.Add(new SquadOwnedFile(
                relativePath,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                "codex",
                Adopted: false));
        }

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetRoot, scope),
            _state.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: scope,
                TargetRoot: ".",
                InstalledAtUtc: SquadStateTestLayout.InstalledAt,
                Degradations: [],
                Files: ownedFiles)));
        _state.Layout.WriteText(
            _state.Store.ResolveLockPath(targetRoot, scope),
            _state.Store.SerializeLock(new SquadLock(
                Schema: "kyber-squad.lock/v1",
                SquadVersion: "1.2.3",
                CliVersion: "1.2.3",
                McpVersion: "1.2.3",
                Bundle: "full",
                Targets: ["codex"],
                Exclusions: [],
                Translation: "best-effort",
                BundleDigest: new string('a', 64),
                AssetDigest: new string('b', 64),
                Apm: new SquadApmIdentity("0.28.0", new string('c', 40), new string('d', 64)))));
    }

    /// <summary>
    /// Writes a global receipt as authentic legacy v1 JSON — the shape a pre-#91 CLI wrote, with no explicit
    /// <c>layout</c> field — rather than through the serializer, which upgrades a global receipt to v2.
    /// </summary>
    private void SeedLegacyGlobalDeployment(
        string targetRoot,
        params (string RelativePath, string Content)[] files)
    {
        JsonArray filesJson = [];
        foreach ((string relativePath, string content) in files)
        {
            string fullPath = Path.Combine(targetRoot, relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _state.Files.WriteAllText(fullPath, content);
            filesJson.Add(new JsonObject
            {
                ["relativePath"] = relativePath,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                ["target"] = "codex",
                ["adopted"] = false
            });
        }

        JsonObject receiptJson = new()
        {
            ["schema"] = "kyber-squad.receipt/v1",
            ["scope"] = "global",
            ["targetRoot"] = ".",
            ["installedAtUtc"] = SquadStateTestLayout.InstalledAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["degradations"] = new JsonArray(),
            ["files"] = filesJson
        };

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global),
            receiptJson.ToJsonString() + "\n");
        _state.Layout.WriteText(
            _state.Store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global),
            _state.Store.SerializeLock(SquadStateTestLayout.Lock()));
    }
}
