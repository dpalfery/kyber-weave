using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T5 contract: <c>arbiter uninstall --target &lt;t&gt;</c> removes only receipt-owned
/// artifacts, honors <c>--remove-key</c>, and reports misuse without side effects.
/// RED: <c>ArbiterUninstallCommand</c> and <c>ArbiterUninstallSettings</c> do not exist yet.
/// </summary>
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
