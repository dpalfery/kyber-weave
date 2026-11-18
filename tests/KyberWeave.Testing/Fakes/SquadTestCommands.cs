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
using KyberWeave.Data.Host;
using KyberWeave.Data.Review;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Configuration;

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
        IKyberWeaveConfigRepository? configs = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadInstallCommand(
            SquadHostDefaults.CreateLifecycleService(userPaths, store, releaseSource, renderer, globalRoots: roots, environmentReader: environmentReader),
            store,
            new SquadStateMigration(store, store.UserPaths),
            new FileSquadTargetResolver(),
            roots,
            new SquadConsoleOptions(isInteractive, readAnswer),
            configs ?? new FileKyberWeaveConfigRepository(RealRepositoryTreeProbe.Create()),
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
        IKyberWeaveConfigRepository? configs = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadUpdateCommand(
            SquadHostDefaults.CreateLifecycleService(userPaths, store, releaseSource, renderer, globalRoots: roots, environmentReader: environmentReader),
            instanceService ?? new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(userPaths ?? store.UserPaths)),
            store,
            new SquadStateMigration(store, store.UserPaths),
            new FileSquadTargetResolver(),
            roots,
            new PhysicalSquadFileAccess(),
            new SquadConsoleOptions(isInteractive, readAnswer),
            configs ?? new FileKyberWeaveConfigRepository(RealRepositoryTreeProbe.Create()),
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
        Func<string, string?>? environmentReader = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        ISquadGlobalRootResolver roots = globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader);
        return new SquadUninstallCommand(
            lifecycleService ?? SquadHostDefaults.CreateLifecycleService(userPaths, store, globalRoots: roots, environmentReader: environmentReader),
            store,
            new SquadStateMigration(store, store.UserPaths),
            roots,
            new SquadConsoleOptions(isInteractive, readAnswer));
    }

    public static SquadInstanceRemoveCommand InstanceRemove(
        ISquadUserPaths? userPaths = null,
        ISquadInstanceService? instanceService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null)
    {
        ISquadUserPaths paths = userPaths ?? SquadHostDefaults.ResolveUserPaths();
        return new SquadInstanceRemoveCommand(
            instanceService ?? new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(paths)),
            paths,
            new PhysicalSquadFileAccess(),
            new SquadConsoleOptions(isInteractive, readAnswer));
    }

    public static SquadPackCommand Pack(string? workingDirectory = null) =>
        new(new SquadPackService(
            new FakeProcessEnvironment(currentDirectory: workingDirectory ?? "/kw/work"),
            new FileSquadSourceLocator(),
            new FileSquadPackager(RealSquadPacker.Create())));

    public static SquadRunCommand Run(
        ISquadUserPaths? userPaths = null,
        SquadModelPresetSelectionStore? selectionStore = null,
        Func<ProcessStartInfo, int>? runner = null,
        Func<string, string?>? getEnv = null,
        Func<string, string?>? findOnPath = null,
        string? workingDirectory = null,
        Func<SquadTarget, string, Task<SquadInstanceEnsureResult>>? ensureOverride = null)
    {
        ISquadUserPaths paths = userPaths ?? SquadHostDefaults.ResolveUserPaths();
        return new SquadRunCommand(
            ensureOverride is not null
                ? new FuncInstanceService(ensureOverride)
                : new LazyInstanceService(() => SquadHostDefaults.CreateInstanceService(paths)),
            selectionStore ?? RealSquadSelectionStore.Create(paths),
            ensureOverride is not null ? new AnyPresetStore() : new SquadModelPresetStore(paths),
            new ProcessSquadHarnessLauncher(),
            new ProcessEnvironment(),
            new SquadRunOptions(runner, getEnv, findOnPath, workingDirectory));
    }

    public static SquadStatusCommand Status(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        return new SquadStatusCommand(
            store,
            new SquadStateMigration(store, store.UserPaths),
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader),
            new SquadModelPresetStore(store.UserPaths),
            SquadFileSystemPathSemantics.Instance);
    }

    public static SquadDoctorCommand Doctor(
        IProcessExecutor? executor = null,
        ISquadUserPaths? userPaths = null,
        string? workingDirectory = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadRenderer? renderer = null,
        SquadStateStore? stateStore = null,
        Func<string, string?>? environmentReader = null,
        IKyberWeaveConfigRepository? configs = null)
    {
        SquadStateStore store = stateStore ?? SquadHostDefaults.ResolveStateStore(userPaths, environmentReader);
        return new SquadDoctorCommand(
            store,
            new SquadStateMigration(store, store.UserPaths),
            globalRoots ?? SquadHostDefaults.ResolveGlobalRoots(environmentReader),
            renderer ?? SquadHostDefaults.ResolveRenderer(),
            new SquadModelPresetStore(store.UserPaths),
            new FileSquadDeploymentPlanner(),
            new FileSquadResponseGuidanceStore(),
            new FakeProcessEnvironment(readVariable: environmentReader),
            new FileSquadSourceLocator(),
            new FileSquadSourceLoader(),
            new FileSquadSkillShadowing(),
            new FileDevinImportOverlapInspector(),
            new FileZCodeMcpInspector(),
            new SquadDoctorOptions(executor, workingDirectory),
            SquadFileSystemPathSemantics.Instance,
            configs ?? new FileKyberWeaveConfigRepository(RealRepositoryTreeProbe.Create()));
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
            new FileSquadDeploymentPlanner(),
            new SquadTransactionFactory(store),
            SquadFileSystemPathSemantics.Instance);
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
            new FileSquadDeploymentPlanner(),
            new SquadTransactionFactory(store),
            SquadFileSystemPathSemantics.Instance);
    }

    public static SquadPresetShowCommand PresetShow(
        ISquadUserPaths? userPaths = null,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? canonicalProfileResolver = null)
    {
        SquadStateStore store = SquadHostDefaults.ResolveStateStore(userPaths);
        return new SquadPresetShowCommand(
            userPaths ?? store.UserPaths,
            new SquadModelPresetStore(store.UserPaths),
            Profiles(store, canonicalProfileResolver));
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
            RealSquadSelectionStore.Create(paths));
    }

    private static ISquadCanonicalProfileResolver Profiles(
        SquadStateStore store,
        Func<string?, ISquadUserPaths, SquadCanonicalProfiles?>? resolver) =>
        resolver is not null
            ? new FuncProfileResolver(version => resolver(version, store.UserPaths))
            : new CheckoutOrSnapshotCanonicalProfileResolver(
                new PhysicalSquadFileAccess(),
                new FileSquadSourceLoader(),
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
