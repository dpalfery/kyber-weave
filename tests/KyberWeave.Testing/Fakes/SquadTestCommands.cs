using System.Diagnostics;
using KyberWeave.Application.Squad.Packaging;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Review;
using KyberWeave.Testing.Squad;
using Spectre.Console;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the Squad commands over the physical adapters with whichever collaborators a test replaces, the way the
/// container builds them in the real host. The parameter names and order follow what each command used to accept.
/// </summary>
internal static class SquadTestCommands
{
    public static SquadInstallCommand Install(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        KyberDashCapturePluginStep? capturePluginStep = null,
        Func<string, string?>? environmentReader = null,
        IKyberWeaveConfigRepository? configs = null,
        SquadConsole? output = null)
    {
        SquadConsole console = output ?? CapturedSquadConsole();
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadInstallCommand(
            SquadHostDefaults.CreateLifecycleService(userPaths, store, releaseSource, renderer, globalRoots: roots, environmentReader: environmentReader),
            store,
            RealSquadState.Migration(store, store.UserPaths),
            new SquadTargetResolver(new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance, new ProcessEnvironment()),
            roots,
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new ProcessEnvironment(),
            PhysicalRoots(console),
            configs ?? RealFileKyberWeaveConfigRepository.Create(),
            capturePluginStep);
    }

