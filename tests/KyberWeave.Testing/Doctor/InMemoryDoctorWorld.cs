using System.Text.Json;
using KyberWeave.Application.Doctor;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Doctor;

/// <summary>
/// The real repository doctor over an in-memory file system, a fake configuration repository, a scripted MCP server and
/// a fake environment, so a test of what <c>doctor</c> decides reads and writes no disk and starts no process. The
/// caller scripts git and CodeGraph through a <see cref="ScriptedProcessExecutor"/>.
/// </summary>
internal sealed class InMemoryDoctorWorld
{
    /// <summary>The reply a healthy server gives <c>initialize</c>.</summary>
    private const string Initialize = DoctorHarness.InitializeReply;

    /// <summary>Creates a world holding an empty repository root.</summary>
    internal InMemoryDoctorWorld()
    {
        Files = new InMemoryFileSystem();
        Root = Path.Combine(Files.Root, "kw", "repo");
        Locks = Path.Combine(Files.Root, "kw", "locks");
        Files.CreateDirectory(Root);
        Files.CreateDirectory(Locks);
        Paths = new FakePathResolver(Files);
        Environment = new FakeProcessEnvironment(
            currentDirectory: Path.Combine(Files.Root, "kw", "work"),
            homeDirectory: Path.Combine(Files.Root, "kw", "home"),
            isInputRedirected: true,
            isFileSystemCaseInsensitive: !Files.CaseSensitive);
        DoctorFiles = new InMemoryDoctorFileSystem(Files);
        ToolSettings = new ConfigFileToolSettings(Config);
        CodeGraphIndexProvisioner provisioner = new(
            DoctorFiles,
            new FakeCodeGraphIndexWorkspace(),
            Paths,
            Environment,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        Doctor = new RepositoryDoctorService(
            provisioner,
            new WorktreeIndexEnsurer(provisioner, DoctorFiles, ToolSettings),
            new McpServerLiveProbe(Mcp, Paths, Environment),
            new PostCheckoutHookInstaller(new InMemoryGitHookFiles(Files), DoctorFiles, ToolSettings, Paths, Environment),
            DoctorFiles,
            ToolSettings,
            Environment,
            Paths);
    }

    /// <summary>Gets the in-memory file system.</summary>
    internal InMemoryFileSystem Files { get; }

    /// <summary>Gets the repository root, a folder of <see cref="Files"/>.</summary>
    internal string Root { get; }

    /// <summary>Gets the directory the index lock lives in.</summary>
    internal string Locks { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    internal FakePathResolver Paths { get; }

    /// <summary>Gets the environment whose working directory is not <see cref="Root"/>.</summary>
    internal FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the configuration repository the tool settings read.</summary>
    internal FakeKyberWeaveConfigRepository Config { get; } = new();

    /// <summary>Gets the scripted MCP server.</summary>
    internal FakeMcpStdioSessionFactory Mcp { get; } = new();

    /// <summary>Gets the doctor's read seam over <see cref="Files"/>.</summary>
    internal InMemoryDoctorFileSystem DoctorFiles { get; }

    /// <summary>Gets the repository tool settings.</summary>
    internal IRepositoryToolSettings ToolSettings { get; }

    /// <summary>Gets the doctor.</summary>
    internal RepositoryDoctorService Doctor { get; }

    /// <summary>Gets the console the command writes to.</summary>
    internal FakeConsole Console { get; } = new();

    /// <summary>Scripts <paramref name="starts"/> healthy MCP sessions that serve <paramref name="root"/>.</summary>
    /// <param name="root">The checkout the server says it serves.</param>
    /// <param name="starts">How many times the probe will start the server.</param>
    internal void HealthyMcp(string root, int starts = 1)
    {
        for (int i = 0; i < starts; i++)
        {
            _ = Mcp.Reply(Initialize).Reply(Call($"provenance: root={root} rev=abc123 dirty=no documents=7"));
        }
    }

    /// <summary>Writes the repository configuration, and the docs folder that makes the repository governed.</summary>
    /// <param name="config">The configuration text.</param>
    /// <param name="root">The repository root; <see cref="Root"/> when null.</param>
    internal void WriteGoverned(string config, string? root = null)
    {
        string repository = root ?? Root;
        string directory = Path.Combine(repository, ".kyber-weave");
        Files.CreateDirectory(directory);
        Files.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), config);
        Config.WriteRepositoryConfig(repository, config);
        Files.CreateDirectory(Path.Combine(repository, "docs"));
    }

    /// <summary>The <c>doctor</c> command over this world.</summary>
    /// <param name="terminal">Whether standard input is a terminal.</param>
    /// <param name="executor">Runs git and CodeGraph.</param>
    /// <param name="mcpExecutable">The MCP executable to probe.</param>
    internal DoctorCommand Command(bool terminal, ScriptedProcessExecutor executor, string mcpExecutable) =>
        new(
            Doctor,
            new FakeProcessEnvironment(
                currentDirectory: Environment.CurrentDirectory,
                isInputRedirected: !terminal,
                isFileSystemCaseInsensitive: !Files.CaseSensitive),
            Console,
            Paths,
            executor,
            executor,
            mcpExecutable);

    private static string Call(string text) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":"
        + JsonSerializer.Serialize(text) + "}]}}";
}
