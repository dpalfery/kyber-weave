using KyberWeave.Core.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The Claude hook coverage check over the file-system seam: the project's settings, agent and skill files live in
/// memory, so which sources would fire for a dispatch is decided without a disk or a Claude install.
/// </summary>
public sealed class ClaudeHookCoverageTests
{
    private const string Project = "/repo";
    private const string Home = "/home/dev";
    private const string ProjectHook = "kyber-weave-arbiter hook --harness claude";
    private const string GlobalHook = "kyber-weave-arbiter hook --harness claude --scope global";

    private readonly InMemoryFileSystem _files = new();

    private ClaudeHookCoverage Coverage() => new(_files);

    private static string Json(string eventName, string matcher, string command) =>
        $$$"""{"hooks":{"{{{eventName}}}":[{"matcher":"{{{matcher}}}","hooks":[{"type":"command","command":"{{{command}}}"}]}]}}""";

    private static string Agent(string eventName, string matcher, string command) =>
        $"---\nname: worker\nhooks:\n  {eventName}:\n    - matcher: {matcher}\n      hooks:\n        - type: command\n          command: {command}\n---\nbody\n";

    private void Write(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private IReadOnlyList<string> Candidates(string? agentType = null, string eventName = "PreToolUse", string tool = "Task") =>
        Coverage().CandidateSources(Project, Home, agentType, eventName, tool);

    [Fact]
    public void AProjectSettingsFileWithTheProjectHookUnderTheEventsContainerCoversTheDispatch()
    {
        Write("/repo/.claude/settings.json", Json("PreToolUse", "Task|Agent", ProjectHook));

        Assert.Equal([Path.Combine(".claude", "settings.json")], Candidates());
    }

    [Fact]
    public void ASourceWithoutTheProjectHookOrUnderAnotherEventOrToolDoesNotCover()
    {
        Write("/repo/.claude/settings.json", Json("PostToolUse", "Task", ProjectHook));
        Write("/repo/.claude/settings.local.json", Json("PreToolUse", "Task", GlobalHook));

        Assert.Empty(Candidates());
        Assert.Equal([Path.Combine(".claude", "settings.json")], Candidates(eventName: "PostToolUse"));
        Assert.Empty(Candidates(eventName: "PostToolUse", tool: "Bash"));
    }

    [Fact]
    public void ASourceThatCannotBeParsedOrReadDoesNotFireAndNothingThrows()
    {
        Write("/repo/.claude/settings.json", "{ not json");
        Write("/repo/.claude/settings.local.json", Json("PreToolUse", "Task", ProjectHook));
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.OpenRead) && operation.Path!.EndsWith("settings.local.json", StringComparison.Ordinal),
            new IOException("locked"));

        Assert.Empty(Candidates());
    }

    [Fact]
    public void ASourceLargerThanTheReadBoundDoesNotFire()
    {
        Write("/repo/.claude/settings.json", Json("PreToolUse", "Task", ProjectHook) + new string(' ', (1024 * 1024) + 1));

        Assert.Empty(Candidates());
    }

    [Fact]
    public void AMatcherThatDoesNotCompileMatchesNothing()
    {
        Write("/repo/.claude/settings.json", Json("PreToolUse", "(", ProjectHook));

        Assert.Empty(Candidates());
    }

    [Fact]
    public void TheProjectAgentFileWinsOverThePersonalOneAndAPersonalOneIsListedByFullPath()
    {
        Write("/repo/.claude/agents/worker.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/home/dev/.claude/agents/worker.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/home/dev/.claude/agents/solo.md", Agent("PreToolUse", "Task", ProjectHook));

        Assert.Equal([Path.Combine(".claude", "agents", "worker.md")], Candidates("worker"));
        Assert.Equal(["/home/dev/.claude/agents/solo.md"], Candidates("solo"));
    }

    [Theory]
    [InlineData("../worker")]
    [InlineData("plugin:worker")]
    public void AnAgentTypeThatIsNotAFileNameNamesNoFile(string agentType)
    {
        Write("/repo/.claude/agents/worker.md", Agent("PreToolUse", "Task", ProjectHook));

        Assert.Empty(Candidates(agentType));
    }

    [Fact]
    public void APersonalSkillShadowsAProjectSkillOfTheSameName()
    {
        Write("/repo/.claude/skills/review/SKILL.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/home/dev/.claude/skills/review/SKILL.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/repo/.claude/skills/plan/SKILL.md", Agent("PreToolUse", "Task", ProjectHook));

        Assert.Equal(
            [Path.Combine(".claude", "skills", "plan", "SKILL.md"), "/home/dev/.claude/skills/review/SKILL.md"],
            Candidates());
    }

    [Fact]
    public void ALinkedSkillFolderIsNeverFollowed()
    {
        _files.CreateDirectory("/elsewhere/leak");
        _files.WriteAllText("/elsewhere/leak/SKILL.md", Agent("PreToolUse", "Task", ProjectHook));
        _files.CreateDirectory("/repo/.claude/skills");
        _files.CreateDirectoryLink("/repo/.claude/skills/leak", "/elsewhere/leak");

        Assert.Empty(Candidates());
    }

    [Fact]
    public void ASkillsFolderThatCannotBeListedContributesNothing()
    {
        Write("/repo/.claude/skills/plan/SKILL.md", Agent("PreToolUse", "Task", ProjectHook));
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.EnumerateEntries), new IOException("denied"));

        Assert.Empty(Candidates());
    }

    [Fact]
    public void TheProjectSourcesCoverEveryProjectAgentFileUnderAnyAgentType()
    {
        Write("/repo/.claude/settings.json", Json("PreToolUse", "Task", ProjectHook));
        Write("/repo/.claude/agents/b.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/repo/.claude/agents/a.md", Agent("PreToolUse", "Task", ProjectHook));
        Write("/repo/.claude/agents/c.md", Agent("PostToolUse", "Task", ProjectHook));

        IReadOnlyList<string> sources = Coverage().ProjectCandidateSources(Project, Home, "PreToolUse", "Task");

        Assert.Equal(
            [
                Path.Combine(".claude", "settings.json"),
                Path.Combine(".claude", "agents", "a.md"),
                Path.Combine(".claude", "agents", "b.md"),
            ],
            sources);
    }

    [Fact]
    public void TheEffectiveSourcesListEverySourceThatExistsBeforeAnyIsRead()
    {
        Write("/repo/.claude/settings.json", "{ not json");
        Write("/repo/.claude/agents/worker.md", "no frontmatter");

        Assert.Equal(
            [Path.Combine(".claude", "settings.json"), Path.Combine(".claude", "agents", "worker.md")],
            Coverage().EffectiveSources(Project, Home, "worker"));
    }

    [Fact]
    public void AReadTheCallerSuppliesDecidesTheSourceInPlaceOfTheFileSystem()
    {
        Assert.True(Coverage().Fires("/anywhere/settings.json", "PreToolUse", "Task", _ => Json("PreToolUse", "Task", ProjectHook)));
        Assert.False(Coverage().Fires("/anywhere/settings.json", "PreToolUse", "Task", _ => throw new InvalidDataException("too big")));
    }
}
