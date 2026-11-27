using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Brief;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>squad brief</c> writes for a printed brief and for each refusal, over fake console and builder.</summary>
public sealed class SquadBriefCommandTests : IDisposable
{
    private readonly TestConsole _ansi = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new();

    public void Dispose() => _ansi.Dispose();

    [Fact]
    public void Execute_UnknownFormat_RefusesWithTheFormats()
    {
        int exit = Command(new RefusingBuilder("never reached")).Execute(null!, new SquadBriefSettings { Agent = "a", Format = "xml" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad brief: error: 'xml' is not a format. Use text or json.\n", Ansi());
        Assert.Equal(string.Empty, _streams.Output);
    }

    [Fact]
    public void Execute_BuilderRefuses_PrintsItsReasonEscaped()
    {
        int exit = Command(new RefusingBuilder("no [file]")).Execute(null!, new SquadBriefSettings { Agent = "a" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal("kyber-weave squad brief: error: no [file]\n", Ansi());
    }

    [Fact]
    public void Execute_Built_WritesTheBriefToStandardOutputNotTheMarkupConsole()
    {
        BriefResult result = new("agent", new BriefFacts(null, null, null), [], []);

        int exit = Command(new BuildingBuilder(result)).Execute(null!, new SquadBriefSettings { Agent = "a", Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(BriefFormatter.ToJson(result) + "\n", _streams.Output);
        Assert.Equal(string.Empty, Ansi());
    }

    private SquadBriefCommand Command(ISquadBriefBuilder builder)
    {
        InMemoryFileSystem files = new();
        FakePathResolver paths = new(files);
        FakeProcessEnvironment environment = new();
        ArbiterRepoRoot roots = new(files, paths, environment);
        return new(
            builder,
            new FakeKyberWeaveConfigRepository(),
            new SquadConsole(_ansi, _streams),
            environment,
            new PlanArguments(roots, files, paths, environment, _ansi),
            roots);
    }

    private string Ansi() => _ansi.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class RefusingBuilder(string refusal) : ISquadBriefBuilder
    {
        public string? ResolveAutonomy(string repositoryRoot, string? given, KyberWeaveConfig config) => null;

        public bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string error)
        {
            result = null;
            error = refusal;
            return false;
        }

        public bool AcceptsFacts(string? autonomy, string? mode, out string error)
        {
            error = string.Empty;
            return true;
        }
    }

    private sealed class BuildingBuilder(BriefResult built) : ISquadBriefBuilder
    {
        public string? ResolveAutonomy(string repositoryRoot, string? given, KyberWeaveConfig config) => null;

        public bool TryBuild(SquadBriefRequest request, out BriefResult? result, out string error)
        {
            result = built;
            error = string.Empty;
            return true;
        }

        public bool AcceptsFacts(string? autonomy, string? mode, out string error)
        {
            error = string.Empty;
            return true;
        }
    }
}
