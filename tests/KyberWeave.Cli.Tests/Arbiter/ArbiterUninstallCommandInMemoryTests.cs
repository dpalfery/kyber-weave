using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Configuration;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What <c>arbiter uninstall</c> removes and what it says, over the shared file-system, path, environment, config,
/// state and credential fakes with the real uninstaller wired between them: the home, the receipts and the hook files
/// are all in memory, so nothing here starts a process or touches the operator's home.
/// </summary>
public sealed class ArbiterUninstallCommandInMemoryTests : IDisposable
{
    private const string ClaudeForeign = """
        {
          "model": "sonnet",
          "permissions": { "allow": ["Bash"] },
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "^Read$",
                "hooks": [
                  { "type": "command", "command": "user-owned read hook" }
                ]
              }
            ]
          },
          "custom": { "keep": "this stays" },
          "user": "keep me"
        }
        """;

    private const string CodexForeign = """
        {
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "^Read$",
                "hooks": [
                  { "type": "command", "command": "user-owned read hook" }
                ]
              }
            ]
          },
          "custom": { "keep": "codex stays" },
          "version": 1
        }
        """;

    private const string FactoryForeign = """
        {
          "SubagentStop": [
            { "hooks": [ { "type": "command", "command": "user-own-stop-notifier" } ] }
          ]
        }
        """;

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly FakeCredentialStore _store = new();
    private readonly InMemorySquadState _state;
    private readonly SquadGlobalRoots _globalRoots;
    private readonly string _home;
    private readonly string _work;
    private readonly ArbiterTestHost _host;

