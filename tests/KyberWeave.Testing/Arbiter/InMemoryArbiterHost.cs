using KyberWeave.Application;
using KyberWeave.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Composition;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console.Cli;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The Arbiter commands over the container the real host assembles, with every outside resource faked: an in-memory
/// file system, a fake environment, a captured console, a fake clock and identifiers, scripted processes and an
/// in-memory credential store. A command test builds one and runs commands against it, so nothing touches the disk,
/// the process or the host's console.
/// </summary>
internal sealed class InMemoryArbiterHost : IDisposable
{
    private readonly ArbiterTestHost _host;
    private readonly Action<IServiceCollection>? _configure;
    private readonly Dictionary<string, string?> _variables = new(StringComparer.Ordinal);

    private InMemoryArbiterHost(Action<IServiceCollection>? configure, string? workingDirectory, string? homeDirectory)
    {
        WorkingDirectory = workingDirectory ?? ArbiterTestRoots.Under("work");
        HomeDirectory = homeDirectory ?? ArbiterTestRoots.Under("home");
        Files = new InMemoryFileSystem(Clock);
        Files.CreateDirectory(WorkingDirectory);
        Files.CreateDirectory(HomeDirectory);
        Paths = new FakePathResolver(Files);
        Environment = new FakeProcessEnvironment(
            currentDirectory: WorkingDirectory,
            homeDirectory: HomeDirectory,
            tempDirectory: ArbiterTestRoots.Under("temp"),
            applicationBaseDirectory: ArbiterTestRoots.Under("app"),
            isFileSystemCaseInsensitive: !Files.CaseSensitive,
            readVariable: name => _variables.GetValueOrDefault(name));
        Console = new TestConsole { EmitAnsiSequences = false };
        Console.Profile.Width = 400;
        State = new InMemoryArbiterState(Files);
        _configure = configure;
        _host = ArbiterTestHost.Create(RegisterFakes);
    }

    private void RegisterFakes(IServiceCollection services)
    {
        _ = services.AddSingleton<IWritableFileSystem>(Files);
        _ = services.AddSingleton<IFileSystem>(Files);
        _ = services.AddSingleton<IPathResolver>(Paths);
        _ = services.AddSingleton<IProcessEnvironment>(Environment);
        _ = services.AddSingleton<IAnsiConsole>(new ForwardingAnsiConsole(() => Console));
        _ = services.AddSingleton<IConsole>(Standard);
        _ = services.AddSingleton<TimeProvider>(Clock);
        _ = services.AddSingleton<IIdGenerator>(Ids);
        _ = services.AddSingleton<IArbiterLogStore>(State.Log);
        _ = services.AddSingleton<IRepositoryFileStore>(new InMemoryRepositoryFileStore(Files));
        _ = services.AddSingleton<IProcessExecutor>(Processes);
        _ = services.AddSingleton<IProcessRunner>(Runner);
        _ = services.AddSingleton<ICredentialStore>(Credentials);
        _ = services.AddSingleton<IFileStatReader>(new InMemoryFileStatReader(Files));
        _ = services.AddSingleton<IBoundedFileReader>(new InMemoryBoundedFileReader(Files));
        _configure?.Invoke(services);
        _ = services.AddArbiterHost();
    }

    /// <summary>Gets the directory the process is said to have started in.</summary>
    internal string WorkingDirectory { get; }

    /// <summary>Gets the operator's home directory.</summary>
    internal string HomeDirectory { get; }

    /// <summary>Gets the in-memory file system every adapter reads and writes.</summary>
    internal InMemoryFileSystem Files { get; }

    /// <summary>Gets the path resolver over <see cref="Files"/>.</summary>
    internal FakePathResolver Paths { get; }

    /// <summary>Gets the process environment; set a variable through <see cref="SetVariable"/>.</summary>
    internal FakeProcessEnvironment Environment { get; }

    /// <summary>Gets the console the commands write to; its output is what the operator would have seen.</summary>
    internal TestConsole Console { get; }

    /// <summary>Gets the standard-stream console the key input reads.</summary>
    internal FakeConsole Standard { get; } = new(isInputRedirected: true);

