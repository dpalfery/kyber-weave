using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for the version-keyed canonical profile snapshot store the preset verbs
///     resolve on an installed CLI. A snapshot that cannot be read counts as absent so a
///     damaged home state fails closed with the update remedy instead of throwing.
/// </summary>
public sealed class SquadCanonicalProfileStoreTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakeSquadUserPaths _userPaths;

    public SquadCanonicalProfileStoreTests()
    {
        _files = new InMemoryFileSystem(_clock);
        _userPaths = new FakeSquadUserPaths(Path.Combine(_files.Root, "appdata"));
    }

    private SquadCanonicalProfileStore NewStore() => new(_userPaths, _files);

    private string ProfilesDirectory => Path.Combine(_userPaths.HomeDirectory, ".kyber-weave", "squad", "profiles");

    [Fact]
    public void WriteThenReadRoundTripsProfilesAndAgentMap()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        SquadCanonicalProfiles snapshot = CreateSnapshot("1.2.3");

        store.Write(snapshot);

        string expectedPath = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles",
            "1.2.3.yml");
        Assert.True(_files.FileExists(expectedPath), $"Expected snapshot file at '{expectedPath}'.");
        SquadCanonicalProfiles? reread = store.TryRead("1.2.3");
        Assert.NotNull(reread);
        Assert.Equal("1.2.3", reread.SquadVersion);
        Assert.Equal(3, reread.Profiles.Profiles.Count);
        Assert.True(reread.Profiles.Profiles.TryGetValue("general", out SquadModelProfile? general));
        Assert.NotNull(general);
        Assert.Equal("inherit", general.Default);
        Assert.Equal("claude-sonnet-5-5", general.HarnessModels["claude"]);
        Assert.Equal("Grok 4.7 (copilot)", general.HarnessModels["copilot"]);
        Assert.True(reread.Profiles.Profiles.TryGetValue("deep-planning", out SquadModelProfile? deepPlanning));
        Assert.NotNull(deepPlanning);
        Assert.Equal("claude-sonnet-5-5[effort=high]", deepPlanning.HarnessModels["cursor"]);
        Assert.True(reread.Profiles.Profiles.TryGetValue("fast", out SquadModelProfile? fast));
        Assert.NotNull(fast);
        Assert.Equal("inherit", fast.Default);
        Assert.Empty(fast.HarnessModels);
        Assert.Equal(2, reread.AgentProfiles.Count);
        Assert.Equal("deep-planning", reread.AgentProfiles["architect"]);
        Assert.Equal("general", reread.AgentProfiles["implementer"]);
    }

    [Fact]
    public void WriteOverwritesAnExistingSnapshotAtomically()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();

        store.Write(CreateSnapshot("1.2.3"));
        store.Write(CreateSnapshotWithAgent("1.2.3", "reviewer", "general"));

        SquadCanonicalProfiles? reread = store.TryRead("1.2.3");
        Assert.NotNull(reread);
        Assert.Equal("general", reread.AgentProfiles["reviewer"]);
        Assert.False(reread.AgentProfiles.ContainsKey("architect"));
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        Assert.Empty(_files.EnumerateFiles(profilesDirectory, "*.tmp", recursive: false).ToArray());
        Assert.Single(_files.EnumerateFiles(profilesDirectory, "*.yml", recursive: false).ToArray());
    }

    [Fact]
    public void TryReadReturnsExactVersionWhenMultipleSnapshotsExist()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshot("1.2.3"));
        store.Write(CreateSnapshotWithAgent("1.2.4", "reviewer", "general"));

        SquadCanonicalProfiles? reread = store.TryRead("1.2.3");

        Assert.NotNull(reread);
        Assert.Equal("1.2.3", reread.SquadVersion);
        Assert.False(reread.AgentProfiles.ContainsKey("reviewer"));
    }

    [Fact]
    public void TryReadLatestReturnsTheMostRecentlyWrittenSnapshot()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshotWithAgent("1.2.4", "reviewer", "general"));
        _clock.Advance(TimeSpan.FromHours(1));
        store.Write(CreateSnapshot("1.2.3"));

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.2.3", latest.SquadVersion);
    }

    [Fact]
    public void TryReadLatestBreaksWriteTimeTiesByNumericVersion()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshot("1.9.0"));
        store.Write(CreateSnapshot("1.10.0"));

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        // A lexical tie-break crowns 1.9.0 because '9' sorts after '1'; the
        // newer release must win when the write times tie.
        Assert.NotNull(latest);
        Assert.Equal("1.10.0", latest.SquadVersion);
    }

    [Fact]
    public void SnapshotAgentRosterResolvesNamesCaseInsensitively()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshot("1.2.3"));

        SquadCanonicalProfiles? reread = store.TryRead("1.2.3");

        // The checkout roster is OrdinalIgnoreCase; the snapshot roster must match it,
        // or a differently-cased deployed identity rewrites on checkout and silently
        // skips once installed.
        Assert.NotNull(reread);
        Assert.True(reread.AgentProfiles.TryGetValue("ARCHITECT", out string? category));
        Assert.Equal("deep-planning", category);
    }

    [Fact]
    public void TryReadReturnsNullWhenProfilesDirectoryIsMissing()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotFileIsCorrupt()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        _files.CreateDirectory(profilesDirectory);
        _files.WriteAllText(Path.Combine(profilesDirectory, "1.2.3.yml"), "schema: kyber-squad.canonical-profiles/v1\nprofiles: [unclosed\n");
        SquadCanonicalProfileStore store = NewStore();

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotSchemaIsUnexpected()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        _files.CreateDirectory(profilesDirectory);
        _files.WriteAllText(Path.Combine(profilesDirectory, "1.2.3.yml"), "schema: kyber-squad.model-profiles/v1\nsquad-version: 1.2.3\nprofiles: {}\nagents: {}\n");
        SquadCanonicalProfileStore store = NewStore();

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenEmbeddedVersionDoesNotMatchFileName()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshot("1.2.4"));
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        _files.MoveFile(
            Path.Combine(profilesDirectory, "1.2.4.yml"),
            Path.Combine(profilesDirectory, "1.2.3.yml"));

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotPathIsADirectory()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        _files.CreateDirectory(Path.Combine(profilesDirectory, "1.2.3.yml"));
        SquadCanonicalProfileStore store = NewStore();

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadLatestReturnsNullWhenProfilesDirectoryIsMissing()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();

        Assert.Null(store.TryReadLatest());
    }

    [Fact]
    public void TryReadLatestSkipsCorruptSnapshots()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        _files.CreateDirectory(profilesDirectory);
        _files.WriteAllText(Path.Combine(profilesDirectory, "1.2.3.yml"), "not yaml at all: [unclosed");
        SquadCanonicalProfileStore store = NewStore();
        store.Write(CreateSnapshot("1.2.4"));

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.2.4", latest.SquadVersion);
    }

    [Fact]
    public void WriteThenReadRoundTripsEmptySections()
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();
        SquadCanonicalProfiles snapshot = new(
            "1.2.3",
            new SquadModelProfiles(
                "kyber-squad.model-profiles/v1",
                new SortedDictionary<string, SquadModelProfile>(StringComparer.Ordinal),
                "models.yml"),
            new Dictionary<string, string>(StringComparer.Ordinal));

        store.Write(snapshot);

        SquadCanonicalProfiles? reread = store.TryRead("1.2.3");
        Assert.NotNull(reread);
        Assert.Equal("1.2.3", reread.SquadVersion);
        Assert.Empty(reread.Profiles.Profiles);
        Assert.Empty(reread.AgentProfiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorWithEmptyOrWhitespaceHomeDirectoryThrowsArgumentException(string homeDirectory)
    {
        ISquadUserPaths userPaths = new FakeSquadUserPaths("/dummy/appdata", homeDirectory);
        Assert.Throws<ArgumentException>(() => new SquadCanonicalProfileStore(userPaths, _files));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../traversal")]
    [InlineData("a/b")]
    public void WriteRejectsVersionsThatEscapeTheProfilesDirectory(string version)
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();

        Assert.Throws<ArgumentException>(() => store.Write(CreateSnapshot(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../traversal")]
    [InlineData("a/b")]
    public void TryReadRejectsVersionsThatEscapeTheProfilesDirectory(string version)
    {
        FakeSquadUserPaths userPaths = _userPaths;
        SquadCanonicalProfileStore store = NewStore();

        Assert.Throws<ArgumentException>(() => store.TryRead(version));
    }

    private static SquadCanonicalProfiles CreateSnapshot(string version)
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal)
        {
            ["general"] = new SquadModelProfile(
                "inherit",
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["claude"] = "claude-sonnet-5-5",
                    ["copilot"] = "Grok 4.7 (copilot)"
                }),
            ["deep-planning"] = new SquadModelProfile(
                "inherit",
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cursor"] = "claude-sonnet-5-5[effort=high]"
                }),
            ["fast"] = new SquadModelProfile(
                "inherit",
                new SortedDictionary<string, string>(StringComparer.Ordinal))
        };
        Dictionary<string, string> agents = new(StringComparer.Ordinal)
        {
            ["architect"] = "deep-planning",
            ["implementer"] = "general"
        };
        return new SquadCanonicalProfiles(
            version,
            new SquadModelProfiles("kyber-squad.model-profiles/v1", profiles, "models.yml"),
            agents);
    }

    private static SquadCanonicalProfiles CreateSnapshotWithAgent(
        string version,
        string agentName,
        string category)
    {
        Dictionary<string, string> agents = new(StringComparer.Ordinal)
        {
            [agentName] = category
        };
        return new SquadCanonicalProfiles(
            version,
            CreateSnapshot(version).Profiles,
            agents);
    }
}
