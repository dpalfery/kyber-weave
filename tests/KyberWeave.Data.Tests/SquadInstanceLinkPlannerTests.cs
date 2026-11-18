using System.Collections.Frozen;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for <see cref="SquadInstanceLinkPlanner" />, <see cref="SquadLinkRecord" />,
///     <see cref="SquadLinkKind" />, and <see cref="SquadLinkAction" /> planning logic.
/// </summary>
public sealed class SquadInstanceLinkPlannerTests
{
    [Fact]
    public void AllSquadLinkKindEnumValuesAreDefined()
    {
        Assert.True(Enum.IsDefined(SquadLinkKind.File));
        Assert.True(Enum.IsDefined(SquadLinkKind.Directory));
        Assert.Equal(2, Enum.GetValues<SquadLinkKind>().Length);
    }

    [Fact]
    public void SquadLinkRecordPositionalRecordPreservesAllConstructorProperties()
    {
        SquadLinkRecord record = new("settings.json", SquadLinkKind.File, "/main/settings.json");

        Assert.Equal("settings.json", record.RelativePath);
        Assert.Equal(SquadLinkKind.File, record.Kind);
        Assert.Equal("/main/settings.json", record.TargetPath);

        SquadLinkRecord identical = new("settings.json", SquadLinkKind.File, "/main/settings.json");
        Assert.Equal(record, identical);
    }

    [Fact]
    public void SquadLinkActionRecordsPreserveConstructorProperties()
    {
        SquadLinkRecord record = new("history.sqlite", SquadLinkKind.File, "/main/history.sqlite");

        SquadLinkAction.Add addAction = new(record);
        Assert.Equal(record, addAction.Record);

        SquadLinkAction.Remove removeAction = new(record);
        Assert.Equal(record, removeAction.Record);

        SquadLinkAction.Keep keepAction = new("custom-file.txt", "instance copy kept");
        Assert.Equal("custom-file.txt", keepAction.RelativePath);
        Assert.Equal("instance copy kept", keepAction.Warning);
    }

    [Fact]
    public void EveryTopLevelEntryOfMainHomeIsLinkedExceptRenderedPaths()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/.claude/settings.json"),
            new("history.sqlite", SquadLinkKind.File, "/main/.claude/history.sqlite"),
            new("logs", SquadLinkKind.Directory, "/main/.claude/logs")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();
        Assert.Equal(3, adds.Count);

