using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     The canonical profile snapshot store reads and writes through the file-system seam, so every case here runs over
///     the in-memory file system and a fake clock.
/// </summary>
public sealed class SquadCanonicalProfileStoreSeamTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly string _home;
    private readonly string _profilesDirectory;
    private readonly SquadCanonicalProfileStore _store;

    public SquadCanonicalProfileStoreSeamTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _home = Path.Combine(_files.Root, "home");
        _profilesDirectory = Path.Combine(_home, ".kyber-weave", "squad", "profiles");
        _store = new SquadCanonicalProfileStore(new FakeSquadUserPaths(Path.Combine(_files.Root, "appdata"), _home), _files);
    }

    [Fact]
    public void WriteThenReadRoundTripsProfilesAndAgentMapThroughTheSeam()
    {
        _store.Write(Snapshot("1.2.3", ("architect", "deep-planning"), ("implementer", "general")));

        Assert.True(_files.FileExists(Path.Combine(_profilesDirectory, "1.2.3.yml")));
        SquadCanonicalProfiles? reread = _store.TryRead("1.2.3");
        Assert.NotNull(reread);
        Assert.Equal("1.2.3", reread.SquadVersion);
        Assert.Equal("claude-sonnet-5-5", reread.Profiles.Profiles["general"].HarnessModels["claude"]);
        Assert.Equal("deep-planning", reread.AgentProfiles["ARCHITECT"]);
    }

    [Fact]
    public void WriteOverwritesAnExistingSnapshotAndLeavesNoTemporaryFile()
    {
        _store.Write(Snapshot("1.2.3", ("architect", "deep-planning")));
        _store.Write(Snapshot("1.2.3", ("reviewer", "general")));

        SquadCanonicalProfiles? reread = _store.TryRead("1.2.3");
        Assert.NotNull(reread);
        Assert.Equal("general", reread.AgentProfiles["reviewer"]);
        Assert.False(reread.AgentProfiles.ContainsKey("architect"));
        Assert.Empty(_files.EnumerateFiles(_profilesDirectory, "*.tmp", recursive: false));
        Assert.Single(_files.EnumerateFiles(_profilesDirectory, "*.yml", recursive: false));
    }

    [Fact]
    public void TryReadReturnsNullWhenTheDirectoryIsMissingTheFileIsCorruptOrThePathIsADirectory()
    {
        Assert.Null(_store.TryRead("1.2.3"));
        Assert.Null(_store.TryReadLatest());

        _files.CreateDirectory(_profilesDirectory);
        _files.WriteAllText(Path.Combine(_profilesDirectory, "1.2.3.yml"), "schema: x\nprofiles: [unclosed\n");
        _files.CreateDirectory(Path.Combine(_profilesDirectory, "1.2.4.yml"));

        Assert.Null(_store.TryRead("1.2.3"));
        Assert.Null(_store.TryRead("1.2.4"));
    }

    [Fact]
    public void TryReadLatestPrefersTheNewestWriteAndSkipsCorruptSnapshots()
    {
        _files.CreateDirectory(_profilesDirectory);
        _files.WriteAllText(Path.Combine(_profilesDirectory, "1.2.0.yml"), "not yaml at all: [unclosed");
        _store.Write(Snapshot("1.2.3", ("architect", "deep-planning")));
        _clock.Advance(TimeSpan.FromHours(1));
        _store.Write(Snapshot("1.2.2", ("architect", "deep-planning")));

        SquadCanonicalProfiles? latest = _store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.2.2", latest.SquadVersion);
    }

    [Fact]
    public void TryReadLatestBreaksWriteTimeTiesByNumericVersion()
    {
        _store.Write(Snapshot("1.9.0", ("architect", "deep-planning")));
        _store.Write(Snapshot("1.10.0", ("architect", "deep-planning")));

        SquadCanonicalProfiles? latest = _store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.10.0", latest.SquadVersion);
    }

    [Fact]
    public void TryReadReadsASnapshotWrittenWithAByteOrderMark()
    {
        _store.Write(Snapshot("1.2.3", ("architect", "deep-planning")));
        string path = Path.Combine(_profilesDirectory, "1.2.3.yml");
        byte[] bytes = _files.ReadAllBytes(path);
        _files.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. bytes]);

        Assert.NotNull(_store.TryRead("1.2.3"));
    }

    private static SquadCanonicalProfiles Snapshot(string version, params (string Agent, string Category)[] agents)
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal)
        {
            ["general"] = new SquadModelProfile(
                "inherit",
                new SortedDictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-sonnet-5-5" }),
            ["deep-planning"] = new SquadModelProfile("inherit", new SortedDictionary<string, string>(StringComparer.Ordinal))
        };
        return new SquadCanonicalProfiles(
            version,
            new SquadModelProfiles("kyber-squad.model-profiles/v1", profiles, "models.yml"),
            agents.ToDictionary(a => a.Agent, a => a.Category, StringComparer.Ordinal));
    }
}
