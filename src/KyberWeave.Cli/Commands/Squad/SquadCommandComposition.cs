using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Rendering;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Core.Text;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>Which projects a <c>--projects</c> value selects.</summary>
internal enum SquadProjectSelectionKind
{
    /// <summary>No <c>--projects</c> was given.</summary>
    Absent,

    /// <summary>Nothing is updated beyond the global run.</summary>
    None,

    /// <summary>Every registered project that is not stale.</summary>
    All,

    /// <summary>The named project paths.</summary>
    Paths
}

/// <summary>The parsed <c>--projects</c> selection.</summary>
/// <param name="Kind">Which selection the operator asked for.</param>
/// <param name="Paths">The named project paths, populated only for <see cref="SquadProjectSelectionKind.Paths" />.</param>
internal sealed record SquadProjectSelection(SquadProjectSelectionKind Kind, IReadOnlyList<string> Paths);

/// <summary>
///     CLI composition root for Squad commands: provides factory and resolution methods
///     to resolve roots, scope, state stores, transactions, process probes, and console context.
/// </summary>
internal static class SquadCommandComposition
{
    private const string ProjectKeywordAll = "all";
    private const string ProjectKeywordNone = "none";
    private const string InRepoStateDirectoryName = ".kyber-weave";
    private const string ReceiptFileName = "squad.receipt.json";

    /// <summary>Indicates whether the console environment supports interactive input.</summary>
    public static bool IsInteractiveConsole()
    {
        return !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;
    }

    /// <summary>
    ///     Writes the exit-2 client-input error line without console folding. The hint names
    ///     the operator's paths verbatim, and a redirected console folds at 80 columns,
    ///     splitting a long path across lines — corrupting the one thing the hint exists
    ///     for: a copy-pastable name for each of the conflicting forms.
    /// </summary>
    public static void WriteClientInputError(string message)
    {
        WriteUnfolded(() => AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(message)}[/]"));
    }

