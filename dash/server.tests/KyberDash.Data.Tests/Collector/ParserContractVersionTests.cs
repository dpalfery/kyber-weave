using System.Text;
using KyberDash.Application;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Pins the per-registration parser contract versions <see cref="HarnessSourceRegistry"/> stamps on
/// discovered source units, and that a version change makes a stored checkpoint not reusable so the
/// next refresh re-reads the unit.
/// </summary>
/// <remarks>
/// The <c>droid</c>, <c>cursor</c> and <c>warp</c> mappers stopped producing split or text-derived
/// token counts, so their units declare contract "2": a checkpoint written under "1" must not be
/// reused, or the estimated rows it covers would stay in the store. The <c>copilot-cli</c> mapper
/// stores fresh input net of cache tokens and declares its own contract version for the same reason, and
/// <c>roo-code</c> declares "3" because its task id now comes from the task directory. Every other
/// registration keeps "1", so its stored checkpoints stay reusable and nothing else is re-read.
/// </remarks>
public class ParserContractVersionTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "fake-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ChangedHarnesses = ["droid", "cursor", "warp"];
    private const string RooHarnessId = "roo-code";
    private const string CopilotCliHarnessId = "copilot-cli";

    [Fact]
    public void DiscoverAll_StampsVersionTwoOnDroidCursorAndWarpUnitsAndVersionOneOnEveryOtherRegistration()
    {
        SourceDiscoveryResult result = new HarnessSourceRegistry().DiscoverAll(
            new SourceDiscoveryContext(HomeRoot, StateRoot, FilesForEveryRegistration()));

        foreach (string harnessId in ChangedHarnesses)
        {
            List<SourceUnit> units = [.. result.Sources.Where(unit => unit.HarnessId == harnessId)];
            Assert.NotEmpty(units);
            Assert.All(units, static unit => Assert.Equal("2", unit.ParserContractVersion));
        }

        List<SourceUnit> rooUnits = [.. result.Sources.Where(static unit => unit.HarnessId == RooHarnessId)];
        Assert.NotEmpty(rooUnits);
        Assert.All(rooUnits, static unit => Assert.Equal(HarnessSourceRegistry.RooTaskDirectoryIdParserContractVersion, unit.ParserContractVersion));

        List<SourceUnit> copilotUnits = [.. result.Sources.Where(static unit => unit.HarnessId == CopilotCliHarnessId)];
        Assert.NotEmpty(copilotUnits);
        Assert.All(copilotUnits, static unit => Assert.Equal(HarnessSourceRegistry.CopilotCliFreshInputParserContractVersion, unit.ParserContractVersion));

        List<SourceUnit> others = [.. result.Sources.Where(static unit => !ChangedHarnesses.Contains(unit.HarnessId) && unit.HarnessId != RooHarnessId && unit.HarnessId != CopilotCliHarnessId)];
        Assert.True(
            others.Select(static unit => unit.HarnessId).Distinct(StringComparer.Ordinal).Count() >= 13,
            "The fixture should discover units for most other registrations.");
        Assert.All(others, static unit => Assert.Equal(HarnessSourceRegistry.DefaultParserContractVersion, unit.ParserContractVersion));
        Assert.Equal("1", HarnessSourceRegistry.DefaultParserContractVersion);
    }

    [Theory]
    [InlineData("droid")]
    [InlineData("cursor")]
    [InlineData("warp")]
    [InlineData("copilot-cli")]
    public void CheckpointIsReusable_RefusesAVersionOneCheckpointForAChangedHarnessUnit(string harnessId)
    {
        SourceUnit unit = DiscoveredUnit(harnessId);

        bool reusable = CanonRefreshEngine.CheckpointIsReusable(
            CheckpointStampedOne(unit),
            new CoverageRequest(unit.RevisionToken, unit.ParserContractVersion));

        Assert.False(reusable);
    }

    [Fact]
    public void CheckpointIsReusable_KeepsAVersionOneCheckpointReusableForAClaudeCodeUnit()
    {
        SourceUnit unit = DiscoveredUnit("claude-code");

        bool reusable = CanonRefreshEngine.CheckpointIsReusable(
            CheckpointStampedOne(unit),
            new CoverageRequest(unit.RevisionToken, unit.ParserContractVersion));

        Assert.True(reusable);
    }

    private static SourceUnit DiscoveredUnit(string harnessId)
    {
        SourceDiscoveryResult result = new HarnessSourceRegistry().DiscoverForHarness(
            harnessId,
            new SourceDiscoveryContext(HomeRoot, StateRoot, FilesForEveryRegistration()));
        return result.Sources.First(unit => unit.HarnessId == harnessId);
    }

    private static SourceCheckpoint CheckpointStampedOne(SourceUnit unit) => new(
        HarnessId: unit.HarnessId,
        SourceKey: unit.SourceKey,
        ProviderId: unit.ProviderId,
        ParserId: unit.ParserId,
        ParserContractVersion: "1",
        Format: unit.Format,
        SourceRootLabel: unit.SourceRootLabel,
        RevisionToken: unit.RevisionToken,
        CoveredFromUtc: "2026-11-01T00:00:00.000Z",
        CoveredThroughUtc: "2026-11-24T00:00:00.000Z",
        LastAttemptUtc: "2026-11-24T00:00:00.000Z",
        LastSuccessUtc: "2026-11-24T00:00:00.000Z",
        LastStatus: "ok",
        LastErrorCode: null,
        UnitCount: 1,
        RecordCount: 1);

    private static MemoryHarnessFileSystem FilesForEveryRegistration()
    {
        MemoryHarnessFileSystem fs = new();
        string[] homeFiles =
        [
            ".claude/projects/repo/session-1.jsonl",
            ".codex/sessions/2026/11/15/rollout-1.jsonl",
            ".copilot/session-state/sess-1/events.jsonl",
            ".gemini/antigravity/conversations/conv-1.pb",
            ".gemini/antigravity-cli/conversations/cli-1.pb",
            ".commandcode/projects/my-repo/cc-1.jsonl",
            ".local/share/kilo/kilo.db",
            "Library/Application Support/Code/User/globalStorage/kilocode.kilo-code/tasks/k1/ui_messages.json",
            ".local/share/devin/cli/transcripts/devin-1.json",
            ".local/share/devin/cli/sessions.db",
            "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite",
            ".factory/sessions/proj/droid-1.jsonl",
            ".factory/sessions/proj/droid-1.settings.json",
            "Library/Application Support/Cursor/User/globalStorage/state.vscdb",
            ".cursor/projects/my-proj/agent-transcripts/ca-1.jsonl",
            "Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/tasks/c1/ui_messages.json",
            ".cline/data/sessions/cs1/cs1.messages.json",
            "Library/Application Support/Code/User/globalStorage/rooveterinaryinc.roo-cline/tasks/r1/ui_messages.json",
            "Library/Application Support/Code/User/workspaceStorage/ws1/chatSessions/chat-1.jsonl",
            ".pi/agent/sessions/proj/pi-1.jsonl",
            ".kimi-code/sessions/wd_repo_1234567890ab/sess-1/wire.jsonl",
        ];

        foreach (string relative in homeFiles)
        {
            fs.AddFile(Path.Combine(HomeRoot, relative), BaselineUtc);
        }

        fs.AddFile(Path.Combine(StateRoot, SettingsRepository.AntigravityStatusLineRelativePath), BaselineUtc);
        return fs;
    }

    /// <summary>In-memory <see cref="IHarnessFileSystem"/> holding empty JSON files at fixed write times.</summary>
    private sealed class MemoryHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, DateTimeOffset> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, DateTimeOffset lastWriteUtc)
        {
            string normalized = Normalize(path);
            files[normalized] = lastWriteUtc;
            for (string? dir = Path.GetDirectoryName(normalized); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                _ = directories.Add(dir);
            }
        }

        public bool DirectoryExists(string path) => directories.Contains(Normalize(path));

        public bool FileExists(string path) => files.ContainsKey(Normalize(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string root = Normalize(directoryPath);
            return files.Keys
                .Where(file => IsUnder(file, root, recursive) && Matches(Path.GetFileName(file), searchPattern))
                .OrderBy(static file => file, StringComparer.Ordinal)
                .ToList();
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string root = Normalize(directoryPath);
            return directories
                .Where(dir => string.Equals(Path.GetDirectoryName(dir), root, StringComparison.Ordinal))
                .OrderBy(static dir => dir, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(Normalize(filePath), out lastWriteUtc))
            {
                lengthBytes = Encoding.UTF8.GetByteCount("{}");
                return true;
            }

            lengthBytes = 0;
            return false;
        }

        public string ReadAllText(string filePath) =>
            files.ContainsKey(Normalize(filePath))
                ? "{}"
                : throw new FileNotFoundException("Memory file not found.", filePath);

        public string GetFullPath(string path) => Normalize(path);

        private static string Normalize(string path) =>
            Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

        private static bool IsUnder(string file, string root, bool recursive)
        {
            string? parent = Path.GetDirectoryName(file);
            if (parent is null)
            {
                return false;
            }

            return string.Equals(parent, root, StringComparison.Ordinal)
                || (recursive && parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

        private static bool Matches(string fileName, string pattern) =>
            pattern.StartsWith("*.", StringComparison.Ordinal)
                ? fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
