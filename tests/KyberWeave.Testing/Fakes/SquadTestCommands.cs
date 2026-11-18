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
            ensureOverride is not null ? new AnyPresetStore() : new SquadModelPresetStore(paths, new PhysicalFileSystem()),
            RealSquadServices.ProcessHarnessLauncher(),
            new ProcessEnvironment(),
            new SquadRunOptions(runner, getEnv, findOnPath, workingDirectory),
            output ?? CapturedSquadConsole(),
            new PhysicalPathResolver(new PhysicalFileSystem()));
    }

    /// <summary>
    /// Builds <c>squad run</c> over an in-memory Squad state: its file system, path rules, environment and home, so a run
    /// test touches no disk or real process. A test that reaches the instance ensure supplies <paramref name="ensureOverride"/>;
    /// without one the ensure throws, so a row that was meant to stop earlier fails loudly.
    /// </summary>
    public static SquadRunCommand InMemoryRun(
        InMemorySquadState state,
        ISquadModelPresetSelectionStore selectionStore,
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? findOnPath = null,
        string? workingDirectory = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null,
        SquadConsole? output = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(selectionStore);
        return new SquadRunCommand(
            new FuncInstanceService(ensureOverride ?? ((_, _) => throw new NotSupportedException("The test reached the instance ensure without supplying one."))),
            selectionStore,
            ensureOverride is not null ? new AnyPresetStore() : new SquadModelPresetStore(state.UserPaths, state.Files),
            new ProcessSquadHarnessLauncher(new SquadHarnessLauncher(state.Files, state.Environment)),
            state.Environment,
            new SquadRunOptions(runner, getEnv, findOnPath, workingDirectory),
            output ?? CapturedSquadConsole(),
            state.Paths);
    }

    /// <summary>
    /// The real lifecycle service over an in-memory Squad state: its file system, state store, planner, transactions and
    /// clock, with the release source and renderer a test supplies. Nothing touches the disk. A global-scope run supplies
    /// <paramref name="globalRoots"/>, the per-target roots the renderer anchors absolute links to, <paramref name="environmentReader"/> the variables it reads, and <paramref name="observer"/> sees each transaction step.
    /// </summary>
    public static SquadLifecycleService InMemoryLifecycle(
        InMemorySquadState state,
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null,
        ISquadTransactionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        InMemorySquadServices services = new(state.Files);
        return new SquadLifecycleService(
            releaseSource,
            renderer,
            state.Store,
            new SquadModelPresetStore(state.UserPaths, state.Files),
            new SquadModelPresetSelectionStore(state.UserPaths, state.Files, state.Semantics),
            services.SourceLoader(),
            services.PresetValidator(),
            state.Planner,
            new InMemoryTransactions(state),
            new SquadResponseGuidanceReader(state.Files, state.Files, state.Semantics),
            new FactoryHooksShadowing(state.Files),
            new SquadCanonicalProfileStore(state.UserPaths, state.Files),
            new InMemorySquadFileAccess(state.Files),
            state.Semantics,
            state.Time,
            observer: observer,
            globalRoots: globalRoots,
            environmentReader: environmentReader);
    }

    /// <summary><c>squad install</c> over the in-memory lifecycle of <paramref name="state"/>.</summary>
    public static SquadInstallCommand InMemoryInstall(
        InMemorySquadState state,
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        SquadConsole? output = null,
        ISquadGlobalRootResolver? globalRoots = null,
        bool isInteractive = false,
        Func<string, bool>? readAnswer = null)
    {
        SquadConsole console = output ?? CapturedSquadConsole();
        return new SquadInstallCommand(
            InMemoryLifecycle(state, releaseSource, renderer, globalRoots),
            state.Store,
            state.Migration,
            new SquadTargetResolver(state.Files, state.Semantics, state.Environment),
            globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory),
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            state.Environment,
            new SquadCommandRoots(state.Files, state.Paths, state.Environment, console),
            new FakeKyberWeaveConfigRepository(),
            null);
    }

    /// <summary><c>squad update</c> over the in-memory lifecycle of <paramref name="state"/>.</summary>
    public static SquadUpdateCommand InMemoryUpdate(
        InMemorySquadState state,
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        SquadConsole? output = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadInstanceService? instances = null,
        bool isInteractive = false,
        Func<string, bool>? readAnswer = null)
    {
        SquadConsole console = output ?? CapturedSquadConsole();
        return new SquadUpdateCommand(
            InMemoryLifecycle(state, releaseSource, renderer, globalRoots),
            instances ?? new FuncInstanceService((_, _) => throw new NotSupportedException("The test reached the instance ensure.")),
            state.Store,
            state.Migration,
            new SquadTargetResolver(state.Files, state.Semantics, state.Environment),
            globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory),
            new InMemorySquadFileAccess(state.Files),
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            state.Environment,
            new SquadCommandRoots(state.Files, state.Paths, state.Environment, console),
            new FakeKyberWeaveConfigRepository(),
            null,
            null);
    }

    /// <summary>The real instance service over the in-memory state, link file system, link sync and lifecycle of <paramref name="state"/>.</summary>
    public static SquadInstanceService InMemoryInstances(
        InMemorySquadState state,
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ISquadGlobalRootResolver roots = globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory);
        return new SquadInstanceService(
            InMemoryLifecycle(state, releaseSource, renderer, roots),
            new SquadModelPresetStore(state.UserPaths, state.Files),
            new FileSystemSquadLinkFileSystem(state.Files),
            roots,
            new SquadInstanceLinkSync(state.Files, state.Paths, new InProcessLease(state.LeaseHost), state.Environment),
            new InMemorySquadFileAccess(state.Files),
            state.Paths,
            state.Environment,
            state.Store);
    }

    /// <summary><c>squad instance remove</c> over the in-memory state of <paramref name="state"/> and an instance service.</summary>
    public static SquadInstanceRemoveCommand InMemoryInstanceRemove(
        InMemorySquadState state,
        ISquadInstanceService instances,
        bool isInteractive = false,
        Func<string, bool>? readAnswer = null,
        SquadConsole? output = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new SquadInstanceRemoveCommand(
            instances,
            state.UserPaths,
            new InMemorySquadFileAccess(state.Files),
            new SquadConsoleOptions(isInteractive, readAnswer),
            output ?? CapturedSquadConsole(),
            state.Paths,
            state.Environment);
    }

    /// <summary><c>squad status</c> over the in-memory state, file system and environment of <paramref name="state"/>.</summary>
    public static SquadStatusCommand InMemoryStatus(
        InMemorySquadState state,
        ISquadGlobalRootResolver? globalRoots = null,
        SquadConsole? output = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        SquadConsole console = output ?? CapturedSquadConsole();
        return new SquadStatusCommand(
            state.Store,
            state.Migration,
            globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory),
            new SquadModelPresetStore(state.UserPaths, state.Files),
            state.Semantics,
            console,
            state.Files,
            state.Environment,
            new SquadCommandRoots(state.Files, state.Paths, state.Environment, console));
    }

    /// <summary><c>squad uninstall</c> over the in-memory lifecycle of <paramref name="state"/>; not interactive unless <paramref name="isInteractive"/> is set.</summary>
    public static SquadUninstallCommand InMemoryUninstall(
        InMemorySquadState state,
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        ISquadGlobalRootResolver? globalRoots = null,
        SquadConsole? output = null,
        bool isInteractive = false,
        Func<string, bool>? readAnswer = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        SquadConsole console = output ?? CapturedSquadConsole();
        ISquadGlobalRootResolver roots = globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory);
        return new SquadUninstallCommand(
            InMemoryLifecycle(state, releaseSource, renderer, globalRoots),
            state.Store,
            state.Migration,
            roots,
            new SquadConsoleOptions(isInteractive, readAnswer),
            console,
            new SquadTargetRootConfirmation(console),
            state.Environment,
            new SquadCommandRoots(state.Files, state.Paths, state.Environment, console));
    }

    private sealed class InMemoryTransactions(InMemorySquadState state) : ISquadTransactionFactory
    {
        public ISquadTransaction Create(ISquadTransactionObserver? observer) => state.Transaction(observer);
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
            new SquadModelPresetStore(store.UserPaths, new PhysicalFileSystem()),
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
            new SquadModelPresetStore(store.UserPaths, new PhysicalFileSystem()),
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
            PhysicalRoots(CapturedSquadConsole()),
            McpWiringTestServices.Wiring);
    }

    /// <summary>
    /// Builds <c>squad doctor</c> over an in-memory Squad state: its file system, path rules, environment and Squad source
    /// loader, so a doctor test touches no disk, process or real home. Pass <paramref name="state"/> to share a tree the test
    /// arranged.
    /// </summary>
    public static SquadDoctorCommand InMemoryDoctor(
        InMemorySquadState state,
        IProcessExecutor? executor = null,
        string? workingDirectory = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadRenderer? renderer = null,
        IKyberWeaveConfigRepository? configs = null,
        SquadConsole? output = null,
        IMcpWiringService? wiring = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        InMemorySquadServices services = new(state.Files);
        SquadConsole console = output ?? CapturedSquadConsole();
        return new SquadDoctorCommand(
            state.Store,
            state.Migration,
            globalRoots ?? new FakeSquadGlobalRootResolver(state.UserPaths.HomeDirectory),
            renderer ?? new SquadRendererRegistry(SquadHostDefaults.CreateRenderers(), services.RenderInputReader()),
            new SquadModelPresetStore(state.UserPaths, state.Files),
            state.Planner,
            new SquadResponseGuidanceReader(state.Files, state.Files, state.Semantics),
            state.Environment,
            new SquadPackSourceLocator(state.Files, state.Paths, state.Environment),
            services.SourceLoader(),
            new SquadSkillShadowing(state.Files, state.Semantics, state.Environment),
            new DevinImportOverlap(state.Files, state.Semantics, state.Environment),
            new ZCodeMcpConfiguration(state.Files, state.Semantics, state.Environment),
            new SquadDoctorOptions(executor, workingDirectory),
            state.Semantics,
            configs ?? new FakeKyberWeaveConfigRepository(),
            console,
            state.Files,
            new SquadCommandRoots(state.Files, state.Paths, state.Environment, console),
            wiring ?? new FakeMcpWiringService());
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
            new SquadModelPresetStore(store.UserPaths, new PhysicalFileSystem()),
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
            new SquadModelPresetStore(store.UserPaths, new PhysicalFileSystem()),
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
            new SquadModelPresetStore(store.UserPaths, new PhysicalFileSystem()),
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
            new SquadModelPresetStore(paths, new PhysicalFileSystem()),
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
                new SquadCanonicalProfileStore(store.UserPaths, new PhysicalFileSystem()));

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
