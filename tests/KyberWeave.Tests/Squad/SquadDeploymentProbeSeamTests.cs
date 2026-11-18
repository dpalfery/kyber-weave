using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves the read-only deployment probes — target resolution, skill shadowing and Factory hook shadowing — through
/// the in-memory file system alone.
/// </summary>
public sealed class SquadDeploymentProbeSeamTests
{
    private const string Root = "/work";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly FakeProcessEnvironment _environment = new();

    private FakePathResolver Paths => new(_files);

    private SquadTargetResolver Targets => new(_files, Paths, _environment);

    private SquadSkillShadowing SkillShadowing => new(_files, Paths, _environment);

    private FactoryHooksShadowing HooksShadowing => new(_files);

    private static SquadTargetResolutionRequest Install(string root) =>
        new() { RootPath = root, Operation = SquadTargetOperation.Install };

    [Fact]
    public void AHarnessMarkerDirectoryInTheRootSelectsThatTarget()
    {
        _files.CreateDirectory("/work/.claude");

        SquadTargetResolutionDecision decision = Targets.Resolve(Install(Root));

        Assert.Equal(SquadTargetResolutionKind.Resolved, decision.Kind);
        Assert.Equal([SquadTarget.Claude], decision.Targets);
        Assert.Equal(SquadTargetResolutionSource.Markers, decision.Source);
    }

    [Fact]
    public void AFileMarkerSelectsItsTargetEvenThoughItIsNotADirectory()
    {
        _files.CreateDirectory("/work/.github");
        _files.WriteAllText("/work/.github/copilot-instructions.md", "# rules");

        SquadTargetResolutionDecision decision = Targets.Resolve(Install(Root));

        Assert.Equal([SquadTarget.Copilot], decision.Targets);
    }

