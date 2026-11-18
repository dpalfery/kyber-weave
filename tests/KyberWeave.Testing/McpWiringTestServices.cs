using KyberWeave.Application;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace KyberWeave.Tests;

/// <summary>The MCP wiring service built the way the CLI container builds it, over the physical file system.</summary>
internal static class McpWiringTestServices
{
    private static readonly Lazy<ServiceProvider> Provider = new(() =>
        new ServiceCollection()
            .AddSingleton<IMcpDeclarationReader, McpDeclarationReader>()
            .AddWeaveApplication()
            .BuildServiceProvider());

    /// <summary>The service.</summary>
    public static IMcpWiringService Wiring => Provider.Value.GetRequiredService<IMcpWiringService>();

    /// <summary>The repair, built by the container.</summary>
    public static McpWiringRepairer Repairer => Provider.Value.GetRequiredService<McpWiringRepairer>();

    /// <summary>The repository-containment rule, over the physical path resolver.</summary>
    public static McpRepositoryPath RepositoryPath => Provider.Value.GetRequiredService<McpRepositoryPath>();

    /// <summary>The receipt store, over the physical file system.</summary>
    public static IMcpWiringReceipts Receipts => Provider.Value.GetRequiredService<IMcpWiringReceipts>();

    /// <summary>
    /// The same wiring services over an in-memory file system, so a unit test of a wiring behaviour reads and
    /// writes no disk, starts no program and reads no clock.
    /// </summary>
    /// <param name="clock">The clock the receipt's write times come from; a fixed one when null.</param>
    /// <returns>The services, over an empty in-memory file system with a created repository root.</returns>
    public static InMemoryMcpWiring InMemory(FakeTimeProvider? clock = null) => new(clock);
}

/// <summary>
/// The MCP wiring services over one in-memory file system: the ready-to-use equivalent of the container above,
/// with every collaborator a wording test observes exposed.
/// </summary>
internal sealed class InMemoryMcpWiring
{
    /// <summary>Creates the services over a new in-memory file system holding an empty repository root.</summary>
    /// <param name="clock">The clock the receipt's write times come from; a fixed one when null.</param>
    public InMemoryMcpWiring(FakeTimeProvider? clock = null)
    {
        Files = new InMemoryFileSystem();
        Clock = clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));
        Root = Path.Combine(Files.Root, "repo");
        Files.CreateDirectory(Root);

        Paths = new FakePathResolver(Files);

        // A process standing somewhere other than the repository it is asked about, which is the
        // situation the wiring check runs in: `docs init` is started from wherever the operator is,
        // and a launch command that only resolves against the working directory is a bug the
        // inspector suite has to be able to see.
        Environment = new FakeProcessEnvironment(
            currentDirectory: Files.Root,
            homeDirectory: Path.Combine(Files.Root, "home"),
            tempDirectory: Path.Combine(Files.Root, "tmp"),
            isFileSystemCaseInsensitive: !Files.CaseSensitive);

        ConfigFiles = new InMemoryMcpConfigFiles(Files);
        RepositoryPath = new McpRepositoryPath(Paths, Environment);
        Receipts = new McpWiringReceiptStore(Files);
        Inspector = new McpWiringInspector(ConfigFiles, RepositoryPath);
        Repairer = new McpWiringRepairer(ConfigFiles, Receipts, Clock, RepositoryPath);
        Renamer = new McpServerRenamer(
            ConfigFiles,
            new McpDeclarationReader(new FakeKyberWeaveConfigRepository()),
            Receipts,
            Clock,
            RepositoryPath);
        Wiring = new McpWiringService(Inspector, Repairer, Renamer, new McpWiringFixes(ConfigFiles, Renamer, RepositoryPath), Launch);
    }

    /// <summary>Gets the file system every service here reads and writes.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the clock the receipt's write times come from.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Gets the repository root, a folder under the in-memory file system's root.</summary>
    public string Root { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    public FakePathResolver Paths { get; }

    /// <summary>Gets the environment whose working directory is <see cref="Root"/>.</summary>
    public FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the MCP configuration file seam over <see cref="Files"/>.</summary>
    public InMemoryMcpConfigFiles ConfigFiles { get; }

    /// <summary>Gets the repository-containment rule over the in-memory seams.</summary>
    public McpRepositoryPath RepositoryPath { get; }

    /// <summary>Gets the receipt store over <see cref="Files"/>.</summary>
    public IMcpWiringReceipts Receipts { get; }

    /// <summary>Gets the inspector.</summary>
    public McpWiringInspector Inspector { get; }

    /// <summary>Gets the repairer.</summary>
    public McpWiringRepairer Repairer { get; }

    /// <summary>Gets the renamer.</summary>
    public McpServerRenamer Renamer { get; }

    /// <summary>Gets the wiring service, composed exactly as the container composes it.</summary>
    public IMcpWiringService Wiring { get; }

    /// <summary>Gets the launch-command factory, which reads the in-memory <c>PATH</c> the environment holds.</summary>
    public EnvironmentLaunchCommandResolverFactory LaunchFactory => new(Files, Paths, Environment);

    /// <summary>Gets the launch-command factory the wiring service is composed with.</summary>
    public ILaunchCommandResolverFactory Launch => LaunchFactory;

    /// <summary>The full path of a repository-relative path written with forward slashes.</summary>
    /// <param name="relativePath">The path, relative to <see cref="Root"/>.</param>
    /// <returns>The full path inside the in-memory tree.</returns>
    public string FullPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads a repository-relative file as text.</summary>
    /// <param name="relativePath">The path, relative to <see cref="Root"/>.</param>
    /// <returns>The file's text.</returns>
    public string Read(string relativePath) => Files.ReadAllText(FullPath(relativePath));

    /// <summary>Whether a repository-relative file exists.</summary>
    /// <param name="relativePath">The path, relative to <see cref="Root"/>.</param>
    /// <returns>True when the file exists.</returns>
    public bool FileExists(string relativePath) => Files.FileExists(FullPath(relativePath));

    /// <summary>Creates a repository-relative directory, and its parents.</summary>
    /// <param name="relativePath">The directory, relative to <see cref="Root"/>.</param>
    public void CreateDirectory(string relativePath) => Files.CreateDirectory(FullPath(relativePath));
}
