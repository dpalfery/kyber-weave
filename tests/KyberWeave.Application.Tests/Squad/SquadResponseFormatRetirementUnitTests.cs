using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>SquadResponseFormatRetirementTests</c>: an update whose release no longer renders the
/// <c>response-format</c> skill deletes the copies the operator never edited and keeps an edited copy, with its
/// receipt claim and a kept-file warning. Runs over an in-memory install; no disk and no real renderer.
/// </summary>
public sealed class SquadResponseFormatRetirementUnitTests
{
    private static readonly SquadTarget[] Targets = [SquadTarget.Claude, SquadTarget.Cursor, SquadTarget.Factory];

    private readonly InMemorySquadState _state = new();

    /// <summary>
    /// A prior install owns the skill on three targets; after the operator edits one copy, an update from a release
    /// without the skill deletes the other two, keeps the edited one and its receipt claim, and warns.
    /// </summary>
    [Fact]
    public async Task UpdateDeletesUneditedResponseFormatCopiesAndKeepsTheEditedOne()
    {
        string root = _state.CreateRoot("retire-response-format");
        using FakeSquadReleaseSource release = new() { Files = _state.Files };

        SquadLifecycleResult install = await Lifecycle(release, Renderer(withSkill: true)).InstallAsync(
            new SquadInstallRequest(root, SquadDeploymentScope.Project, Targets, Version: "1.2.3"));
        Assert.True(install.Success, string.Join("; ", install.Errors ?? []));

        string[] copies = Targets.Select(target => SkillPath(root, target)).ToArray();
        Assert.All(copies, path => Assert.True(_state.Files.FileExists(path)));
        string edited = copies[0];
        const string editedBody = "Operator-edited copy that update must keep.\n";
        _state.Files.WriteAllBytes(edited, Encoding.UTF8.GetBytes(editedBody));

        SquadLifecycleResult update = await Lifecycle(release, Renderer(withSkill: false)).UpdateAsync(
            new SquadUpdateRequest(root, SquadDeploymentScope.Project, Targets, Version: "1.2.4"));

        Assert.True(update.Success, string.Join("; ", update.Errors ?? []));
        Assert.True(_state.Files.FileExists(edited));
        Assert.Equal(editedBody, Encoding.UTF8.GetString(_state.Files.ReadAllBytes(edited)));
        Assert.All(copies.Skip(1), path => Assert.False(_state.Files.FileExists(path)));

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(_state.Store.ReadReceipt(root, SquadDeploymentScope.Project));
        Assert.Equal(
            [SkillRelative(Targets[0])],
            receipt.Files.Where(file => file.RelativePath.Contains("response-format", StringComparison.Ordinal))
                .Select(file => file.RelativePath));
        Assert.Contains(
            update.Warnings ?? [],
            warning => warning.Contains("Kept the locally edited deployed file", StringComparison.Ordinal)
                       && warning.Contains("response-format", StringComparison.Ordinal));
    }

    private SquadLifecycleService Lifecycle(FakeSquadReleaseSource release, FakeSquadRenderer renderer) =>
        SquadTestCommands.InMemoryLifecycle(_state, release, renderer);

    private static FakeSquadRenderer Renderer(bool withSkill)
    {
        List<SquadDeploymentFile> files = [];
        foreach (SquadTarget target in Targets)
        {
            string token = SquadTargetCatalog.GetToken(target);
            files.Add(new SquadDeploymentFile(SkillRelative(target).Replace("response-format", "task-gate", StringComparison.Ordinal), Encoding.UTF8.GetBytes("task-gate\n"), token));
            if (withSkill)
                files.Add(new SquadDeploymentFile(SkillRelative(target), Encoding.UTF8.GetBytes("Seeded response-format body.\n"), token));
        }

        return FakeSquadRenderer.Emitting(files);
    }

    private static string SkillRelative(SquadTarget target) => $".{SquadTargetCatalog.GetToken(target)}/skills/response-format/SKILL.md";

    private static string SkillPath(string root, SquadTarget target) => Path.Combine(root, SkillRelative(target));
}