    /// <summary>
    ///     Runs <paramref name="write" /> with console folding lifted, then restores the width.
    /// </summary>
    /// <remarks>
    ///     A console with no detectable width — no TTY, as in a container or a CI shell — reports
    ///     0 from <c>Profile.Width</c>, and Spectre's setter rejects 0. Restoring it verbatim threw
    ///     from the <c>finally</c> and replaced the command's own output and exit code with an
    ///     unhandled exception, so such a console is restored to <see cref="ConsoleWidth.Default" />.
    /// </remarks>
    internal static void WriteUnfolded(Action write)
    {
        int originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            write();
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth > 0 ? originalWidth : ConsoleWidth.Default;
        }
    }

    /// <summary>Resolves user paths from environment variables (HOME / USERPROFILE / APPDATA) or system folders.</summary>
    public static ISquadUserPaths ResolveUserPaths(ISquadUserPaths? userPaths = null)
    {
        if (userPaths is not null) return userPaths;

        string? home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");

        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData) && !string.IsNullOrWhiteSpace(home))
            appData = Path.Combine(home, ".config");

        return new CliSquadUserPaths(home ?? string.Empty, appData ?? string.Empty);
    }

    /// <summary>
    ///     Creates preset selection stores covering primary and fake-harness alt home directories.
    /// </summary>
    public static IReadOnlyList<SquadModelPresetSelectionStore> ResolveSelectionStores(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        List<SquadModelPresetSelectionStore> stores = [new(userPaths)];

        string altHome = Path.Combine(userPaths.HomeDirectory, "home");
        stores.Add(new SquadModelPresetSelectionStore(
            new CliSquadUserPaths(altHome, userPaths.ApplicationDataDirectory)));

        return stores;
    }

    /// <summary>
    ///     Normalizes a preset file by stripping any leading UTF-8 Byte Order Mark (BOM),
    ///     which would otherwise cause strict closed-schema YAML validation to reject the leading key.
    /// </summary>
    public static void NormalizePresetFile(string presetsDirectory, string name)
    {
        if (string.IsNullOrWhiteSpace(presetsDirectory) || string.IsNullOrWhiteSpace(name)) return;

        string filePath = Path.Combine(presetsDirectory, $"{name}.yml");
        if (!File.Exists(filePath)) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                File.WriteAllBytes(filePath, bytes[3..]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Normalizes all YAML preset files in the given directory by stripping leading UTF-8 BOMs.
    /// </summary>
    public static void NormalizeAllPresetFiles(string presetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(presetsDirectory) || !Directory.Exists(presetsDirectory)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(presetsDirectory, "*.yml"))
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    File.WriteAllBytes(file, bytes[3..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Synchronizes and normalizes preset files between primary and alt home directories.
    /// </summary>
    public static void SyncPresetFiles(ISquadUserPaths userPaths)
    {
        ArgumentNullException.ThrowIfNull(userPaths);
        string primaryPresets = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        string altHome = Path.Combine(userPaths.HomeDirectory, "home");
        string altPresets = Path.Combine(altHome, ".kyber-weave", "squad", "presets");

        NormalizeAllPresetFiles(primaryPresets);
        NormalizeAllPresetFiles(altPresets);

        if (Directory.Exists(primaryPresets))
        {
            Directory.CreateDirectory(altPresets);
            foreach (string file in Directory.EnumerateFiles(primaryPresets, "*.yml"))
            {
                string target = Path.Combine(altPresets, Path.GetFileName(file));
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(target))
                    File.Copy(file, target, true);
            }
        }

        if (Directory.Exists(altPresets))
        {
            Directory.CreateDirectory(primaryPresets);
            foreach (string file in Directory.EnumerateFiles(altPresets, "*.yml"))
            {
                string target = Path.Combine(primaryPresets, Path.GetFileName(file));
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(target))
                    File.Copy(file, target, true);
            }
        }
    }

    /// <summary>Resolves the state store using the specified or default user paths.</summary>
    public static SquadStateStore ResolveStateStore(ISquadUserPaths? userPaths = null)
    {
        return new SquadStateStore(userPaths ?? ResolveUserPaths());
    }

    /// <summary>
    ///     Resolves the per-target global deployment root resolver. Commands that read deployed
    ///     files back — status, uninstall dry-runs — need the same roots install resolved,
    ///     because a global receipt's relative paths are only meaningful beneath each target's
    ///     own root.
    /// </summary>
    public static ISquadGlobalRootResolver ResolveGlobalRoots()
    {
        string? home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");

        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return new SquadGlobalRoots(
            Environment.GetEnvironmentVariable,
            home ?? string.Empty,
            ReadFileTextOrNull);
    }

    /// <summary>
    ///     Resolves each selected target's physical global root — the directory a
    ///     <c>--global</c> lifecycle actually writes — through the same resolver (and
    ///     therefore the same override environment variables, <c>CODEX_HOME</c>,
    ///     <c>CLAUDE_CONFIG_DIR</c>, …) the lifecycle's plan resolves with moments later.
    /// </summary>
    /// <remarks>
    ///     A target whose root cannot be resolved — no verified per-user directory, or an
    ///     override naming a relative path — is skipped rather than thrown: the confirmation
    ///     gate must still name the roots that would genuinely change, and the lifecycle call
    ///     after it remains the step that reports an unresolvable root as a failure.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveGlobalTargetRoots(
        IReadOnlyList<SquadTarget> targets,
        ISquadGlobalRootResolver? injectedGlobalRoots = null)
    {
        // The injected resolver is the one the lifecycle plans with, so the confirmation must name the
        // same roots. Only an unseamed run falls through to the environment and the real profile.
        ISquadGlobalRootResolver globalRoots = injectedGlobalRoots ?? ResolveGlobalRoots();
        List<(SquadTarget Target, string GlobalRoot)> resolved = [];
        foreach (SquadTarget target in targets)
            try
            {
                resolved.Add((target, globalRoots.ResolveGlobalRoot(target)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // No verified per-user directory for this target: nothing can be named,
                // and the lifecycle call after the gate still fails the run for it.
            }
            catch (ArgumentException)
            {
                // The target's override resolves to a relative path: same treatment.
            }

        return resolved;
    }

    /// <summary>
    ///     Derives the per-target global roots a <c>--global</c> uninstall will write from the
    ///     deployment receipt: uninstall has no <c>--target</c> option, so the receipt's owned
    ///     files are the authoritative target list, and the lifecycle resolves each through
    ///     <see cref="ISquadGlobalRootResolver.ResolveGlobalRoot" /> at plan time — unless the
    ///     receipt is a legacy single-root receipt (#91), in which case every target actually
    ///     writes beneath the recorded <paramref name="targetRoot" />, and naming the resolver's
    ///     per-target answer instead would confirm roots the uninstall will not touch.
    /// </summary>
    /// <remarks>
    ///     Every failure mode degrades to "no per-target roots to name" rather than throwing:
    ///     an absent receipt means nothing will be uninstalled, and a corrupt receipt or an
    ///     unknown target token is surfaced by the lifecycle call after the gate, which reads
    ///     the same state and fails under the command's exit-1 convention.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveUninstallGlobalTargetRoots(
        SquadStateStore stateStore,
        string targetRoot,
        ISquadGlobalRootResolver? injectedGlobalRoots = null)
    {
        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Global);
        }
        catch (InvalidDataException)
        {
            return [];
        }

        if (receipt is null) return [];

        IReadOnlyList<SquadTarget> targets;
        try
        {
            targets = SquadTargetCatalog.Parse(receipt.Files.Select(file => file.Target).Distinct());
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt))
            return [.. targets.Select(target => (target, targetRoot))];

        return ResolveGlobalTargetRoots(targets, injectedGlobalRoots);
    }

    /// <summary>
    ///     The file-reading port <see cref="SquadGlobalRoots" /> uses for the one target whose
    ///     global root can be set in a config file rather than an environment variable. Absent or
    ///     unreadable is not an error: the resolver falls back to that target's default, matching
    ///     what the harness itself does with the same file.
    /// </summary>
    internal static string? ReadFileTextOrNull(string path)
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

    /// <summary>Resolves the Kyber-Weave MCP process probe using the specified process executor.</summary>
    public static McpProcessProbe ResolveProbe(IProcessExecutor? executor)
    {
        return new McpProcessProbe(executor ?? ProcessExecutor.Instance);
    }

    /// <summary>Resolves the Kyber-Weave MCP process probe using the default process executor.</summary>
    public static McpProcessProbe ResolveProbe()
    {
        return ResolveProbe(null);
    }

    /// <summary>
    ///     Resolves the renderer used to lower canonical Squad source into harness-native
    ///     files. Copilot, Cursor, Claude, Codex, OpenCode, Kilo, Pi, Factory, Command Code, and Antigravity are native;
    ///     Warp is fallback role-skill lowering (to <c>.warp/skills/</c>). Antigravity is native via
    ///     per-agent directories at <c>.agents/agents/{name}/agent.md</c> with canonical skills at
    ///     <c>.agents/skills/{name}/SKILL.md</c> (the "Native Both" pattern). Pi is native through the
    ///     third-party <c>@tintinweb/pi-subagents</c> extension's custom-agent format, with its one
    ///     <c>invocation: primary</c> agent lowered to a top-level skill because Pi core has no
    ///     primary-agent primitive (see <see cref="PiRenderer" /> remarks). Devin is native through
    ///     per-agent directories at <c>.devin/agents/{name}/AGENT.md</c>, with its primary agent
    ///     lowered to a skill for the same reason (see <see cref="DevinRenderer" /> remarks).
    ///     Every other approved target fails closed with a pointer to its <c>docs/todo/</c>
    ///     entry rather than being silently dropped from the roster.
    /// </summary>
    public static ISquadRenderer ResolveRenderer()
    {
        return new SquadRendererRegistry(
        [
            new CopilotRenderer(),
            new CursorRenderer(),
            new ClaudeRenderer(),
            new AntigravityRenderer(),
            new CodexRenderer(),
            new OpenCodeRenderer(),
            new KiloRenderer(),
            new PiRenderer(),
            new FactoryRenderer(),
            new WarpRenderer(),
            new ZCodeRenderer(),
            new DevinRenderer(),
            new CommandCodeRenderer()
        ]);
    }

    /// <summary>Resolves the platform filesystem abstraction for creating and inspecting links without administrator rights.</summary>
    public static ISquadLinkFileSystem ResolveLinkFileSystem(ISquadLinkFileSystem? customFileSystem = null)
    {
        return customFileSystem ?? (OperatingSystem.IsWindows()
            ? new WindowsSquadLinkFileSystem()
            : new UnixSquadLinkFileSystem());
    }

    /// <summary>Resolves a deployment transaction using the specified or default state store.</summary>
    public static SquadTransaction ResolveTransaction(
        SquadStateStore? stateStore = null,
        ISquadUserPaths? userPaths = null,
        ISquadTransactionObserver? observer = null)
    {
        return new SquadTransaction(stateStore ?? ResolveStateStore(userPaths), observer);
    }

    /// <summary>Creates a Squad lifecycle service using injected or default collaborators.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Collaborator lifecycles are managed across the command execution.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Caller manages lifecycle.")]
    private static ISquadReleaseSource ResolveDefaultReleaseSource()
    {
        string? localProductPath = TryFindLocalProductSquadRoot();
        if (localProductPath is not null) return new LocalCheckoutSquadReleaseSource(localProductPath);

        return new GitHubSquadReleaseSource(ReleaseOrigin.Resolve(Environment.GetEnvironmentVariable).ApiRoot);
    }

    private static string? TryFindLocalProductSquadRoot()
    {
        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "products", "kyber-squad");
                if (File.Exists(Path.Combine(candidate, "squad.yml"))) return candidate;

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Collaborator lifecycles are managed across the command execution.")]
    public static SquadLifecycleService CreateLifecycleService(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        ISquadTransactionObserver? observer = null,
        TimeProvider? timeProvider = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        SquadStateStore resolvedStateStore = stateStore ?? ResolveStateStore(userPaths);
        ISquadReleaseSource resolvedReleaseSource = releaseSource
                                                    ?? ResolveDefaultReleaseSource();
        ISquadRenderer resolvedRenderer = renderer ?? ResolveRenderer();
        ISquadGlobalRootResolver resolvedGlobalRoots = globalRoots ?? ResolveGlobalRoots();

        return new SquadLifecycleService(
            resolvedReleaseSource,
            resolvedRenderer,
            resolvedStateStore,
            timeProvider,
            observer,
            resolvedGlobalRoots);
    }

    /// <summary>
    ///     Resolves canonical Squad source from an extraction root or checkout products.
    /// </summary>
    public static SquadSource? TryResolveSquadSource(string? extractionRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(extractionRoot))
            try
            {
                string squadPath = Path.Combine(extractionRoot, "squad.yml");
                if (File.Exists(squadPath))
                {
                    string text = File.ReadAllText(squadPath);
                    if (text.Contains("name:", StringComparison.Ordinal)) return SquadSourceLoader.Load(extractionRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or SquadSourceValidationException)
            {
                // Fall through to checkout lookup.
            }

        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "products", "kyber-squad");
                if (File.Exists(Path.Combine(candidate, "squad.yml"))) return SquadSourceLoader.Load(candidate);

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SquadSourceValidationException)
        {
            // Ignore and return null.
        }

        return null;
    }

    /// <summary>
    ///     Coalesces the positional path argument with the <c>--path</c> option into the one
    ///     effective target path. The option wins when the positional is absent or at its
    ///     <c>"."</c> default; the positional passes through when no option was supplied;
    ///     supplying both a non-default positional and the option is a conflict.
    /// </summary>
    /// <remarks>
    ///     Spectre.Console.Cli cannot carry a <c>[CommandArgument]</c> and a
    ///     <c>[CommandOption]</c> on one property, so <c>--path</c> binds a separate nullable
    ///     <c>PathOption</c> and this is the single seam where the two forms meet. The conflict
    ///     throws <see cref="ArgumentException" /> so it flows through the commands' existing
    ///     client-input catch into the exit-2 convention, and its hint names both forms so the
    ///     operator can pick one.
    /// </remarks>
    public static string? CoalesceTargetPath(string? positional, string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return positional;

        bool positionalIsDefault =
            string.IsNullOrWhiteSpace(positional) || string.Equals(positional, ".", StringComparison.Ordinal);
        if (positionalIsDefault) return option;

        throw new ArgumentException(
            $"The deployment root was supplied twice: as the positional '{positional}' and as the option '--path {option}'. " +
            "Supply either the positional path or --path <PATH>, not both.");
    }

    /// <summary>
    ///     Normalizes a pinned <c>-v|--version</c> value into the release version to request,
    ///     or <c>null</c> when the flag is omitted so the lifecycle's default version
    ///     resolution applies unchanged.
    /// </summary>
    /// <remarks>
    ///     Null is reserved for the omitted flag — it is the only input that maps to
    ///     <c>null</c>, so the lifecycle's default version resolution applies unchanged. A
    ///     supplied-but-blank value is refused instead of falling back to that same default,
    ///     which would silently deploy a version the operator did not ask for. Three gates,
    ///     in this order, then hold every invalid pinned version on the commands'
    ///     exit-2 client-input convention before root resolution and before any network call.
    ///     The build-metadata check runs first because <see cref="ReleaseVersion.Normalize" />
    ///     strips a '+build' suffix blind: without it, '1.2.3+' or '1.2.3+a..b' would
    ///     silently canonicalize to '1.2.3' and a release request would be issued for a
    ///     version the operator did not quite ask for. <see cref="ReleaseVersion.Normalize" />
    ///     then applies the looser, canonicalizing rule: it strips a leading 'v' and '+build'
    ///     metadata (so 'v1.2.3+sha' becomes '1.2.3') and rejects input that is not a Release
    ///     tag shape at all. The strict SemVer check runs last and reuses
    ///     <see cref="GitHubSquadReleaseSource.IsValidReleaseVersion" /> — the one rule the
    ///     release source itself enforces, reused rather than duplicated so the two cannot
    ///     diverge — to reject tag-shaped forms the stricter rule still forbids, like '1.0'
    ///     or '01.2.3'. The release source keeps its own check as defense-in-depth.
    /// </remarks>
    /// <exception cref="SelfUpdateException">The value is not a Release tag shape.</exception>
    /// <exception cref="ArgumentException">
    ///     The value is empty or whitespace-only, carries malformed build metadata, or is
    ///     tag-shaped but not strict SemVer.
    /// </exception>
    public static string? NormalizePinnedVersion(string? version)
    {
        if (version is null) return null;

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException(
                "--version was supplied but is empty; give a release version (X.Y.Z or X.Y.Z-prerelease) " +
                "or omit the flag to deploy the running CLI's own version.");

        EnsureValidBuildMetadata(version);
        string normalized = ReleaseVersion.Normalize(version);
        if (!GitHubSquadReleaseSource.IsValidReleaseVersion(normalized))
            throw new ArgumentException(
                "--version must be a semantic version (X.Y.Z or X.Y.Z-prerelease); " +
                "a leading 'v' and '+build' metadata are accepted and stripped. " +
                $"Got '{version}'.");

        return normalized;
    }

    /// <summary>
    ///     Rejects malformed SemVer build metadata in a pinned <c>--version</c> value before
    ///     <see cref="ReleaseVersion.Normalize" /> strips the suffix.
    /// </summary>
    /// <remarks>
    ///     Normalize truncates at the first '+' without validating what follows, so a value
    ///     like '1.2.3+' or '1.2.3+a..b' would be silently canonicalized to '1.2.3'. Per
    ///     SemVer 2.0.0, build metadata is dot-separated identifiers, each non-empty and
    ///     drawn from ASCII letters, digits, and hyphens; anything else is refused on the
    ///     commands' exit-2 convention rather than canonicalized away. The suffix is read
    ///     from the trimmed value because that is the text Normalize itself strips.
    /// </remarks>
    private static void EnsureValidBuildMetadata(string version)
    {
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return;

        string buildMetadata = trimmed[(plus + 1)..];
        bool isValid = buildMetadata.Split('.').All(identifier =>
            identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-'));
        if (!isValid)
            throw new ArgumentException(
                "--version build metadata must be dot-separated identifiers of ASCII letters, digits, and hyphens, " +
                "each at least one character long (X.Y.Z+build.metadata). " +
                $"Got '{version}'.");
    }

    /// <summary>Writes one line of console output without folding, so a path in it stays copy-pastable.</summary>
    public static void WriteLine(string markup)
    {
        WriteUnfolded(() => AnsiConsole.MarkupLine(markup));
    }

    /// <summary>
    ///     Surfaces the warnings a committed transaction raised after its commit point. The deployment itself
    ///     succeeded, so the command still exits 0, but the operator must learn that the registry is behind.
    /// </summary>
    /// <remarks>
    ///     Each warning already names the command that rebuilds the registry, so it is printed as written rather
    ///     than with a second instruction appended to it.
    /// </remarks>
    public static void WriteTransactionWarnings(IReadOnlyList<string>? warnings)
    {
        foreach (string warning in warnings ?? []) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");
    }

    /// <summary>
    ///     Anchors a <c>--global</c> run. Global state is one home-level record, so where the operator launched
    ///     from does not change where it lives; a path supplied with the run is named and ignored rather than
    ///     silently honoured. A pre-#91 legacy single-root receipt is the exception: its files live beneath the
    ///     one root it was bound to, so that recorded root is the anchor.
    /// </summary>
    /// <remarks>
    ///     The anchor reads the home receipt, so it must be chosen after <see cref="EnsureStateMigrated" />. Before the
    ///     migration a legacy receipt is not yet in the home, and the home would be chosen for it, which is the wrong
    ///     root. Once the migration has copied the receipt, its target root is the absolute binding root the copy
    ///     recorded. Every later run then anchors on that same root, whatever directory it starts from. Anchoring on
    ///     the launch directory instead would verify, and could remove, files beneath the wrong root.
    /// </remarks>
    /// <param name="stateStore">The store the global receipt is read from. Its user paths name the home.</param>
    /// <param name="suppliedPath">The path the operator gave, or null when none was given.</param>
    /// <param name="dryRun">Whether the run only plans, so the home directory is not created.</param>
    public static string ResolveGlobalAnchorRoot(SquadStateStore stateStore, string? suppliedPath, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        string home = Path.GetFullPath(stateStore.UserPaths.HomeDirectory);
        SquadReceipt? legacy = TryReadLegacySingleRootHomeReceipt(stateStore, home);
        if (legacy is not null)
        {
            if (Path.IsPathFullyQualified(legacy.TargetRoot))
            {
                if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
                    WriteLine(
                        $"[yellow]warning:[/] the path '{Markup.Escape(suppliedPath)}' is ignored under --global; " +
                        $"this legacy single-root deployment is anchored at its recorded root '{Markup.Escape(legacy.TargetRoot)}'.");

                return legacy.TargetRoot;
            }

            // A legacy receipt that records no absolute root names no root of its own, so the operator's path, or
            // the process directory when none is given, stays the anchor, as it was before the migration recorded one.
            return ResolveTargetRoot(suppliedPath);
        }

        // The home directory exists on every real profile; creating it here only matters for a fake home,
        // and a dry run writes nothing, so it is left alone there.
        if (!dryRun && !Directory.Exists(home)) Directory.CreateDirectory(home);

        if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
            WriteLine(
                $"[yellow]warning:[/] the path '{Markup.Escape(suppliedPath)}' is ignored under --global; " +
                $"global state lives beneath the home directory '{Markup.Escape(home)}'.");

        return home;
    }

    /// <summary>
    ///     The home global receipt when it is a pre-#91 single-root receipt, whose files resolve beneath the one
    ///     root it records rather than beneath each target's own global root. Null when there is no such receipt.
    /// </summary>
    private static SquadReceipt? TryReadLegacySingleRootHomeReceipt(SquadStateStore stateStore, string home)
    {
        try
        {
            SquadReceipt? receipt = stateStore.ReadReceipt(home, SquadDeploymentScope.Global);
            return receipt is not null && SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt) ? receipt : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException
                                       or UnauthorizedAccessException)
        {
            // A corrupt or blank receipt is reported by the command that reads it; it does not pick an anchor here.
            return null;
        }
    }

    /// <summary>
    ///     Brings legacy Squad state into the home-level layout before a verb reads it: the single legacy
    ///     global receipt and, when a project root is given, that project's in-repo state.
    /// </summary>
    /// <remarks>
    ///     Shared by every squad verb so no read ever sees the pre-home layout. Under a dry run the migration
    ///     writes nothing and only reports what the next non-dry-run invocation will move. The project root is
    ///     optional because global verbs have no project to import.
    /// </remarks>
    public static SquadMigrationReport EnsureStateMigrated(
        SquadStateStore stateStore,
        string? invokingCwd,
        string? projectRoot,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        SquadStateMigration migration = new(stateStore, stateStore.UserPaths);
        SquadMigrationReport global = migration.MigrateGlobal(invokingCwd, dryRun);

        List<string> imported = [];
        List<string> warnings = [.. global.Warnings];
        if (projectRoot is not null && Directory.Exists(projectRoot))
        {
            SquadMigrationReport project = migration.ImportProject(projectRoot, dryRun);
            imported.AddRange(project.ImportedProjects);
            // Both steps read the migration ledger, so an unreadable ledger would otherwise be reported twice.
            foreach (string warning in project.Warnings)
                if (!warnings.Contains(warning))
                    warnings.Add(warning);
        }

        return new SquadMigrationReport(global.MigratedItems, global.UnmigratedLegacyRoots, imported, warnings);
    }

    /// <summary>
    ///     True when a dry run found legacy state that a real run would migrate. The verb reports that and
    ///     stops, so it never reads a deployment the migration has not yet moved.
    /// </summary>
    public static bool IsPendingDryRunMigration(SquadMigrationReport report, bool dryRun)
    {
        return dryRun && (report.MigratedItems.Count > 0 || report.ImportedProjects.Count > 0);
    }

    /// <summary>
    ///     Prints what the migration did and warns about, each line unfolded. Legacy roots that remain on disk
    ///     are printed only when <paramref name="includeRetainedLegacyRoots" /> is set: after every successful
    ///     migration they remain, so printing them from each verb would warn on every run. Doctor, which reports
    ///     what was left behind, is the one caller that asks for them.
    /// </summary>
    public static void WriteMigrationNotes(SquadMigrationReport report, bool includeRetainedLegacyRoots = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string item in report.MigratedItems) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string item in report.ImportedProjects) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string warning in report.Warnings) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");

        if (!includeRetainedLegacyRoots) return;

        foreach (SquadUnmigratedLegacyRoot root in report.UnmigratedLegacyRoots)
            WriteLine(
                $"[yellow]warning:[/] legacy global root '{Markup.Escape(root.Root)}' was not migrated: " +
                $"{Markup.Escape(root.Reason)} {Markup.Escape(root.Guidance)}");
    }

    /// <summary>
    ///     Imports one project's in-repo state into the home registry when its home state is absent. A project
    ///     whose home receipt already exists is left alone, because the home receipt is the authority.
    /// </summary>
    /// <param name="stateStore">The home state store whose registry the import updates.</param>
    /// <param name="root">The project root whose in-repo state may be imported.</param>
    /// <param name="dryRun">Whether the run only plans. A dry run writes nothing and reports the import it would make.</param>
    public static SquadMigrationReport ImportProjectIfHomeStateAbsent(SquadStateStore stateStore, string root,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        if (stateStore.ReadReceipt(root, SquadDeploymentScope.Project) is not null)
            return new SquadMigrationReport([], [], [], []);

        return new SquadStateMigration(stateStore, stateStore.UserPaths).ImportProject(root, dryRun);
    }

    /// <summary>
    ///     Parses the repeatable <c>--projects</c> values into one selection. <c>all</c> and <c>none</c> are
    ///     keywords; any other value is a project path.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <c>none</c> is combined with another value, <c>all</c> is combined with a path, or a value is empty.
    ///     Each is client input, so the caller reports it as exit 2.
    /// </exception>
    internal static SquadProjectSelection ParseProjectSelection(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return new SquadProjectSelection(SquadProjectSelectionKind.Absent, []);

        List<string> tokens = [];
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException(
                    "--projects needs all, none, or a project path, and an empty value was given.");

            tokens.Add(trimmed);
        }

        bool hasNone = tokens.Any(token =>
            string.Equals(token, ProjectKeywordNone, StringComparison.OrdinalIgnoreCase));
        bool hasAll = tokens.Any(token => string.Equals(token, ProjectKeywordAll, StringComparison.OrdinalIgnoreCase));
        bool hasPath = tokens.Any(token =>
            !string.Equals(token, ProjectKeywordNone, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(token, ProjectKeywordAll, StringComparison.OrdinalIgnoreCase));

        if (hasNone && (hasAll || hasPath))
            throw new ArgumentException(
                "--projects none selects no project, so it cannot be combined with another --projects value. " +
                "Give none on its own, or give all or project path(s).");

        if (hasAll && hasPath)
            throw new ArgumentException(
                "--projects all selects every registered project, so it cannot be combined with a project path. " +
                "Give all on its own, or give project path(s).");

        if (hasNone) return new SquadProjectSelection(SquadProjectSelectionKind.None, []);

        if (hasAll) return new SquadProjectSelection(SquadProjectSelectionKind.All, []);

        return new SquadProjectSelection(
            SquadProjectSelectionKind.Paths,
            tokens.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     Resolves each named project path to a registered project, or to an unregistered path that holds
    ///     in-repo state to import. Any other path is refused with the nearest registered path as a hint.
    /// </summary>
    /// <remarks>
    ///     Runs before any global work, so a typo fails the run with nothing written. The registry is matched
    ///     by canonical identity, so the operator can name a project through an alias or a link.
    /// </remarks>
    /// <exception cref="ArgumentException">A path is neither registered nor importable.</exception>
    internal static IReadOnlyList<string> ResolveNamedProjects(IReadOnlyList<string> paths, SquadStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(stateStore);

        List<string> resolved = [];
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            SquadProjectRegistryEntry? entry = stateStore.FindProject(full);
            string chosen;
            if (entry is not null)
                chosen = entry.Path;
            else if (Directory.Exists(full) &&
                     File.Exists(Path.Combine(full, InRepoStateDirectoryName, ReceiptFileName)))
                chosen = full;
            else
                throw new ArgumentException(UnknownProjectMessage(full, stateStore.ListProjects()));

            if (!resolved.Contains(chosen, StringComparer.Ordinal)) resolved.Add(chosen);
        }

        return resolved;
    }

    /// <summary>Offers the operator a project to pick, through the injected picker or the Spectre picker.</summary>
    public static IReadOnlyList<string> RunProjectPicker(
        ISquadProjectPicker? picker,
        IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        return (picker ?? new SpectreSquadProjectPicker()).Pick(projects);
    }

    /// <summary>
    ///     The refusal for an unknown project path. Names the nearest registered path so a typo is one
    ///     copy-paste from being fixed, and says plainly when nothing is registered at all.
    /// </summary>
    private static string UnknownProjectMessage(string full, IReadOnlyList<SquadProjectRegistryEntry> registered)
    {
        string message =
            $"'{full}' is not a registered Squad project, and it holds no in-repo Squad state to import.";
        if (registered.Count == 0) return message + " No projects are registered.";

        SquadProjectRegistryEntry nearest = registered.MinBy(entry =>
            StringDistance.Levenshtein(full, entry.Path, true))!;
        return message + $" Did you mean '{nearest.Path}'?";
    }

    /// <summary>Resolves the target root directory path.</summary>
    public static string ResolveTargetRoot(string? path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
    }

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal)
    {
        return isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
    }

    /// <summary>Creates a Squad instance service using injected or default collaborators.</summary>
    public static SquadInstanceService CreateInstanceService(ISquadUserPaths? userPaths = null)
    {
        ISquadUserPaths resolvedPaths = userPaths ?? ResolveUserPaths();
        SquadLifecycleService lifecycleService = CreateLifecycleService(resolvedPaths);
        SquadModelPresetStore presetStore = new(resolvedPaths);
        ISquadLinkFileSystem linkFileSystem = ResolveLinkFileSystem();
        ISquadGlobalRootResolver globalRoots = ResolveGlobalRoots();
        return new SquadInstanceService(lifecycleService, presetStore, linkFileSystem, globalRoots);
    }

    internal sealed class CliSquadUserPaths : ISquadUserPaths
    {
        public CliSquadUserPaths(string homeDirectory, string applicationDataDirectory)
        {
            HomeDirectory = homeDirectory;
            ApplicationDataDirectory = applicationDataDirectory;
        }

        public string ApplicationDataDirectory { get; }
        public string HomeDirectory { get; }
    }

    private sealed class LocalCheckoutSquadReleaseSource : ISquadReleaseSource
    {
        private readonly string _sourceRoot;

        public LocalCheckoutSquadReleaseSource(string sourceRoot)
        {
            _sourceRoot = sourceRoot;
        }

        public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(request.DestinationPath);
            List<string> extractedFiles = [];

            foreach (string sourceFilePath in Directory.EnumerateFiles(_sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(_sourceRoot, sourceFilePath);
                string destinationFilePath = Path.Combine(request.DestinationPath, relativePath);
                string? destinationFileDirectory = Path.GetDirectoryName(destinationFilePath);
                if (destinationFileDirectory is not null) Directory.CreateDirectory(destinationFileDirectory);

                File.Copy(sourceFilePath, destinationFilePath, true);
                extractedFiles.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            }

            extractedFiles.Sort(StringComparer.Ordinal);
            string assetName = $"kyber-squad-{request.Version}.zip";
            SquadReleaseResult result = new(
                request.Version,
                new SquadReleaseAsset(assetName, new Uri($"https://localhost/{assetName}"), 1024),
                new SquadReleaseChecksum(assetName, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
                Path.GetFullPath(request.DestinationPath),
                extractedFiles);

            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
///     Removes an instance deployment and its recorded state without modifying main harness files.
/// </summary>
public sealed class SquadInstanceRemoveCommand : Command<SquadInstanceRemoveSettings>
{
    private readonly SquadInstanceService? _instanceService;
    private readonly bool? _isInteractive;
    private readonly Func<string, bool>? _readAnswer;
    private readonly ISquadUserPaths? _userPaths;

    /// <summary>Creates a new squad instance remove command with default dependencies.</summary>
    public SquadInstanceRemoveCommand()
    {
    }

    /// <summary>Creates a new squad instance remove command with injectable dependencies.</summary>
    public SquadInstanceRemoveCommand(
        ISquadUserPaths? userPaths = null,
        SquadInstanceService? instanceService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null)
    {
        _userPaths = userPaths;
        _instanceService = instanceService;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadInstanceRemoveSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            SquadCommandComposition.WriteClientInputError(
                "No target specified. Specify a target using --target <target>.");
            return 2;
        }

        SquadTarget target;
        try
        {
            IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse([settings.Target]);
            target = targets[0];
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Preset))
        {
            SquadCommandComposition.WriteClientInputError(
                "Preset name is required. Specify a preset using --preset <name>.");
            return 2;
        }

        ISquadUserPaths userPaths = _userPaths ?? SquadCommandComposition.ResolveUserPaths();
        string targetToken = SquadTargetCatalog.GetToken(target);
        string instanceRoot = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instances", targetToken,
            settings.Preset);
        string instanceStateDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "instance-state",
            targetToken, settings.Preset);

        if (!Directory.Exists(instanceRoot) && !Directory.Exists(instanceStateDir))
        {
            SquadCommandComposition.WriteClientInputError($"Instance '{targetToken}/{settings.Preset}' not found.");
            return 2;
        }

        bool isInteractive = _isInteractive ?? SquadCommandComposition.IsInteractiveConsole();
        if (isInteractive && !settings.Yes)
        {
            Func<string, bool> read = _readAnswer ?? (_ => true);
            if (!read($"Remove instance '{targetToken}/{settings.Preset}'?"))
            {
                SquadCommandComposition.WriteLine("[yellow]Declined by user.[/]");
                return 2;
            }
        }

        SquadInstanceService service = _instanceService ?? SquadCommandComposition.CreateInstanceService(userPaths);
        service.RemoveAsync(target, settings.Preset, cancellationToken).GetAwaiter().GetResult();

        SquadCommandComposition.WriteLine($"[green]Removed instance '{targetToken}/{settings.Preset}'.[/]");
        return 0;
    }
}
