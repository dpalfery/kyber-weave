using System.Reflection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Diagnoses prerequisites, toolchain availability, and deployment health for Kyber-Squad.
/// </summary>
public sealed class SquadDoctorCommand : Command<SquadDoctorSettings>
{
    private const string ArbiterServer = McpServerNames.Arbiter;

    private readonly IProcessExecutor? _executor;
    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly ISquadRenderer? _renderer;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadUserPaths? _userPaths;
    private readonly string? _workingDirectory;
    private readonly Func<string, string?>? _environmentReader;

    /// <summary>Creates a new doctor command using default dependencies.</summary>
    public SquadDoctorCommand()
    {
    }

    /// <summary>Creates a new doctor command using injectable dependencies.</summary>
    internal SquadDoctorCommand(
        IProcessExecutor? executor = null,
        ISquadUserPaths? userPaths = null,
        string? workingDirectory = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadRenderer? renderer = null,
        SquadStateStore? stateStore = null,
        Func<string, string?>? environmentReader = null)
    {
        _executor = executor;
        _userPaths = userPaths;
        _stateStore = stateStore;
        _workingDirectory = workingDirectory;
        _globalRoots = globalRoots;
        _renderer = renderer;
        _environmentReader = environmentReader;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadDoctorSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Coalesce the positional path with --path; invalid client input returns exit
        // code 2 before any output or work — the same rule every other squad command
        // applies, because two names for one deployment root are an operator error, not
        // a preference to resolve silently.
        string? effectivePath;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        // The named root is the diagnosed root. The unresolved default — "." positional,
        // no --path — keeps the injected working directory, the process's current
        // directory in production, exactly as before the option existed.
        string workingDirectory = SquadCommandComposition.ResolveTargetRoot(
            effectivePath is null or "." ? _workingDirectory : effectivePath);

        // Doctor reads home-level state, so it resolves the store once. A --global doctor is anchored at the
        // home directory whatever the working directory is, and a path given with it is named and ignored.
        // The anchor resolves as a dry run, so doctor never creates the home directory. Doctor is not read-only:
        // the migration below runs with dryRun: false and may move legacy state into the home.
        SquadStateStore homeStateStore =
            _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths, _environmentReader);
        if (settings.Global) _ = SquadCommandComposition.ResolveGlobalAnchorRoot(homeStateStore, effectivePath, true);

        AnsiConsole.MarkupLine("[bold]Kyber-Squad Doctor[/]");
        AnsiConsole.WriteLine();

        bool hasIssues = false;

        // 1. CLI Version
        string cliVersion = GetCliVersion();
        AnsiConsole.MarkupLine($"  [green]ok[/] CLI Version: [bold]{Markup.Escape(cliVersion)}[/]");

