using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A Squad update removes a retired agent's rendered file only when the lock records that the Squad wrote it and
/// the host left it unedited; an edited file is kept and reported. This runs a whole install and update on the real
/// disk, so it is the end-to-end proof beside the planner's in-memory cases.
/// </summary>
public sealed class RemovedAgentLifecycleTests
{
    /// <summary>
    ///     The retirement of the fourteen agents the removal phase deletes, on a global deployment of two
    ///     harnesses: every file the lock records as Squad's own, unedited, goes with its agent, while the one the
    ///     host edited survives and is reported. The retired names come from the shipped removal map, so a name
    ///     dropped from that map fails here rather than passing on a fixture written to match.
    /// </summary>
    [Fact]
    public async Task EveryRemovedAgentFileIsRemovedFromEveryTargetUnlessTheHostEditedIt()
    {
        using TempDirectory temp = new();
        using SquadHomeHarness harness = new(temp.Path);
        string anchor = harness.MakeDirectory("anchor");
        string[] removed = [.. PlanTaskAgentPick.RemovedAgents.Keys.Order(StringComparer.Ordinal)];
        SquadTarget[] targets = [SquadTarget.Codex, SquadTarget.Claude];
        const string edited = "csharp-dev";
        const string editedContent = "operator edit\n";

        SquadLifecycleResult install = await harness.Lifecycle(FakeSquadRenderer.ScriptedAgents(["conductor", .. removed]))
            .InstallAsync(new SquadInstallRequest(
                anchor,
                SquadDeploymentScope.Global,
                targets,
                Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        string editedPath = Path.Combine(harness.GlobalRoot(SquadTarget.Codex), "agents", edited + ".md");
        Assert.True(File.Exists(editedPath), "The first release must render the agent file this test edits.");
        await File.WriteAllTextAsync(editedPath, editedContent, new UTF8Encoding(false));

        SquadLifecycleResult update = await harness.Lifecycle(FakeSquadRenderer.ScriptedAgents("conductor"))
            .UpdateAsync(new SquadUpdateRequest(
                anchor,
                SquadDeploymentScope.Global,
                targets,
                Version: "1.2.4"));
        Assert.True(update.Success, DescribeFailure(update));

        foreach (SquadTarget target in targets)
        {
            foreach (string agent in removed)
            {
                string path = Path.Combine(harness.GlobalRoot(target), "agents", agent + ".md");
                if (target == SquadTarget.Codex && string.Equals(agent, edited, StringComparison.Ordinal))
                {
                    Assert.True(File.Exists(path), "A file the host edited must survive the update that retires it.");
                    Assert.Equal(editedContent, await File.ReadAllTextAsync(path));
                    continue;
                }

                Assert.False(
                    File.Exists(path),
                    $"'{target}' still carries the removed agent '{agent}', which the lock records as unedited Squad output.");
            }
        }

        string warnings = string.Join(
            Environment.NewLine,
            (update.Warnings ?? []).Select(line => line.Replace(Path.DirectorySeparatorChar, '/')));
        Assert.Contains($"agents/{edited}.md", warnings, StringComparison.Ordinal);
        Assert.Contains("Kept the locally edited", warnings, StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            harness.Store.ReadReceipt(anchor, SquadDeploymentScope.Global));
        Assert.DoesNotContain(receipt.Files, file =>
            file.RelativePath.EndsWith(".md", StringComparison.Ordinal)
            && removed.Contains(Path.GetFileNameWithoutExtension(file.RelativePath), StringComparer.Ordinal)
            && !(string.Equals(file.Target, "codex", StringComparison.Ordinal)
                 && string.Equals(file.RelativePath, $"agents/{edited}.md", StringComparison.Ordinal)));
    }

    private static string DescribeFailure(SquadLifecycleResult result) =>
        string.Join("; ", (result.Errors ?? []).Concat(result.Warnings ?? []));
}
