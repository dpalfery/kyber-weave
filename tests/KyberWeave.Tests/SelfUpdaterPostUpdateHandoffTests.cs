using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The hand-off that finishes an update in the new binary. The old process probes its own image once before the
/// first commit, then, after every binary is committed, starts the installed kyber-weave as
/// <c>update &lt;version&gt; --finish-update</c>. The KyberDash, tray and arbiter steps run in that child, because
/// the old image cannot load the arbiter code it has not loaded yet.
/// </summary>
/// <remarks>
/// The seam is the constructor parameter <c>finishingProcess</c>. Every test here fakes it, so no test starts a real
/// process. Where a test wants the child's real finish-mode work, it uses <see cref="LoopbackFinishingProcess" />.
/// </remarks>
public sealed class SelfUpdaterPostUpdateHandoffTests : IDisposable
{
    private static readonly ProcessResult QuietSuccess = new(0, string.Empty, string.Empty);

    private readonly TempDirectory _install = new();
    private readonly TempDirectory _assets = new();

    public void Dispose()
    {
        _install.Dispose();
        _assets.Dispose();
    }

    /// <summary>
    /// The hand-off is a real process start, so it runs once, for the update's own release, after every binary is in
    /// place. The updating process does not run KyberDash or the arbiter refresh itself any more: a refresh inside the
    /// old image is what crashed with exit 1, and the child does both.
    /// </summary>
    [Fact]
    public void ReleaseChangedUpdateHandsOffToTheInstalledBinaryAfterEveryCommit()
    {
        InstallKyberDash();
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: true, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(handler, host, new SelfUpdateOptions("0.2.0", false, false), record);

        SeamCall handOff = Assert.Single(
            record.SeamCalls,
            call => call.Arguments.Count > 0 && call.Arguments[0] == "update");
        Assert.Equal(host.ProcessPath, handOff.File);
        Assert.Equal<string>(["update", "0.2.0", "--finish-update"], handOff.Arguments);
        Assert.Equal("new-cli", handOff.InstalledBytes);
        // Kyber-weave, the MCP server, KyberDash and the arbiter are all committed before the hand-off.
        Assert.Equal(4, handOff.InstalledLinesBefore);
        Assert.DoesNotContain(record.Verbs, verb => !verb.InHandOff);
        Assert.Equal(0, record.ParentRefreshes);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal($"updated kyber-weave 0.2.0 → {host.ProcessPath}", outcome.Message);
    }

