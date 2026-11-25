using System.Globalization;
using System.Text;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Unit tests for <see cref="HarnessSourceRegistry"/>, <see cref="IHarnessSourceReader"/>,
/// and <see cref="IHarnessMapper"/> over an in-memory fake filesystem seam.
/// </summary>
public class HarnessSourceRegistryTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "fake-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SqliteCacheRoot_FollowsTheConfiguredStateDirectory()
    {
        string state = Path.Combine(HomeRoot, "configured-state");

        Assert.Equal(Path.Combine(Path.GetFullPath(state), "sqlite-ro"), HarnessSourceRegistry.SqliteCacheRoot(state));
        Assert.IsType<MicrosoftDataSqliteHarnessSource>(HarnessSourceRegistry.CreateSqliteSource(state));
    }

    [Fact]
    public void SqliteCacheRoot_RejectsABlankStateDirectory()
    {
        _ = Assert.Throws<ArgumentException>(() => HarnessSourceRegistry.SqliteCacheRoot(" "));
    }

    [Fact]
    public void Registry_CoversAll17Bl2IdsAndAll8EvidencedLegacyIds()
    {
        HarnessSourceRegistry registry = new();

        string[] expectedBl2Ids =
        [
            "antigravity",
            "antigravity-cli",
            "claude-code",
            "claude-desktop",
            "claude-unclassified",
            "codex-desktop",
            "commandcode",
            "copilot",
            "copilot-agent",
            "copilot-cli",
            "cursor",
            "cursor-agent",
            "droid",
            "kilo-shared-runtime",
            "opencode",
            "pi",
            "zcode",
        ];

        string[] expectedLegacyIds =
        [
            "kilo-vscode-legacy",
            "copilot-vscode",
            "devin",
            "warp",
            "kimi-code",
            "roo-code",
            "cline",
            "cline-cli",
        ];

        foreach (string id in expectedBl2Ids)
        {
            Assert.NotNull(registry.Resolve(id));
        }

        foreach (string id in expectedLegacyIds)
        {
            Assert.NotNull(registry.Resolve(id));
        }
    }

    [Fact]
    public void DiscoverAll_FindsClaudeCodexCopilotCliAntigravityAndStatusLineViaFakeFs()
    {
        FakeHarnessFileSystem fs = new();
        fs.AddFile(Combine(HomeRoot, ".claude/projects/repo/session-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".codex/sessions/2026/11/15/rollout-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".copilot/session-state/sess-1/events.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".gemini/antigravity/conversations/conv-1.pb"), "pb", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".gemini/antigravity/brain/conv-1/.system_generated/logs/transcript.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".gemini/antigravity-cli/conversations/cli-1.pb"), "pb", BaselineUtc);
        fs.AddFile(Combine(StateRoot, SettingsRepository.AntigravityStatusLineRelativePath), "{}", BaselineUtc);

        HarnessSourceRegistry registry = new();
        SourceDiscoveryResult result = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs));

        Assert.Empty(result.Problems);
        Assert.Contains(result.Sources, static s => s.HarnessId == "claude-code" && s.SourceKey.EndsWith("session-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "codex-cli" && s.SourceKey.EndsWith("rollout-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "copilot-cli" && s.SourceKey.EndsWith("events.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "antigravity" && s.SourceKey.EndsWith("conv-1.pb", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "antigravity" && s.SourceKey.EndsWith("transcript.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "antigravity-cli" && s.SourceKey.EndsWith("cli-1.pb", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "antigravity" && s.SourceKey.EndsWith("antigravity-statusline.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_LocalCompanionLargerThanTheExportCap_IsRefusedWithoutReadingIt()
    {
        FakeHarnessFileSystem fs = new();
        string transcript = Combine(HomeRoot, ".factory/sessions/proj/droid-1.jsonl");
        string companion = Combine(HomeRoot, ".factory/sessions/proj/droid-1.settings.json");
        fs.AddFile(transcript, "{}", BaselineUtc);
        fs.AddFile(companion, "{}", BaselineUtc);
        fs.ReportLength(companion, new HarnessSqliteLimits().ExportCapBytes + 1);
        HarnessSourceRegistry registry = new();
        SourceUnit unit = Assert.Single(
            registry.DiscoverForHarness("droid", new SourceDiscoveryContext(HomeRoot, StateRoot, fs)).Sources);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => registry.GetReader("droid")!.Read(unit, fs));

        Assert.StartsWith("SQLITE_EXPORT_CAP_EXCEEDED", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fs.GetFullPath(companion), fs.ReadPaths);
    }

    [Fact]
    public void DiscoverAll_FindsAllEvidencedTypeScriptPortRootsViaFakeFs()
    {
        FakeHarnessFileSystem fs = new();
        fs.AddFile(Combine(HomeRoot, ".commandcode/projects/my-repo/cc-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, "Library/Application Support/Code/User/globalStorage/kilocode.kilo-code/tasks/k1/ui_messages.json"), "[]", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".local/share/devin/cli/transcripts/devin-1.json"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".factory/sessions/proj/droid-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".factory/sessions/proj/droid-1.settings.json"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".cursor/projects/my-proj/agent-transcripts/ca-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, "Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/tasks/c1/ui_messages.json"), "[]", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".cline/data/sessions/cs1/cs1.messages.json"), "[]", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, "Library/Application Support/Code/User/globalStorage/rooveterinaryinc.roo-cline/tasks/r1/ui_messages.json"), "[]", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, "Library/Application Support/Code/User/workspaceStorage/ws1/chatSessions/chat-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".pi/agent/sessions/proj/pi-1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".kimi-code/sessions/wd_repo_1234567890ab/sess-1/wire.jsonl"), "{}", BaselineUtc);

        HarnessSourceRegistry registry = new();
        SourceDiscoveryResult result = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs));

        Assert.Empty(result.Problems);
        Assert.Contains(result.Sources, static s => s.HarnessId == "commandcode" && s.SourceKey.EndsWith("cc-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "kilo-vscode-legacy" && s.SourceKey.EndsWith("ui_messages.json", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "devin" && s.SourceKey.EndsWith("devin-1.json", StringComparison.Ordinal));

        SourceUnit droidUnit = Assert.Single(result.Sources, static s => s.HarnessId == "droid");
        Assert.EndsWith("droid-1.jsonl", droidUnit.SourceKey, StringComparison.Ordinal);
        Assert.NotNull(droidUnit.CompanionKey);
        Assert.EndsWith("droid-1.settings.json", droidUnit.CompanionKey, StringComparison.Ordinal);

        Assert.Contains(result.Sources, static s => s.HarnessId == "cursor" && s.SourceKey.EndsWith("ca-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "cline" && s.SourceKey.EndsWith("ui_messages.json", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "cline-cli" && s.SourceKey.EndsWith("cs1.messages.json", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "roo-code" && s.SourceKey.EndsWith("ui_messages.json", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "copilot-vscode" && s.SourceKey.EndsWith("chat-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "pi" && s.SourceKey.EndsWith("pi-1.jsonl", StringComparison.Ordinal));
        Assert.Contains(result.Sources, static s => s.HarnessId == "kimi-code" && s.SourceKey.EndsWith("wire.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscoverAll_AppliesOptionalSinceUtcFilterWhenSetAndReturnsAllWhenUnset()
    {
        FakeHarnessFileSystem fs = new();
        DateTimeOffset olderUtc = BaselineUtc.AddDays(-10);
        DateTimeOffset newerUtc = BaselineUtc.AddDays(-1);
        DateTimeOffset cutoffUtc = BaselineUtc.AddDays(-5);

        fs.AddFile(Combine(HomeRoot, ".claude/projects/repo/old.jsonl"), "{}", olderUtc);
        fs.AddFile(Combine(HomeRoot, ".commandcode/projects/repo/new.jsonl"), "{}", newerUtc);

        HarnessSourceRegistry registry = new();

        SourceDiscoveryResult unfiltered = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs, SinceUtc: null));
        Assert.Equal(2, unfiltered.Sources.Count);

        SourceDiscoveryResult filtered = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs, SinceUtc: cutoffUtc));
        SourceUnit surviving = Assert.Single(filtered.Sources);
        Assert.Equal("commandcode", surviving.HarnessId);
        Assert.EndsWith("new.jsonl", surviving.SourceKey, StringComparison.Ordinal);

        SourceDiscoveryResult futureCutoff = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs, SinceUtc: BaselineUtc.AddDays(1)));
        Assert.Empty(futureCutoff.Sources);
    }

    [Fact]
    public void Registry_PreservesNormalizeHarnessNameAliasFolds()
    {
        HarnessSourceRegistry registry = new();

        Assert.Equal("claude-code", HarnessSourceRegistry.NormalizeHarnessId("claude-desktop"));
        Assert.Equal("cursor", HarnessSourceRegistry.NormalizeHarnessId("cursor-agent"));

        FakeHarnessFileSystem fs = new();
        fs.AddFile(Combine(HomeRoot, ".claude/projects/repo/s1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".cursor/projects/repo/agent-transcripts/c1.jsonl"), "{}", BaselineUtc);

        SourceDiscoveryResult claudeDesktopResult = registry.DiscoverForHarness("claude-desktop", new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        SourceUnit claudeUnit = Assert.Single(claudeDesktopResult.Sources);
        Assert.Equal("claude-code", claudeUnit.HarnessId);

        SourceDiscoveryResult cursorAgentResult = registry.DiscoverForHarness("cursor-agent", new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        SourceUnit cursorUnit = Assert.Single(cursorAgentResult.Sources);
        Assert.Equal("cursor", cursorUnit.HarnessId);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("copilot-agent")]
    public void DiscoverForHarness_OtlpOnlyIdsReportNoLocalFilesAndNoProblems(string harnessId)
    {
        FakeHarnessFileSystem fs = new();
        HarnessSourceRegistry registry = new();

        Assert.True(registry.IsOtlpOnly(harnessId));
        IHarnessSourceReader? reader = registry.GetReader(harnessId);
        Assert.NotNull(reader);
        Assert.True(reader.IsOtlpOnly);

        SourceDiscoveryResult result = registry.DiscoverForHarness(harnessId, new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        Assert.Empty(result.Sources);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("zcode")]
    [InlineData("unknown-custom-harness")]
    public void DiscoverForHarness_UnevidencedZcodeOrUnknownIdEmitsSourceFormatUnevidencedProblem(string harnessId)
    {
        FakeHarnessFileSystem fs = new();
        HarnessSourceRegistry registry = new();

        Assert.Null(registry.GetReader(harnessId));

        SourceDiscoveryResult result = registry.DiscoverForHarness(harnessId, new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        Assert.Empty(result.Sources);
        CollectedProblem problem = Assert.Single(result.Problems);
        Assert.Equal(HarnessSourceRegistry.UnevidencedSourceFormatCode, problem.Code);
        Assert.Equal("SOURCE_FORMAT_UNEVIDENCED", problem.Code);
        Assert.Equal(CanonPricing.NormalizeHarnessName(harnessId), problem.HarnessId);
    }

    [Fact]
    public void DiscoverAll_WhenZcodeDatabaseExistsOnDisk_EmitsSourceFormatUnevidencedProblem()
    {
        FakeHarnessFileSystem fs = new();
        fs.AddFile(Combine(HomeRoot, ".zcode/cli/db/db.sqlite"), "sqlite", BaselineUtc);

        HarnessSourceRegistry registry = new();
        SourceDiscoveryResult result = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs));

        Assert.Empty(result.Sources);
        CollectedProblem problem = Assert.Single(result.Problems);
        Assert.Equal("SOURCE_FORMAT_UNEVIDENCED", problem.Code);
        Assert.Equal("zcode", problem.HarnessId);
        Assert.EndsWith("db.sqlite", problem.Location, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windsurf")]
    [InlineData("cascade")]
    [InlineData("aider")]
    public void Registry_ExcludesBl3IdsWithZeroDiscoveriesAndZeroReaders(string harnessId)
    {
        FakeHarnessFileSystem fs = new();
        fs.AddFile(Combine(HomeRoot, ".windsurf/sessions/w1.jsonl"), "{}", BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".aider.chat.history.md"), "# history", BaselineUtc);

        HarnessSourceRegistry registry = new();

        Assert.True(registry.IsExcluded(harnessId));
        Assert.Null(registry.GetReader(harnessId));

        SourceDiscoveryResult perHarness = registry.DiscoverForHarness(harnessId, new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        Assert.Empty(perHarness.Sources);
        Assert.Empty(perHarness.Problems);

        SourceDiscoveryResult all = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        Assert.Empty(all.Sources);
        Assert.Empty(all.Problems);
    }

    [Fact]
    public void TranscriptReaderRepository_DelegatesDiscoverLocalSourcesToHarnessSourceRegistry()
    {
        FakeHarnessFileSystem fs = new();
        fs.AddFile(
            Combine(HomeRoot, ".claude/projects/repo/session-1.jsonl"),
            "{\"type\":\"assistant\",\"sessionId\":\"session-a\",\"timestamp\":\"2026-01-01T00:00:01Z\",\"message\":{\"id\":\"msg-1\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":1}}}",
            BaselineUtc);
        fs.AddFile(Combine(HomeRoot, ".commandcode/projects/repo/cc-1.jsonl"), "{}", BaselineUtc);

        TranscriptReaderRepository repository = new(HomeRoot, fs);
        IReadOnlyList<RefreshSourceInput> sources = repository.DiscoverLocalSources(BaselineUtc.AddHours(-1));

        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, static s => s.HarnessId == "claude-code" && s.Format == TranscriptReaderRepository.ClaudeFormat);
        Assert.Contains(sources, static s => s.HarnessId == "commandcode");
    }

    [Fact]
    public void ReaderAndMapperContracts_SupportPureInMemoryReadAndMapWithoutClockOrDisk()
    {
        FakeHarnessFileSystem fs = new();
        string path = Combine(HomeRoot, ".commandcode/projects/repo/s1.jsonl");
        fs.AddFile(path, "{\"type\":\"turn\"}\n", BaselineUtc);

        HarnessSourceRegistry registry = new();
        IHarnessSourceReader? reader = registry.GetReader("commandcode");
        Assert.NotNull(reader);

        SourceDiscoveryResult discovered = registry.DiscoverForHarness("commandcode", new SourceDiscoveryContext(HomeRoot, StateRoot, fs));
        SourceUnit unit = Assert.Single(discovered.Sources);

        NativeSourceUnit? nativeUnit = reader.Read(unit, fs);
        Assert.NotNull(nativeUnit);
        Assert.Equal("{\"type\":\"turn\"}\n", nativeUnit.RawText);
        Assert.Equal(16, nativeUnit.SizeBytes);

        IHarnessMapper mapper = new StubCommandCodeMapper();
        CollectedBatch batch = mapper.Map(nativeUnit, BaselineUtc);
        Assert.Equal("commandcode", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Equal(path, batch.Source.Locator);
    }

    [Fact]
    public void Kilo_DiscoversOneUnitPerConversationAndReadsTheExport()
    {
        string relative = ".local/share/kilo/kilo.db";
        SqliteRig rig = new(relative, new KiloSqliteConversationSource());
        rig.Database.AddRow("kilo-roots", Row(("id", "root-1"), ("title", "Root")));
        rig.Database.AddRow("kilo-revision", Row(("max_updated", 1_700_000_000_000L), ("message_count", 2L)));
        rig.Database.AddRow("kilo-root", Row(("id", "root-1"), ("title", "Root")));

        SourceUnit unit = rig.DiscoverSingle("kilo-shared-runtime");

        rig.AssertUnit(unit, "kilo-shared-runtime", "kilo", "kilo-sqlite-reader", "kilo-sqlite", "root-1");
        Assert.Contains("\"root-1\"", rig.ReadExport("kilo-shared-runtime", unit), StringComparison.Ordinal);
    }

    [Fact]
    public void Warp_DiscoversOneUnitPerConversationAndReadsTheExport()
    {
        string relative = "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite";
        SqliteRig rig = new(relative, new WarpSqliteConversationSource());
        rig.Database.AddRow("warp.conversations", Row(("conversation_id", "conv-1"), ("last_modified_at", "2026-11-01 10:00:00")));
        rig.Database.AddRow(
            "warp.conversation",
            Row(("conversation_id", "conv-1"), ("conversation_data", "{}"), ("last_modified_at", "2026-11-01 10:00:00")));

        SourceUnit unit = rig.DiscoverSingle("warp");

        rig.AssertUnit(unit, "warp", "warp", "warp-sqlite-reader", "warp-sqlite", "conv-1");
        Assert.Contains("conv-1", rig.ReadExport("warp", unit), StringComparison.Ordinal);
    }

    [Fact]
    public void Devin_DiscoversOneSqliteUnitPerSessionAndKeepsTranscriptUnits()
    {
        string relative = ".local/share/devin/cli/sessions.db";
        SqliteRig rig = new(relative, new DevinSqliteConversationSource());
        rig.Files.AddFile(Combine(HomeRoot, ".local/share/devin/cli/transcripts/devin-1.json"), "{}", BaselineUtc);
        rig.Database.AddRow(
            "devin.conversations",
            Row(("id", "sess-1"), ("last_activity_at", "2026-11-01T10:00:00Z"), ("node_count", 3L)));
        rig.Database.AddRow(
            "devin.session",
            Row(
                ("id", "sess-1"),
                ("model", "m"),
                ("title", "t"),
                ("working_directory", "/w"),
                ("created_at", "2026-11-01T09:00:00Z"),
                ("last_activity_at", "2026-11-01T10:00:00Z"),
                ("metadata", null)));

        SourceDiscoveryResult result = rig.Registry.DiscoverForHarness("devin", rig.Context());

        Assert.Empty(result.Problems);
        SourceUnit transcript = Assert.Single(result.Sources, static s => s.Format == "devin-transcript-json");
        Assert.EndsWith("devin-1.json", transcript.SourceKey, StringComparison.Ordinal);
        Assert.Equal(HarnessSourceRegistry.DefaultParserContractVersion, transcript.ParserContractVersion);
        SourceUnit unit = Assert.Single(result.Sources, static s => s.Format == "devin-sqlite");
        rig.AssertUnit(unit, "devin", "devin", "devin-reader", "devin-sqlite", "sess-1");
        Assert.Contains("sess-1", rig.ReadExport("devin", unit), StringComparison.Ordinal);
        Assert.Equal("{}", rig.Registry.GetReader("devin")!.Read(transcript, rig.Files)!.RawText);
    }

    [Fact]
    public void Cursor_DiscoversOneVscdbUnitPerComposerAndKeepsAgentTranscripts()
    {
        string relative = "Library/Application Support/Cursor/User/globalStorage/state.vscdb";
        SqliteRig rig = new(relative, new CursorSqliteConversationSource());
        rig.Files.AddFile(Combine(HomeRoot, ".cursor/projects/my-proj/agent-transcripts/ca-1.jsonl"), "{}", BaselineUtc);
        rig.Database.AddRow("cursor-composers", Row(("composer_id", "comp-1"), ("bubble_count", 1L), ("max_rowid", 5L)));
        rig.Database.AddRow(
            "cursor-bubbles",
            Row(
                ("bubble_key", "bubbleId:comp-1:b1"),
                ("input_tokens", 1L),
                ("output_tokens", 2L),
                ("model", "m"),
                ("created_at", "2026-11-01T10:00:00Z"),
                ("request_id", "r"),
                ("user_text", "hi"),
                ("text_length", 2L),
                ("bubble_type", 1L),
                ("context_window", null),
                ("code_blocks", null)));

        SourceDiscoveryResult result = rig.Registry.DiscoverForHarness("cursor", rig.Context());

        Assert.Empty(result.Problems);
        SourceUnit agent = Assert.Single(result.Sources, static s => s.Format == "cursor-agent-jsonl");
        Assert.EndsWith("ca-1.jsonl", agent.SourceKey, StringComparison.Ordinal);
        Assert.Equal(HarnessSourceRegistry.NoEstimatedTokensParserContractVersion, agent.ParserContractVersion);
        SourceUnit unit = Assert.Single(result.Sources, static s => s.Format == "cursor-vscdb");
        rig.AssertUnit(unit, "cursor", "cursor", "cursor-vscdb-reader", "cursor-vscdb", "comp-1");
        Assert.Contains("bubbleId:comp-1:b1", rig.ReadExport("cursor", unit), StringComparison.Ordinal);
        Assert.Equal("{}", rig.Registry.GetReader("cursor")!.Read(agent, rig.Files)!.RawText);
    }

    [Fact]
    public void DiscoverAll_UnreadableWarpDatabase_YieldsOneUnreadableProblem()
    {
        string relative = "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite";
        FakeHarnessFileSystem fs = new();
        string path = Combine(HomeRoot, relative);
        fs.AddFile(path, "sqlite", BaselineUtc);
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.FailOpen(path);

        HarnessSourceRegistry registry = new(sqlite);
        SourceDiscoveryResult result = registry.DiscoverAll(new SourceDiscoveryContext(HomeRoot, StateRoot, fs));

        CollectedProblem problem = Assert.Single(result.Problems);
        Assert.Equal("SQLITE_SOURCE_UNREADABLE", problem.Code);
        Assert.Equal("warp", problem.HarnessId);
        Assert.Empty(result.Sources);
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(static v => v.Key, static v => v.Value, StringComparer.Ordinal);

    private sealed class SqliteRig
    {
        private readonly string databasePath;

        public SqliteRig(string relativePath, ISqliteConversationSource source)
        {
            databasePath = Combine(HomeRoot, relativePath);
            Files = new FakeHarnessFileSystem();
            Files.AddFile(databasePath, "sqlite", BaselineUtc);
            Sqlite = new InMemoryHarnessSqliteSource();
            Database = Sqlite.AddDatabase(databasePath);
            foreach ((string table, IReadOnlyList<string> columns) in source.RequiredColumns)
            {
                Database.AddTable(table, [.. columns]);
            }

            Registry = new HarnessSourceRegistry(Sqlite);
        }

        public FakeHarnessFileSystem Files { get; }

        public InMemoryHarnessSqliteSource Sqlite { get; }

        public FakeDatabase Database { get; }

        public HarnessSourceRegistry Registry { get; }

        public SourceDiscoveryContext Context() => new(HomeRoot, StateRoot, Files);

        public SourceUnit DiscoverSingle(string harnessId)
        {
            SourceDiscoveryResult result = Registry.DiscoverForHarness(harnessId, Context());
            Assert.Empty(result.Problems);
            return Assert.Single(result.Sources);
        }

        public void AssertUnit(SourceUnit unit, string harnessId, string providerId, string parserId, string format, string conversationId)
        {
            Assert.Equal(harnessId, unit.HarnessId);
            Assert.Equal(providerId, unit.ProviderId);
            Assert.Equal(parserId, unit.ParserId);
            Assert.Equal(format, unit.Format);
            Assert.Equal("4", HarnessSourceRegistry.SqliteRowExportParserContractVersion);
            Assert.Equal(HarnessSourceRegistry.SqliteRowExportParserContractVersion, unit.ParserContractVersion);
            Assert.Equal(databasePath + "#" + conversationId, unit.SourceKey);
        }

        public string ReadExport(string harnessId, SourceUnit unit)
        {
            NativeSourceUnit? native = Registry.GetReader(harnessId)!.Read(unit, Files);
            Assert.NotNull(native);
            Assert.NotNull(native.RawText);
            return native.RawText;
        }
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class StubCommandCodeMapper : IHarnessMapper
    {
        public string HarnessId => "commandcode";

        public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc) =>
            new(
                Source: new CollectedSource(HarnessId, SourceKind.File, unit.Unit.SourceKey),
                Sessions: [],
                Records: [],
                Problems: []);
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, FakeFileEntry> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = NormalizePath(path);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            files[normalized] = new FakeFileEntry(content, bytes.LongLength, lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public List<string> ReadPaths { get; } = [];

        /// <summary>Makes the file report <paramref name="lengthBytes"/> as its size while its content stays the same.</summary>
        public void ReportLength(string path, long lengthBytes)
        {
            string normalized = NormalizePath(path);
            files[normalized] = files[normalized] with { LengthBytes = lengthBytes };
        }

        public bool DirectoryExists(string path) =>
            directories.Contains(NormalizePath(path));

        public bool FileExists(string path) =>
            files.ContainsKey(NormalizePath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = NormalizePath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (parent is null)
                {
                    continue;
                }

                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    : string.Equals(parent, normalizedRoot, StringComparison.Ordinal);

                if (inScope && MatchesPattern(Path.GetFileName(filePath), searchPattern))
                {
                    matches.Add(filePath);
                }
            }

            return matches;
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string normalizedRoot = NormalizePath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static d => d, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(NormalizePath(filePath), out FakeFileEntry? entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath)
        {
            string normalized = NormalizePath(filePath);
            ReadPaths.Add(normalized);
            if (!files.TryGetValue(normalized, out FakeFileEntry? entry))
            {
                throw new FileNotFoundException("Fake file not found.", normalized);
            }

            return entry.Content;
        }

        public string GetFullPath(string path) => NormalizePath(path);

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record FakeFileEntry(string Content, long LengthBytes, DateTimeOffset LastWriteUtc);
    }
}
