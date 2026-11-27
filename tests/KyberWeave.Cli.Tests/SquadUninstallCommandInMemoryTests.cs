using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Testing.Squad;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>squad uninstall</c> removes, refuses and reports over an in-memory Squad state: a missing receipt,
/// a project deployment, a dry run, a corrupt receipt, one named harness, invalid input, the global scope and
/// a legacy global receipt. No test here touches the disk.
/// </summary>
public sealed class SquadUninstallCommandInMemoryTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly InMemorySquadState _state = new();
    private readonly SquadConsole _console;
    private readonly FakeSquadReleaseSource _release;
    private readonly FakeSquadRenderer _renderer = new();

    public SquadUninstallCommandInMemoryTests()
    {
        _ansi.Profile.Width = 200;
        _console = new SquadConsole(_ansi, new FakeConsole());
        _release = new FakeSquadReleaseSource { Files = _state.Files };
    }

    public void Dispose()
    {
        _ansi.Dispose();
        _release.Dispose();
    }

    [Fact]
    public void Uninstall_MissingReceipt_ExitsZeroAsNoOp()
    {
        string targetDir = CreateRoot("no-receipt-uninstall");

        int exitCode = Execute(new SquadUninstallSettings
        {
            PathOption = targetDir,
            Global = false,
            DryRun = false
        });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Uninstall_ExistingReceipt_ExecutesTransactionAndRemovesFilesAndState()
    {
        string targetDir = CreateRoot("uninstall-target");
        SeedProjectDeployment(targetDir,
            ("agents/architect.md", "You are architect.\nPlan first.\n", "codex"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n", "codex"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string deployedFile2 = Path.Combine(targetDir, "skills", "test-dev", "SKILL.md");
        string receiptPath = _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);
        string lockPath = _state.Store.ResolveLockPath(targetDir, SquadDeploymentScope.Project);

        Assert.True(_state.Files.FileExists(deployedFile1));
        Assert.True(_state.Files.FileExists(deployedFile2));
        Assert.True(_state.Files.FileExists(receiptPath));
        Assert.True(_state.Files.FileExists(lockPath));

        int exitCode = Execute(new SquadUninstallSettings
        {
            PathOption = targetDir,
            Global = false,
            DryRun = false
        });

        Assert.Equal(0, exitCode);
        Assert.False(_state.Files.FileExists(deployedFile1));
        Assert.False(_state.Files.FileExists(deployedFile2));
        Assert.False(_state.Files.FileExists(receiptPath));
        Assert.False(_state.Files.FileExists(lockPath));
    }

    [Fact]
    public void Uninstall_DryRun_PreservesFilesAndState()
    {
        string targetDir = CreateRoot("uninstall-dryrun-target");
        SeedProjectDeployment(targetDir,
            ("agents/architect.md", "You are architect.\nPlan first.\n", "codex"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n", "codex"));

        string deployedFile1 = Path.Combine(targetDir, "agents", "architect.md");
        string receiptPath = _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project);

        int exitCode = Execute(new SquadUninstallSettings
        {
            PathOption = targetDir,
            Global = false,
            DryRun = true
        });

        Assert.Equal(0, exitCode);
        Assert.True(_state.Files.FileExists(deployedFile1));
        Assert.True(_state.Files.FileExists(receiptPath));
    }

    [Fact]
    public void Uninstall_WhenCorruptReceipt_ExitsOne()
    {
        string targetDir = CreateRoot("corrupt-receipt-uninstall");

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project),
            "invalid json {[[");

        int exitCode = Execute(new SquadUninstallSettings
        {
            PathOption = targetDir,
            Global = false,
            DryRun = false
        });

        Assert.Equal(1, exitCode);
        Assert.Contains("error", Output(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_NamedHarness_RemovesOnlyThatHarnessUntilTheLastOneGoes()
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-one-harness");

        int firstExitCode = Execute(new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir });

        Assert.Equal(0, firstExitCode);
        Assert.False(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(receiptPath));
        SquadReceipt? remaining = _state.Store.ReadReceipt(targetDir, SquadDeploymentScope.Project);
        Assert.Equal(["codex"], remaining!.Files.Select(file => file.Target).Distinct());

        int lastExitCode = Execute(new SquadUninstallSettings { Harness = "codex", Project = true, PathOption = targetDir });

        Assert.Equal(0, lastExitCode);
        Assert.False(_state.Files.FileExists(codexFile));
        Assert.False(_state.Files.FileExists(receiptPath));
    }

    [Fact]
    public void Uninstall_NamedHarnessDryRun_PreservesFilesAndReceipt()
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-one-harness-dry-run");

        int exitCode = Execute(new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir, DryRun = true });

        Assert.Equal(0, exitCode);
        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(receiptPath));
    }

    [Fact]
    public void Uninstall_InvalidHarnessInput_ExitsTwoWithoutChanges()
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-invalid-harness-input");

        (SquadUninstallSettings Settings, string ExpectedMessage)[] invalid =
        [
            (new() { Harness = "nope", Project = true, PathOption = targetDir }, "nope"),
            (new() { Harness = "all", Project = true, PathOption = targetDir }, "exactly one harness"),
            (new() { Harness = "claude", Project = true, PathOption = targetDir }, "'claude' is not installed"),
            (new() { Harness = "cursor", Project = true, Global = true, PathOption = targetDir }, "mutually exclusive"),
            (new() { Harness = "cursor", PathOption = targetDir }, "--path is only valid with --project"),
            (new() { Harness = "cursor" }, "'cursor' is not installed in the global deployment")
        ];

        foreach ((SquadUninstallSettings settings, string expectedMessage) in invalid)
        {
            int exitCode = Execute(settings);
            Assert.Equal(2, exitCode);
            Assert.Contains(expectedMessage, Output(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(receiptPath));
    }

    [Fact]
    public void Uninstall_NamedHarnessWithoutProject_UsesTheGlobalScopeAndKeepsTheOtherHarness()
    {
        string home = _state.UserPaths.HomeDirectory;
        _state.Files.CreateDirectory(home);
        FakeSquadGlobalRootResolver roots = new(home);
        string codexFile = Path.Combine(home, ".codex", "agents", "architect.md");
        string cursorFile = Path.Combine(home, ".cursor", "agents", "reviewer.md");

        SeedGlobalDeployment(roots, home,
            ("agents/architect.md", "You are architect.\n", "codex"),
            ("agents/reviewer.md", "You are reviewer.\n", "cursor"));

        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));

        int firstExitCode = Execute(new SquadUninstallSettings { Harness = "cursor", Yes = true });

        Assert.Equal(0, firstExitCode);
        Assert.False(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(codexFile));
        SquadReceipt remaining = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(home, SquadDeploymentScope.Global));
        Assert.Equal(["codex"], remaining.Files.Select(file => file.Target).Distinct());

        int lastExitCode = Execute(new SquadUninstallSettings { Harness = "codex", Yes = true });

        Assert.Equal(0, lastExitCode);
        Assert.False(_state.Files.FileExists(codexFile));
        Assert.Null(_state.Store.ReadReceipt(home, SquadDeploymentScope.Global));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./deploy")]
    public void Uninstall_DirectoryGivenAsTheHarness_ExitsTwoNamingProjectAndPath(string directory)
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-directory-as-harness");

        int exitCode = Execute(new SquadUninstallSettings { Harness = directory, PathOption = targetDir });

        Assert.Equal(2, exitCode);
        Assert.Contains("--project", Output(), StringComparison.Ordinal);
        Assert.Contains("--path", Output(), StringComparison.Ordinal);
        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(receiptPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Uninstall_BlankPathOption_ExitsTwoWithoutChanges(string blank)
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-blank-path");

        foreach (SquadUninstallSettings settings in new[]
                 {
                     new SquadUninstallSettings { PathOption = blank },
                     new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = blank }
                 })
        {
            int exitCode = Execute(settings);

            Assert.Equal(2, exitCode);
            Assert.Contains("must not be blank", Output(), StringComparison.Ordinal);
        }

        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));
        Assert.True(_state.Files.FileExists(receiptPath));
    }

    [Fact]
    public void Uninstall_GlobalAndProjectWithoutAHarness_ExitsTwo()
    {
        int exitCode = Execute(new SquadUninstallSettings { Global = true, Project = true });

        Assert.Equal(2, exitCode);
        Assert.Contains("mutually exclusive", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_NamedHarnessWithCorruptReceipt_ReportsTheReadErrorAndChangesNothing()
    {
        (string targetDir, string codexFile, string cursorFile, string receiptPath) =
            SeedCodexAndCursorProject("uninstall-corrupt-receipt");
        _state.Files.WriteAllText(receiptPath, "{ not json");

        int exitCode = Execute(new SquadUninstallSettings { Harness = "cursor", Project = true, PathOption = targetDir });

        string output = Output();
        Assert.Equal(1, exitCode);
        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is not installed", output, StringComparison.Ordinal);
        Assert.True(_state.Files.FileExists(codexFile));
        Assert.True(_state.Files.FileExists(cursorFile));
        Assert.Equal("{ not json", _state.Files.ReadAllText(receiptPath));
    }

    [Fact]
    public void UninstallReceiptLayout_LegacyGlobalReceipt_ConfirmationNamesRecordedRoot()
    {
        string targetRoot = CreateRoot("legacy-global-uninstall");
        SeedLegacyGlobalDeployment(targetRoot,
            (".codex/agents/conductor.toml", "conductor content"));

        int exitCode = Execute(new SquadUninstallSettings
        {
            PathOption = targetRoot,
            Global = true,
            Yes = true
        });

        Assert.Contains($"codex writes beneath {targetRoot}.", Output(), StringComparison.Ordinal);
    }

    private int Execute(SquadUninstallSettings settings) =>
        SquadTestCommands.InMemoryUninstall(
            _state,
            _release,
            _renderer,
            new FakeSquadGlobalRootResolver(_state.UserPaths.HomeDirectory),
            _console).Execute(null!, settings);

    private string Output() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string CreateRoot(string name)
    {
        string root = Path.Combine(_state.Files.Root, "projects", name);
        _state.Files.CreateDirectory(root);
        return _state.Layout.PhysicalOf(root);
    }

    private (string TargetDir, string CodexFile, string CursorFile, string ReceiptPath)
        SeedCodexAndCursorProject(string name)
    {
        string targetDir = CreateRoot(name);
        SeedProjectDeployment(targetDir,
            ("agents/architect.md", "You are architect.\n", "codex"),
            ("skills/test-dev/SKILL.md", "Use when writing tests.\n", "cursor"));

        return (
            targetDir,
            Path.Combine(targetDir, "agents", "architect.md"),
            Path.Combine(targetDir, "skills", "test-dev", "SKILL.md"),
            _state.Store.ResolveReceiptPath(targetDir, SquadDeploymentScope.Project));
    }

    private void SeedProjectDeployment(
        string targetRoot,
        params (string RelativePath, string Content, string Target)[] files)
    {
        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content, string target) in files)
        {
            string fullPath = Path.Combine(targetRoot, relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _state.Files.WriteAllText(fullPath, content);
            ownedFiles.Add(new SquadOwnedFile(relativePath, Digest(content), target, Adopted: false));
        }

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Project),
            _state.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: SquadDeploymentScope.Project,
                TargetRoot: ".",
                InstalledAtUtc: SquadStateTestLayout.InstalledAt,
                Degradations: [],
                Files: ownedFiles)));
        _state.Layout.WriteText(
            _state.Store.ResolveLockPath(targetRoot, SquadDeploymentScope.Project),
            _state.Store.SerializeLock(new SquadLock(
                Schema: "kyber-squad.lock/v1",
                SquadVersion: "1.2.3",
                CliVersion: "1.2.3",
                McpVersion: "1.2.3",
                Bundle: "full",
                Targets: [.. ownedFiles.Select(file => file.Target).Distinct(StringComparer.Ordinal)],
                Exclusions: [],
                Translation: "best-effort",
                BundleDigest: new string('a', 64),
                AssetDigest: new string('b', 64),
                Apm: new SquadApmIdentity("0.28.0", new string('c', 40), new string('d', 64)))));
    }

    private void SeedGlobalDeployment(
        FakeSquadGlobalRootResolver roots,
        string home,
        params (string RelativePath, string Content, string Target)[] files)
    {
        List<SquadOwnedFile> ownedFiles = [];
        foreach ((string relativePath, string content, string target) in files)
        {
            string fullPath = Path.Combine(roots.ResolveGlobalRoot(SquadTargetCatalog.Parse([target])[0]), relativePath);
            _state.Files.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _state.Files.WriteAllText(fullPath, content);
            ownedFiles.Add(new SquadOwnedFile(relativePath, Digest(content), target, Adopted: false));
        }

        _state.Layout.WriteText(
            _state.Store.ResolveReceiptPath(home, SquadDeploymentScope.Global),
            _state.Store.SerializeReceipt(new SquadReceipt(
                Schema: "kyber-squad.receipt/v1",
                Scope: SquadDeploymentScope.Global,
                TargetRoot: ".",
                InstalledAtUtc: SquadStateTestLayout.InstalledAt,
                Degradations: [],
                Files: ownedFiles)));
        _state.Layout.WriteText(
            _state.Store.ResolveLockPath(home, SquadDeploymentScope.Global),
            _state.Store.SerializeLock(new SquadLock(
                Schema: "kyber-squad.lock/v1",
                SquadVersion: "1.2.3",
                CliVersion: "1.2.3",
                McpVersion: "1.2.3",
                Bundle: "full",
                Targets: [.. ownedFiles.Select(file => file.Target).Distinct(StringComparer.Ordinal)],
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
                ["sha256"] = Digest(content),
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

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