    /// <summary>
    /// The <c>--no-arbiter</c> opt-out must reach the finishing child, which then skips the arbiter hook refresh. Without
    /// the forwarding line the child re-renders hooks the owner opted out of, and no other test sees it.
    /// </summary>
    [Fact]
    public void NoArbiterOptOutReachesTheFinishingProcessAndTheChildRefreshesNoHooks()
    {
        InstallKyberDash();
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: true, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();
        TrackingRefresher refresher = new(record);

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, NoArbiter: true),
            record,
            refresher: refresher);

        SeamCall handOff = Assert.Single(
            record.SeamCalls,
            call => call.Arguments.Count > 0 && call.Arguments[0] == "update");
        Assert.Equal(host.ProcessPath, handOff.File);
        Assert.Equal<string>(["update", "0.2.0", "--finish-update", "--no-arbiter"], handOff.Arguments);
        Assert.Single(handOff.Arguments, argument => argument == "--no-arbiter");
        Assert.DoesNotContain("--no-mcp", handOff.Arguments);
        Assert.DoesNotContain("--release-candidate", handOff.Arguments);
        Assert.Empty(record.Refreshes);
        Assert.Equal(0, outcome.ExitCode);
    }

    /// <summary>
    /// The control for the opt-out above: with no <c>NoArbiter</c>, the hand-off does not carry <c>--no-arbiter</c>, and
    /// the finishing child runs the arbiter hook refresh once, inside the hand-off. This proves the refresh the opt-out
    /// suppresses is real and reachable.
    /// </summary>
    [Fact]
    public void WithoutNoArbiterTheHandOffCarriesNoOptOutAndTheChildRefreshesArbiterHooksOnce()
    {
        InstallKyberDash();
        InstallArbiter();
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: true, withArbiter: true);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();
        TrackingRefresher refresher = new(record);

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false),
            record,
            refresher: refresher);

        SeamCall handOff = Assert.Single(
            record.SeamCalls,
            call => call.Arguments.Count > 0 && call.Arguments[0] == "update");
        Assert.Equal<string>(["update", "0.2.0", "--finish-update"], handOff.Arguments);
        Assert.DoesNotContain("--no-arbiter", handOff.Arguments);
        bool onlyRefreshInsideHandOff = Assert.Single(record.Refreshes);
        Assert.True(onlyRefreshInsideHandOff);
        Assert.Equal(0, outcome.ExitCode);
    }

    /// <summary>
    /// Before the first commit the old image rehearses the hand-off once, with <c>--version</c> and its output
    /// discarded, so a broken installed binary is found before anything is replaced. The result is ignored.
    /// </summary>
    [Fact]
    public void ExactlyOneEarlierSeamCallProbesTheOldImageBeforeAnyInstalledFileChanges()
    {
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        RunUpdate(handler, host, new SelfUpdateOptions("0.2.0", false, false), record);

        SeamCall probe = Assert.Single(
            record.SeamCalls,
            call => call.Arguments.Count == 1 && call.Arguments[0] == "--version");
        Assert.Same(probe, record.SeamCalls[0]);
        Assert.Equal(host.ProcessPath, probe.File);
        Assert.True(probe.DiscardOutput);
        Assert.Equal("old-cli", probe.InstalledBytes);
        Assert.Equal(0, probe.InstalledLinesBefore);
    }

    /// <summary>
    /// The binaries are already committed when the hand-off fails, so the message says so and gives the one command
    /// that finishes the job. Nothing is rolled back.
    /// </summary>
    [Fact]
    public void HandOffThatExitsOneReportsTheBinariesInstalledAndTheRetryCommand()
    {
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false),
            record,
            handOff: (_, arguments, _) => arguments[0] == "update" ? 1 : 0);

        Assert.Single(record.SeamCalls, call => call.Arguments[0] == "update");
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("installed", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("kyber-weave update 0.2.0", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
    }

    /// <summary>
    /// With forwarded opt-outs the retry command has to carry them, or the retry re-runs KyberDash and the arbiter
    /// refresh the owner explicitly opted out of. The existing test above proves the version still appears; this one
    /// proves the flags do too.
    /// </summary>
    [Fact]
    public void HandOffThatExitsOneCarriesTheForwardedOptOutsInTheRetryCommand()
    {
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, NoArbiter: true, NoRestart: true),
            record,
            handOff: (_, arguments, _) => arguments[0] == "update" ? 1 : 0);

        Assert.Single(record.SeamCalls, call => call.Arguments[0] == "update");
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(
            "kyber-weave update 0.2.0 --no-arbiter --no-restart",
            outcome.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--no-kyberdash", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("installed", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
    }

    /// <summary>
    /// A hand-off that cannot start (here, Windows refusing to launch the new image) is an honest failure: the cause
    /// and the retry command, never the bare exception text, and never the project registry, which it did not touch.
    /// </summary>
    [Fact]
    public void HandOffThatCannotStartReportsTheCauseAndTheRetryCommand()
    {
        Win32Exception failure = new(5, "Access is denied.");
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false),
            record,
            handOff: (_, arguments, _) => ThrowOnHandOff(failure, arguments));

        Assert.Single(record.SeamCalls, call => call.Arguments[0] == "update");
        AssertHonestHandOffFailure(outcome, host, failure.Message, failure.Message);
    }

    /// <summary>
    /// A new binary that cannot load one of its own components (here, a text-encoding assembly it was built against)
    /// is the same honest failure. The cause named is the component, not the registry.
    /// </summary>
    [Fact]
    public void HandOffThatFailsToLoadAComponentReportsTheCauseAndTheRetryCommand()
    {
        FileNotFoundException failure = new(
            "Could not load file or assembly",
            "System.Text.Encoding.Extensions, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false),
            record,
            handOff: (_, arguments, _) => ThrowOnHandOff(failure, arguments));

        Assert.Single(record.SeamCalls, call => call.Arguments[0] == "update");
        AssertHonestHandOffFailure(outcome, host, "System.Text.Encoding.Extensions", failure.Message);
    }

    /// <summary>
    /// The tree-kill of a hung child can throw <see cref="AggregateException"/> (its per-process results are
    /// collected into one). The hand-off's catch must translate it into the honest installed-plus-retry message,
    /// never the bare wrapper text, and never a registry-failure claim.
    /// </summary>
    [Fact]
    public void HandOffThatThrowsAnAggregateReportsTheBinariesInstalledAndTheRetryCommand()
    {
        AggregateException failure = new(
            "one or more tree members refused to terminate",
            new InvalidOperationException("Process 1234 still running after Kill."));
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false),
            record,
            handOff: (_, arguments, _) => ThrowOnHandOff(failure, arguments));

        Assert.Single(record.SeamCalls, call => call.Arguments[0] == "update");
        AssertHonestHandOffFailure(outcome, host, failure.Message, failure.Message);
    }

    /// <summary>
    /// Asking for the version this binary already runs does not hand off. Both steps run in-process, the arbiter
    /// refresh runs once, and the message is the existing "already on" line.
    /// </summary>
    /// <remarks>Passes today: the already-on path has always run both steps in-process.</remarks>
    [Fact]
    public void AlreadyOnTheRequestedVersionRunsBothStepsInProcessAndNeverHandsOff()
    {
        InstallKyberDash();
        InstallArbiter();
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(handler, host, new SelfUpdateOptions("0.2.0", false, false), record);

        Assert.Empty(record.SeamCalls);
        Assert.Contains(record.Verbs, verb => verb.Arguments.Count > 0 && verb.Arguments[0] == "engine");
        Assert.Equal(1, record.ParentRefreshes);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("already on 0.2.0", outcome.Message);
    }

    /// <summary>
    /// The child that finish mode runs downloads and commits nothing. It runs the KyberDash engine verb, the tray (even
    /// when the recorded tray version cannot be read, because the binaries just moved), and the arbiter refresh once,
    /// then prints no outcome line.
    /// </summary>
    [Fact]
    public void FinishUpdateOnTheInstalledVersionRunsTheStepsWithoutDownloadingOrPrintingAnOutcome()
    {
        InstallKyberDash();
        InstallArbiter();
        WriteTrayRecord("""{"path":"/Applications/KyberDash.app"}""");
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record);

        Assert.Empty(handler.Uris);
        Assert.Empty(record.SeamCalls);
        Assert.Contains(record.Verbs, verb => verb.Arguments.Count > 0 && verb.Arguments[0] == "engine");
        SpawnedCall tray = Assert.Single(record.Spawned);
        Assert.Equal<string>(["menubar", "--update", "--version", "0.2.0"], tray.Arguments);
        Assert.Single(record.Refreshes);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(string.Empty, outcome.Message);
    }

    /// <summary>
    /// Finish mode is only for the binary the update just installed. Given a different version it refuses before it
    /// downloads or runs anything, and it names both versions so the owner can see which one is wrong.
    /// </summary>
    [Fact]
    public void FinishUpdateWithAHostVersionThatDiffersRefusesAndRunsNothing()
    {
        using MapHandler handler = MapRelease("0.2.0", withKyberDash: false, withArbiter: false);
        SelfUpdateHost host = CreateHost("0.1.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("0.1.0", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("0.2.0", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Uris);
        Assert.Empty(record.Verbs);
        Assert.Empty(record.Spawned);
        Assert.Empty(record.Refreshes);
        Assert.Equal("old-cli", File.ReadAllText(host.ProcessPath));
    }

    /// <summary>
    /// Finish mode has no release to look up. Without a version it refuses with the usage line, before any request.
    /// </summary>
    [Fact]
    public void FinishUpdateWithoutAVersionRefusesWithAUsageHintAndNeverLooksUpARelease()
    {
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions(null, false, false, FinishUpdate: true),
            record);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("kyber-weave update <version>", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Uris);
    }

    /// <summary>
    /// When the arbiter refresh cannot load a component (a YAML library, here) in finish mode, the binaries are already
    /// updated, so this is a warning with exit 0. It names what was not done, the component, and the retry command. It
    /// never says the project registry could not be read, because that was not the failure.
    /// </summary>
    [Fact]
    public void FinishUpdateWarnsThatArbiterHooksWereNotRefreshedWhenAComponentCannotLoad()
    {
        InstallArbiter();
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();
        FileNotFoundException missing = new(
            "Could not load file or assembly 'YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e'.",
            "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e");

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record,
            refresher: new TrackingRefresher(record, missing));

        Assert.Equal(0, outcome.ExitCode);
        string warning = Assert.Single(
            record.Log,
            line => line.Contains("arbiter hooks were not refreshed", StringComparison.Ordinal));
        Assert.Contains("program component could not be loaded", warning, StringComparison.Ordinal);
        Assert.Contains("YamlDotNet", warning, StringComparison.Ordinal);
        Assert.Contains("binaries are already updated", warning, StringComparison.Ordinal);
        Assert.Contains("kyber-weave update 0.2.0", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Log, line => line.Contains("project registry", StringComparison.Ordinal));
    }

    /// <summary>A file that exists but cannot be loaded is a component failure, the same as a missing one.</summary>
    [Fact]
    public void ArbiterRefreshClassifiesAFileLoadFailureAsAComponentLoadFailure()
    {
        FileLoadException failure = new(
            "Could not load file or assembly 'YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e'.",
            "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e");

        HandOffRecord record = RunFinishWithRefresherFailure(failure);

        AssertComponentLoadWarning(record);
    }

    /// <summary>A binary built for another runtime is a component failure too.</summary>
    [Fact]
    public void ArbiterRefreshClassifiesABadImageFormatAsAComponentLoadFailure()
    {
        BadImageFormatException failure = new(
            "An attempt was made to load a program with an incorrect format.",
            "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e");

        HandOffRecord record = RunFinishWithRefresherFailure(failure);

        AssertComponentLoadWarning(record);
    }

    /// <summary>
    /// A missing file that is an ordinary path (a config file, say) is not a component the program failed to load, so
    /// it keeps the generic warning.
    /// </summary>
    /// <remarks>Passes today: nothing classifies a component load yet, so every refresh failure is the generic warning.</remarks>
    [Fact]
    public void ArbiterRefreshDoesNotClassifyAPlainPathNotFoundAsAComponentLoadFailure()
    {
        FileNotFoundException failure = new(
            "Could not find file '/x/arbiter.yml'.",
            "/x/arbiter.yml");

        HandOffRecord record = RunFinishWithRefresherFailure(failure);

        Assert.DoesNotContain(
            record.Log,
            line => line.Contains("program component could not be loaded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A corrupt project registry is reported as such, and finish mode still exits 0 because the binaries are updated.
    /// </summary>
    /// <remarks>Passes today: the registry read has always been reported as its own warning.</remarks>
    [Fact]
    public void FinishUpdateWarnsThatTheProjectRegistryCouldNotBeReadWhenItIsCorrupt()
    {
        InstallArbiter();
        FakeSquadUserPaths userPaths = new(Path.Combine(_install.Path, "corrupt-home"));
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);
        string stateDirectory = store.ResolveHomeStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, "registry.json"), "{ this is not a registry");
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record,
            environment: HomeAndAppData(userPaths));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(
            record.Log,
            line => line.Contains("project registry could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// A corrupt project registry leaves no project to refresh, but the global targets do not depend on it. The global
    /// hook is still rewritten, and the registry is the one thing named as not refreshed.
    /// </summary>
    [Fact]
    public void FinishUpdateWithACorruptRegistryStillRefreshesTheGlobalHook()
    {
        InstallArbiter();
        FakeSquadUserPaths userPaths = new(Path.Combine(_install.Path, "corrupt-with-globals"));
        Directory.CreateDirectory(userPaths.HomeDirectory);
        SquadStateStore store = new(userPaths);
        string stateDirectory = store.ResolveHomeStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(Path.Combine(stateDirectory, "registry.json"), "{ this is not a registry");
        UserHostConfig.WriteGlobalTargets(userPaths.HomeDirectory, ["cursor"]);
        string hookPath = WriteLegacyCursorHook(userPaths.HomeDirectory, "/legacy/kyber-weave-arbiter hook");
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();
        Func<string, string?> environment = HomeAndAppData(userPaths);

        SelfUpdateOutcome outcome = RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record,
            environment: environment,
            refresher: new ArbiterHookRefresher(record.Log.Add, environment));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(
            record.Log,
            line => line.Contains("project registry could not be read", StringComparison.Ordinal));
        string content = File.ReadAllText(hookPath);
        Assert.Contains("kyber-weave-arbiter hook --harness cursor --scope global", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/legacy/kyber-weave-arbiter hook", content, StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>Writes a Cursor global hook file that still names the old arbiter command, and returns its path.</summary>
    private static string WriteLegacyCursorHook(string home, string legacyCommand)
    {
        string hookPath = Path.Combine(
            new SquadGlobalRoots(_ => null, home).ResolveGlobalRoot(SquadTarget.Cursor),
            "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        string token = SquadTargetCatalog.GetToken(SquadTarget.Cursor);
        string content = $$"""
            {
              "preToolUse": [
                {
                  "command": "{{legacyCommand}} --harness {{token}} --scope global",
                  "matcher": "Task",
                  "timeout": 5,
                  "failClosed": true
                }
              ],
              "postToolUse": []
            }
            """;
        File.WriteAllText(hookPath, content);
        return hookPath;
    }

    /// <summary>One seam call: what the old process asked for, and what the installed file held at that moment.</summary>
    private sealed record SeamCall(
        string File,
        IReadOnlyList<string> Arguments,
        bool DiscardOutput,
        string InstalledBytes,
        int InstalledLinesBefore);

    /// <summary>One process or capture the updater started, and whether it started inside the hand-off.</summary>
    private sealed record SpawnedCall(string File, IReadOnlyList<string> Arguments, bool InHandOff);

    /// <summary>Everything the fakes saw. <see cref="InHandOff"/> is true only while the seam is running the child.</summary>
    private sealed class HandOffRecord
    {
        public bool InHandOff { get; set; }

        public List<string> Log { get; } = [];

        public List<SeamCall> SeamCalls { get; } = [];

        public List<SpawnedCall> Verbs { get; } = [];

        public List<SpawnedCall> Spawned { get; } = [];

        /// <summary>One entry per arbiter refresh; true when the refresh ran inside the hand-off.</summary>
        public List<bool> Refreshes { get; } = [];

        public int ParentRefreshes => Refreshes.Count(inHandOff => !inHandOff);
    }

    /// <summary>
    /// Wraps the hand-off seam so each call records the installed bytes and how many commit lines were logged before
    /// it, and so the child's own captures and refreshes are marked as inside the hand-off.
    /// </summary>
    private sealed class SeamProbe(HandOffRecord record, Func<string, IReadOnlyList<string>, bool, int> handOff)
    {
        public int Invoke(string fileName, IReadOnlyList<string> arguments, bool discardOutput)
        {
            int installedLines = record.Log.Count(line => line.StartsWith("installed ", StringComparison.Ordinal));
            record.SeamCalls.Add(new SeamCall(
                fileName,
                arguments.ToArray(),
                discardOutput,
                File.ReadAllText(fileName),
                installedLines));

            bool previous = record.InHandOff;
            record.InHandOff = true;
            try
            {
                return handOff(fileName, arguments, discardOutput);
            }
            finally
            {
                record.InHandOff = previous;
            }
        }
    }

    private sealed class TrackingRefresher(HandOffRecord record, Exception? failure = null) : IArbiterHookRefresher
    {
        public ArbiterHookRefreshResult Refresh(IReadOnlyList<string> projectRoots)
        {
            ArgumentNullException.ThrowIfNull(projectRoots);
            record.Refreshes.Add(record.InHandOff);
            if (failure is not null)
                throw failure;

            return new ArbiterHookRefreshResult(projectRoots.Count, Failed: 0);
        }
    }

    /// <summary>
    /// Runs one update through the seam. The default hand-off is the loopback child; a test that needs a different
    /// result passes its own <paramref name="handOff"/>. Every capture, spawn and refresh is recorded.
    /// </summary>
    private SelfUpdateOutcome RunUpdate(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        SelfUpdateOptions options,
        HandOffRecord record,
        Func<string, IReadOnlyList<string>, bool, int>? handOff = null,
        Func<string, string?>? environment = null,
        IArbiterHookRefresher? refresher = null)
    {
        Func<string, string?> readEnvironment = environment ?? HomeAndAppData(_install.Path);
        IArbiterHookRefresher arbiter = refresher ?? new TrackingRefresher(record);
        Func<string, IReadOnlyList<string>, ProcessResult> capture = (file, args) =>
        {
            record.Verbs.Add(new SpawnedCall(file, args.ToArray(), record.InHandOff));
            return QuietSuccess;
        };
        Func<string, IReadOnlyList<string>, int> spawn = (file, args) =>
        {
            record.Spawned.Add(new SpawnedCall(file, args.ToArray(), record.InHandOff));
            return 0;
        };

        LoopbackFinishingProcess loopback = new(
            handler,
            host,
            record.Log.Add,
            readEnvironment,
            spawn,
            captureProcess: capture,
            arbiterHookRefresher: arbiter);
        Func<string, IReadOnlyList<string>, bool, int> loopbackSeam = loopback.Invoke;
        SeamProbe probe = new(record, handOff ?? loopbackSeam);

        using SelfUpdater updater = new(
            handler,
            host,
            record.Log.Add,
            readEnvironment,
            spawn,
            captureProcess: capture,
            arbiterHookRefresher: arbiter,
            finishingProcess: probe.Invoke);
        return updater.Run(options);
    }

    /// <summary>Runs a finish-mode update whose arbiter refresh throws the given failure, and returns what was recorded.</summary>
    private HandOffRecord RunFinishWithRefresherFailure(Exception failure)
    {
        InstallArbiter();
        using MapHandler handler = new();
        SelfUpdateHost host = CreateHost("0.2.0");
        HandOffRecord record = new();

        RunUpdate(
            handler,
            host,
            new SelfUpdateOptions("0.2.0", false, false, FinishUpdate: true),
            record,
            refresher: new TrackingRefresher(record, failure));
        return record;
    }

    private static void AssertComponentLoadWarning(HandOffRecord record)
    {
        string warning = Assert.Single(
            record.Log,
            line => line.Contains("arbiter hooks were not refreshed", StringComparison.Ordinal));
        Assert.Contains("program component could not be loaded", warning, StringComparison.Ordinal);
        Assert.Contains("kyber-weave update 0.2.0", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(record.Log, line => line.Contains("project registry", StringComparison.Ordinal));
    }

    /// <summary>The honest hand-off failure: exit 1, the binaries named as installed, the retry command, and no raw text as the whole message.</summary>
    private void AssertHonestHandOffFailure(
        SelfUpdateOutcome outcome,
        SelfUpdateHost host,
        string causeFragment,
        string rawText)
    {
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("installed", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("kyber-weave update 0.2.0", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(causeFragment, outcome.Message, StringComparison.Ordinal);
        Assert.NotEqual(rawText, outcome.Message);
        Assert.DoesNotContain("project registry", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("new-cli", File.ReadAllText(host.ProcessPath));
    }

    /// <summary>A hand-off fake that throws the given failure for the real hand-off and answers the rehearsal with 0.</summary>
    private static int ThrowOnHandOff(Exception failure, IReadOnlyList<string> arguments)
    {
        if (arguments[0] == "update")
            throw failure;

        return 0;
    }

    private static Func<string, string?> HomeAndAppData(string home) => name => name switch
    {
        "HOME" => home,
        "USERPROFILE" => home,
        "APPDATA" => Path.Combine(home, ".config"),
        _ => null
    };

    private static Func<string, string?> HomeAndAppData(FakeSquadUserPaths userPaths) => name => name switch
    {
        "HOME" => userPaths.HomeDirectory,
        "APPDATA" => userPaths.ApplicationDataDirectory,
        _ => null
    };

    /// <summary>Writes the tray record under the same home the updater reads, so the tray step has a record to find.</summary>
    private void WriteTrayRecord(string json)
    {
        string config = Path.Combine(_install.Path, ".kyberdash");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "tray.json"), json);
    }

    private SelfUpdateHost CreateHost(string currentVersion)
    {
        string path = Path.Combine(_install.Path, BinaryInstaller.InstalledFileName("kyber-weave", windows: false));
        File.WriteAllText(path, "old-cli");
        return new SelfUpdateHost(path, currentVersion, "osx-arm64", false, false);
    }

    /// <summary>Places an already-installed kyberdash beside the CLI.</summary>
    private void InstallKyberDash()
    {
        File.WriteAllText(
            Path.Combine(_install.Path, BinaryInstaller.InstalledFileName("kyberdash", windows: false)),
            "old-kyberdash");
    }

    /// <summary>Places an already-installed arbiter beside the CLI.</summary>
    private void InstallArbiter()
    {
        File.WriteAllText(
            Path.Combine(_install.Path, BinaryInstaller.InstalledFileName("kyber-weave-arbiter", windows: false)),
            "old-arbiter");
    }

    /// <summary>A macOS release for this host's RID, with the assets an update downloads and checks against SHA256SUMS.</summary>
    private MapHandler MapRelease(string version, bool withKyberDash, bool withArbiter)
    {
        MapHandler handler = new();
        string tag = "v" + version;
        string cliName = BinaryInstaller.ArchiveName("kyber-weave", "osx-arm64");
        string mcpName = BinaryInstaller.ArchiveName("kyber-weave-mcp", "osx-arm64");
        byte[] cliBytes = WriteAsset(cliName, "kyber-weave", "new-cli");
        byte[] mcpBytes = WriteAsset(mcpName, "kyber-weave-mcp", "new-mcp");
        string sums = $"{Sha(cliBytes)}  {cliName}\n{Sha(mcpBytes)}  {mcpName}\n";
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, cliName).AbsoluteUri, cliBytes);
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, mcpName).AbsoluteUri, mcpBytes);

        if (withKyberDash)
        {
            string dashName = BinaryInstaller.ArchiveName("kyberdash", PlatformRid.KyberDashRid("osx-arm64"));
            byte[] dashBytes = WriteAsset(dashName, "kyberdash", "new-kyberdash");
            sums += $"{Sha(dashBytes)}  {dashName}\n";
            handler.MapFile(GitHubReleaseClient.AssetUri(tag, dashName).AbsoluteUri, dashBytes);
        }

        if (withArbiter)
        {
            string arbiterName = BinaryInstaller.ArchiveName("kyber-weave-arbiter", "osx-arm64");
            byte[] arbiterBytes = WriteAsset(arbiterName, "kyber-weave-arbiter", "new-arbiter");
            sums += $"{Sha(arbiterBytes)}  {arbiterName}\n";
            handler.MapFile(GitHubReleaseClient.AssetUri(tag, arbiterName).AbsoluteUri, arbiterBytes);
        }

        handler.MapFile(
            GitHubReleaseClient.AssetUri(tag, "SHA256SUMS.txt").AbsoluteUri,
            Encoding.UTF8.GetBytes(sums));
        return handler;
    }

    private byte[] WriteAsset(string archiveName, string entryName, string content)
    {
        string archivePath = Path.Combine(_assets.Path, archiveName);
        using (FileStream file = File.Create(archivePath))
        using (GZipStream gzip = new(file, CompressionLevel.Fastest))
        using (TarWriter writer = new(gzip))
        using (MemoryStream data = new(Encoding.UTF8.GetBytes(content)))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = data });
        }

        return File.ReadAllBytes(archivePath);
    }

    // reason: the digests in SHA256SUMS.txt are lowercase hex by sha256sum convention, so the test must emit lowercase.
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification =
            "SHA256SUMS.txt and the digests it is compared against are lowercase hex by sha256sum convention; ToUpperInvariant would not match the published format.")]
    private static string Sha(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);

        public List<string> Uris { get; } = [];

        public void MapFile(string uri, byte[] bytes)
        {
            _map[uri] = bytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Uris.Add(uri);
            if (_map.TryGetValue(uri, out byte[]? bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new StringContent("not found", Encoding.UTF8, "text/plain")
            });
        }
    }
}
