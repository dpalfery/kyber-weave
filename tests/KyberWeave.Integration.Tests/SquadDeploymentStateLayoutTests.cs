using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Deployment-state contracts: receipt layout and state resolution on a real volume. The rest of the theme runs over
/// the in-memory fakes in <c>KyberWeave.Application.Tests</c>; these three cases stay because only a real volume can
/// answer the host's case rule.
/// </summary>
public sealed class SquadDeploymentStateLayoutTests : SquadDeploymentStateTestBase
{
    /// <summary>
    ///     On a real case-insensitive filesystem the two spellings name one directory, so they must resolve to
    ///     the same home-anchored receipt. On a case-sensitive filesystem the probe cannot be built and the
    ///     test returns without asserting; that case is covered by the Windows sibling test and the in-memory
    ///     twin in the application project.
    /// </summary>
    [Fact]
    public void ResolveGlobalStateCaseAliasesShareHomeAnchoredReceiptOnARealCaseInsensitiveVolume()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "CaseSensitiveProbe");
        string caseAlias = Path.Combine(fixture.Path, "casesensitiveprobe");
        Directory.CreateDirectory(targetRoot);
        if (!Directory.Exists(caseAlias))
            return;

        string applicationData = Path.Combine(fixture.Path, "application-data");
        string home = Path.Combine(applicationData, "home");
        Directory.CreateDirectory(home);
        SquadStateStore store = Store(applicationData);
        string expected = Path.Combine(
            SquadPhysicalRootIdentities.Resolve(home, SquadFileSystemPathSemantics.Instance).PhysicalPath,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");

        AssertSameLocation(expected, store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global));
        AssertSameLocation(expected, store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));
    }

    /// <summary>
    ///     Two case-distinct sibling roots on a real case-sensitive volume keep separate physical identities,
    ///     transactions and leases, so two deployments of them proceed independently.
    /// </summary>
    [Fact]
    public async Task ResolvePhysicalRootCaseDistinctSiblingsRemainDistinctOnARealCaseSensitiveVolume()
    {
        using TempDirectory fixture = new();
        string upperRoot = Path.Combine(fixture.Path, "Root");
        string lowerRoot = Path.Combine(fixture.Path, "root");
        string symlinkRoot = Path.Combine(fixture.Path, "root-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            Assert.Equal(
                SquadPhysicalRootIdentities.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance),
                SquadPhysicalRootIdentities.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance));
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        Directory.CreateSymbolicLink(symlinkRoot, upperRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentities.Resolve(upperRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentities.Resolve(lowerRoot, SquadFileSystemPathSemantics.Instance);
        SquadPhysicalRootIdentity symlinkIdentity = SquadPhysicalRootIdentities.Resolve(symlinkRoot, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(applicationData);

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            store.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Project),
            store.ResolveTransactionDirectory(lowerRoot, SquadDeploymentScope.Project));
        Assert.Equal(upperIdentity, symlinkIdentity);

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = RealSquadDeployment.Planner.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan siblingPlan = RealSquadDeployment.Planner.CreateInstall(
            lowerRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/lower.toml", "lower")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = RealSquadDeployment.Transaction(store, blocker);
        Task activeExecution = BlockedTransaction.Start(() => activeTransaction.Execute(activePlan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            activeExecution,
            "The first case-distinct root did not acquire its physical-root lease.");

        Exception? siblingException;
        try
        {
            SquadTransaction siblingTransaction = RealSquadDeployment.Transaction(store);
            siblingException = Record.Exception(() => siblingTransaction.Execute(siblingPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        Assert.Null(siblingException);
        Assert.Equal("upper", Read(upperRoot, ".codex/agents/upper.toml"));
        Assert.Equal("lower", Read(lowerRoot, ".codex/agents/lower.toml"));
    }

    /// <summary>
    ///     A case alias of a root on a real case-insensitive volume is one physical root, so it shares the
    ///     receipt and the lease, and a second deployment of it is refused while the first runs.
    /// </summary>
    [Fact]
    public async Task ResolvePhysicalRootCaseAliasesConvergeOnARealCaseInsensitiveVolume()
    {
        using TempDirectory fixture = new();
        string actualRoot = Path.Combine(fixture.Path, "CaseProbe");
        string caseAlias = Path.Combine(fixture.Path, "caseprobe");
        string symlinkAlias = Path.Combine(fixture.Path, "case-probe-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(actualRoot);
        Directory.CreateSymbolicLink(symlinkAlias, actualRoot);
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentities.Resolve(actualRoot, SquadFileSystemPathSemantics.Instance);

        Assert.Equal(actualIdentity, SquadPhysicalRootIdentities.Resolve(symlinkAlias, SquadFileSystemPathSemantics.Instance));
        if (!Directory.Exists(caseAlias))
        {
            Assert.NotEqual(
                Path.GetFullPath(actualRoot),
                Path.GetFullPath(caseAlias));
            return;
        }

        SquadPhysicalRootIdentity aliasIdentity = SquadPhysicalRootIdentities.Resolve(caseAlias, SquadFileSystemPathSemantics.Instance);
        SquadStateStore store = Store(applicationData);
        Assert.Equal(actualIdentity.PhysicalPath, aliasIdentity.PhysicalPath);
        Assert.Equal(actualIdentity.Key, aliasIdentity.Key);
        Assert.Equal(
            store.ResolveReceiptPath(actualRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = RealSquadDeployment.Planner.CreateInstall(
            actualRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/actual.toml", "actual")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        // Project scope, not global: the lease of a project is its physical root, so a case alias of that
        // root contends. Global state is leased on the home, which this root is not, so a global alias
        // would correctly not contend with a project at an unrelated root.
        SquadDeploymentPlan aliasPlan = RealSquadDeployment.Planner.CreateInstall(
            caseAlias,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = RealSquadDeployment.Transaction(store, blocker);
        Task activeExecution = BlockedTransaction.Start(() => activeTransaction.Execute(activePlan));
        BlockedTransaction.WaitForStep(
            blocker.Reached,
            activeExecution,
            "The physical-root alias did not acquire its shared lease.");

        Exception? aliasException;
        try
        {
            SquadTransaction aliasTransaction = RealSquadDeployment.Transaction(store);
            aliasException = Record.Exception(() => aliasTransaction.Execute(aliasPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        AssertActiveLeaseConflict(aliasException);
    }
}