    [Fact]
    public void ARootWithNoMarkerFailsNonInteractivelyWithTheInstallRecoveryCommand()
    {
        SquadTargetResolutionDecision decision = Targets.Resolve(Install(Root));

        Assert.Equal(SquadTargetResolutionKind.Failure, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Equal(2, decision.ExitCode);
        Assert.Equal("kyber-weave squad install --target <target>", decision.RecoveryCommand);
    }

    [Fact]
    public void ARootWithNoMarkerAsksAnInteractiveCallerToChoose()
    {
        SquadTargetResolutionDecision decision = Targets.Resolve(new SquadTargetResolutionRequest
        {
            RootPath = Root,
            Operation = SquadTargetOperation.Install,
            IsInteractive = true
        });

        Assert.Equal(SquadTargetResolutionKind.InteractiveSelectionRequired, decision.Kind);
        Assert.Empty(decision.Targets);
        Assert.Null(decision.ExitCode);
    }

    [Fact]
    public void AProjectSkillThatDiffersFromEveryInstalledCopyIsReported()
    {
        InstallSkill("/installed/review/SKILL.md", "Be strict.");
        string projectPath = WriteProjectSkill("review", "Be kind.");

        IReadOnlyList<SquadSkillShadow> shadows = SkillShadowing.Find(
            Root,
            [new SquadInstalledSkill("review", "/installed/review/SKILL.md")],
            new HashSet<string>(StringComparer.Ordinal));

        SquadSkillShadow shadow = Assert.Single(shadows);
        Assert.Equal("review", shadow.Name);
        Assert.Equal(projectPath, shadow.ProjectPath);
        Assert.Equal("/installed/review/SKILL.md", shadow.InstalledPath);
    }

    [Fact]
    public void AProjectCopyEqualToAnInstalledCopyIsNotReported()
    {
        InstallSkill("/installed/review/SKILL.md", "Be kind.");
        WriteProjectSkill("review", "Be kind.");

        IReadOnlyList<SquadSkillShadow> shadows = SkillShadowing.Find(
            Root,
            [new SquadInstalledSkill("review", "/installed/review/SKILL.md")],
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(shadows);
    }

    [Fact]
    public void AProjectCopyTheDeploymentOwnsIsNeverReported()
    {
        InstallSkill("/installed/review/SKILL.md", "Be strict.");
        string projectPath = WriteProjectSkill("review", "Be kind.");

        IReadOnlyList<SquadSkillShadow> shadows = SkillShadowing.Find(
            Root,
            [new SquadInstalledSkill("review", "/installed/review/SKILL.md")],
            new HashSet<string>([projectPath], StringComparer.Ordinal));

        Assert.Empty(shadows);
    }

    [Fact]
    public void AProjectCopyOfAnotherSkillNameIsNotReported()
    {
        InstallSkill("/installed/review/SKILL.md", "Be strict.");
        WriteProjectSkill("builder", "Be strict.");

        IReadOnlyList<SquadSkillShadow> shadows = SkillShadowing.Find(
            Root,
            [new SquadInstalledSkill("review", "/installed/review/SKILL.md")],
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(shadows);
    }

    [Fact]
    public void SettingsHooksWithoutAHooksFileDropTheFactoryBlockAndRecordWhy()
    {
        _files.CreateDirectory("/work/.factory");
        _files.WriteAllText("/work/.factory/settings.json", "{\"hooks\":{}}");

        FactoryHooksShadowingOutcome outcome = HooksShadowing.Resolve(
            Root,
            [SquadTarget.Factory],
            new SquadArbiterWiring(true, 10),
            SquadDeploymentScope.Project,
            Blocks());

        Assert.Equal(["claude"], outcome.Blocks!.Select(block => block.Target));
        SquadDegradationRecord record = Assert.Single(outcome.Degradations);
        Assert.Equal(FactoryHooksShadowing.DegradationCode, record.Code);
        Assert.StartsWith(FactoryHooksShadowing.Reason, record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void AHooksFileLeavesTheFactoryBlockInPlace()
    {
        _files.CreateDirectory("/work/.factory");
        _files.WriteAllText("/work/.factory/settings.json", "{\"hooks\":{}}");
        _files.WriteAllText("/work/.factory/hooks.json", "{}");

        FactoryHooksShadowingOutcome outcome = HooksShadowing.Resolve(
            Root,
            [SquadTarget.Factory],
            new SquadArbiterWiring(true, 10),
            SquadDeploymentScope.Project,
            Blocks());

        Assert.Equal(["factory", "claude"], outcome.Blocks!.Select(block => block.Target));
        Assert.Empty(outcome.Degradations);
    }

    [Fact]
    public void SettingsThatDoNotParseAlsoDropTheFactoryBlock()
    {
        _files.CreateDirectory("/work/.factory");
        _files.WriteAllText("/work/.factory/settings.json", "{ not json");

        FactoryHooksShadowingOutcome outcome = HooksShadowing.Resolve(
            Root,
            [SquadTarget.Factory],
            new SquadArbiterWiring(true, 10),
            SquadDeploymentScope.Project,
            Blocks());

        SquadDegradationRecord record = Assert.Single(outcome.Degradations);
        Assert.StartsWith(FactoryHooksShadowing.UnparsableReason, record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledArbiterLeavesTheFactoryBlockAloneWhateverTheFilesSay()
    {
        _files.CreateDirectory("/work/.factory");
        _files.WriteAllText("/work/.factory/settings.json", "{\"hooks\":{}}");

        FactoryHooksShadowingOutcome outcome = HooksShadowing.Resolve(
            Root,
            [SquadTarget.Factory],
            new SquadArbiterWiring(false, 10),
            SquadDeploymentScope.Project,
            Blocks());

        Assert.Equal(["factory", "claude"], outcome.Blocks!.Select(block => block.Target));
        Assert.Empty(outcome.Degradations);
    }

    private void InstallSkill(string path, string body)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, $"---\nname: review\n---\n{body}\n");
    }

    private string WriteProjectSkill(string name, string body)
    {
        string location = SquadSkillOutputPaths.ProjectSkillRoots()[0].Replace('/', Path.DirectorySeparatorChar);
        string path = Path.Combine(Root, location, name, "SKILL.md");
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, $"---\nname: {name}\n---\n{body}\n");
        return path;
    }

    private static IReadOnlyList<SquadRenderedBlock> Blocks() =>
    [
        new("factory", ".factory/hooks.json", SquadHookBlockFormat.Factory, [new("hooks", JsonNode.Parse("{}")!)]),
        new("claude", ".claude/settings.json", SquadHookBlockFormat.Claude, [new("hooks", JsonNode.Parse("{}")!)])
    ];
}
