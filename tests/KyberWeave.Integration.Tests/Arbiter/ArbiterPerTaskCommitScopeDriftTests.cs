using KyberWeave.Core.Arbiter.Facts;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// A per-task commit is now the normal case, so the scope-drift rule must still see what a
/// finished task changed. It reads the paths changed since the ledger's pre-dispatch
/// snapshot, and that set has to include a path whose change is committed while the tree is
/// clean — otherwise every committed task looks as though it changed nothing, and
/// <c>KW-ARB-DIFF-001</c> cannot see drift at all.
/// </summary>
/// <remarks>
/// The repository is built through <see cref="RunStartProbe" />, the suite's allow-listed
/// driver of a real git repository, because the proof needs two commits: a snapshot taken
/// on a clean tree, then one commit on top with the tree clean again.
/// </remarks>
public sealed class ArbiterPerTaskCommitScopeDriftTests
{
    [Fact]
    public async Task PerTaskCommitsDoNotHideScopeDrift()
    {
        using RunStartProbe probe = await RunStartProbe.CreateAsync();
        GitSnapshot? snapshot = await RealGitFacts.Create().CaptureSnapshotAsync(probe.Root);
        Assert.NotNull(snapshot);

        await probe.GitAsync("switch", "-c", "claude/feat/one-task", "origin/main");
        string committed = Path.Combine(probe.Root, "src", "OneTask.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(committed)!);
        await File.WriteAllTextAsync(committed, "// one task's work\n", System.Text.Encoding.UTF8);
        await probe.GitAsync("add", "-A");
        await probe.GitAsync("commit", "-m", "test(squad): one task, one commit");
        Assert.Equal(string.Empty, (await probe.GitOutputAsync("status", "--porcelain")).Trim());

        IReadOnlySet<string> changed = await RealGitFacts.Create().GetChangedPathsSinceSnapshotAsync(probe.Root, snapshot!);

        Assert.Contains("src/OneTask.cs", changed);
    }
}