    /// <summary>Gets the clock the commands read; advance it to move time.</summary>
    internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Gets the identifier generator.</summary>
    internal FakeIdGenerator Ids { get; } = new();

    /// <summary>Gets the stores over <see cref="Files"/> and the shared append-only log.</summary>
    internal InMemoryArbiterState State { get; }

    /// <summary>Gets the program probe: what each child process answers, and the calls it received.</summary>
    internal FakeProcessExecutor Processes { get; } = new();

    /// <summary>Gets the process runner a test scripts.</summary>
    internal ScriptedProcessRunner Runner { get; } = new();

    /// <summary>Gets the operating-system credential store in memory.</summary>
    internal FakeCredentialStore Credentials { get; } = new();

    /// <summary>Everything the commands wrote to <see cref="Console"/>.</summary>
    internal string Output => Console.Output;

    /// <summary>Builds a host.</summary>
    /// <param name="configure">Replaces any collaborator; it runs after the host's own fakes, so its registration wins.</param>
    /// <param name="workingDirectory">The working directory; a platform-qualified <c>work</c> folder when null.</param>
    /// <param name="homeDirectory">The home directory; a platform-qualified <c>home</c> folder when null.</param>
    internal static InMemoryArbiterHost Create(
        Action<IServiceCollection>? configure = null,
        string? workingDirectory = null,
        string? homeDirectory = null) =>
        new(configure, workingDirectory, homeDirectory);

    /// <summary>
    /// Builds a command app over the same fakes, so a test drives the argument surface a user types. The app owns its
    /// own container, whose services are the host's shared fakes.
    /// </summary>
    /// <param name="register">Adds the commands the app exposes.</param>
    internal CommandApp App(Action<IConfigurator> register)
    {
        ServiceCollection services = [];
        RegisterFakes(services);
        _ = services.AddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        services.TryAddSingleton<IRepositoryToolSettings, KyberWeave.Core.Doctor.ConfigFileToolSettings>();
        _ = services.AddSquadCliServices();
        _ = services.AddArbiterCliServices();
        _ = services.AddWeaveApplication();
        CommandApp app = new(new ServiceCollectionTypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.ConfigureConsole(new ForwardingAnsiConsole(() => Console));
            config.UseStrictParsing();
            register(config);
        });
        return app;
    }

    /// <summary>Builds a command, resolving each constructor parameter from the container.</summary>
    /// <typeparam name="T">The command type.</typeparam>
    internal T Command<T>()
        where T : class =>
        _host.Command<T>();

    /// <summary>Resolves a registered service.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal T Get<T>()
        where T : notnull =>
        _host.Get<T>();

    /// <summary>Sets an environment variable the commands will see.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value; null removes it.</param>
    internal void SetVariable(string name, string? value) => _variables[name] = value;

    /// <summary>Runs a command and reports its exit code with only what it wrote to the console during the call.</summary>
    /// <param name="execute">Runs the command.</param>
    internal ArbiterCommandRun Run(Func<int> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        int before = Console.Output.Length;
        int exitCode = execute();
        return new ArbiterCommandRun(exitCode, Console.Output[before..]);
    }

    /// <summary>A path under the working directory.</summary>
    /// <param name="segments">The names below the working directory.</param>
    internal string InWork(params string[] segments) => Path.Combine([WorkingDirectory, .. segments]);

    /// <summary>A path under the home directory.</summary>
    /// <param name="segments">The names below the home directory.</param>
    internal string InHome(params string[] segments) => Path.Combine([HomeDirectory, .. segments]);

    /// <summary>Writes a file, creating its folder first.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="content">The text.</param>
    /// <returns>The path.</returns>
    internal string Write(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Files.CreateDirectory(directory);
        }

        Files.WriteAllText(path, content);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _host.Dispose();
        Console.Dispose();
    }
}

/// <summary>What one command run did: its exit code and what it wrote to the console.</summary>
/// <param name="ExitCode">Gets the exit code.</param>
/// <param name="Output">Gets the console output of this run alone.</param>
internal sealed record ArbiterCommandRun(int ExitCode, string Output);
