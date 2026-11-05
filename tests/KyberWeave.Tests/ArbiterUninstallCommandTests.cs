using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T5 contract: <c>arbiter uninstall --target &lt;t&gt;</c> removes only receipt-owned
/// artifacts, honors <c>--remove-key</c>, and reports misuse without side effects.
/// RED: <c>ArbiterUninstallCommand</c> and <c>ArbiterUninstallSettings</c> do not exist yet.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterUninstallCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _home = new();
    private readonly FakeCredentialStore _store = new();
    private readonly AsyncLocal<bool> _isolated = new();

    private readonly Func<string> _savedHome;
    private readonly Func<ICredentialStore> _savedStore;
    private readonly Func<string, string?> _savedEnvironment;

    public ArbiterUninstallCommandTests()
    {
        _savedHome = ArbiterCommandComposition.HomeDirectory;
        _savedStore = ArbiterCommandComposition.CredentialStore;
        _savedEnvironment = ArbiterCommandComposition.GetEnvironmentVariable;

        ArbiterCommandComposition.HomeDirectory = () => _isolated.Value ? _home.Path : _savedHome();
        ArbiterCommandComposition.CredentialStore = () => _isolated.Value ? _store : _savedStore();
        ArbiterCommandComposition.GetEnvironmentVariable = _ => _isolated.Value ? null : _savedEnvironment(_);
    }

    public void Dispose()
    {
        ArbiterCommandComposition.HomeDirectory = _savedHome;
        ArbiterCommandComposition.CredentialStore = _savedStore;
        ArbiterCommandComposition.GetEnvironmentVariable = _savedEnvironment;
        _temp.Dispose();
        _home.Dispose();
    }

    /// <summary>
    /// The kilo uninstall removes the receipt-owned plugin file and owned blocks,
    /// and never touches a neighboring non-owned file. A bulk delete would pass
    /// the owned assertion and still destroy the operator's own plugin.
    /// </summary>
    [Fact]
    public void Uninstall_KiloTarget_RemovesOnlyReceiptOwnedPluginFile()
    {
        string host = NewDir("uninstall-owned");
        string owned = Path.Combine(host, ".kilo", "plugin", "kyber-arbiter.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        File.WriteAllText(owned, "// receipt-owned kyber-arbiter shim\n");
        string nonOwned = Path.Combine(host, ".kilo", "plugin", "custom.ts");
        File.WriteAllText(nonOwned, "// operator-owned plugin, never touched\n");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" }));

        Assert.Equal(0, execution.ExitCode);
        Assert.False(File.Exists(owned), "receipt-owned kyber-arbiter.ts must be removed");
        Assert.True(File.Exists(nonOwned), "non-owned plugin files must be left untouched");
    }

    /// <summary>
    /// Without <c>--remove-key</c> the credential-store entry survives the uninstall.
    /// Silently deleting a credential on uninstall would be hard to reverse (D6).
    /// </summary>
    [Fact]
    public void Uninstall_WithoutRemoveKey_LeavesCredentialStoreEntryUntouched()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-KEEP-7f3a9c1e";
        _store.Write(origin, key);
        string host = NewDir("uninstall-keep-key");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(key, _store.Read(origin));
    }

    /// <summary>
    /// With <c>--remove-key</c> the injected fake-store entry is deleted.
    /// </summary>
    [Fact]
    public void Uninstall_WithRemoveKey_DeletesCredentialStoreEntry()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-DROP-1a2b3c4d";
        _store.Write(origin, key);
        string host = NewDir("uninstall-drop-key");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Null(_store.Read(origin));
    }

    /// <summary>
    /// An unknown target is client input, not a crash: exit 2 naming the roster.
    /// </summary>
    [Fact]
    public void Uninstall_InvalidTarget_ExitsTwoNamingTheRoster()
    {
        string host = NewDir("uninstall-bad-target");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "hal9000" }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("hal9000", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kilo", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No deployment is a no-op: exit 0 with a "nothing to uninstall" status.
    /// </summary>
    [Fact]
    public void Uninstall_NoDeployment_ExitsZeroAsNoOp()
    {
        string host = NewDir("uninstall-empty");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("nothing to uninstall", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unreadable receipt is not an absent one. Falling back to a path-only delete would
    /// remove an operator-owned file at the same path on the strength of a receipt the tool
    /// could not read, so the command must fail and leave the file alone.
    /// </summary>
    [Fact]
    public void Uninstall_UnreadableReceipt_FailsWithoutDeletingTheOperatorFile()
    {
        string host = NewDir("uninstall-corrupt-receipt");
        string operatorFile = Path.Combine(host, ".kilo", "plugin", "kyber-arbiter.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(operatorFile)!);
        File.WriteAllText(operatorFile, "// operator-owned plugin\n");

        SquadStateStore store = NewStateStore("uninstall-corrupt-receipt-home");
        string receiptPath = store.ResolveReceiptPath(host, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, "{ this is not a receipt");

        ArbiterUninstallCommand command = new(uninstaller: null, stateStore: store);
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo" }));

        Assert.Equal(1, execution.ExitCode);
        Assert.True(
            File.Exists(operatorFile),
            "an unreadable receipt must not fall back to a path-only delete");
    }

    /// <summary>
    /// <c>--remove-key</c> needs the endpoint to know which origin it deletes. When the host
    /// configuration cannot be loaded the origin is unknown, so the key must survive instead of
    /// the command guessing the default origin.
    /// </summary>
    [Fact]
    public void Uninstall_RemoveKeyWithUnreadableConfig_FailsAndKeepsTheKey()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-BAD-CONFIG-2b3c4d5e";
        _store.Write(origin, key);
        string host = NewDir("uninstall-bad-config");
        string directory = Path.Combine(host, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), "arbiter: [unclosed\n");

        ArbiterUninstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("could not be loaded", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key, _store.Read(origin));
    }

    /// <summary>
    /// The key is stored per endpoint origin, not per target. Deleting it while another target
    /// is still deployed removes that target's auth too, so the operator is warned.
    /// </summary>
    [Fact]
    public void Uninstall_RemoveKeyWithAnotherTargetDeployed_WarnsThatTheKeyIsShared()
    {
        const string origin = "https://api.typesafe.ai";
        const string key = "TS-UNINSTALL-SHARED-3c4d5e6f";
        _store.Write(origin, key);
        string host = NewDir("uninstall-shared-key");
        SquadStateStore store = NewStateStore("uninstall-shared-key-home");
        WriteReceipt(
            store,
            host,
            [
                OwnedFile(".kilo/plugin/kyber-arbiter.ts", "kilo"),
                OwnedFile(".opencode/plugins/kyber-arbiter.ts", "opencode"),
            ]);

        ArbiterUninstallCommand command = new(uninstaller: null, stateStore: store);
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterUninstallSettings { Path = host, Target = "kilo", RemoveKey = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("shared", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_store.Read(origin));
    }

    /// <summary>
    /// The shared-key warning is best effort. A store that cannot even resolve its home throws
    /// <see cref="ArgumentException"/>; that must not escape the helper and crash <c>--remove-key</c>.
    /// </summary>
    [Fact]
    public void AnotherTargetIsDeployed_UnreadableState_ReportsNoOtherTargetWithoutThrowing()
    {
        SquadStateStore store = new(new BlankHomeUserPaths(Path.Combine(_temp.Path, "blank-home")));

        Assert.False(
            ArbiterUninstallCommand.AnotherTargetIsDeployed(_temp.Path, SquadTarget.Kilo, store));
    }

    private SquadStateStore NewStateStore(string name)
    {
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, name));
        Directory.CreateDirectory(userPaths.HomeDirectory);
        return new SquadStateStore(userPaths);
    }

    private static void WriteReceipt(
        SquadStateStore store,
        string root,
        IReadOnlyList<SquadOwnedFile> files)
    {
        string path = store.ResolveReceiptPath(root, SquadDeploymentScope.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            store.SerializeReceipt(new SquadReceipt(
                "kyber-squad.receipt/v1",
                SquadDeploymentScope.Project,
                root,
                DateTimeOffset.UtcNow,
                [],
                files)));
    }

    private static SquadOwnedFile OwnedFile(string relativePath, string target)
    {
        return new SquadOwnedFile(relativePath, new string('0', 64), target, Adopted: false);
    }

    /// <summary>A user-paths stub with no home, so any state read fails on an unresolved home.</summary>
    private sealed class BlankHomeUserPaths(string applicationDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;

        public string HomeDirectory => string.Empty;
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private CommandExecution Capture(Func<int> execute)
    {
        _isolated.Value = true;
        try
        {
            CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
            return new CommandExecution(execution.Result, execution.Output);
        }
        finally
        {
            _isolated.Value = false;
        }
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public string? Read(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            return _entries.TryGetValue(origin, out string? key) ? key : null;
        }

        public void Write(string origin, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            ArgumentException.ThrowIfNullOrEmpty(key);
            _entries[origin] = key;
        }

        public void Delete(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            _entries.Remove(origin);
        }
    }

    /// <summary>
    /// Probe for the uninstall seam behind <c>ArbiterUninstallCommand</c>: it binds
    /// the <c>IArbiterUninstaller</c> port the command resolves its collaborator through.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "The probe binds the uninstaller port so the contract names it; the command tests drive the behavior.")]
    private sealed class RecordingArbiterUninstaller : IArbiterUninstaller
    {
        public void Uninstall(string target)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target);
        }
    }
}