    public static SquadUpdateCommand Update(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadProjectPicker? picker = null,
        ISquadInstanceService? instanceService = null,
        KyberDashCapturePluginStep? capturePluginStep = null,
        Func<string, string?>? environmentReader = null,
        IKyberWeaveConfigRepository? configs = null,
        SquadConsole? output = null)
    {
        SquadConsole console = output ?? CapturedSquadConsole();
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadUpdateCommand(
            SquadHostDefaults.CreateLifecycleService(userPaths, store, releaseSource, renderer, globalRoots: roots, environmentReader: environmentReader),
            instanceService ?? new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(userPaths ?? store.UserPaths)),
            store,
            RealSquadState.Migration(store, store.UserPaths),
            new SquadTargetResolver(new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance, new ProcessEnvironment()),
            roots,
            new PhysicalSquadFileAccess(),
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new ProcessEnvironment(),
            PhysicalRoots(console),
            configs ?? RealFileKyberWeaveConfigRepository.Create(),
            picker,
            capturePluginStep);
    }

    public static SquadUninstallCommand Uninstall(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadLifecycleService? lifecycleService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null,
        SquadConsole? output = null)
    {
        SquadConsole console = output ?? CapturedSquadConsole();
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadUninstallCommand(
            lifecycleService ?? SquadHostDefaults.CreateLifecycleService(userPaths, store, globalRoots: roots, environmentReader: environmentReader),
            store,
            RealSquadState.Migration(store, store.UserPaths),
            roots,
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            new ProcessEnvironment(),
            PhysicalRoots(console));
    }

    public static SquadInstanceRemoveCommand InstanceRemove(
        ISquadUserPaths? userPaths = null,
        ISquadInstanceService? instanceService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        SquadConsole? output = null)
    {
        ISquadUserPaths paths = userPaths ?? SquadHostDefaults.ResolveUserPaths();
        return new SquadInstanceRemoveCommand(
            instanceService ?? new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(paths)),
            paths,
            new PhysicalSquadFileAccess(),
            new SquadConsoleOptions(isInteractive, readAnswer),
            output ?? CapturedSquadConsole(),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment());
    }

    public static SquadPackCommand Pack(string? workingDirectory = null) =>
        new(new SquadPackService(
            new FakeProcessEnvironment(currentDirectory: workingDirectory ?? "/kw/work"),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            RealSquadServices.SourceLocator(),
            RealSquadServices.Packager(RealSquadPacker.Create())),
            CapturedConsole());

    public static SquadRunCommand Run(
        ISquadUserPaths? userPaths = null,
        SquadModelPresetSelectionStore? selectionStore = null,
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? findOnPath = null,
        string? workingDirectory = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null,
        SquadConsole? output = null)
    {
        ISquadUserPaths paths = userPaths ?? SquadHostDefaults.ResolveUserPaths();
        return new SquadRunCommand(
            ensureOverride is not null
                ? new FuncInstanceService(ensureOverride)
                : new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(paths)),
            selectionStore ?? RealSquadSelectionStore.Create(paths),
            ensureOverride is not null ? new AnyPresetStore() : new SquadModelPresetStore(paths),
            RealSquadServices.ProcessHarnessLauncher(),
            new ProcessEnvironment(),
            new SquadRunOptions(runner, getEnv, findOnPath, workingDirectory),
            output ?? CapturedSquadConsole(),
            new PhysicalPathResolver(new PhysicalFileSystem()));
    }

    public static SquadStatusCommand Status(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null)
    {
        SquadConsole console = CapturedSquadConsole();
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        return new SquadStatusCommand(
            store,
            RealSquadState.Migration(store, store.UserPaths),
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader),
            new SquadModelPresetStore(store.UserPaths),
            SquadFileSystemPathSemantics.Instance,
            console,
            new PhysicalFileSystem(),
            new ProcessEnvironment(),
            PhysicalRoots(console));
    }

    public static SquadDoctorCommand Doctor(
        IProcessExecutor? executor = null,
        ISquadUserPaths? userPaths = null,
        string? workingDirectory = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadRenderer? renderer = null,
        SquadStateStore? stateStore = null,
        Func<string, string?>? environmentReader = null,
        IKyberWeaveConfigRepository? configs = null,
        SquadConsole? output = null,
        IWritableFileSystem? files = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        return new SquadDoctorCommand(
            store,
            RealSquadState.Migration(store, store.UserPaths),
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader),
            renderer ?? SquadHostDefaults.ResolveRenderer(),
            new SquadModelPresetStore(store.UserPaths),
            RealSquadDeployment.Planner,
            new SquadResponseGuidanceReader(new PhysicalFileSystem()),
            new FakeProcessEnvironment(readVariable: environmentReader),
            RealSquadServices.SourceLocator(),
            RealSquadServices.SourceLoader(),
            new SquadSkillShadowing(new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance, new ProcessEnvironment()),
            new DevinImportOverlap(files ?? new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance, new ProcessEnvironment()),
            new ZCodeMcpConfiguration(files ?? new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance, new ProcessEnvironment()),
            new SquadDoctorOptions(executor, workingDirectory),
            SquadFileSystemPathSemantics.Instance,
            configs ?? RealFileKyberWeaveConfigRepository.Create(),
            output ?? CapturedSquadConsole(),
            files ?? new PhysicalFileSystem(),
            PhysicalRoots(CapturedSquadConsole()));
    }

    public static SquadPresetUseCommand PresetUse(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? canonicalProfileResolver = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths);
        return new SquadPresetUseCommand(
            store,
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(),
            new SquadModelPresetStore(store.UserPaths),
            RealSquadSelectionStore.Create(store.UserPaths),
            Profiles(store, canonicalProfileResolver),
            RealSquadDeployment.Planner,
            RealSquadDeployment.TransactionFactory(store),
            SquadFileSystemPathSemantics.Instance,
            PhysicalRoots(CapturedSquadConsole()),
            new PhysicalFileSystem(),
            CapturedSquadConsole(),
            TimeProvider.System,
            RealSquadServices.PresetValidator());
    }

    public static SquadPresetClearCommand PresetClear(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? canonicalProfileResolver = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths);
        return new SquadPresetClearCommand(
            store,
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(),
            new SquadModelPresetStore(store.UserPaths),
            RealSquadSelectionStore.Create(store.UserPaths),
            Profiles(store, canonicalProfileResolver),
            RealSquadDeployment.Planner,
            RealSquadDeployment.TransactionFactory(store),
            SquadFileSystemPathSemantics.Instance,
            PhysicalRoots(CapturedSquadConsole()),
            new PhysicalFileSystem(),
            CapturedSquadConsole(),
            TimeProvider.System);
    }

    public static SquadPresetShowCommand PresetShow(
        ISquadUserPaths? userPaths = null,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? canonicalProfileResolver = null)
    {
        SquadStateStore store = SquadHostDefaults.ResolveStateStore(userPaths);
        return new SquadPresetShowCommand(
            userPaths ?? store.UserPaths,
            new SquadModelPresetStore(store.UserPaths),
            Profiles(store, canonicalProfileResolver),
            CapturedSquadConsole(),
            RealSquadServices.PresetValidator());
    }

    public static SquadPresetListCommand PresetList(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths);
        ISquadUserPaths paths = userPaths ?? store.UserPaths;
        return new SquadPresetListCommand(
            paths,
            store,
            new SquadModelPresetStore(paths),
            RealSquadSelectionStore.Create(paths),
            PhysicalRoots(CapturedSquadConsole()),
            new PhysicalFileSystem(),
            CapturedSquadConsole());
    }

    /// <summary>The list verb over the stores, roots, file system and console a test supplies.</summary>
    public static SquadPresetListCommand PresetListOver(
        ISquadUserPaths userPaths,
        ISquadStateStore stateStore,
        ISquadModelPresetStore presets,
        ISquadModelPresetSelectionStore selections,
        SquadCommandRoots roots,
        IFileSystem files,
        SquadConsole console) =>
        new(userPaths, stateStore, presets, selections, roots, files, console);

    /// <summary>The show verb over the stores and console a test supplies.</summary>
    public static SquadPresetShowCommand PresetShowOver(
        ISquadUserPaths userPaths,
        ISquadModelPresetStore presets,
        Func<string?, SquadCanonicalProfiles?> canonicalProfileResolver,
        SquadConsole console) =>
        new(userPaths, presets, new FuncProfileResolver(canonicalProfileResolver), console, RealSquadServices.PresetValidator());

    /// <summary>The use verb over the stores, roots, file system and console a test supplies; it never starts a transaction.</summary>
    public static SquadPresetUseCommand PresetUseOver(
        ISquadStateStore stateStore,
        ISquadModelPresetStore presets,
        ISquadModelPresetSelectionStore selections,
        Func<string?, SquadCanonicalProfiles?> canonicalProfileResolver,
        SquadCommandRoots roots,
        IFileSystem files,
        SquadConsole console) =>
        new(
            stateStore,
            SquadHostDefaults.ResolveGlobalRoots(_ => null),
            presets,
            selections,
            new FuncProfileResolver(canonicalProfileResolver),
            RealSquadDeployment.Planner,
            new UnusedTransactions(),
            SquadFileSystemPathSemantics.Instance,
            roots,
            files,
            console,
            TimeProvider.System,
            RealSquadServices.PresetValidator());

    /// <summary>The clear verb over the stores, roots, file system and console a test supplies; it never starts a transaction.</summary>
    public static SquadPresetClearCommand PresetClearOver(
        ISquadStateStore stateStore,
        ISquadModelPresetStore presets,
        ISquadModelPresetSelectionStore selections,
        Func<string?, SquadCanonicalProfiles?> canonicalProfileResolver,
        SquadCommandRoots roots,
        IFileSystem files,
        SquadConsole console) =>
        new(
            stateStore,
            SquadHostDefaults.ResolveGlobalRoots(_ => null),
            presets,
            selections,
            new FuncProfileResolver(canonicalProfileResolver),
            RealSquadDeployment.Planner,
            new UnusedTransactions(),
            SquadFileSystemPathSemantics.Instance,
            roots,
            files,
            console,
            TimeProvider.System);

    private sealed class UnusedTransactions : ISquadTransactionFactory
    {
        public ISquadTransaction Create(ISquadTransactionObserver? observer) => throw new NotSupportedException();
    }

    /// <summary>A console whose output goes to whichever test is capturing the process console.</summary>
    public static SquadConsole ProcessCapturedConsole() => CapturedSquadConsole();

    /// <summary>A capture-plugin step whose output goes to whichever test is capturing the process console.</summary>
    public static KyberDashCapturePluginStep CapturePluginStep(IProcessExecutor executor, Func<string?> locateKyberDash) =>
        new(executor, locateKyberDash, CapturedSquadConsole());

    /// <summary>The roots over an in-memory file system and a fake environment, for a command test that touches no disk.</summary>
    public static SquadCommandRoots InMemoryRoots(
        SquadConsole console,
        string currentDirectory = "/kw/work",
        InMemoryFileSystem? files = null)
    {
        InMemoryFileSystem fileSystem = files ?? new InMemoryFileSystem();
        return new SquadCommandRoots(
            fileSystem,
            new FakePathResolver(fileSystem),
            new FakeProcessEnvironment(currentDirectory: currentDirectory),
            console);
    }

    private static SquadCommandRoots PhysicalRoots(SquadConsole console)
    {
        PhysicalFileSystem files = new();
        return new SquadCommandRoots(files, new PhysicalPathResolver(files), new ProcessEnvironment(), console);
    }

    /// <summary>
    /// The console a command writes to in a test: the process's router, installed before the command is built, so
    /// output goes to whichever test is capturing when the command writes.
    /// </summary>
    internal static SquadConsole CapturedSquadConsole() => new(CapturedConsole(), new SystemConsole());

    private static IAnsiConsole CapturedConsole()
    {
        ProcessConsoleCapture.EnsureInstalled();
        return AnsiConsole.Console;
    }

    private static ISquadCanonicalProfileResolver Profiles(
        SquadStateStore store,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? resolver) =>
        resolver is not null
            ? new FuncProfileResolver(version => resolver(version, store.UserPaths))
            : new CheckoutOrSnapshotCanonicalProfileResolver(
                new PhysicalSquadFileAccess(),
                RealSquadServices.SourceLoader(),
                new SquadCanonicalProfileStore(store.UserPaths));

    private sealed class FuncProfileResolver(Func<string?, SquadCanonicalProfiles?> resolve) : ISquadCanonicalProfileResolver
    {
        public SquadCanonicalProfiles? Resolve(string? preferredVersion) => resolve(preferredVersion);
    }

    private sealed class LazyInstanceService(Func<ISquadInstanceService> create) : ISquadInstanceService
    {
        private readonly Lazy<ISquadInstanceService> _inner = new(create);

        public Task<SquadInstanceEnsureResult> EnsureAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            _inner.Value.EnsureAsync(target, presetName, cancellationToken);

        public Task RemoveAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            _inner.Value.RemoveAsync(target, presetName, cancellationToken);

        public Task RefreshAllAsync(CancellationToken cancellationToken = default) =>
            _inner.Value.RefreshAllAsync(cancellationToken);
    }

    private sealed class FuncInstanceService(Func<SquadTarget, string, Task<SquadInstanceEnsureResult>> ensure) : ISquadInstanceService
    {
        public Task<SquadInstanceEnsureResult> EnsureAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            ensure(target, presetName);

        public Task RemoveAsync(SquadTarget target, string presetName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AnyPresetStore : ISquadModelPresetStore
    {
        public IReadOnlyList<string> List() => [];

        public SquadModelPreset? TryLoad(string name) =>
            new(name, string.Empty, string.Empty, new Dictionary<string, SquadModelProfile>());
    }
}
