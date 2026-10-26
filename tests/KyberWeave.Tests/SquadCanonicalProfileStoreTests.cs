using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for the version-keyed canonical profile snapshot store the preset verbs
///     resolve on an installed CLI. A snapshot that cannot be read counts as absent so a
///     damaged home state fails closed with the update remedy instead of throwing.
/// </summary>
public sealed class SquadCanonicalProfileStoreTests
{
    [Fact]
    public void WriteThenReadRoundTripsProfilesAndAgentMap()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
        SquadCanonicalProfiles snapshot = CreateSnapshot("1.2.3");

        store.Write(snapshot);

        string expectedPath = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles",
            "1.2.3.yml");
        Assert.True(File.Exists(expectedPath), $"Expected snapshot file at '{expectedPath}'.");
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
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);

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
        Assert.Empty(Directory.GetFiles(profilesDirectory, "*.tmp"));
        Assert.Single(Directory.GetFiles(profilesDirectory, "*.yml"));
    }

    [Fact]
    public void TryReadReturnsExactVersionWhenMultipleSnapshotsExist()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
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
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
        store.Write(CreateSnapshot("1.2.3"));
        store.Write(CreateSnapshotWithAgent("1.2.4", "reviewer", "general"));
        string olderPath = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles",
            "1.2.4.yml");
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow - TimeSpan.FromHours(1));

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.2.3", latest.SquadVersion);
    }

    [Fact]
    public void TryReadLatestBreaksWriteTimeTiesByNumericVersion()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
        store.Write(CreateSnapshot("1.9.0"));
        store.Write(CreateSnapshot("1.10.0"));
        DateTime tied = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        File.SetLastWriteTimeUtc(Path.Combine(profilesDirectory, "1.9.0.yml"), tied);
        File.SetLastWriteTimeUtc(Path.Combine(profilesDirectory, "1.10.0.yml"), tied);

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        // A lexical tie-break crowns 1.9.0 because '9' sorts after '1'; the
        // newer release must win when the write times tie.
        Assert.NotNull(latest);
        Assert.Equal("1.10.0", latest.SquadVersion);
    }

    [Fact]
    public void TryReadReturnsNullWhenProfilesDirectoryIsMissing()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotFileIsCorrupt()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "1.2.3.yml"),
            "schema: kyber-squad.canonical-profiles/v1\nprofiles: [unclosed\n",
            Encoding.UTF8);
        SquadCanonicalProfileStore store = new(userPaths);

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotSchemaIsUnexpected()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "1.2.3.yml"),
            "schema: kyber-squad.model-profiles/v1\nsquad-version: 1.2.3\nprofiles: {}\nagents: {}\n",
            Encoding.UTF8);
        SquadCanonicalProfileStore store = new(userPaths);

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenEmbeddedVersionDoesNotMatchFileName()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
        store.Write(CreateSnapshot("1.2.4"));
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        File.Move(
            Path.Combine(profilesDirectory, "1.2.4.yml"),
            Path.Combine(profilesDirectory, "1.2.3.yml"));

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadReturnsNullWhenSnapshotPathIsADirectory()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        Directory.CreateDirectory(Path.Combine(profilesDirectory, "1.2.3.yml"));
        SquadCanonicalProfileStore store = new(userPaths);

        Assert.Null(store.TryRead("1.2.3"));
    }

    [Fact]
    public void TryReadLatestReturnsNullWhenProfilesDirectoryIsMissing()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);

        Assert.Null(store.TryReadLatest());
    }

    [Fact]
    public void TryReadLatestSkipsCorruptSnapshots()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string profilesDirectory = Path.Combine(
            userPaths.HomeDirectory,
            ".kyber-weave",
            "squad",
            "profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "1.2.3.yml"),
            "not yaml at all: [unclosed",
            Encoding.UTF8);
        SquadCanonicalProfileStore store = new(userPaths);
        store.Write(CreateSnapshot("1.2.4"));

        SquadCanonicalProfiles? latest = store.TryReadLatest();

        Assert.NotNull(latest);
        Assert.Equal("1.2.4", latest.SquadVersion);
    }

    [Fact]
    public void WriteThenReadRoundTripsEmptySections()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);
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
        ISquadUserPaths userPaths = new FakeSquadUserPathsWithCustomHome(homeDirectory);
        Assert.Throws<ArgumentException>(() => new SquadCanonicalProfileStore(userPaths));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../traversal")]
    [InlineData("a/b")]
    public void WriteRejectsVersionsThatEscapeTheProfilesDirectory(string version)
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);

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
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadCanonicalProfileStore store = new(userPaths);

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

    private sealed class FakeSquadUserPathsWithCustomHome(string homeDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory => "/dummy/appdata";
        public string HomeDirectory { get; } = homeDirectory;
    }
}
