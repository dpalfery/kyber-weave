using KyberWeave.Testing.Squad;
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
/// data directory and global root is in the in-memory file system, and the process working directory is never changed.
/// </remarks>
public sealed class SquadLegacySingleRootCliTests
{
    private const string OwnedRelativePath = ".codex/agents/alpha.toml";
    private const string LegacyVersion = "1.2.3";
    private const string UpdatedVersion = "1.2.4";

    private static readonly DateTimeOffset InstalledAt = new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);
    private static readonly byte[] OwnedBytes = Encoding.UTF8.GetBytes("alpha\n");

    /// <summary>
    /// The first status run after upgrade and a later one, both launched from a directory that is not the bound
    /// root, must verify the owned file beneath the bound root and must write nothing beneath the launch directory.
    /// </summary>
    [Fact]
    public void StatusFromAnotherDirectoryVerifiesTheLegacyFileBeneathItsBindingRootOnEveryRun()
    {
        Fixture f = Arrange();
        string elsewhere = f.MakeDirectory("elsewhere");
        IReadOnlyDictionary<string, SquadTreeEntry> elsewhereBefore = f.Capture(elsewhere);

        CommandExecution firstRun = Capture(() =>
            f.Status().Execute(null!, new SquadStatusSettings { Path = elsewhere, Global = true }));
        CommandExecution laterRun = Capture(() =>
            f.Status().Execute(null!, new SquadStatusSettings { Path = elsewhere, Global = true }));

        AssertExit(firstRun, 0);
        AssertExit(laterRun, 0);
        Assert.Contains("ok", LineContaining(firstRun.Output, OwnedRelativePath), StringComparison.Ordinal);
        Assert.Contains("ok", LineContaining(laterRun.Output, OwnedRelativePath), StringComparison.Ordinal);
        Assert.DoesNotContain("Drift or missing files detected", firstRun.Output, StringComparison.Ordinal);
        Assert.Null(SquadTreeComparison.Difference(elsewhereBefore, f.Capture(elsewhere)));
        Assert.Equal(OwnedBytes, f.State.Files.ReadAllBytes(OwnedPath(f.Binding)));
    }

    /// <summary>
    /// A global uninstall launched from another directory must remove the legacy file it owns beneath the bound
    /// root and the global receipt, and must leave the launch directory as it was.
    /// </summary>
    [Fact]
    public void UninstallFromAnotherDirectoryRemovesTheLegacyFileBeneathItsBindingRoot()
    {
        Fixture f = Arrange();
        string elsewhere = f.MakeDirectory("elsewhere");
        IReadOnlyDictionary<string, SquadTreeEntry> elsewhereBefore = f.Capture(elsewhere);

        CommandExecution execution = Capture(() =>
            f.Uninstall().Execute(null!, new SquadUninstallSettings { PathOption = elsewhere, Global = true, Yes = true }));

        AssertExit(execution, 0);
        Assert.False(f.State.Files.FileExists(OwnedPath(f.Binding)), "The legacy file beneath its binding root must be removed.");
        Assert.False(f.State.Files.FileExists(HomeGlobalReceiptPath(f)), "The migrated global receipt must be removed with it.");
        Assert.Null(SquadTreeComparison.Difference(elsewhereBefore, f.Capture(elsewhere)));
    }

    /// <summary>
    /// A global update of a legacy single-root receipt is refused by design (#91). The refusal must leave the
    /// bound file and the launch directory exactly as they were, so a refused run cannot become a partial one.
    /// </summary>
    [Fact]
    public void UpdateFromAnotherDirectoryRefusesTheLegacyReceiptAndWritesNothing()
    {
        Fixture f = Arrange();
        string elsewhere = f.MakeDirectory("elsewhere");
        IReadOnlyDictionary<string, SquadTreeEntry> elsewhereBefore = f.Capture(elsewhere);

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
        Assert.Equal(OwnedBytes, f.State.Files.ReadAllBytes(OwnedPath(f.Binding)));
        Assert.Null(SquadTreeComparison.Difference(elsewhereBefore, f.Capture(elsewhere)));
    }

    #region Fixtures

    /// <summary>
    /// Seeds the pre-home layout: a single-root receipt and lock under the per-user legacy root keyed by the
    /// binding root, the one file the receipt owns beneath that root, and a registry entry that lets the
    /// migration bind the legacy receipt to that root by key. The receipt keeps the relative target root the
    /// store wrote before #91, so the binding is not supplied by the receipt itself.
    /// </summary>
    private static Fixture Arrange()
    {
        Fixture f = new();
        string binding = f.MakeDirectory("legacy-target");
        f.Binding = binding;
        f.State.Store.RegisterProject(binding, ["codex"], InstalledAt);
        f.State.Layout.WriteLegacyGlobal(binding, LegacyReceipt(), LegacyLock());

        f.State.Files.CreateDirectory(Path.GetDirectoryName(OwnedPath(binding))!);
        f.State.Files.WriteAllBytes(OwnedPath(binding), OwnedBytes);
        return f;
    }

    private static SquadReceipt LegacyReceipt() =>
        new(
            SquadStateSchemas.ReceiptSchemaV1,
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
        Path.Combine(f.State.Layout.HomeGlobalDirectory(), "squad.receipt.json");

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

    private sealed class Fixture
    {
        public Fixture()
        {
            State = new InMemorySquadState();
            Root = Path.Combine(State.Files.Root, "legacy-cli");
            State.Files.CreateDirectory(Root);
            ReleaseSource = new FakeSquadReleaseSource { Files = State.Files };
            GlobalRoots = new TempGlobalRoots(Path.Combine(Root, "global-roots"));
        }

        public InMemorySquadState State { get; }

        public string Root { get; }

        public string Binding { get; set; } = string.Empty;

        public FakeSquadReleaseSource ReleaseSource { get; }

        public TempGlobalRoots GlobalRoots { get; }

        public string MakeDirectory(string name)
        {
            string path = Path.Combine(Root, name);
            State.Files.CreateDirectory(path);
            return path;
        }

        public IReadOnlyDictionary<string, SquadTreeEntry> Capture(string directory) =>
            SquadStateTreeCapture.Capture(State.Files, directory);

        public SquadStatusCommand Status() =>
            SquadTestCommands.InMemoryStatus(State, GlobalRoots);

        public SquadUninstallCommand Uninstall() =>
            SquadTestCommands.InMemoryUninstall(State, ReleaseSource, FakeSquadRenderer.Empty(), GlobalRoots);

        public SquadUpdateCommand Update() =>
            SquadTestCommands.InMemoryUpdate(State, ReleaseSource, FakeSquadRenderer.Empty(), globalRoots: GlobalRoots);
    }

    private sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) =>
            Path.Combine(root, SquadTargetCatalog.GetToken(target));
    }

    #endregion
}
