using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The harness gating report over the shared path, file-system, environment and state fakes: which harness owns a
/// global hook is decided from the receipt and the files in memory, never from the process's own working directory.
/// </summary>
public sealed class ArbiterHarnessGatingSeamsTests
{
    private const string Home = "/kw/home";
    private const string Root = "/kw/repo";
    private const string ShimPath = "/kw/home/.kilo/plugin/kyber-arbiter.ts";
    private const string Digest = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/work", homeDirectory: Home);

    public ArbiterHarnessGatingSeamsTests()
    {
        _files.CreateDirectory(Home);
        _files.CreateDirectory(Root);
        _files.CreateDirectory("/kw/home/.kilo");
        _files.CreateDirectory("/kw/home/.kilo/plugin");
        _paths = new FakePathResolver(_files);
    }

    /// <summary>
    /// A shim hook the receipt records still owns its harness when the receipt spells the path with a redundant
    /// <c>./</c> segment: the seam normalizes both spellings before they are compared, as the full-path call it
    /// replaced did.
    /// </summary>
    [Fact]
    public void Build_CountsAShimHookOwned_WhenTheReceiptPathCarriesARedundantSegment()
    {
        _files.WriteAllText(ShimPath, "// kyber-arbiter");
        FakeSquadStateStore store = StoreOwning("./plugin/kyber-arbiter.ts");

        HarnessGatingReport report = Gating(store).Build(Root, Home, KyberWeaveConfigDefaults.Product, []);

        Assert.Contains("kilo", report.UnlistedGlobalTokens);
        Assert.Contains(report.Rows, row => row.Token == "kilo");
    }

    /// <summary>
    /// On a volume that folds names, a receipt spelling that differs only in case names the same shim file, so the
    /// harness still owns its global hook. A plain spelling comparison would report the hook as not installed.
    /// </summary>
    [Fact]
    public void Build_CountsAShimHookOwned_WhenTheReceiptSpellingDiffersOnlyInCase()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        files.CreateDirectory(Home);
        files.CreateDirectory(Root);
        files.CreateDirectory("/kw/home/.kilo");
        files.CreateDirectory("/kw/home/.kilo/plugin");
        files.WriteAllText(ShimPath, "// kyber-arbiter");
        FakeSquadStateStore store = StoreOwning("PLUGIN/kyber-arbiter.ts");
        ArbiterHarnessGating gating = new(
            store,
            new FakeSquadGlobalRootResolver(Home),
            new NoClaudeHooks(),
            files,
            new NoDeployment(),
            new FakePathResolver(files),
            _environment);

        HarnessGatingReport report = gating.Build(Root, Home, KyberWeaveConfigDefaults.Product, []);

        Assert.Contains("kilo", report.UnlistedGlobalTokens);
    }

    /// <summary>A receipt file for another harness names no shim of this one, so the harness is not installed here.</summary>
    [Fact]
    public void Build_CountsNoShimHookOwned_WhenTheReceiptNamesAnotherFile()
    {
        _files.WriteAllText(ShimPath, "// kyber-arbiter");
        FakeSquadStateStore store = StoreOwning("plugin/other.ts");

        HarnessGatingReport report = Gating(store).Build(Root, Home, KyberWeaveConfigDefaults.Product, []);

        Assert.DoesNotContain("kilo", report.UnlistedGlobalTokens);
        Assert.DoesNotContain(report.Rows, row => row.Token == "kilo");
    }

    private ArbiterHarnessGating Gating(FakeSquadStateStore store) =>
        new(store, new FakeSquadGlobalRootResolver(Home), new NoClaudeHooks(), _files, new NoDeployment(), _paths, _environment);

    /// <summary>A global receipt that records one Kilo file at the spelling the test names.</summary>
    private static FakeSquadStateStore StoreOwning(string relativePath)
    {
        FakeSquadStateStore store = new(new FakeSquadUserPaths(Home));
        store.Add(new SquadReceipt(
            "kyber-squad.receipt/v3",
            SquadDeploymentScope.Global,
            Home,
            DateTimeOffset.UnixEpoch,
            [],
            [new SquadOwnedFile(relativePath, Digest, "kilo", Adopted: false)]));
        return store;
    }

    /// <summary>The project declares no Claude hook source, so only the other harnesses' rows are decided here.</summary>
    private sealed class NoClaudeHooks : IClaudeHookCoverage
    {
        public IReadOnlyList<string> EffectiveSources(string projectRoot, string homeDirectory, string? agentType) => [];

        public IReadOnlyList<string> CandidateSources(
            string projectRoot,
            string homeDirectory,
            string? agentType,
            string eventName,
            string toolName) => [];

        public IReadOnlyList<string> ProjectCandidateSources(
            string projectRoot,
            string homeDirectory,
            string eventName,
            string toolName) => [];
    }

    /// <summary>No project deployment exists, so no row comes from one.</summary>
    private sealed class NoDeployment : IArbiterUninstaller
    {
        public void Uninstall(string root, SquadDeploymentScope scope, string target) =>
            throw new InvalidOperationException("A gating report never uninstalls.");

        public bool HasDeployment(string root, SquadTarget target, SquadDeploymentScope scope) => false;

        public bool AnotherTargetIsDeployed(string root, SquadTarget target) => false;
    }
}
