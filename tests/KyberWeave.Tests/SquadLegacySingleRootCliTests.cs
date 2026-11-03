using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the CLI contract for a pre-#91 single-root global deployment. The first run after upgrade migrates
/// the receipt into the home, and every later run must act on the files beneath the root the receipt was
/// bound to, whatever directory the run starts from.
/// </summary>
/// <remarks>
/// The legacy receipt's files live beneath one recorded root, not beneath a target's global root, so a run
/// anchored anywhere else verifies or removes the wrong files. The run's directory is supplied through the
/// path argument, which is the anchor a bare run takes from the process directory. Every fake home, application
/// data directory and global root is beneath the temp directory, and the process working directory is never changed.
/// </remarks>
public sealed class SquadLegacySingleRootCliTests : IDisposable
{
    private const string OwnedRelativePath = ".codex/agents/alpha.toml";
    private const string LegacyVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    private static readonly DateTimeOffset InstalledAt = new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);
    private static readonly byte[] OwnedBytes = Encoding.UTF8.GetBytes("alpha\n");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The first status run after upgrade and a later one, both launched from a directory that is not the bound
    /// root, must verify the owned file beneath the bound root and must write nothing beneath the launch directory.
    /// </summary>
    [Fact]
    public void StatusFromAnotherDirectoryVerifiesTheLegacyFileBeneathItsBindingRootOnEveryRun()
    {
        using Fixture f = Arrange("status");
        string elsewhere = f.MakeDirectory("elsewhere");
        TreeSnapshot elsewhereBefore = DirectoryTreeSnapshot.SnapshotTree(elsewhere);

        CommandExecution firstRun = Capture(() =>
            f.Status().Execute(null!, new SquadStatusSettings { Path = elsewhere, Global = true }));
        CommandExecution laterRun = Capture(() =>
            f.Status().Execute(null!, new SquadStatusSettings { Path = elsewhere, Global = true }));

        AssertExit(firstRun, 0);
        AssertExit(laterRun, 0);
        Assert.Contains("ok", LineContaining(firstRun.Output, OwnedRelativePath), StringComparison.Ordinal);
        Assert.Contains("ok", LineContaining(laterRun.Output, OwnedRelativePath), StringComparison.Ordinal);
        Assert.DoesNotContain("Drift or missing files detected", firstRun.Output, StringComparison.Ordinal);
        DirectoryTreeSnapshot.AssertTreeUnchanged(elsewhere, elsewhereBefore);
        Assert.Equal(OwnedBytes, File.ReadAllBytes(OwnedPath(f.Binding)));
    }

    /// <summary>
    /// A global uninstall launched from another directory must remove the legacy file it owns beneath the bound
    /// root and the global receipt, and must leave the launch directory as it was.
    /// </summary>
    [Fact]
    public void UninstallFromAnotherDirectoryRemovesTheLegacyFileBeneathItsBindingRoot()
    {
        using Fixture f = Arrange("uninstall");
        string elsewhere = f.MakeDirectory("elsewhere");
        TreeSnapshot elsewhereBefore = DirectoryTreeSnapshot.SnapshotTree(elsewhere);

        CommandExecution execution = Capture(() =>
            f.Uninstall().Execute(null!, new SquadUninstallSettings { Path = elsewhere, Global = true, Yes = true }));

        AssertExit(execution, 0);
        Assert.False(File.Exists(OwnedPath(f.Binding)), "The legacy file beneath its binding root must be removed.");
        Assert.False(File.Exists(HomeGlobalReceiptPath(f)), "The migrated global receipt must be removed with it.");
        DirectoryTreeSnapshot.AssertTreeUnchanged(elsewhere, elsewhereBefore);
    }

    /// <summary>
    /// A global update of a legacy single-root receipt is refused by design (#91). The refusal must leave the
    /// bound file and the launch directory exactly as they were, so a refused run cannot become a partial one.
    /// </summary>
    [Fact]
    public void UpdateFromAnotherDirectoryRefusesTheLegacyReceiptAndWritesNothing()
    {
        using Fixture f = Arrange("update");
        string elsewhere = f.MakeDirectory("elsewhere");
        TreeSnapshot elsewhereBefore = DirectoryTreeSnapshot.SnapshotTree(elsewhere);

        CommandExecution execution = Capture(() =>
            f.Update().Execute(null!, new SquadUpdateSettings
            {
                Path = elsewhere,
                Global = true,
                Version = UpdatedVersion,
                Yes = true
            }));

        AssertExit(execution, 1);
        Assert.Contains("legacy single-root layout", execution.Output, StringComparison.Ordinal);
        Assert.Equal(OwnedBytes, File.ReadAllBytes(OwnedPath(f.Binding)));
        DirectoryTreeSnapshot.AssertTreeUnchanged(elsewhere, elsewhereBefore);
    }

    #region Fixtures

    /// <summary>
    /// Seeds the pre-home layout: a single-root receipt and lock under the per-user legacy root keyed by the
    /// binding root, the one file the receipt owns beneath that root, and a registry entry that lets the
    /// migration bind the legacy receipt to that root by key. The receipt keeps the relative target root the
    /// store wrote before #91, so the binding is not supplied by the receipt itself.
    /// </summary>
    private Fixture Arrange(string name)
    {
        Fixture f = new(Path.Combine(_temp.Path, name));
        string binding = f.MakeDirectory("legacy-target");
        f.Binding = binding;
        f.Store.RegisterProject(binding, ["codex"], InstalledAt);

        string legacyDirectory = Path.Combine(
            f.ApplicationData,
            "KyberWeave",
            "squad",
            "roots",
            SquadPhysicalRootIdentity.Resolve(binding).Key);
        Directory.CreateDirectory(legacyDirectory);
        WriteText(Path.Combine(legacyDirectory, "squad.receipt.json"), f.Store.SerializeReceipt(LegacyReceipt()));
        WriteText(Path.Combine(legacyDirectory, "squad.lock.yml"), f.Store.SerializeLock(LegacyLock()));

        Directory.CreateDirectory(Path.GetDirectoryName(OwnedPath(binding))!);
        File.WriteAllBytes(OwnedPath(binding), OwnedBytes);
        return f;
    }

    private static SquadReceipt LegacyReceipt() =>
        new(
            SquadStateStore.ReceiptSchemaV1,
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [new SquadOwnedFile(OwnedRelativePath, Convert.ToHexStringLower(SHA256.HashData(OwnedBytes)), "codex", Adopted: false)])
        {
            Layout = SquadReceiptLayout.SingleRoot
        };

    private static SquadLock LegacyLock() => new(
        Schema: "kyber-squad.lock/v1",
        SquadVersion: LegacyVersion,
        CliVersion: LegacyVersion,
        McpVersion: LegacyVersion,
        Bundle: "full",
        Targets: ["codex"],
        Exclusions: [],
        Translation: "best-effort",
        BundleDigest: "a".PadRight(64, '0'),
        AssetDigest: "b".PadRight(64, '0'),
        Apm: new SquadApmIdentity("0.28.0", "c".PadRight(40, '0'), "d".PadRight(64, '0')));

    private static string OwnedPath(string binding) =>
        Path.Combine(binding, ".codex", "agents", "alpha.toml");

    private static string HomeGlobalReceiptPath(Fixture f) =>
        Path.Combine(f.Home, ".kyber-weave", "squad", "global", "squad.receipt.json");

    private static void WriteText(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(false));

    private static string LineContaining(string output, string text)
    {
        string[] lines = output
            .Split('\n')
            .Where(line => line.Contains(text, StringComparison.Ordinal))
            .ToArray();
        Assert.True(lines.Length == 1, $"Expected exactly one line naming '{text}', found {lines.Length}:{Environment.NewLine}{output}");
        return lines[0];
    }

    private static void AssertExit(CommandExecution execution, int expected) =>
        Assert.True(
            execution.ExitCode == expected,
            $"Expected exit {expected} but got {execution.ExitCode}:{Environment.NewLine}{execution.Output}");

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class Fixture : IDisposable
    {
        public Fixture(string root)
        {
            Root = root;
            ApplicationData = Path.Combine(root, "app-data");
            Home = Path.Combine(root, "home");
            GlobalRootsDirectory = Path.Combine(root, "global-roots");
            Directory.CreateDirectory(ApplicationData);
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(GlobalRootsDirectory);
            UserPaths = new TempUserPaths(ApplicationData, Home);
            Store = new SquadStateStore(UserPaths);
            ReleaseSource = new FakeSquadReleaseSource();
            GlobalRoots = new TempGlobalRoots(GlobalRootsDirectory);
        }

        public string Root { get; }

        public string Home { get; }

        public string ApplicationData { get; }

        public string GlobalRootsDirectory { get; }

        public string Binding { get; set; } = string.Empty;

        public TempUserPaths UserPaths { get; }

        public SquadStateStore Store { get; }

        public FakeSquadReleaseSource ReleaseSource { get; }

        public TempGlobalRoots GlobalRoots { get; }

        public string MakeDirectory(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public SquadStatusCommand Status() =>
            new(userPaths: UserPaths, stateStore: Store, globalRoots: GlobalRoots);

        public SquadUninstallCommand Uninstall() => new(
            userPaths: UserPaths,
            stateStore: Store,
            lifecycleService: new SquadLifecycleService(ReleaseSource, new EmptyRenderer(), Store, globalRoots: GlobalRoots),
            isInteractive: false,
            readAnswer: null,
            globalRoots: GlobalRoots);

        public SquadUpdateCommand Update() => new(
            userPaths: UserPaths,
            stateStore: Store,
            releaseSource: ReleaseSource,
            renderer: new EmptyRenderer(),
            isInteractive: false,
            readAnswer: _ => true,
            globalRoots: GlobalRoots);

        public void Dispose() => ReleaseSource.Dispose();
    }

    private sealed class TempUserPaths : ISquadUserPaths
    {
        public TempUserPaths(string applicationDataDirectory, string homeDirectory)
        {
            RequireUnderTemp(applicationDataDirectory);
            RequireUnderTemp(homeDirectory);
            ApplicationDataDirectory = applicationDataDirectory;
            HomeDirectory = homeDirectory;
        }

        public string ApplicationDataDirectory { get; }

        public string HomeDirectory { get; }
    }

    private sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) =>
            Path.Combine(root, SquadTargetCatalog.GetToken(target));
    }

    /// <summary>Renders nothing. The paths under test never reach a render: uninstall plans from the receipt, and update is refused first.</summary>
    private sealed class EmptyRenderer : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SquadRenderResult(
                Success: true,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors: []));
    }

    private static void RequireUnderTemp(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Squad CLI tests must keep every fake home and state root beneath the temp directory; got '{full}'.");
        }
    }

    #endregion
}
