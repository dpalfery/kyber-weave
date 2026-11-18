using KyberWeave.Application.RunStart;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.RunStart;

namespace KyberWeave.Tests.RunStart;

/// <summary>Builds the run-start services over the real adapters, for the tests that drive them through a command or a hook.</summary>
internal static class RunStartTestServices
{
    /// <summary>The run records over the real file system and git.</summary>
    internal static IRunStateStore RunStateStore() =>
        new FileRunStateStore(
            new PhysicalFileSystem(),
            new GitCheckedOutBranchReader(
                new ProcessGitCommandRunner(ProcessGitCommandRunner.DefaultTimeout)),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment());

    /// <summary>The run-start evidence over the real adapters.</summary>
    internal static IRunStartEvidence Evidence() => new RunStartEvidenceService(RealGitFacts.Create(), RunStateStore());

    /// <summary>The path with every symbolic link on the way resolved, as the checkout guard reports it.</summary>
    internal static string RealPath(string path) =>
        new RunCheckoutGuard(new PhysicalFileSystem(), RunStateStore(), new ProcessEnvironment()).RealPath(path);

    /// <summary>The checkout guard over the real file system and the given run records.</summary>
    internal static IRunCheckout Checkout(IRunStateStore? runStates = null) =>
        new RunCheckoutGuard(new PhysicalFileSystem(), runStates ?? RunStateStore(), new ProcessEnvironment());
}
