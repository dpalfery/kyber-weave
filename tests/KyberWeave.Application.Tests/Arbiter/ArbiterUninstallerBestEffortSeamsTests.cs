using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The uninstaller's best-effort questions over the shared path, file-system and environment fakes: a state store it
/// cannot read says nothing about another target's deployment, and must not escape as an exception.
/// </summary>
public sealed class ArbiterUninstallerBestEffortSeamsTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    /// <summary>
    /// The shared-key warning is best effort. A store that cannot even resolve its home throws
    /// <see cref="ArgumentException"/>; that must not escape the helper and crash <c>--remove-key</c>.
    /// </summary>
    [Fact]
    public void AnotherTargetIsDeployed_UnreadableState_ReportsNoOtherTargetWithoutThrowing()
    {
        string home = Path.Combine(_files.Root, "home");
        string work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(home);
        _files.CreateDirectory(work);

        FakeSquadStateStore store = new(new FakeSquadUserPaths(home))
        {
            ListProjectsFailure = new ArgumentException("The state store has no readable registry.", nameof(home))
        };
        ArbiterUninstaller uninstaller = new(
            store,
            new FakeSquadGlobalRootResolver(home),
            _files,
            new NoGlobalTargets(),
            new FakeProcessEnvironment(currentDirectory: work, homeDirectory: home),
            new FakePathResolver(_files));

        Assert.False(uninstaller.AnotherTargetIsDeployed(work, SquadTarget.Kilo));
    }

    /// <summary>The user configuration lists no global target, so only the state store decides what is deployed.</summary>
    private sealed class NoGlobalTargets : IUserHostConfigStore
    {
        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target)
        {
        }

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }
}