        Assert.Contains(adds,
            a => a.Record.RelativePath == "settings.json" && a.Record.Kind == SquadLinkKind.File &&
                 a.Record.TargetPath == "/main/.claude/settings.json");
        Assert.Contains(adds,
            a => a.Record.RelativePath == "history.sqlite" && a.Record.Kind == SquadLinkKind.File &&
                 a.Record.TargetPath == "/main/.claude/history.sqlite");
        Assert.Contains(adds,
            a => a.Record.RelativePath == "logs" && a.Record.Kind == SquadLinkKind.Directory &&
                 a.Record.TargetPath == "/main/.claude/logs");
    }

    [Fact]
    public void RenderedFilesAreNeverLinked()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/.claude/settings.json"),
            new("agents/developer.md", SquadLinkKind.File, "/main/.claude/agents/developer.md")
        ];
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/developer.md" };
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();
        Assert.Single(adds);
        Assert.Equal("settings.json", adds[0].Record.RelativePath);

        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "agents/developer.md");
    }

    [Fact]
    public void FolderHoldingRenderedFilesHasNonRenderedEntriesLinkedIndividually()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("agents", SquadLinkKind.Directory, "/main/.claude/agents"),
            new("agents/personal.md", SquadLinkKind.File, "/main/.claude/agents/personal.md"),
            new("agents/custom-helper.md", SquadLinkKind.File, "/main/.claude/agents/custom-helper.md"),
            new("agents/architect.md", SquadLinkKind.File, "/main/.claude/agents/architect.md")
        ];
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/architect.md" };
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        // The 'agents' directory itself must NOT be linked as a whole directory.
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "agents" && a.Record.Kind == SquadLinkKind.Directory);

        // The rendered file must NOT be linked.
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "agents/architect.md");

        // Non-rendered individual files within 'agents/' MUST be linked individually.
        Assert.Contains(adds,
            a => a.Record.RelativePath == "agents/personal.md" && a.Record.Kind == SquadLinkKind.File);
        Assert.Contains(adds,
            a => a.Record.RelativePath == "agents/custom-helper.md" && a.Record.Kind == SquadLinkKind.File);
    }

    [Fact]
    public void DirectoryContainingNoRenderedFilesIsLinkedAsSingleDirectoryLink()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("snippets", SquadLinkKind.Directory, "/main/.claude/snippets"),
            new("snippets/helper.ts", SquadLinkKind.File, "/main/.claude/snippets/helper.ts"),
            new("agents", SquadLinkKind.Directory, "/main/.claude/agents"),
            new("agents/developer.md", SquadLinkKind.File, "/main/.claude/agents/developer.md")
        ];
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/developer.md" };
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        // 'snippets' has no rendered files, so it is linked as a single directory link.
        Assert.Contains(adds, a => a.Record.RelativePath == "snippets" && a.Record.Kind == SquadLinkKind.Directory);

        // Individual files under 'snippets' are not redundantly linked when parent directory is linked.
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "snippets/helper.ts");
    }

    [Fact]
    public void PathsMatchingNeverLinkedAreSkipped()
    {
        SquadInstanceRow customRow = new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            [],
            ["telemetry.json", "cache", "temp-run.lock"],
            SquadInstanceStatus.Ready,
            null);

        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/settings.json"),
            new("telemetry.json", SquadLinkKind.File, "/main/telemetry.json"),
            new("cache", SquadLinkKind.Directory, "/main/cache"),
            new("temp-run.lock", SquadLinkKind.File, "/main/temp-run.lock")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            customRow,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();
        Assert.Single(adds);
        Assert.Equal("settings.json", adds[0].Record.RelativePath);

        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "telemetry.json");
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "cache");
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "temp-run.lock");
    }

    [Fact]
    public void ExtraLinksFromCatalogRowArePlannedForLinking()
    {
        SquadInstanceRow customRow = new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            [".credentials", "custom-cert.pem"],
            [],
            SquadInstanceStatus.Ready,
            null);

        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/settings.json"),
            new(".credentials", SquadLinkKind.File, "/main/.credentials"),
            new("custom-cert.pem", SquadLinkKind.File, "/main/custom-cert.pem")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            customRow,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        Assert.Contains(adds, a => a.Record.RelativePath == ".credentials" && a.Record.Kind == SquadLinkKind.File);
        Assert.Contains(adds, a => a.Record.RelativePath == "custom-cert.pem" && a.Record.Kind == SquadLinkKind.File);
        Assert.Contains(adds, a => a.Record.RelativePath == "settings.json" && a.Record.Kind == SquadLinkKind.File);
    }

    /// <summary>
    ///     Regression test: ExtraLinks specified on SquadInstanceRow must generate SquadLinkAction.Add actions for each extra
    ///     link.
    /// </summary>
    [Fact]
    public void ExtraLinksSpecifiedOnSquadInstanceRowGenerateAddActionForEachExtraLink()
    {
        SquadInstanceRow rowWithExtraLinks = new(
            SquadTarget.Claude,
            "claude",
            SquadInstanceKind.HomeReplace,
            "CLAUDE_CONFIG_DIR",
            [".credentials.json", "custom-certs", "plugins/tools.json"],
            [],
            SquadInstanceStatus.Ready,
            null);

        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/.claude/settings.json"),
            new(".credentials.json", SquadLinkKind.File, "/main/.claude/.credentials.json"),
            new("custom-certs", SquadLinkKind.Directory, "/main/.claude/custom-certs"),
            new("plugins/tools.json", SquadLinkKind.File, "/main/.claude/plugins/tools.json")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            rowWithExtraLinks,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        foreach (string extraLink in rowWithExtraLinks.ExtraLinks)
            Assert.Contains(adds, a => a.Record.RelativePath == extraLink);
    }

    [Fact]
    public void RecordedLinksWhoseTargetInMainHomeNoLongerExistsAreRemoved()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/settings.json")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

        SquadLinkRecord recordedSettings = new("settings.json", SquadLinkKind.File, "/main/settings.json");
        SquadLinkRecord recordedOldTool = new("old-tool.json", SquadLinkKind.File, "/main/old-tool.json");
        SquadLinkRecord recordedLegacyDir = new("legacy-dir", SquadLinkKind.Directory, "/main/legacy-dir");

        IReadOnlyList<SquadLinkRecord> recordedLinks =
        [
            recordedSettings,
            recordedOldTool,
            recordedLegacyDir
        ];
        IReadOnlyList<string> instanceListing = ["settings.json", "old-tool.json", "legacy-dir"];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Remove> removes = actions.OfType<SquadLinkAction.Remove>().ToList();
        Assert.Equal(2, removes.Count);

        Assert.Contains(removes, r => r.Record.RelativePath == "old-tool.json");
        Assert.Contains(removes, r => r.Record.RelativePath == "legacy-dir");
        Assert.DoesNotContain(removes, r => r.Record.RelativePath == "settings.json");
    }

    [Fact]
    public void ExistingInstanceEntryNotRecordedAsLinkIsKeptWithWarning()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/settings.json")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);

        SquadLinkRecord recordedSettings = new("settings.json", SquadLinkKind.File, "/main/settings.json");
        IReadOnlyList<SquadLinkRecord> recordedLinks = [recordedSettings];

        IReadOnlyList<string> instanceListing =
        [
            "settings.json",
            "user-custom-notes.txt",
            "local-plugin"
        ];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Keep> keeps = actions.OfType<SquadLinkAction.Keep>().ToList();
        Assert.Equal(2, keeps.Count);

        SquadLinkAction.Keep notesKeep = Assert.Single(keeps, k => k.RelativePath == "user-custom-notes.txt");
        Assert.Equal("instance copy kept", notesKeep.Warning);

        SquadLinkAction.Keep pluginKeep = Assert.Single(keeps, k => k.RelativePath == "local-plugin");
        Assert.Equal("instance copy kept", pluginKeep.Warning);

        Assert.DoesNotContain(keeps, k => k.RelativePath == "settings.json");
    }

    /// <summary>
    ///     Acceptance criterion 7.4: An instance SHALL share the main home's settings and history through links.
    ///     WHERE the harness stores sign-in in a file, the instance SHALL share sign-in through a link too.
    /// </summary>
    [Fact]
    public void AcceptanceCriterion7_4_InstanceSharesSettingsHistoryAndSignInFilesThroughLinks()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/Users/test/.claude/settings.json"),
            new("history.sqlite", SquadLinkKind.File, "/Users/test/.claude/history.sqlite"),
            new("auth.json", SquadLinkKind.File, "/Users/test/.claude/auth.json")
        ];
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/worker.md" };
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        SquadLinkAction.Add settingsAdd = Assert.Single(adds, a => a.Record.RelativePath == "settings.json");
        Assert.Equal(SquadLinkKind.File, settingsAdd.Record.Kind);
        Assert.Equal("/Users/test/.claude/settings.json", settingsAdd.Record.TargetPath);

        SquadLinkAction.Add historyAdd = Assert.Single(adds, a => a.Record.RelativePath == "history.sqlite");
        Assert.Equal(SquadLinkKind.File, historyAdd.Record.Kind);
        Assert.Equal("/Users/test/.claude/history.sqlite", historyAdd.Record.TargetPath);

        SquadLinkAction.Add authAdd = Assert.Single(adds, a => a.Record.RelativePath == "auth.json");
        Assert.Equal(SquadLinkKind.File, authAdd.Record.Kind);
        Assert.Equal("/Users/test/.claude/auth.json", authAdd.Record.TargetPath);
    }

    /// <summary>
    ///     Acceptance criterion 7.4a: Exception: WHERE a harness rewrites its sign-in file by rename,
    ///     the instance SHALL NOT link that file, and the user SHALL sign in at most once per instance.
    /// </summary>
    [Fact]
    public void AcceptanceCriterion7_4a_SignInFileRewrittenByRenameInNeverLinkedIsNotLinked()
    {
        SquadInstanceRow harnessRow = new(
            SquadTarget.Codex,
            "codex",
            SquadInstanceKind.HomeReplace,
            "CODEX_HOME",
            [],
            ["session.json"],
            SquadInstanceStatus.Ready,
            null);

        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/Users/test/.codex/settings.json"),
            new("history.db", SquadLinkKind.File, "/Users/test/.codex/history.db"),
            new("session.json", SquadLinkKind.File, "/Users/test/.codex/session.json")
        ];
        IReadOnlySet<string> renderedPaths = FrozenSet<string>.Empty;
        IReadOnlyList<SquadLinkRecord> recordedLinks = [];
        IReadOnlyList<string> instanceListing = [];

        IReadOnlyList<SquadLinkAction> actions = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            harnessRow,
            recordedLinks,
            instanceListing);

        List<SquadLinkAction.Add> adds = actions.OfType<SquadLinkAction.Add>().ToList();

        Assert.Contains(adds, a => a.Record.RelativePath == "settings.json");
        Assert.Contains(adds, a => a.Record.RelativePath == "history.db");

        // The session file rewritten by rename is in NeverLinked and must NOT be linked.
        Assert.DoesNotContain(adds, a => a.Record.RelativePath == "session.json");
    }

    [Fact]
    public void PlanIsDeterministicPureFunctionWithNoSideEffects()
    {
        IReadOnlyList<SquadLinkRecord> mainListing =
        [
            new("settings.json", SquadLinkKind.File, "/main/settings.json"),
            new("agents", SquadLinkKind.Directory, "/main/agents"),
            new("agents/custom.md", SquadLinkKind.File, "/main/agents/custom.md"),
            new("agents/rendered.md", SquadLinkKind.File, "/main/agents/rendered.md")
        ];
        IReadOnlySet<string> renderedPaths = new HashSet<string> { "agents/rendered.md" };
        SquadInstanceRow row = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
        IReadOnlyList<SquadLinkRecord> recordedLinks =
        [
            new("old.json", SquadLinkKind.File, "/main/old.json")
        ];
        IReadOnlyList<string> instanceListing = ["local.txt"];

        IReadOnlyList<SquadLinkAction> firstRun = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        IReadOnlyList<SquadLinkAction> secondRun = SquadInstanceLinkPlanner.Plan(
            mainListing,
            renderedPaths,
            row,
            recordedLinks,
            instanceListing);

        Assert.Equal(firstRun.Count, secondRun.Count);
        for (int i = 0; i < firstRun.Count; i++) Assert.Equal(firstRun[i], secondRun[i]);
    }

    private static SquadLinkRecord CreateSampleRecord(string relativePath, SquadLinkKind kind, string targetPath)
    {
        return new SquadLinkRecord(relativePath, kind, targetPath);
    }

    private static IReadOnlyList<SquadLinkAction> PlanSample(
        IReadOnlyList<SquadLinkRecord> mainListing,
        IReadOnlySet<string> renderedPaths,
        SquadInstanceRow row,
        IReadOnlyList<SquadLinkRecord> recordedLinks,
        IReadOnlyList<string> instanceListing)
    {
        return SquadInstanceLinkPlanner.Plan(mainListing, renderedPaths, row, recordedLinks, instanceListing);
    }
}
