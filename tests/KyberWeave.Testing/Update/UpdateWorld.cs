using KyberWeave.Application;
using KyberWeave.Cli.Composition;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Testing;
using KyberWeave.Contracts.Services;
using KyberWeave.Application.Update;
using KyberWeave.Data.Update;
using KyberWeave.Domain.Update;

namespace KyberWeave.Testing.Update;

/// <summary>
/// One self-update's whole outside world, in memory: an install directory on an in-memory file system, the real
/// <c>SelfUpdateSystem</c> over scripted processes and permission bits, and the updater and container built over them.
/// A test arranges the install and the release, runs the update and reads the install back; nothing touches the disk,
/// a process, the network or the host's environment.
/// </summary>
internal sealed class UpdateWorld : IDisposable
{
    /// <summary>The captured outcome of a process or verb that succeeded and printed nothing.</summary>
    internal static readonly ProcessResult QuietSuccess = new(0, string.Empty, string.Empty);

    private readonly List<IDisposable> _owned = [];

    /// <summary>Creates a world with an empty install directory.</summary>
    internal UpdateWorld()
    {
        Files = new InMemoryFileSystem();
        Paths = new FakePathResolver(Files);
        Install = Path.Combine(Files.Root, "install");
        Files.CreateDirectory(Install);
    }

    /// <summary>Gets the file system everything reads and writes.</summary>
    internal InMemoryFileSystem Files { get; }

    /// <summary>Gets the path seam over <see cref="Files"/>.</summary>
    internal FakePathResolver Paths { get; }

    /// <summary>Gets the directory the binaries are installed in, and the home the updater reads unless a test names another.</summary>
    internal string Install { get; }

    /// <summary>Gets the last loopback hand-off a helper built, so a test can read the child's own outcome.</summary>
    internal LoopbackFinishingProcess? LastChild { get; private set; }

    /// <summary>Gets the programs the update system's bounded verbs ran, most recently built system first.</summary>
    internal ScriptedProcessExecutor? LastProcesses { get; private set; }

    /// <summary>A path under the install directory.</summary>
    /// <param name="segments">The names below it.</param>
    internal string InInstall(params string[] segments) => Path.Combine([Install, .. segments]);

    /// <summary>Reads an environment from a table of variables.</summary>
    /// <param name="home">The home directory both <c>HOME</c> and <c>USERPROFILE</c> name; the install directory when null.</param>
    /// <param name="appData">The <c>APPDATA</c> value; absent when null.</param>
    /// <returns>The reader.</returns>
    internal Func<string, string?> HomeEnvironment(string? home = null, string? appData = null)
    {
        string directory = home ?? Install;
        return name => name switch
        {
            "HOME" => directory,
            "USERPROFILE" => directory,
            "APPDATA" => appData,
            _ => null,
        };
    }

    /// <summary>Builds a process environment whose directories are under this world's root.</summary>
    /// <param name="readVariable">Reads a variable; null answers every variable with null.</param>
    /// <returns>The environment.</returns>
    internal FakeProcessEnvironment Environment(Func<string, string?>? readVariable = null) => new(
        currentDirectory: Path.Combine(Files.Root, "work"),
        homeDirectory: Path.Combine(Files.Root, "home"),
        tempDirectory: Path.Combine(Files.Root, "temp"),
        isFileSystemCaseInsensitive: !Files.CaseSensitive,
        readVariable: readVariable);

