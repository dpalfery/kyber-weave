using KyberWeave.Data.Configuration;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>What <c>docs onboarding</c> resolves, records and prints, over the shared file-system, path and console fakes.</summary>
public sealed class DocsOnboardingCommandTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";
    private const string ConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";
    private const string AgentsPath = "/kw/work/repo/AGENTS.md";
    private const string HostConfig = "ontology:\n  docs-root: docs\n";

    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };

    public DocsOnboardingCommandTests() => _console.Profile.Width = 400;

    public void Dispose() => _console.Dispose();

    [Fact]
    public void Execute_AnswerThatIsNotOneOfTheTwo_IsRefusedAndWritesNothing()
    {
        WriteConfig();

        int exit = Run("maybe");

        Assert.Equal(2, exit);
        Assert.Contains("'maybe' is not an onboarding answer", Output(), StringComparison.Ordinal);
        Assert.Equal(HostConfig, _files.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Execute_RelativeRoot_ResolvesAgainstTheWorkingDirectoryTheEnvironmentNames()
    {
        WriteConfig();

        int exit = Run("done", "repo");

        Assert.Equal(0, exit);
        Assert.Contains("onboarding: done", _files.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Contains("recorded in .kyber-weave/kyber-weave.yml", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RootThatDoesNotExist_IsRefused()
    {
        int exit = Run("done", "/kw/work/missing");

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave docs onboarding: error: directory '/kw/work/missing' does not exist.\n",
            Output());
    }

    [Fact]
    public void Execute_RootThatIsAFile_IsRefused()
    {
        _files.CreateDirectory(WorkingDirectory);
        _files.WriteAllText("/kw/work/file.txt", "x");

        int exit = Run("done", "/kw/work/file.txt");

        Assert.Equal(2, exit);
        Assert.Equal(
            "kyber-weave docs onboarding: error: '/kw/work/file.txt' is a file, not a directory.\n",
            Output());
    }

    [Fact]
    public void Execute_NoHostConfiguration_FailsWithThePointerToDocsInit()
    {
        _files.CreateDirectory(Root);

        int exit = Run("done", Root);

        Assert.Equal(1, exit);
        Assert.Contains("no host configuration was found under '/kw/work/repo'", Output(), StringComparison.Ordinal);
        Assert.Contains("run 'kyber-weave docs init .'", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ConfigThatIsASymbolicLink_IsNotWrittenThrough()
    {
        _files.CreateDirectory("/kw/work/repo/.kyber-weave");
        _files.WriteAllText("/kw/work/real.yml", HostConfig);
        _files.CreateFileLink(ConfigPath, "/kw/work/real.yml");

        int exit = Run("done", Root);

        Assert.Equal(1, exit);
        Assert.Contains("is a symbolic link. Refusing to write through a link.", Output(), StringComparison.Ordinal);
        Assert.Equal(HostConfig, _files.ReadAllText("/kw/work/real.yml"));
    }

    [Fact]
    public void Execute_ConfigThatCannotBeRead_FailsWithTheReason()
    {
        WriteConfig();
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && operation.Path == ConfigPath,
            new IOException("disk gone"));

        int exit = Run("done", Root);

        Assert.Equal(1, exit);
        Assert.Contains("could not be read: disk gone", Output(), StringComparison.Ordinal);
        Assert.Contains("check that the file is readable", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ConfigThatCannotBeWritten_FailsWithTheReason()
    {
        WriteConfig();
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.WriteAllTextAtomic) && operation.Path == ConfigPath,
            new UnauthorizedAccessException("denied"));

        int exit = Run("done", Root);

        Assert.Equal(1, exit);
        Assert.Contains("could not be written: denied", Output(), StringComparison.Ordinal);
        Assert.Equal(HostConfig, _files.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Execute_NoAgentsFile_RecordsTheAnswerAndWarnsThatNoBlockWasWritten()
    {
        WriteConfig();

        int exit = Run("declined", Root);

        Assert.Equal(0, exit);
        Assert.Contains("onboarding: declined", _files.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Contains("AGENTS.md does not exist, so no Config Reg block was written", Output(), StringComparison.Ordinal);
        Assert.False(_files.FileExists(AgentsPath));
    }

    [Fact]
    public void Execute_AgentsFile_GetsTheRegistryBlockWithTheAnswer()
    {
        WriteConfig();
        _files.WriteAllText(AgentsPath, "# Agents\n");

        int exit = Run("done", Root);

        Assert.Equal(0, exit);
        string agents = _files.ReadAllText(AgentsPath);
        Assert.StartsWith("# Agents\n", agents, StringComparison.Ordinal);
        Assert.Contains("onboarding", agents, StringComparison.Ordinal);
        Assert.Contains("Config Reg block in AGENTS.md refreshed", Output(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_AgentsFileThatIsASymbolicLink_FailsAfterRecordingTheAnswer()
    {
        WriteConfig();
        _files.WriteAllText("/kw/work/real-agents.md", "# Agents\n");
        _files.CreateFileLink(AgentsPath, "/kw/work/real-agents.md");

        int exit = Run("done", Root);

        Assert.Equal(1, exit);
        Assert.Contains("the answer was recorded, but '/kw/work/repo/AGENTS.md' is a symbolic link", Output(), StringComparison.Ordinal);
        Assert.Equal("# Agents\n", _files.ReadAllText("/kw/work/real-agents.md"));
    }

    [Fact]
    public void Execute_AnswerAlreadyRecorded_WritesNeitherFile()
    {
        WriteConfig();
        Assert.Equal(0, Run("done", Root));
        _files.WriteAllText(AgentsPath, "# Agents\n");
        Assert.Equal(0, Run("done", Root));
        string agentsAfterFirst = _files.ReadAllText(AgentsPath);
        string configAfterFirst = _files.ReadAllText(ConfigPath);
        _files.AddFault(FileSystemFault.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.WriteAllTextAtomic),
            new IOException("must not write")));

        int exit = Run("done", Root);

        Assert.Equal(0, exit);
        Assert.Equal(configAfterFirst, _files.ReadAllText(ConfigPath));
        Assert.Equal(agentsAfterFirst, _files.ReadAllText(AgentsPath));
    }

    private void WriteConfig()
    {
        _files.CreateDirectory("/kw/work/repo/.kyber-weave");
        _files.WriteAllText(ConfigPath, HostConfig);
    }

    private int Run(string answer, string? path = null)
    {
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory);
        FakePathResolver paths = new(_files);
        FileKyberWeaveConfigRepository configs = new(new FileRepositoryTreeProbe(_files, paths, environment), _files, paths, environment);
        DocsOnboardingCommand command = new(configs, _console, _files, paths, environment);
        DocsOnboardingSettings settings = path is null
            ? new DocsOnboardingSettings { Answer = answer }
            : new DocsOnboardingSettings { Answer = answer, Path = path };
        return command.Execute(null!, settings, CancellationToken.None);
    }

    private string Output() => _console.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
}