    public ArbiterUninstallCommandInMemoryTests()
    {
        _home = Path.Combine(_files.Root, "home");
        _work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_work);
        _console.Profile.Width = 400;
        _state = new InMemorySquadState(
            existingFiles: _files,
            homeDirectory: _home,
            applicationDataDirectory: Path.Combine(_files.Root, "application-data"));
        _globalRoots = new SquadGlobalRoots(_ => null, _home, isWindows: false);
        _host = ArbiterTestHost.Create(RegisterFakes);
    }

    public void Dispose()
    {
        _host.Dispose();
        _console.Dispose();
        _streams.Dispose();
    }

    // The command and the real uninstaller resolve the injected home, file system, config, state and store, never the
    // real home, keychain or environment.
    private void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IFileSystem>(_files);
        services.AddSingleton<IWritableFileSystem>(_files);
        services.AddSingleton<IPathResolver>(new FakePathResolver(_files));
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            currentDirectory: _work,
            homeDirectory: _home));
        services.AddSingleton<IUserHostConfigStore>(new FileUserHostConfigStore(_files));
        services.AddSingleton<ISquadStateStore>(_state.Store);
        services.AddSingleton<ICredentialStore>(_store);
        services.AddSingleton<ISquadGlobalRootResolver>(_globalRoots);
        services.AddSingleton<IAnsiConsole>(_console);
        services.AddSingleton<IConsole>(_streams);
        services.AddSingleton(new SquadConsoleOptions(IsInteractive: false));
    }

    /// <summary>
    /// The kilo uninstall removes the receipt-owned plugin file and owned blocks,
    /// and never touches a neighboring non-owned file. A bulk delete would pass
    /// the owned assertion and still destroy the operator's own plugin.
    /// </summary>
    [Fact]
    public async Task Uninstall_KiloTarget_RemovesOnlyReceiptOwnedPluginFile()
    {
        string host = NewDir("uninstall-owned");
        string owned = Path.Combine(host, ".kilo", "plugin", "kyber-arbiter.ts");
        WriteFile(owned, "// receipt-owned kyber-arbiter shim\n");
        string nonOwned = Path.Combine(host, ".kilo", "plugin", "custom.ts");
        WriteFile(nonOwned, "// operator-owned plugin, never touched\n");
        WriteProjectReceipt(host, OwnedFile(".kilo/plugin/kyber-arbiter.ts", "kilo"));

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(_files.FileExists(owned), "receipt-owned kyber-arbiter.ts must be removed");
        Assert.True(_files.FileExists(nonOwned), "non-owned plugin files must be left untouched");
    }

    /// <summary>
    /// Without <c>--remove-key</c> the credential-store entry survives the uninstall.
    /// Silently deleting a credential on uninstall would be hard to reverse (D6).
    /// </summary>
    [Fact]
    public async Task Uninstall_WithoutRemoveKey_LeavesCredentialStoreEntryUntouched()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-KEEP-7f3a9c1e";
        await _store.WriteAsync(origin, key);
        string host = NewDir("uninstall-keep-key");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(key, await _store.ReadAsync(origin));
    }

    /// <summary>
    /// With <c>--remove-key</c> the injected fake-store entry is deleted.
    /// </summary>
    [Fact]
    public async Task Uninstall_WithRemoveKey_DeletesCredentialStoreEntry()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-DROP-1a2b3c4d";
        await _store.WriteAsync(origin, key);
        string host = NewDir("uninstall-drop-key");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Null(await _store.ReadAsync(origin));
    }

    /// <summary>
    /// An unknown target is client input, not a crash: exit 2 naming the roster.
    /// </summary>
    [Fact]
    public async Task Uninstall_InvalidTarget_ExitsTwoNamingTheRoster()
    {
        string host = NewDir("uninstall-bad-target");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "hal9000" },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("hal9000", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kilo", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No deployment is a no-op: exit 0 with a "nothing to uninstall" status.
    /// </summary>
    [Fact]
    public async Task Uninstall_NoDeployment_ExitsZeroAsNoOp()
    {
        string host = NewDir("uninstall-empty");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        string status = string.Join(' ', execution.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("nothing to uninstall", status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unreadable receipt is not an absent one. Falling back to a path-only delete would
    /// remove an operator-owned file at the same path on the strength of a receipt the tool
    /// could not read, so the command must fail and leave the file alone.
    /// </summary>
    [Fact]
    public async Task Uninstall_UnreadableReceipt_FailsWithoutDeletingTheOperatorFile()
    {
        string host = NewDir("uninstall-corrupt-receipt");
        string operatorFile = Path.Combine(host, ".kilo", "plugin", "kyber-arbiter.ts");
        WriteFile(operatorFile, "// operator-owned plugin\n");
        WriteFile(
            _state.Store.ResolveReceiptPath(host, SquadDeploymentScope.Project),
            "{ this is not a receipt");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.True(
            _files.FileExists(operatorFile),
            "an unreadable receipt must not fall back to a path-only delete");
    }

    /// <summary>
    /// <c>--remove-key</c> needs the endpoint to know which origin it deletes. When the host
    /// configuration cannot be loaded the origin is unknown, so the key must survive instead of
    /// the command guessing the default origin.
    /// </summary>
    [Fact]
    public async Task Uninstall_RemoveKeyWithUnreadableConfig_FailsAndKeepsTheKey()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-BAD-CONFIG-2b3c4d5e";
        await _store.WriteAsync(origin, key);
        string host = NewDir("uninstall-bad-config");
        WriteFile(Path.Combine(host, ".kyber-weave", "kyber-weave.yml"), "arbiter: [unclosed\n");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("could not be loaded", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key, await _store.ReadAsync(origin));
    }

    /// <summary>
    /// The key is stored per endpoint origin, not per target. Deleting it while another target
    /// is still deployed removes that target's auth too, so the operator is warned.
    /// </summary>
    [Fact]
    public async Task Uninstall_RemoveKeyWithAnotherTargetDeployed_WarnsThatTheKeyIsShared()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-SHARED-3c4d5e6f";
        await _store.WriteAsync(origin, key);
        string host = NewDir("uninstall-shared-key");
        WriteProjectReceipt(
            host,
            OwnedFile(".kilo/plugin/kyber-arbiter.ts", "kilo"),
            OwnedFile(".opencode/plugins/kyber-arbiter.ts", "opencode"));

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("shared", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await _store.ReadAsync(origin));
    }

    /// <summary>
    /// Cursor and Codex share a single hook file, so uninstall must remove only the owned block and
    /// leave the user's bytes beside it untouched. A whole-file rewrite would re-encode unrelated
    /// hook entries and silently change user content. The operator content and the block the
    /// installer splices beside it are arranged here, in memory.
    /// </summary>
    [Fact]
    public async Task Uninstall_GlobalCodexHook_RemovesOnlyOwnedBlockAndPreservesForeignBytes()
    {
        string home = _home;
        string hooksPath = Path.Combine(home, ".codex", "hooks.json");
        WriteFile(hooksPath, InstalledBlock(SquadHookBlockFormat.Codex, "codex", CodexForeign));
        WriteGlobalReceipt(home, blocks: [OwnedBlock("hooks.json", "codex")]);

        string installed = _files.ReadAllText(hooksPath);
        Assert.Contains("kyber-weave-arbiter hook --harness codex", installed, StringComparison.Ordinal);
        Assert.Contains("user-owned read hook", installed, StringComparison.Ordinal);

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => uninstall.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "codex" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        string after = _files.ReadAllText(hooksPath);
        Assert.DoesNotContain("kyber-weave-arbiter hook --harness codex", after, StringComparison.Ordinal);
        Assert.Contains("user-owned read hook", after, StringComparison.Ordinal);
        Assert.Contains("codex stays", after, StringComparison.Ordinal);
        Assert.Contains("custom", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Factory's global file carries a SubagentStop group with no matcher beside Pre and Post. A
    /// global uninstall removes every Squad entry, including the stop group, and leaves the
    /// operator's own stop hook in place.
    /// </summary>
    [Fact]
    public async Task Uninstall_GlobalFactoryHook_RemovesSubagentStopAndKeepsTheUsersOwn()
    {
        string home = _home;
        string hooksPath = Path.Combine(home, ".factory", "hooks.json");
        WriteFile(
            hooksPath,
            InstalledBlock(SquadHookBlockFormat.Factory, "factory", FactoryForeign, subagentStop: true));
        WriteGlobalReceipt(home, blocks: [OwnedBlock("hooks.json", "factory")]);

        string installed = _files.ReadAllText(hooksPath);
        Assert.Contains("kyber-weave-arbiter hook --harness factory", installed, StringComparison.Ordinal);
        Assert.Contains("\"SubagentStop\"", installed, StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", installed, StringComparison.Ordinal);

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => uninstall.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "factory" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        string after = _files.ReadAllText(hooksPath);
        Assert.DoesNotContain("kyber-weave-arbiter hook --harness factory", after, StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// OpenCode installs its owned shim under the target's true global root, not under the project
    /// convention it would otherwise guess from the home directory. A global uninstall must remove
    /// the actual plugin file and leave the home-owned operator file alone.
    /// </summary>
    [Fact]
    public async Task Uninstall_GlobalOpenCodePlugin_RemovesOwnedFileUnderTheRealGlobalRoot()
    {
        string home = _home;
        string globalRoot = _globalRoots.ResolveGlobalRoot(SquadTarget.OpenCode);
        string owned = Path.Combine(globalRoot, "plugins", "kyber-arbiter.ts");
        string nonOwned = Path.Combine(home, ".opencode", "plugins", "custom.ts");
        WriteFile(owned, "// kyber-arbiter shim\n");
        WriteFile(nonOwned, "// operator-owned plugin, never touched\n");
        WriteGlobalReceipt(
            home,
            files: [OwnedFile("plugins/kyber-arbiter.ts", "opencode")]);

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => uninstall.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "opencode" },
            CancellationToken.None));

        Assert.True(execution.ExitCode == 0, execution.Output);
        Assert.False(_files.FileExists(owned), "the installed OpenCode shim must be deleted under the real global root");
        Assert.True(_files.FileExists(nonOwned), "operator-owned files next to the target root must stay intact");
    }

    /// <summary>
    /// Uninstalling one global target must not disturb any other target that remains installed in the
    /// same user home. A target ledger can hold multiple global hooks; removing one should rewrite only
    /// that target's entry.
    /// </summary>
    [Fact]
    public async Task Uninstall_OneGlobalTarget_LeavesOtherGlobalTargetsInstalled()
    {
        string home = _home;
        new FileUserHostConfigStore(_files).WriteGlobalTargets(home, ["claude", "codex"]);
        string claudeSettings = Path.Combine(home, ".claude", "settings.json");
        string codexHooks = Path.Combine(home, ".codex", "hooks.json");
        WriteFile(claudeSettings, InstalledBlock(SquadHookBlockFormat.Claude, "claude", ClaudeForeign));
        WriteFile(codexHooks, InstalledBlock(SquadHookBlockFormat.Codex, "codex", CodexForeign));
        WriteGlobalReceipt(
            home,
            blocks: [OwnedBlock("settings.json", "claude"), OwnedBlock("hooks.json", "codex")]);

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => uninstall.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "claude" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        IReadOnlyList<string> targets = new FileUserHostConfigStore(_files).ReadGlobalTargets(home);
        Assert.DoesNotContain(targets, target => string.Equals(target, "claude", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(targets, target => string.Equals(target, "codex", StringComparison.OrdinalIgnoreCase));
        Assert.True(_files.FileExists(codexHooks));
    }

    /// <summary>
    /// <c>--path</c> is only legal for an explicit project uninstall. A bare global uninstall
    /// should reject it before touching anything.
    /// </summary>
    [Fact]
    public async Task Uninstall_PathWithoutProject_ExitsTwoWithScopeError()
    {
        string home = NewDir("uninstall-path-without-project");

        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Target = "claude", PathOption = home },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("--project", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--path", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scope flags are mutually exclusive: a global uninstall does not also opt into project
    /// scope. The command must fail with the install-rule id rather than silently picking one.
    /// </summary>
    [Fact]
    public async Task Uninstall_GlobalAndProject_ExitsTwoWithKwArbInstall001()
    {
        ArbiterUninstallCommand command = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Target = "claude", Global = true, Project = true },
            CancellationToken.None));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("KW-ARB-INSTALL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two block-bearing targets in one home write <c>hooks.json</c> beneath their own global
    /// roots, so the files differ even though the recorded paths do not. Uninstalling one target
    /// must remove that target's block and leave the other's file and receipt entry intact: the
    /// receipt is the ownership record, and a removal that recomputed the path instead of reading
    /// it would look for the block where it is not.
    /// </summary>
    [Fact]
    public async Task Uninstall_GlobalCodex_RemovesOnlyTheCodexBlockAndKeepsTheCursorBlock()
    {
        string home = _home;
        string cursorHooks = Path.Combine(home, ".cursor", "hooks.json");
        string codexHooks = Path.Combine(home, ".codex", "hooks.json");
        WriteFile(cursorHooks, InstalledBlock(SquadHookBlockFormat.Cursor, "cursor", CodexForeign));
        WriteFile(codexHooks, InstalledBlock(SquadHookBlockFormat.Codex, "codex", CodexForeign));
        WriteGlobalReceipt(
            home,
            blocks: [OwnedBlock("hooks.json", "cursor"), OwnedBlock("hooks.json", "codex")]);

        Assert.Equal(
            ["codex", "cursor"],
            ReadGlobalReceipt(home).Blocks.Select(block => block.Target).Order(StringComparer.Ordinal).ToArray());

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = await CaptureAsync(() => uninstall.ExecuteAsync(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "codex" },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(
            "kyber-weave-arbiter hook --harness cursor",
            _files.ReadAllText(cursorHooks),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "kyber-weave-arbiter hook --harness codex",
            _files.ReadAllText(codexHooks),
            StringComparison.Ordinal);

        SquadOwnedBlock remaining = Assert.Single(ReadGlobalReceipt(home).Blocks);
        Assert.Equal("cursor", remaining.Target);
        Assert.Equal("hooks.json", remaining.RelativePath);
    }

    /// <summary>Writes the operator content with the Squad block the installer splices beside it.</summary>
    private static string InstalledBlock(
        SquadHookBlockFormat format,
        string harness,
        string existing,
        bool subagentStop = false)
    {
        JsonNode read = JsonNode.Parse(
            $$"""{"matcher":"^Read$","hooks":[{"type":"command","command":"kyber-weave-arbiter hook --harness {{harness}}"}]}""")!;
        JsonNode task = JsonNode.Parse(
            $$"""{"matcher":"^Task$","hooks":[{"type":"command","command":"kyber-weave-arbiter hook --harness {{harness}}"}]}""")!;
        IReadOnlyList<JsonNode>? stop = subagentStop ? [read] : null;
        return SquadHookJsonBlock.SpliceContent(format, existing, [read], [task], stop).Content;
    }

    private static SquadOwnedFile OwnedFile(string relativePath, string target) =>
        new(relativePath, new string('0', 64), target, Adopted: false);

    private static SquadOwnedBlock OwnedBlock(string relativePath, string target) =>
        new(relativePath, target, CreatedFile: false, []);

    private void WriteProjectReceipt(string root, params SquadOwnedFile[] files)
    {
        SquadReceipt receipt = new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            root,
            DateTimeOffset.UnixEpoch,
            [],
            files);
        WriteFile(_state.Store.ResolveReceiptPath(root, SquadDeploymentScope.Project), _state.Store.SerializeReceipt(receipt));
    }

    private void WriteGlobalReceipt(
        string root,
        IReadOnlyList<SquadOwnedFile>? files = null,
        IReadOnlyList<SquadOwnedBlock>? blocks = null)
    {
        SquadOwnedBlock[] ownedBlocks = [.. blocks ?? []];
        SquadReceipt receipt = new(
            ownedBlocks.Length > 0 ? "kyber-squad.receipt/v3" : "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            root,
            DateTimeOffset.UnixEpoch,
            [],
            files ?? [])
        {
            Layout = SquadReceiptLayout.PerTargetRoots,
            Blocks = ownedBlocks
        };
        WriteFile(_state.Store.ResolveReceiptPath(root, SquadDeploymentScope.Global), _state.Store.SerializeReceipt(receipt));
    }

    private SquadReceipt ReadGlobalReceipt(string home) =>
        Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(home, SquadDeploymentScope.Global));

    private void WriteFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _files.CreateDirectory(directory);
        }

        _files.WriteAllText(path, content);
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_work, name);
        _files.CreateDirectory(dir);
        return dir;
    }

    private async Task<CommandExecution> CaptureAsync(Func<Task<int>> execute)
    {
        int exitCode = await execute();
        return new CommandExecution(exitCode, _console.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
