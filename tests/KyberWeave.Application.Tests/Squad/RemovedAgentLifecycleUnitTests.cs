using System.Text;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Fakes;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>RemovedAgentLifecycleTests</c>: an update whose release no longer renders the retired agents
/// removes every unedited Squad-written file on every target, keeps the one the host edited and reports it. The
/// whole install and update run over the in-memory file system, state and global roots.
/// </summary>
public sealed class RemovedAgentLifecycleUnitTests
{
    private const string Edited = "csharp-dev";
    private const string EditedContent = "operator edit\n";

    private readonly InMemorySquadState _state = new();

    /// <summary>Every retired agent file goes from every target unless the host edited it; the edited one stays, reported.</summary>
    [Fact]
    public async Task EveryRemovedAgentFileIsRemovedFromEveryTargetUnlessTheHostEditedIt()
    {
        string anchor = _state.CreateRoot("anchor");
        string[] removed = [.. PlanTaskAgentPick.RemovedAgents.Keys.Order(StringComparer.Ordinal)];
        SquadTarget[] targets = [SquadTarget.Codex, SquadTarget.Claude];
        SquadGlobalRoots roots = new(_ => null, _state.UserPaths.HomeDirectory);
        using FakeSquadReleaseSource release = new() { Files = _state.Files };

        SquadLifecycleResult install = await SquadTestCommands
            .InMemoryLifecycle(_state, release, FakeSquadRenderer.ScriptedAgents(["conductor", .. removed]), roots)
            .InstallAsync(new SquadInstallRequest(anchor, SquadDeploymentScope.Global, targets, Version: "1.2.3"));
        Assert.True(install.Success, Describe(install));

        string editedPath = AgentPath(roots, SquadTarget.Codex, Edited);
        Assert.True(_state.Files.FileExists(editedPath), "The first release must render the agent file this test edits.");
        _state.Files.WriteAllBytes(editedPath, new UTF8Encoding(false).GetBytes(EditedContent));

        SquadLifecycleResult update = await SquadTestCommands
            .InMemoryLifecycle(_state, release, FakeSquadRenderer.ScriptedAgents("conductor"), roots)
            .UpdateAsync(new SquadUpdateRequest(anchor, SquadDeploymentScope.Global, targets, Version: "1.2.4"));
        Assert.True(update.Success, Describe(update));

        foreach (SquadTarget target in targets)
        {
            foreach (string agent in removed)
            {
                string path = AgentPath(roots, target, agent);
                if (target == SquadTarget.Codex && agent == Edited)
                {
                    Assert.Equal(EditedContent, Encoding.UTF8.GetString(_state.Files.ReadAllBytes(path)));
                    continue;
                }

                Assert.False(_state.Files.FileExists(path), $"'{target}' still carries the removed agent '{agent}'.");
            }
        }

        string warnings = string.Join('\n', (update.Warnings ?? []).Select(line => line.Replace('\\', '/')));
        Assert.Contains($"agents/{Edited}.md", warnings, StringComparison.Ordinal);
        Assert.Contains("Kept the locally edited", warnings, StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(anchor, SquadDeploymentScope.Global));
        Assert.DoesNotContain(receipt.Files, file =>
            removed.Contains(Path.GetFileNameWithoutExtension(file.RelativePath), StringComparer.Ordinal)
            && !(file.Target == "codex" && file.RelativePath == $"agents/{Edited}.md"));
    }

    private static string AgentPath(SquadGlobalRoots roots, SquadTarget target, string agent) =>
        Path.Combine(roots.ResolveGlobalRoot(target), "agents", agent + ".md");

    private static string Describe(SquadLifecycleResult result) =>
        string.Join("; ", (result.Errors ?? []).Concat(result.Warnings ?? []));
}
