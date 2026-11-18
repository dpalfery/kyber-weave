using KyberWeave.Application.Agents;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Agents;

/// <summary>Agent discovery and loading over the in-memory file system, with a relative root read against the fake working directory.</summary>
public sealed class AgentFileReaderTests
{
    private const string Work = "/kw/work";
    private const string Agent = "---\nname: architect\ndescription: Plans implementations.\n---\nPlan first.\n";

    private readonly InMemoryFileSystem _files = new();
    private readonly AgentFileReader _reader;

    public AgentFileReaderTests() =>
        _reader = new AgentFileReader(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: Work));

    [Fact]
    public void ARelativeRootIsReadAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/proj/.cursor/agents");
        _files.WriteAllText("/kw/work/proj/.cursor/agents/architect.agent.md", Agent);

        AgentSet all = _reader.LoadAll("proj");

        AgentModel agent = Assert.Single(all.Agents);
        Assert.Equal("architect", agent.RoleName);
        Assert.Equal(HarnessKind.Cursor, agent.Harness);
    }

    [Fact]
    public void AHarnessFilterKeepsOnlyThatHarnessesAgents()
    {
        _files.CreateDirectory("/kw/work/proj/.cursor/agents");
        _files.WriteAllText("/kw/work/proj/.cursor/agents/architect.agent.md", Agent);

        Assert.Empty(_reader.LoadAll("/kw/work/proj", HarnessKind.Claude).Agents);
        Assert.Single(_reader.LoadAll("/kw/work/proj", HarnessKind.Cursor).Agents);
    }

    [Fact]
    public void AMissingRootLoadsNothing()
    {
        Assert.Empty(_reader.LoadAll("absent").Agents);
    }
}
