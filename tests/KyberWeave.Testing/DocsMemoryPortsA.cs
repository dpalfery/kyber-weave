using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// A whole documentation host held in memory: a file system, the path and environment seams over it, a clock, an
/// identifier source, a captured console and the process seams, with a container that wires every one of them in
/// place of the physical adapter. A command or service built from <see cref="Services"/> touches no disk, starts no
/// process and reads no host console.
/// </summary>
internal sealed class MemoryDocsHost
{
    /// <summary>Creates a host whose repository folder is empty.</summary>
    /// <param name="variables">Environment variables the fake environment answers; none by default.</param>
    public MemoryDocsHost(IReadOnlyDictionary<string, string?>? variables = null)
    {
        Files = new InMemoryFileSystem(Clock);
        Paths = new FakePathResolver(Files);
        Repo = Path.Combine(Files.Root, "repo");
        Home = Path.Combine(Files.Root, "home");
        Environment = new FakeProcessEnvironment(
            currentDirectory: Repo,
            homeDirectory: Home,
            tempDirectory: Path.Combine(Files.Root, "tmp"),
            isFileSystemCaseInsensitive: !Files.CaseSensitive,
            applicationBaseDirectory: Path.Combine(Files.Root, "app"),
            readVariable: name => variables is not null && variables.TryGetValue(name, out string? value) ? value : null);
        Store = new InMemoryRepositoryFileStore(Files);
        Console = new TestConsole { EmitAnsiSequences = false };
        Console.Profile.Width = 400;
        Files.CreateDirectory(Repo);
    }

    /// <summary>Gets the clock every seam reads.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

    /// <summary>Gets the file system.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    public FakePathResolver Paths { get; }

    /// <summary>Gets the process environment, started in <see cref="Repo"/>.</summary>
    public FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the repository file store over <see cref="Files"/>.</summary>
    public InMemoryRepositoryFileStore Store { get; }

    /// <summary>Gets the identifier source.</summary>
    public FakeIdGenerator Ids { get; } = new();

    /// <summary>Gets the process runner; every call is scripted, so an unscripted one fails the test.</summary>
    public ScriptedProcessRunner Processes { get; } = new();

    /// <summary>Gets the captured Spectre console.</summary>
    public TestConsole Console { get; }

    /// <summary>Gets the repository folder, under the file system's root.</summary>
    public string Repo { get; }

    /// <summary>Gets a home folder, under the file system's root and distinct from the repository.</summary>
    public string Home { get; }

    /// <summary>Gets everything written to <see cref="Console"/>, with line endings normalised.</summary>
    public string Output => Console.Output.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Gets a path under <see cref="Repo"/> for a slash-separated relative path.</summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The full path.</returns>
    public string InRepo(string relativePath) =>
        Path.Combine(Repo, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes a file, creating its folders.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="content">The text.</param>
    public void Write(string path, string content)
    {
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllText(path, content);
    }

    /// <summary>Writes a file under the repository, creating its folders.</summary>
    /// <param name="relativePath">The slash-separated path under <see cref="Repo"/>.</param>
    /// <param name="content">The text.</param>
    public void WriteInRepo(string relativePath, string content) => Write(InRepo(relativePath), content);

    /// <summary>Writes bytes under the repository, creating its folders.</summary>
    /// <param name="relativePath">The slash-separated path under <see cref="Repo"/>.</param>
    /// <param name="content">The bytes.</param>
    public void WriteBytesInRepo(string relativePath, byte[] content)
    {
        string path = InRepo(relativePath);
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllBytes(path, content);
    }

    /// <summary>Registers every in-memory seam, ahead of the application's own registrations.</summary>
    /// <param name="services">The collection to populate.</param>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IWritableFileSystem>(Files);
        services.AddSingleton<IFileSystem>(Files);
        services.AddSingleton<IRepositoryFileStore>(Store);
        services.AddSingleton<IPathResolver>(Paths);
        services.AddSingleton<IProcessEnvironment>(Environment);
        services.AddSingleton<IMcpConfigFiles>(new InMemoryMcpConfigFiles(Files));
        services.AddSingleton<IIdGenerator>(Ids);
        services.AddSingleton<IProcessRunner>(Processes);
        services.AddSingleton<IBoundedProcessExecutor>(Processes);
        services.AddSingleton<IProgramRunner>(Processes);
        services.AddSingleton<IProcessExecutor>(Processes);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IConsole>(new FakeConsole());
        services.AddSingleton<Spectre.Console.IAnsiConsole>(Console);
        services.AddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
    }

    /// <summary>The container the real host builds, over this host's seams.</summary>
    /// <param name="configure">Further registrations, applied after the in-memory seams and before the application's own.</param>
    /// <returns>The provider.</returns>
    public IServiceProvider Services(Action<IServiceCollection>? configure = null) =>
        DocsTestCommands.Services(services =>
        {
            Register(services);
            configure?.Invoke(services);
        });
}