        // 2. Renderer coverage — which of the declared targets can actually install today.
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        string[] supported = renderer.SupportedTargets
            .Select(SquadTargetCatalog.GetToken)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
        string[] pending = SquadTargetCatalog.All
            .Select(SquadTargetCatalog.GetToken)
            .Except(supported, StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
        AnsiConsole.MarkupLine(
            $"  [green]ok[/] Renderers available: [bold]{Markup.Escape(string.Join(", ", supported))}[/]");
        if (pending.Length > 0)
            AnsiConsole.MarkupLine(
                $"  [grey]info[/] Not yet implemented: [bold]{Markup.Escape(string.Join(", ", pending))}[/] (see docs/todo/<target>.md)");

        // 3. MCP Probe
        McpProcessProbe mcpProbe = SquadCommandComposition.ResolveProbe(_executor);
        ToolProbeResult mcpResult = mcpProbe.Probe();
        if (mcpResult is { IsAvailable: true, Version: not null })
        {
            AnsiConsole.MarkupLine(
                $"  [green]ok[/] Kyber-Weave MCP: [bold]kyber-weave-mcp {Markup.Escape(mcpResult.Version)}[/]");
        }
        else
        {
            string reason = mcpResult.FailureReason ?? "The 'kyber-weave-mcp' executable is not available on PATH.";
            AnsiConsole.MarkupLine($"  [red]fail[/] Kyber-Weave MCP: {Markup.Escape(reason)}");
            hasIssues = true;
        }

        // 3b. Arbiter Probe — only when the host enabled the Arbiter. A missing binary or
        // a version that differs from the CLI's is KW-ARB-BIN-001.
        if (ReportArbiterBinary(workingDirectory))
        {
            hasIssues = true;
        }

        // 3c. Retired MCP server keys. Read only, and it deliberately does not move the exit code: a
        // leftover is a rename this install has not been asked to make, not a broken prerequisite.
        ReportRetiredMcpKeys(workingDirectory);

        // 4. Canonical Source (Maintainer check - only when inside repository root)
        string? canonicalSourcePath = SquadPackSourceLocator.Resolve(workingDirectory);
        bool canonicalSourceValid = false;
        if (canonicalSourcePath is not null)
            try
            {
                SquadSource source = SquadSourceLoader.Load(canonicalSourcePath);
                AnsiConsole.MarkupLine(
                    $"  [green]ok[/] Canonical source: valid ([grey]{Markup.Escape(source.Manifest.Name)}[/], {source.Agents.Count} agents, {source.Skills.Count} skills)");
                canonicalSourceValid = true;

                if (ReportZCodeMcpConfiguration(source, workingDirectory, settings.Global))
                {
                    hasIssues = true;
                }
            }
            catch (SquadSourceValidationException ex)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] Canonical source validation failed: {Markup.Escape(ex.Message)}");
                hasIssues = true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]fail[/] Canonical source loading failed: {Markup.Escape(ex.Message)}");
                hasIssues = true;
            }

        // 5. Devin duplicate imports. Runs without canonical source, because the duplicates it
        // looks for are what an installed workspace holds, not what the source would render.
        ReportDevinImportOverlap(workingDirectory);

        // 5b. Retired skills. A stale second-brain directory or a receipt that still
        // claims it is a warning, not a failure: the stale copy works.
        ReportRetiredSkills(workingDirectory, homeStateStore);

        // 6. Home state: the migration the other verbs run, then the registry and any retained in-repo state.
        SquadMigrationReport migration = SquadCommandComposition.EnsureStateMigrated(
            homeStateStore,
            Environment.CurrentDirectory,
            settings.Global ? null : workingDirectory,
            false);
        SquadCommandComposition.WriteMigrationNotes(migration, true);
        try
        {
            ReportHomeState(homeStateStore);
        }
        catch (InvalidDataException ex)
        {
            SquadCommandComposition.WriteLine(
                $"  [red]fail[/] Squad project registry is unreadable: {Markup.Escape(ex.Message)}");
            hasIssues = true;
        }

        if (ReportOwnedBlockDrift(workingDirectory, settings.Global))
        {
            hasIssues = true;
        }

        if (settings.Global &&
            ReportGlobalCollisions(workingDirectory, canonicalSourcePath, canonicalSourceValid))
            hasIssues = true;

        AnsiConsole.WriteLine();
        if (hasIssues)
        {
            AnsiConsole.MarkupLine("[red]Doctor found issues with prerequisites or environment.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]All checked prerequisites and components are healthy.[/]");
        return 0;
    }

    public int Execute(CommandContext context, SquadDoctorSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>
    /// Reports every harness in this project still wired under a retired MCP server key, and the
    /// command that renames it.
    /// </summary>
    /// <remarks>
    /// Doctor is where an operator looks when something is off, so a leftover has to be named here.
    /// It is a warning about configuration rather than a failed prerequisite, and the exit code does
    /// not move on it: a healthy install that has not yet been asked to rename its keys is still
    /// healthy. Nothing is written; the rename belongs to <c>docs mcp-migrate</c>.
    /// </remarks>
    private static void ReportRetiredMcpKeys(string workingDirectory)
    {
        IReadOnlyList<McpRenameChange> leftovers =
        [
            .. McpServerRenamer.Scan(workingDirectory)
                .Where(change => string.Equals(change.Code, McpServerRenamer.RenameCode, StringComparison.Ordinal))
        ];

        if (leftovers.Count == 0)
            return;

        CommandHelpers.WriteUnfolded(() =>
        {
            foreach (McpRenameChange leftover in leftovers)
            {
                string file = Markup.Escape(TerminalText.Line(leftover.Path ?? leftover.Harness));

                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] {Markup.Escape(McpServerRenamer.RenameCode)} {file}: "
                    + Markup.Escape(TerminalText.Line(leftover.Message)));

                AnsiConsole.MarkupLine(
                    $"         Run: [grey]{Markup.Escape(McpServerRenamer.MigrateCommand + " --target " + leftover.Harness)}[/]");
            }
        });
    }

    /// <summary>
    /// Probes the <c>kyber-weave-arbiter</c> binary when the host enabled the Arbiter.
    /// </summary>
    /// <remarks>
    /// <c>KW-ARB-BIN-001</c> is a warning that the binary is missing from <c>PATH</c> or its
    /// version differs from the CLI's. A disabled Arbiter needs no binary, so it is reported
    /// as skipped rather than failed. An unreadable configuration also skips: doctor already
    /// reports genuine canonical-source defects elsewhere, and a missing config means the
    /// product-default disabled Arbiter.
    /// </remarks>
    /// <returns><see langword="true"/> when doctor should exit non-zero.</returns>
    private bool ReportArbiterBinary(string workingDirectory)
    {
        if (!ArbiterEnabled(workingDirectory))
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Kyber-Weave Arbiter: not checked (Arbiter is not enabled)");
            return false;
        }

        ArbiterProcessProbe arbiterProbe = SquadCommandComposition.ResolveArbiterProbe(_executor);
        ToolProbeResult arbiterResult = arbiterProbe.Probe();
        string cliVersion = NormalizeCliVersion(GetCliVersion());

        if (arbiterResult is { IsAvailable: true, Version: not null }
            && string.Equals(arbiterResult.Version, cliVersion, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"  [green]ok[/] Kyber-Weave Arbiter: [bold]kyber-weave-arbiter {Markup.Escape(arbiterResult.Version)}[/]");
            return false;
        }

        string reason = arbiterResult.FailureReason
            ?? (arbiterResult.Version is null
                ? "The 'kyber-weave-arbiter' executable is not available on PATH."
                : $"The 'kyber-weave-arbiter' version '{arbiterResult.Version}' differs from the CLI version '{cliVersion}'.");
        AnsiConsole.MarkupLine($"  [red]fail[/] Kyber-Weave Arbiter [bold]KW-ARB-BIN-001[/]: {Markup.Escape(reason)}");
        return true;
    }

    /// <summary>Whether the repository configuration enables the Arbiter. An unreadable
    /// configuration reads as disabled, the product default.</summary>
    private static bool ArbiterEnabled(string workingDirectory)
    {
        KyberWeaveConfigLoadResult configResult = KyberWeaveConfigLoader.TryLoad(workingDirectory);
        return configResult.Success && configResult.Config?.Arbiter.Enabled == true;
    }

    /// <summary>
    /// Normalizes the CLI version for comparison against the probe's semver: the assembly
    /// informational version may carry <c>+build</c> metadata the probe never emits.
    /// </summary>
    private static string NormalizeCliVersion(string version)
    {
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string normalized = plus > 0 ? version[..plus] : version;
        return normalized.Trim().TrimStart('v');
    }

    /// <summary>
    /// Reports whether the ZCode installation declares the MCP servers the canonical toolchain
    /// requires.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a hard failure rather than a warning because ZCode makes it one.
    ///         <c>ZCodeRenderer</c> grants MCP by fully qualified tool name — the only form ZCode's
    ///         exact-match allow-list registers — and <c>validateSubagentMcpRequirements</c> then
    ///         treats each name as a requirement, so an agent whose server is not connected raises a
    ///         configuration error mid-run. Failing here moves that breakage to diagnosis time.
    ///     </para>
    ///     <para>
    ///         It fails only where ZCode is actually in play, keyed on the same <c>.zcode/</c> marker
    ///         <see cref="SquadTargetResolver" /> detects targets with. A repository that has never
    ///         deployed to ZCode is reported as skipped rather than failed, because requiring three MCP
    ///         servers of someone who does not use the harness would make doctor useless to them.
    ///     </para>
    /// </remarks>
    /// <returns><see langword="true"/> when doctor should exit non-zero.</returns>
    private bool ReportZCodeMcpConfiguration(SquadSource source, string workingDirectory, bool isGlobal)
    {
        // The render grants the Arbiter server only to an enabled project-scope wiring
        // (ZCodeRenderer.ArbiterEnforced), so doctor requires it only for a project deployment
        // with the Arbiter enabled. The scope is the one the deployment is inspected at: the same
        // scope ReportOwnedBlockDrift reads the receipt for, so it is never unknowable here.
        bool arbiterGranted = SquadCommandComposition.ResolveScope(isGlobal) == SquadDeploymentScope.Project
            && ArbiterEnabled(workingDirectory);
        IReadOnlyCollection<string> required = source.Toolchain.RequiredMcpTools.Keys
            .Where(server => arbiterGranted || !string.Equals(server, ArbiterServer, StringComparison.Ordinal))
            .ToArray();
        if (required.Count == 0)
        {
            return false;
        }

        string marker = Path.Combine(workingDirectory, ".zcode");
        if (!Directory.Exists(marker))
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] ZCode MCP servers: not checked (no '.zcode/' in this directory)");
            return false;
        }

        string zcodeRoot;
        try
        {
            zcodeRoot = (_globalRoots ?? SquadCommandComposition.ResolveGlobalRoots(_environmentReader))
                .ResolveGlobalRoot(SquadTarget.ZCode);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine(
                $"  [red]fail[/] ZCode MCP servers: cannot resolve the ZCode storage root: {Markup.Escape(ex.Message)}");
            return true;
        }

        ZCodeMcpConfigurationReport report = ZCodeMcpConfiguration.Inspect(
            required,
            zcodeRoot,
            workingDirectory,
            SquadCommandComposition.ReadFileTextOrNull);

        if (report.MissingServers.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"  [green]ok[/] ZCode MCP servers: [bold]{Markup.Escape(string.Join(", ", required.Order(StringComparer.Ordinal)))}[/] configured");
            return false;
        }

        AnsiConsole.MarkupLine(
            $"  [red]fail[/] ZCode MCP servers not configured: [bold]{Markup.Escape(string.Join(", ", report.MissingServers))}[/]");
        AnsiConsole.MarkupLine(
            "         Squad agents on ZCode are granted these servers' tools by exact name, so " +
            "ZCode fails an agent whose server is not connected.");
        AnsiConsole.MarkupLine(
            $"         Declare them under 'mcp.servers' in {Markup.Escape(Path.Combine(zcodeRoot, "cli", "config.json"))}, " +
            "or in a project 'zcode.json' / '.zcode/config.json'.");
        if (report.InspectedPaths.Count == 0)
            AnsiConsole.MarkupLine("         No ZCode configuration file was found to read.");

        return true;
    }

    /// <summary>
    ///     Warns when Devin would load a Squad identity twice: once from <c>.devin/</c> and again
    ///     from a tree it reads natively or imports from another tool.
    /// </summary>
    /// <remarks>
    ///     A warning rather than a failure: both copies are Squad's own and each works, so the
    ///     cost is an ambiguous choice between two same-named profiles, not a broken install.
    ///     Each warning names the remedy, and the remedy differs by tree — an imported tree has a
    ///     <c>read_config_from</c> switch, a native one does not — so the hint is per entry. Keyed
    ///     on the same <c>.devin/</c> marker target resolution uses, so a workspace without Devin
    ///     is reported as skipped.
    /// </remarks>
    private void ReportDevinImportOverlap(string workingDirectory)
    {
        if (!Directory.Exists(Path.Combine(workingDirectory, ".devin")))
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Devin duplicate imports: not checked (no '.devin/' in this directory)");
            return;
        }

        // An unresolvable user directory only hides the user-level import settings; the
        // project-level check still means something without it.
        string? devinUserRoot;
        try
        {
            devinUserRoot = (_globalRoots ?? SquadCommandComposition.ResolveGlobalRoots(_environmentReader))
                .ResolveGlobalRoot(SquadTarget.Devin);
        }
        catch (ArgumentException)
        {
            devinUserRoot = null;
        }

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(
            workingDirectory,
            devinUserRoot,
            SquadCommandComposition.ReadFileTextOrNull);

        if (report.Overlaps.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Devin duplicate imports: none");
            return;
        }

        foreach (DevinImportOverlapEntry overlap in report.Overlaps)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] Devin also loads {overlap.Identities.Count} {overlap.Kind}(s) from " +
                $"'{Markup.Escape(overlap.SourceRoot)}/' that '.devin/' already defines: " +
                $"[bold]{Markup.Escape(string.Join(", ", overlap.Identities))}[/]");
            AnsiConsole.MarkupLine(overlap.ImportSetting is null
                ? $"         Devin reads '{Markup.Escape(overlap.SourceRoot)}/' natively and cannot be told " +
                  "not to; deploy only one of the targets that write there."
                : $"         Set \"read_config_from\": {{ \"{Markup.Escape(overlap.ImportSetting)}\": false }} " +
                  "in .devin/config.json to stop the import (it also drops everything else that " +
                  "import brings), or deploy only one of the two targets.");
        }
    }

    /// <summary>
    /// Warns about the retired <c>second-brain</c> skill, whether it survives as an
    /// unmanaged directory in a deployed target tree or as a retained entry in the
    /// project deployment receipt.
    /// </summary>
    /// <remarks>
    /// A warning rather than a failure: the stale copy works. Each warning names the
    /// remedy: delete the stale directory, then run <c>squad update</c> to reconcile
    /// the receipt. The workflow moved to the Kyber-Docs skills deployed by
    /// <c>docs init</c>, so the warning points there.
    /// </remarks>
    private void ReportRetiredSkills(string workingDirectory, SquadStateStore stateStore)
    {
        List<string> staleDirs = [];
        string[] skillsRoots = [Path.Combine(".claude", "skills"), Path.Combine(".agents", "skills")];
        foreach (string skillsRoot in skillsRoots)
        {
            foreach (string retired in SquadRetiredSkills.RetiredSkillNames)
            {
                string staleDir = Path.Combine(workingDirectory, skillsRoot, retired);
                if (Directory.Exists(staleDir))
                {
                    staleDirs.Add(staleDir);
                }
            }
        }

        List<string> retained = [];
        string? receiptError = null;
        try
        {
            SquadReceipt? receipt = stateStore.ReadReceipt(workingDirectory, SquadDeploymentScope.Project);
            if (receipt is not null)
            {
                retained.AddRange(SquadRetiredSkills.FindRetainedReceiptPaths(receipt));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // An unreadable receipt is state this check could not read. Warn with the
            // reason rather than reporting healthy over unknown state; the registry
            // check below still applies, so this stays a warning, not a failure.
            receiptError = ex.Message;
        }

        if (receiptError is not null)
        {
            string message = receiptError;
            CommandHelpers.WriteUnfolded(() =>
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] Deployment receipt is unreadable: {Markup.Escape(message)}");
                AnsiConsole.MarkupLine(
                    "         Delete the receipt and lock, then run [grey]squad update[/] to reconcile them.");
            });
        }

        if (staleDirs.Count == 0 && retained.Count == 0)
        {
            return;
        }

        CommandHelpers.WriteUnfolded(() =>
        {
            foreach (string staleDir in staleDirs)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] Retired skill '{Markup.Escape(SquadRetiredSkills.SecondBrain)}' still present at [bold]{Markup.Escape(staleDir)}[/].");
            }

            if (retained.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] Deployment receipt still claims retired '{Markup.Escape(SquadRetiredSkills.SecondBrain)}' files: [bold]{Markup.Escape(string.Join(", ", retained))}[/].");
            }

            AnsiConsole.MarkupLine(
                "         Delete the stale directory, then run [grey]squad update[/] to reconcile the receipt.");
            AnsiConsole.MarkupLine(
                $"         The workflow moved to the Kyber-Docs skills ([grey]{Markup.Escape(SquadRetiredSkills.NewHomeSkillPrefix)}*[/], " +
                $"see [grey]{Markup.Escape(SquadRetiredSkills.NewHomeSkill)}[/]); run [grey]docs init[/] to deploy them.");
        });
    }

    /// <summary>
    ///     Reports the home-level registry: each stale entry, and each project whose in-repo Squad state was
    ///     imported but is still present in its repository.
    /// </summary>
    /// <remarks>
    ///     Warnings rather than failures. A stale entry is harmless to the rest of the home, and retained in-repo
    ///     state is kept on purpose by the migration; both need the operator's decision, not a failing exit code.
    ///     The retained-state warning is only raised for a project still registered as live, because a stale
    ///     project's home state is already gone and there is nothing to be authoritative over.
    /// </remarks>
    private static void ReportHomeState(SquadStateStore stateStore)
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects = stateStore.ListProjects();
        if (projects.Count == 0)
        {
            SquadCommandComposition.WriteLine("  [grey]info[/] Registered Squad projects: none");
            return;
        }

        foreach (SquadProjectRegistryEntry project in projects)
        {
            if (project.Stale)
            {
                SquadCommandComposition.WriteLine(
                    $"  [yellow]warn[/] registered project '{Markup.Escape(project.Path)}' is stale " +
                    $"({Markup.Escape(project.StaleReason ?? "unknown")}); " +
                    "global updates skip it until its path is restored or its receipt is reinstalled.");
                continue;
            }

            string inRepoState = Path.Combine(project.Path, ".kyber-weave");
            if (File.Exists(Path.Combine(inRepoState, "squad.receipt.json")) ||
                File.Exists(Path.Combine(inRepoState, "squad.lock.yml")))
                SquadCommandComposition.WriteLine(
                    $"  [yellow]warn[/] in-repo Squad state at '{Markup.Escape(inRepoState)}' is retained after its import. " +
                    "The home record is now authoritative, so remove that directory from version control.");
        }
    }

    /// <returns>
    ///     <see langword="true" /> when the scan itself failed and doctor should exit non-zero.
    ///     Warnings and skipped scans return <see langword="false" />.
    /// </returns>
    private bool ReportGlobalCollisions(
        string workingDirectory,
        string? canonicalSourcePath,
        bool canonicalSourceValid)
    {
        if (canonicalSourcePath is null)
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Global unmanaged-collision scan skipped: canonical source is not available from this working directory.");
            return false;
        }

        if (!canonicalSourceValid)
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Global unmanaged-collision scan skipped: canonical source validation failed.");
            return false;
        }

        ISquadRenderer renderer = _renderer ?? SquadCommandComposition.ResolveRenderer();
        ISquadGlobalRootResolver globalRoots =
            _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots(_environmentReader);
        SquadStateStore stateStore =
            _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths, _environmentReader);

        List<SquadUnmanagedPathCollision> collisions = [];
        bool invalidGlobalRoot = false;
        try
        {
            SquadReceipt? receipt = stateStore.ReadReceipt(workingDirectory, SquadDeploymentScope.Global);
            // Global state is one home-anchored receipt, so there is no sibling receipt to collide with.
            IReadOnlyList<SquadReceipt> siblingReceipts = [];

            SquadLock? squadLock = null;
            try
            {
                squadLock = stateStore.ReadLock(workingDirectory, SquadDeploymentScope.Global);
            }
            catch (Exception)
            {
                // Lock read failure falls back to comparison render without presets.
            }

            Dictionary<SquadTarget, SquadModelPreset>? loadedPresets = null;
            if (squadLock?.ModelPresets is { Count: > 0 })
            {
                SquadModelPresetStore presetStore = new(stateStore.UserPaths);
                foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> entry in squadLock.ModelPresets)
                {
                    try
                    {
                        SquadModelPreset? preset = presetStore.TryLoad(entry.Value.Name);
                        if (preset is not null)
                        {
                            loadedPresets ??= new Dictionary<SquadTarget, SquadModelPreset>();
                            loadedPresets[entry.Key] = preset;
                        }
                    }
                    catch (Exception)
                    {
                        // Omit invalid or unreadable preset from comparison render.
                    }
                }
            }

            SquadSource sharedSource = SquadSourceLoader.Load(canonicalSourcePath);

            foreach (SquadTarget target in renderer.SupportedTargets)
            {
                // A future native renderer whose per-user directory is not yet verified
                // still registers in SupportedTargets. Probing the resolver first keeps
                // doctor --global from crashing the whole scan. Relative override values
                // throw ArgumentException from SquadGlobalRoots; catch that per target so
                // the remaining harnesses still scan.
                string targetGlobalRoot;
                try
                {
                    targetGlobalRoot = globalRoots.ResolveGlobalRoot(target);
                }
                catch (ArgumentOutOfRangeException)
                {
                    AnsiConsole.MarkupLine(
                        $"  [grey]info[/] Global unmanaged-collision scan skipped for '{Markup.Escape(SquadTargetCatalog.GetToken(target))}': no verified global root.");
                    continue;
                }
                catch (ArgumentException ex)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]fail[/] Invalid global root for '{Markup.Escape(SquadTargetCatalog.GetToken(target))}': {Markup.Escape(ex.Message)}");
                    invalidGlobalRoot = true;
                    continue;
                }

                SquadRenderResult render = renderer.RenderAsync(
                        new SquadRenderRequest(
                            canonicalSourcePath,
                            [target],
                            SquadDeploymentScope.Global,
                            GlobalRoots: globalRoots,
                            ModelPresets: loadedPresets,
                            PreloadedSource: sharedSource,
                            EnvironmentReader: _environmentReader))
                    .GetAwaiter()
                    .GetResult();

                if (!render.Success || render.Errors.Count > 0)
                {
                    // Mirror install and update: a failed render is a collision-scan failure, not an empty corpus,
                    // so it must not reach the no-block check below.
                    string error = render.Errors.Count > 0
                        ? string.Join(Environment.NewLine, render.Errors)
                        : "Squad render failed.";
                    throw new SquadRenderValidationException(error);
                }

                List<string> warnings = [];
                string effectiveGuidance = SquadResponseGuidanceSource.ReadEffective(
                    Path.Combine(stateStore.UserPaths.HomeDirectory, ".kyber-weave"),
                    warnings);
                if (!SquadResponsePreferencesInjector.HasAnyBlock(render))
                    warnings.Add(SquadResponsePreferencesInjector.NoBlockWarning);
                SquadCommandComposition.WriteTransactionWarnings(warnings);
                render = SquadResponsePreferencesInjector.Inject(render, effectiveGuidance);

                collisions.AddRange(SquadDeploymentPlan.CollectUnmanagedCollisions(
                    workingDirectory,
                    SquadDeploymentScope.Global,
                    render.Files,
                    globalRoots,
                    receipt,
                    siblingReceipts));
            }
        }
        catch (Exception ex) when (
            ex is SquadRenderValidationException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException or ArgumentException)
        {
            AnsiConsole.MarkupLine(
                $"  [red]fail[/] Global unmanaged-collision scan failed: {Markup.Escape(ex.Message)}");
            return true;
        }

        if (collisions.Count == 0)
            AnsiConsole.MarkupLine("  [green]ok[/] Global unmanaged collisions: none");
        else
            foreach (SquadUnmanagedPathCollision collision in collisions)
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] Unmanaged global file '{Markup.Escape(collision.RelativePath)}' collides with canonical identity '{Markup.Escape(collision.Identity)}'.");

        return invalidGlobalRoot;
    }

    /// <summary>
    /// Reports receipt-owned hook blocks whose entries no longer match the file on disk:
    /// a hand-edited or missing owned entry is drift (Req 8.4), naming the file and the
    /// container.
    /// </summary>
    /// <remarks>
    /// Blocks own entries, never files, so a missing or unparsable file drifts every entry
    /// it should carry rather than reading as a whole-file miss. A deployment without a
    /// receipt, or a receipt without blocks, is reported as skipped rather than failed:
    /// there is nothing owned to drift. The format is resolved from the recorded relative
    /// path the same way uninstall resolves it, so diagnosis never depends on a render
    /// that may no longer exist.
    /// </remarks>
    /// <returns><see langword="true"/> when doctor should exit non-zero.</returns>
    private bool ReportOwnedBlockDrift(string workingDirectory, bool isGlobal)
    {
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(isGlobal);
        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        ISquadGlobalRootResolver globalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();

        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(workingDirectory, scope);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Text.Json.JsonException
            or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"  [yellow]warn[/] Owned blocks: cannot read the ownership receipt: {Markup.Escape(ex.Message)}");
            return false;
        }

        if (receipt is null)
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Owned blocks: not checked (no Kyber-Squad deployment found)");
            return false;
        }

        if (receipt.Blocks.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Owned blocks: none");
            return false;
        }

        bool hasDrift = false;
        foreach (SquadOwnedBlock block in receipt.Blocks)
        {
            string suffix = scope == SquadDeploymentScope.Global ? $" ({block.Target})" : string.Empty;
            string fileLabel = $"{Markup.Escape(block.RelativePath)}{suffix}";

            string fullPath;
            try
            {
                fullPath = SquadDeploymentPlan.ResolveOwnedFilePath(
                    receipt,
                    workingDirectory,
                    globalRoots,
                    new SquadOwnedFile(block.RelativePath, new string('0', 64), block.Target, false));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] Owned block drift: {fileLabel} ({Markup.Escape(ex.Message)})");
                hasDrift = true;
                continue;
            }

            if (!TryResolveBlockFormat(block, out SquadHookBlockFormat format))
            {
                AnsiConsole.MarkupLine($"  [red]fail[/] Owned block drift: {fileLabel} (unknown shared hook file)");
                hasDrift = true;
                continue;
            }

            if (!File.Exists(fullPath))
            {
                foreach (SquadOwnedBlockEntry entry in block.Entries)
                {
                    AnsiConsole.MarkupLine($"  [red]fail[/] Owned block drift: {fileLabel} {Markup.Escape(entry.Container)} (missing)");
                }

                if (block.Entries.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [red]fail[/] Owned block drift: {fileLabel} (missing)");
                }

                hasDrift = true;
                continue;
            }

            string currentJson;
            try
            {
                currentJson = File.ReadAllText(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]fail[/] Owned block drift: {fileLabel} (unreadable: {Markup.Escape(ex.Message)})");
                hasDrift = true;
                continue;
            }

            Dictionary<string, string> expected = new(StringComparer.Ordinal);
            foreach (SquadOwnedBlockEntry entry in block.Entries)
            {
                expected[entry.Container] = entry.Sha256;
            }

            IReadOnlyList<SquadHookDrift> drifts;
            try
            {
                drifts = SquadHookJsonBlock.FindDrift(format, currentJson, expected);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                foreach (SquadOwnedBlockEntry entry in block.Entries)
                {
                    AnsiConsole.MarkupLine($"  [red]fail[/] Owned block drift: {fileLabel} {Markup.Escape(entry.Container)} (unparsable)");
                }

                hasDrift = true;
                continue;
            }

            foreach (SquadHookDrift drift in drifts)
            {
                AnsiConsole.MarkupLine($"  [red]fail[/] Owned block drift: {fileLabel} {Markup.Escape(drift.Location)} ({Markup.Escape(drift.Reason)})");
                hasDrift = true;
            }
        }

        if (!hasDrift)
        {
            int entryCount = receipt.Blocks.Sum(block => block.Entries.Count);
            string entryWord = entryCount == 1 ? "entry" : "entries";
            AnsiConsole.MarkupLine($"  [green]ok[/] Owned blocks: {receipt.Blocks.Count} blocks, {entryCount} {entryWord} healthy");
        }

        return hasDrift;
    }

    /// <summary>
    /// Resolves the hook-file shape for a receipt-owned block from its recorded relative
    /// path, so diagnosis never depends on a render that may no longer exist.
    /// </summary>
    private static bool TryResolveBlockFormat(SquadOwnedBlock block, out SquadHookBlockFormat format)
    {
        foreach (SquadHookBlockFormat candidate in Enum.GetValues<SquadHookBlockFormat>())
        {
            if (string.Equals(
                    SquadHookJsonBlock.RelativePath(candidate),
                    block.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    private static string GetCliVersion()
    {
        Assembly assembly = typeof(SquadDoctorCommand).Assembly;
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion)) return infoVersion;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