    /// <summary>Creates a console that captures what a command writes, owned and disposed by this world.</summary>
    /// <param name="width">The console width; wide enough that a long line stays whole.</param>
    /// <returns>The console.</returns>
    internal TestConsole CreateConsole(int width = 400)
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        console.Profile.Width = width;
        _owned.Add(console);
        return console;
    }

    /// <summary>Builds the real self-update system over this world's file system, with scripted processes and permission bits.</summary>
    /// <returns>The system.</returns>
    internal ISelfUpdateSystem CreateSystem()
    {
        ScriptedProcessExecutor processes = new(_ => QuietSuccess);
        LastProcesses = processes;
        return new SelfUpdateSystem(Files, Paths, processes, new FakeConsoleProcessLauncher(), new FakeUpdateFileModes());
    }

    /// <summary>Writes the running binary of the old version beside the other installed binaries.</summary>
    /// <param name="currentVersion">The version it reports.</param>
    /// <param name="rid">The host's runtime identifier.</param>
    /// <param name="windows">True for a Windows host.</param>
    /// <returns>The host snapshot.</returns>
    internal SelfUpdateHost CreateHost(string currentVersion, string rid, bool windows = false)
    {
        string path = InInstall(BinaryInstaller.InstalledFileName("kyber-weave", windows));
        Files.WriteAllText(path, "old-cli");
        return new SelfUpdateHost(path, currentVersion, rid, windows, false);
    }

    /// <summary>Places an already-installed binary beside the CLI.</summary>
    /// <param name="baseName">The binary's name without an extension.</param>
    /// <param name="content">What it holds.</param>
    /// <param name="windows">True for a Windows host.</param>
    /// <returns>The installed path.</returns>
    internal string InstallBinary(string baseName, string content, bool windows = false)
    {
        string path = InInstall(BinaryInstaller.InstalledFileName(baseName, windows));
        Files.WriteAllText(path, content);
        return path;
    }

    /// <summary>Places an already-installed kyberdash beside the CLI.</summary>
    /// <param name="windows">True for a Windows host.</param>
    /// <returns>The installed path.</returns>
    internal string InstallKyberDash(bool windows = false) => InstallBinary("kyberdash", "old-kyberdash", windows);

    /// <summary>Places an already-installed arbiter beside the CLI.</summary>
    /// <param name="windows">True for a Windows host.</param>
    /// <returns>The installed path.</returns>
    internal string InstallArbiter(bool windows = false) => InstallBinary("kyber-weave-arbiter", "old-arbiter", windows);

    /// <summary>Creates the updater over this world.</summary>
    /// <param name="handler">The release transport.</param>
    /// <param name="host">The running binary's snapshot.</param>
    /// <param name="readEnvironment">Reads a variable; the install directory is home when null.</param>
    /// <param name="log">Receives one line per step.</param>
    /// <param name="runProcess">Runs the tray verb; the update system's when null.</param>
    /// <param name="canWriteDirectory">Probes the install directory; the update system's when null.</param>
    /// <param name="captureProcess">Runs a KyberDash verb; the update system's when null.</param>
    /// <param name="arbiterHookRefresher">Refreshes rendered Arbiter hooks; none when null.</param>
    /// <param name="arbiterTargets">What the refresh covers; required with a refresher.</param>
    /// <param name="finishingProcess">Starts the hand-off child; the update system's when null.</param>
    /// <returns>The updater; the caller disposes it.</returns>
    internal SelfUpdater CreateUpdater(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        Func<string, string?>? readEnvironment = null,
        Action<string>? log = null,
        Func<string, IReadOnlyList<string>, int>? runProcess = null,
        Func<string, bool>? canWriteDirectory = null,
        Func<string, IReadOnlyList<string>, ProcessResult>? captureProcess = null,
        IArbiterHookRefresher? arbiterHookRefresher = null,
        IArbiterRefreshTargets? arbiterTargets = null,
        Func<string, IReadOnlyList<string>, bool, int>? finishingProcess = null) =>
        new(
            handler,
            host,
            Files,
            Paths,
            Environment(readEnvironment ?? HomeEnvironment()),
            CreateSystem(),
            log,
            runProcess,
            canWriteDirectory,
            captureProcess,
            arbiterHookRefresher,
            finishingProcess,
            arbiterTargets);

    /// <summary>Creates the stand-in for the installed binary an update hands off to.</summary>
    /// <param name="handler">The release transport.</param>
    /// <param name="host">The parent's snapshot.</param>
    /// <param name="log">Receives the child's lines.</param>
    /// <param name="readEnvironment">Reads a variable for the child.</param>
    /// <param name="runProcess">Runs the child's tray verb.</param>
    /// <param name="canWriteDirectory">Probes the child's install directory.</param>
    /// <param name="captureProcess">Runs the child's KyberDash verbs.</param>
    /// <param name="arbiterHookRefresher">The child's Arbiter refresher.</param>
    /// <param name="arbiterTargets">What the child's refresh covers.</param>
    /// <returns>The loopback; its <c>Invoke</c> is the hand-off seam.</returns>
    internal LoopbackFinishingProcess Loopback(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        Action<string> log,
        Func<string, string?>? readEnvironment = null,
        Func<string, IReadOnlyList<string>, int>? runProcess = null,
        Func<string, bool>? canWriteDirectory = null,
        Func<string, IReadOnlyList<string>, ProcessResult>? captureProcess = null,
        IArbiterHookRefresher? arbiterHookRefresher = null,
        IArbiterRefreshTargets? arbiterTargets = null)
    {
        LoopbackFinishingProcess loopback = new(
            this,
            handler,
            host,
            log,
            readEnvironment ?? HomeEnvironment(),
            runProcess,
            canWriteDirectory,
            captureProcess,
            arbiterHookRefresher,
            arbiterTargets);
        LastChild = loopback;
        return loopback;
    }

    /// <summary>
    /// Runs one update with a loopback hand-off, the way the command does: the child runs the finish-mode work in
    /// process over the same world.
    /// </summary>
    /// <param name="handler">The release transport.</param>
    /// <param name="host">The running binary's snapshot.</param>
    /// <param name="options">The update options.</param>
    /// <param name="readEnvironment">Reads a variable; the install directory is home when null.</param>
    /// <param name="canWriteDirectory">Probes the install directory; the update system's when null.</param>
    /// <param name="log">Receives one line per step, from the parent and the child.</param>
    /// <param name="runProcess">Runs the tray verb; the update system's when null.</param>
    /// <param name="captureProcess">Runs a KyberDash verb; answers success with no output when null.</param>
    /// <returns>The outcome.</returns>
    internal async Task<SelfUpdateOutcome> RunAsync(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        SelfUpdateOptions options,
        Func<string, string?>? readEnvironment = null,
        Func<string, bool>? canWriteDirectory = null,
        Action<string>? log = null,
        Func<string, IReadOnlyList<string>, int>? runProcess = null,
        Func<string, IReadOnlyList<string>, ProcessResult>? captureProcess = null)
    {
        Func<string, string?> environment = readEnvironment ?? HomeEnvironment();
        Func<string, IReadOnlyList<string>, ProcessResult> capture = captureProcess ?? ((_, _) => QuietSuccess);
        Action<string> sink = log ?? (_ => { });
        LoopbackFinishingProcess loopback = Loopback(
            handler,
            host,
            sink,
            environment,
            runProcess,
            canWriteDirectory,
            capture);
        using SelfUpdater updater = CreateUpdater(
            handler,
            host,
            environment,
            sink,
            runProcess,
            canWriteDirectory,
            capture,
            finishingProcess: loopback.Invoke);
        return await updater.RunAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the container the CLI builds, with every outside resource replaced by this world's fakes, so the
    /// production Arbiter refresher and refresh targets run over the in-memory tree.
    /// </summary>
    /// <param name="readEnvironment">Reads a variable.</param>
    /// <param name="userPaths">The Squad user paths; home and application data from the environment when null.</param>
    /// <param name="configure">Registers further fakes before the host's own.</param>
    /// <returns>The provider; the world disposes it.</returns>
    internal ServiceProvider BuildServices(
        Func<string, string?> readEnvironment,
        ISquadUserPaths? userPaths = null,
        Action<IServiceCollection>? configure = null)
    {
        FakeProcessEnvironment environment = Environment(readEnvironment);
        string home = readEnvironment("HOME") ?? readEnvironment("USERPROFILE") ?? environment.HomeDirectory;
        ISquadUserPaths paths = userPaths ?? new FakeSquadUserPaths(readEnvironment("APPDATA") ?? Path.Combine(home, ".config"), home);
        SquadFileSystemPathSemantics semantics = new(Paths);
        InProcessLease lease = new();
        ScriptedProcessExecutor processes = new(_ => QuietSuccess);
        TestConsole console = CreateConsole();

        ServiceCollection services = [];
        _ = services.AddSingleton<IWritableFileSystem>(Files);
        _ = services.AddSingleton<IFileSystem>(Files);
        _ = services.AddSingleton<IPathResolver>(Paths);
        _ = services.AddSingleton<IFileSystemPathSemantics>(semantics);
        _ = services.AddSingleton<IProcessEnvironment>(environment);
        _ = services.AddSingleton(paths);
        _ = services.AddSingleton<ILease>(lease);
        _ = services.AddSingleton<IIdGenerator>(new FakeIdGenerator());
        _ = services.AddSingleton<TimeProvider>(new FakeTimeProvider(InMemorySquadState.Start));
        _ = services.AddSingleton<ISquadFileAccess>(new InMemorySquadFileAccess(Files));
        _ = services.AddSingleton<ISquadLinkFileSystem>(new FileSystemSquadLinkFileSystem(Files));
        _ = services.AddSingleton<IMcpConfigFiles>(new InMemoryMcpConfigFiles(Files));
        _ = services.AddSingleton<IBoundedProcessExecutor>(processes);
        _ = services.AddSingleton<IConsoleProcessLauncher>(new FakeConsoleProcessLauncher());
        _ = services.AddSingleton<IUpdateFileModes>(new FakeUpdateFileModes());
        _ = services.AddSingleton<ISquadReleaseHttpHandlers>(new UnreachableReleaseHandlers());
        _ = services.AddSingleton<IAnsiConsole>(console);
        FakeConsole standard = new(isInputRedirected: true);
        _owned.Add(standard);
        _ = services.AddSingleton<IConsole>(standard);
        _ = services.AddSingleton<ISquadStateStore>(new SquadStateStore(paths, Files, semantics, Paths, environment, lease));
        configure?.Invoke(services);
        services.TryAddSingleton<IMcpDeclarationReader, McpDeclarationReader>();
        _ = services.AddSquadCliServices();
        _ = services.AddWeaveApplication();
        ServiceProvider provider = services.BuildServiceProvider();
        _owned.Add(provider);
        return provider;
    }

    /// <summary>The production Arbiter hook refresher, resolved from the in-memory container over <paramref name="readEnvironment"/>'s home.</summary>
    /// <param name="readEnvironment">Reads a variable.</param>
    /// <param name="log">Receives one line per step or warning.</param>
    /// <param name="userPaths">The Squad user paths; derived from the environment when null.</param>
    /// <returns>The refresher.</returns>
    internal ArbiterHookRefresher Refresher(
        Func<string, string?> readEnvironment,
        Action<string>? log = null,
        ISquadUserPaths? userPaths = null) =>
        (ArbiterHookRefresher)BuildServices(readEnvironment, userPaths)
            .GetRequiredService<IArbiterHookRefresherFactory>()
            .Create(log ?? (_ => { }));

    /// <summary>The production refresh targets, resolved from the in-memory container over <paramref name="readEnvironment"/>'s home.</summary>
    /// <param name="readEnvironment">Reads a variable.</param>
    /// <param name="userPaths">The Squad user paths; derived from the environment when null.</param>
    /// <returns>The targets the update covers.</returns>
    internal IArbiterRefreshTargets Targets(Func<string, string?> readEnvironment, ISquadUserPaths? userPaths = null) =>
        BuildServices(readEnvironment, userPaths).GetRequiredService<IArbiterRefreshTargets>();

    /// <summary>The state store the container resolves, over this world's file system.</summary>
    /// <param name="userPaths">The user paths the state is anchored to.</param>
    /// <param name="readEnvironment">Reads a variable.</param>
    /// <returns>The store.</returns>
    internal SquadStateStore SquadStore(ISquadUserPaths userPaths, Func<string, string?>? readEnvironment = null) =>
        new(
            userPaths,
            Files,
            new SquadFileSystemPathSemantics(Paths),
            Paths,
            Environment(readEnvironment),
            new InProcessLease());

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
            owned.Dispose();
    }

    private sealed class UnreachableReleaseHandlers : ISquadReleaseHttpHandlers
    {
        public HttpMessageHandler Create() => new ReleaseMapHandler();
    }
}
