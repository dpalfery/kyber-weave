using KyberWeave.Application.Squad.Packaging;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Packaging;

/// <summary>Where the pack service puts archives and which directory it looks for the source in, over fakes and a fake working directory.</summary>
public sealed class SquadPackServiceTests
{
    private const string Work = "/kw/work";

    private readonly RecordingPackager _packager = new();
    private readonly RecordingLocator _locator = new();
    private readonly SquadPackService _service;

    public SquadPackServiceTests() =>
        _service = new SquadPackService(
            new FakeProcessEnvironment(currentDirectory: Work),
            new FakePathResolver(new InMemoryFileSystem()),
            _locator,
            _packager);

    [Fact]
    public void ABlankOutDirectoryPacksUnderArtifactsInTheWorkingDirectory()
    {
        SquadPackOutcome outcome = _service.Pack(new SquadPackRequest("apm", "1.2.3", null, "0.0.1"));

        Assert.Equal(SquadPackStatus.Packed, outcome.Status);
        Assert.Equal(Work, _locator.LookedIn);
        Assert.Equal("/kw/work/artifacts", _packager.OutDirectory);
    }

    [Fact]
    public void ARelativeOutDirectoryIsResolvedAgainstTheWorkingDirectory()
    {
        _ = _service.Pack(new SquadPackRequest("plugins", "1.2.3", "out/../dist", "0.0.1"));

        Assert.Equal("/kw/work/dist", _packager.OutDirectory);
    }

    [Fact]
    public void AnAbsoluteOutDirectoryIsKept()
    {
        _ = _service.Pack(new SquadPackRequest("apm", "1.2.3", "/elsewhere/out", "0.0.1"));

        Assert.Equal("/elsewhere/out", _packager.OutDirectory);
    }

    [Fact]
    public void AnUnknownFormatIsRefusedBeforeAnythingIsLocated()
    {
        SquadPackOutcome outcome = _service.Pack(new SquadPackRequest("zip", null, null, "0.0.1"));

        Assert.Equal(SquadPackStatus.InvalidFormat, outcome.Status);
        Assert.Null(_locator.LookedIn);
    }

    private sealed class RecordingLocator : ISquadSourceLocator
    {
        public string? LookedIn { get; private set; }

        public string? Resolve(string workingDirectory)
        {
            LookedIn = workingDirectory;
            return "/kw/work/source";
        }
    }

    private sealed class RecordingPackager : ISquadPackager
    {
        public string? OutDirectory { get; private set; }

        public void ValidateSource(string sourcePath)
        {
        }

        public string? ResolveCompanionSkillsRoot(string sourcePath) => null;

        public void ValidateComposedTree(string sourcePath, string? companionSkillsRoot, string version, Action<string> warn)
        {
        }

        public string PackApm(string sourcePath, string outDirectory, string version, string? companionSkillsRoot)
        {
            OutDirectory = outDirectory;
            return Path.Combine(outDirectory, "a.zip");
        }

        public string PackPlugins(string sourcePath, string outDirectory, string version, string? companionSkillsRoot)
        {
            OutDirectory = outDirectory;
            return Path.Combine(outDirectory, "p.zip");
        }

        public (string ApmArchivePath, string PluginsArchivePath, string ChecksumFilePath) PackAll(
            string sourcePath,
            string outDirectory,
            string version,
            string? companionSkillsRoot)
        {
            OutDirectory = outDirectory;
            return ("a", "p", "c");
        }
    }
}
