using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>review migrate-task-gates</c> resolves its paths against the process environment's directory, reads and writes
/// the configuration through the file system, and prints on the console it was given, so these cases run over the
/// in-memory file system and a test console with no disk and no terminal.
/// </summary>
public sealed class ReviewMigrateTaskGatesSeamTests
{
    private const string Home = "/kw/work";
    private const string Root = "/kw/work/repo";
    private const string ConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";

    private const string TwoAgents = """
        review:
          task-gates:
            csharp-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]
            dal-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]
        """;

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeRepositoryTreeProbe _tree = new();
    private readonly TestConsole _console = new();

    public ReviewMigrateTaskGatesSeamTests() => _console.Profile.Width = 200;

    private int Run(ReviewMigrateTaskGatesSettings settings, FakeKyberWeaveConfigRepository? configs = null)
    {
        ReviewMigrateTaskGatesCommand command = new(
            configs ?? new FakeKyberWeaveConfigRepository(),
            _files,
            _tree,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: Home),
            _console);
        return command.Execute(null!, settings, CancellationToken.None);
    }

    private void WriteConfig(string yaml)
    {
        _files.CreateDirectory("/kw/work/repo/.kyber-weave");
        _files.WriteAllText(ConfigPath, yaml);
    }

    [Fact]
    public void ARelativePath_IsResolvedAgainstTheEnvironmentsCurrentDirectory_AndAPreviewWritesNothing()
    {
        WriteConfig(TwoAgents);

        int exit = Run(new ReviewMigrateTaskGatesSettings { Path = "repo", Config = ".kyber-weave/kyber-weave.yml" });

        Assert.Equal(0, exit);
        Assert.Equal(TwoAgents, _files.ReadAllText(ConfigPath));
        Assert.Contains("preview, so nothing will be written", _console.Output, StringComparison.Ordinal);
        Assert.Contains(ConfigPath, _console.Output, StringComparison.Ordinal);
        Assert.Contains("-    csharp-dev:", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RewritesTheConfigurationThroughTheFileSystemAndSaysSo()
    {
        WriteConfig(TwoAgents);

        int exit = Run(new ReviewMigrateTaskGatesSettings
        {
            Path = "repo",
            Config = ".kyber-weave/kyber-weave.yml",
            Write = true
        });

        Assert.Equal(0, exit);
        string after = _files.ReadAllText(ConfigPath);
        Assert.Contains("technology-gates:", after, StringComparison.Ordinal);
        Assert.DoesNotContain("csharp-dev:", after, StringComparison.Ordinal);
        Assert.Contains("written", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedConfigurationThatDoesNotExist_IsRefusedWithTheResolvedRoot()
    {
        int exit = Run(new ReviewMigrateTaskGatesSettings { Path = "repo", Config = "missing.yml" });

        Assert.Equal(1, exit);
        Assert.Contains($"no kyber-weave.yml was found under '{Root}'", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfigurationReachedThroughALinkOutOfTheRepository_IsNeverWritten()
    {
        WriteConfig(TwoAgents);
        _tree.EscapingPaths.Add((Root, ".kyber-weave/kyber-weave.yml"));

        int exit = Run(new ReviewMigrateTaskGatesSettings
        {
            Path = "repo",
            Config = ".kyber-weave/kyber-weave.yml",
            Write = true
        });

        Assert.Equal(1, exit);
        Assert.Equal(TwoAgents, _files.ReadAllText(ConfigPath));
        Assert.Contains("symbolic link that leads outside", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableConfiguration_IsRefusedWithTheFileSystemsMessage()
    {
        WriteConfig(TwoAgents);
        _files.FailWhen(operation => operation.Name == nameof(IFileSystem.ReadAllBytes), new IOException("disk gone"));

        int exit = Run(new ReviewMigrateTaskGatesSettings { Path = "repo", Config = ".kyber-weave/kyber-weave.yml" });

        Assert.Equal(1, exit);
        Assert.Contains("could not be read. disk gone", _console.Output, StringComparison.Ordinal);
    }
}
