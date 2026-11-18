using KyberWeave.Testing.Squad;
using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Application.Squad;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the concrete Squad collaborators over the physical adapters for the tests that name each collaborator they
/// replace. No product code calls it: the Squad commands take theirs from the container.
/// </summary>
internal static class SquadHostDefaults
{
    /// <summary>Resolves user paths from environment variables (HOME / USERPROFILE / APPDATA) or system folders.</summary>
    public static ISquadUserPaths ResolveUserPaths(
        ISquadUserPaths? userPaths = null,
        Func<string, string?>? environmentReader = null)
    {
        if (userPaths is not null) return userPaths;

        Func<string, string?> reader = environmentReader ?? Environment.GetEnvironmentVariable;
        string? home = reader("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = reader("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string? appData = reader("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData) && !string.IsNullOrWhiteSpace(home))
            appData = Path.Combine(home, ".config");

        return new CliSquadUserPaths(home ?? string.Empty, appData ?? string.Empty);
    }

    /// <summary>Creates a state store over the resolved user paths.</summary>
    public static SquadStateStore ResolveStateStore(
        ISquadUserPaths? userPaths = null,
        Func<string, string?>? environmentReader = null) =>
        new(
            userPaths ?? ResolveUserPaths(null, environmentReader),
            new PhysicalFileSystem(),
            SquadFileSystemPathSemantics.Instance,
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment());

    /// <summary>Creates the per-target global root resolver over the environment.</summary>
    public static ISquadGlobalRootResolver ResolveGlobalRoots(Func<string, string?>? environmentReader = null)
    {
        Func<string, string?> reader = environmentReader ?? Environment.GetEnvironmentVariable;
        string? home = reader("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = reader("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new SquadGlobalRoots(reader, home ?? string.Empty, ReadFileTextOrNull);
    }

    /// <summary>Creates the registry over one instance of every shipped renderer.</summary>
    public static ISquadRenderer ResolveRenderer() => new SquadRendererRegistry(CreateRenderers(), RealSquadServices.RenderInputReader());

    /// <summary>Creates one instance of every shipped harness renderer.</summary>
    public static IReadOnlyList<ISquadRenderer> CreateRenderers() => SquadRendererSet.Create();

    /// <summary>Creates the platform link file system.</summary>
    public static ISquadLinkFileSystem ResolveLinkFileSystem(ISquadLinkFileSystem? customFileSystem = null) =>
        customFileSystem ?? (OperatingSystem.IsWindows()
            ? new WindowsSquadLinkFileSystem()
            : new UnixSquadLinkFileSystem());

    /// <summary>Creates a deployment transaction over the state store.</summary>
    public static SquadTransaction ResolveTransaction(
        SquadStateStore? stateStore = null,
        ISquadUserPaths? userPaths = null,
        ISquadTransactionObserver? observer = null) =>
        new(stateStore ?? ResolveStateStore(userPaths), observer);

    /// <summary>Creates a lifecycle service over the physical adapters, replacing whichever collaborators are given.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The lifecycle owns the release source for the run.")]
    public static SquadLifecycleService CreateLifecycleService(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        ISquadTransactionObserver? observer = null,
        TimeProvider? timeProvider = null,
        ISquadGlobalRootResolver? globalRoots = null,
        Func<string, string?>? environmentReader = null)
    {
        SquadStateStore store = stateStore ?? ResolveStateStore(userPaths, environmentReader);
        ISquadReleaseSource source = releaseSource
                                     ?? CreateGitHubReleaseSource(environmentReader ?? Environment.GetEnvironmentVariable);
        return CreateLifecycleService(
            source,
            renderer ?? ResolveRenderer(),
            store,
            timeProvider,
            observer,
            globalRoots ?? ResolveGlobalRoots(environmentReader),
            presetStore: null,
            selectionStore: null,
            environmentReader);
    }

    /// <summary>Creates a lifecycle service from explicit collaborators over the physical adapters.</summary>
    public static SquadLifecycleService CreateLifecycleService(
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        SquadStateStore stateStore,
        TimeProvider? timeProvider = null,
        ISquadTransactionObserver? observer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        SquadModelPresetStore? presetStore = null,
        SquadModelPresetSelectionStore? selectionStore = null,
        Func<string, string?>? environmentReader = null,
        IFileSystemPathSemantics? pathSemantics = null) =>
        new(
            releaseSource,
            renderer,
            stateStore,
            presetStore ?? new SquadModelPresetStore(stateStore.UserPaths),
            selectionStore ?? new SquadModelPresetSelectionStore(stateStore.UserPaths, new PhysicalFileSystem(), SquadFileSystemPathSemantics.Instance),
            RealSquadServices.SourceLoader(),
            RealSquadServices.PresetValidator(),
            RealSquadDeployment.Planner,
            new SquadTransactionFactory(stateStore),
            new SquadResponseGuidanceReader(new PhysicalFileSystem()),
            new FactoryHooksShadowing(new PhysicalFileSystem()),
            new SquadCanonicalProfileStore(stateStore.UserPaths),
            new PhysicalSquadFileAccess(),
            pathSemantics ?? SquadFileSystemPathSemantics.Instance,
            timeProvider,
            observer,
            globalRoots,
            environmentReader);

    /// <summary>Creates an instance service over the physical adapters, installing from a local checkout when one is found.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The lifecycle owns the release source for the run.")]
    public static SquadInstanceService CreateInstanceService(ISquadUserPaths? userPaths = null)
    {
        ISquadUserPaths resolvedPaths = userPaths ?? ResolveUserPaths();
        SquadStateStore stateStore = ResolveStateStore(resolvedPaths);
        string? local = new PhysicalSquadFileAccess().FindCheckoutSquadSourceRoot();
        ISquadReleaseSource releaseSource = local is not null
            ? SquadReleaseSources.LocalCheckout(local, RealSquadServices.CompanionSkills())
            : CreateGitHubReleaseSource(Environment.GetEnvironmentVariable);
        SquadLifecycleService lifecycleService = CreateLifecycleService(resolvedPaths, stateStore, releaseSource);
        return new SquadInstanceService(
            lifecycleService,
            new SquadModelPresetStore(resolvedPaths),
            ResolveLinkFileSystem(),
            ResolveGlobalRoots(),
            RealSquadServices.InstanceLinkSync(),
            new PhysicalSquadFileAccess(),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment(),
            stateStore);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The caller owns the release source and disposes it with the run.")]
    private static ISquadReleaseSource CreateGitHubReleaseSource(Func<string, string?> readEnvironment)
    {
        PhysicalFileSystem files = new();
        return new GitHubSquadReleaseSource(
            new SocketSquadReleaseHttpHandlers().Create(),
            ReleaseOrigin.Resolve(readEnvironment).ApiRoot,
            files,
            new PhysicalPathResolver(files),
            new ProcessEnvironment(),
            new SystemIdGenerator());
    }

    private static string? ReadFileTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The home and application-data directories taken from the environment.</summary>
    internal sealed class CliSquadUserPaths(string homeDirectory, string applicationDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;

        public string HomeDirectory { get; } = homeDirectory;
    }
}
