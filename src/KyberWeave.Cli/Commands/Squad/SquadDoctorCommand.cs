using System.Reflection;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
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
/// Diagnoses prerequisites, toolchain availability, and deployment health for Kyber-Squad.
/// </summary>
public sealed class SquadDoctorCommand : Command<SquadDoctorSettings>
{
    private readonly IProcessExecutor? _executor;
    private readonly string? _workingDirectory;
    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly ISquadRenderer? _renderer;

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
        ISquadRenderer? renderer = null)
    {
        _ = userPaths;
        _executor = executor;
        _workingDirectory = workingDirectory;
        _globalRoots = globalRoots;
        _renderer = renderer;
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, SquadDoctorSettings settings, CancellationToken cancellationToken)
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

        AnsiConsole.MarkupLine("[bold]Kyber-Squad Doctor[/]");
        AnsiConsole.WriteLine();

        bool hasIssues = false;

        // 1. CLI Version
        string cliVersion = GetCliVersion();
        AnsiConsole.MarkupLine($"  [green]ok[/] CLI Version: [bold]{Markup.Escape(cliVersion)}[/]");

        // 2. Renderer coverage — which of the ten approved targets can actually install today.
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
        AnsiConsole.MarkupLine($"  [green]ok[/] Renderers available: [bold]{Markup.Escape(string.Join(", ", supported))}[/]");
        if (pending.Length > 0)
        {
            AnsiConsole.MarkupLine($"  [grey]info[/] Not yet implemented: [bold]{Markup.Escape(string.Join(", ", pending))}[/] (see docs/todo/<target>.md)");
        }

        // 3. MCP Probe
        McpProcessProbe mcpProbe = SquadCommandComposition.ResolveProbe(_executor);
        ToolProbeResult mcpResult = mcpProbe.Probe();
        if (mcpResult is { IsAvailable: true, Version: not null })
        {
            AnsiConsole.MarkupLine($"  [green]ok[/] Kyber-Weave MCP: [bold]kyber-weave-mcp {Markup.Escape(mcpResult.Version)}[/]");
        }
        else
        {
            string reason = mcpResult.FailureReason ?? "The 'kyber-weave-mcp' executable is not available on PATH.";
            AnsiConsole.MarkupLine($"  [red]fail[/] Kyber-Weave MCP: {Markup.Escape(reason)}");
            hasIssues = true;
        }

        // 4. Canonical Source (Maintainer check - only when inside repository root)
        string? canonicalSourcePath = SquadPackSourceLocator.Resolve(workingDirectory);
        bool canonicalSourceValid = false;
        if (canonicalSourcePath is not null)
        {
            try
            {
                SquadSource source = SquadSourceLoader.Load(canonicalSourcePath);
                AnsiConsole.MarkupLine($"  [green]ok[/] Canonical source: valid ([grey]{Markup.Escape(source.Manifest.Name)}[/], {source.Agents.Count} agents, {source.Skills.Count} skills)");
                canonicalSourceValid = true;

                if (ReportZCodeMcpConfiguration(source, workingDirectory))
                {
                    hasIssues = true;
                }
            }
            catch (SquadSourceValidationException ex)
            {
                AnsiConsole.MarkupLine($"  [red]fail[/] Canonical source validation failed: {Markup.Escape(ex.Message)}");
                hasIssues = true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]fail[/] Canonical source loading failed: {Markup.Escape(ex.Message)}");
                hasIssues = true;
            }
        }

        if (settings.Global &&
            ReportGlobalCollisions(workingDirectory, canonicalSourcePath, canonicalSourceValid))
        {
            hasIssues = true;
        }

        AnsiConsole.WriteLine();
        if (hasIssues)
        {
            AnsiConsole.MarkupLine("[red]Doctor found issues with prerequisites or environment.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]All checked prerequisites and components are healthy.[/]");
        return 0;
    }

    public int Execute(CommandContext context, SquadDoctorSettings settings) => Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// Reports whether the ZCode installation declares the MCP servers the canonical toolchain
    /// requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a hard failure rather than a warning because ZCode makes it one.
    /// <c>ZCodeRenderer</c> grants MCP by fully qualified tool name — the only form ZCode's
    /// exact-match allow-list registers — and <c>validateSubagentMcpRequirements</c> then
    /// treats each name as a requirement, so an agent whose server is not connected raises a
    /// configuration error mid-run. Failing here moves that breakage to diagnosis time.
    /// </para>
    /// <para>
    /// It fails only where ZCode is actually in play, keyed on the same <c>.zcode/</c> marker
    /// <see cref="SquadTargetResolver"/> detects targets with. A repository that has never
    /// deployed to ZCode is reported as skipped rather than failed, because requiring three MCP
    /// servers of someone who does not use the harness would make doctor useless to them.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when doctor should exit non-zero.</returns>
    private bool ReportZCodeMcpConfiguration(SquadSource source, string workingDirectory)
    {
        IReadOnlyCollection<string> required = source.Toolchain.RequiredMcpTools.Keys.ToArray();
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
            zcodeRoot = (_globalRoots ?? SquadCommandComposition.ResolveGlobalRoots())
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
        {
            AnsiConsole.MarkupLine("         No ZCode configuration file was found to read.");
        }

        return true;
    }

    /// <returns>
    /// <see langword="true"/> when the scan itself failed and doctor should exit non-zero.
    /// Warnings and skipped scans return <see langword="false"/>.
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
        ISquadGlobalRootResolver globalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();

        List<SquadUnmanagedPathCollision> collisions = [];
        bool invalidGlobalRoot = false;
        try
        {
            foreach (SquadTarget target in renderer.SupportedTargets)
            {
                // A future native renderer whose per-user directory is not yet verified
                // still registers in SupportedTargets. Probing the resolver first keeps
                // doctor --global from crashing the whole scan. Relative override values
                // throw ArgumentException from SquadGlobalRoots; catch that per target so
                // the remaining harnesses still scan.
                try
                {
                    _ = globalRoots.ResolveGlobalRoot(target);
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
                            SquadDeploymentScope.Global))
                    .GetAwaiter()
                    .GetResult();

                collisions.AddRange(SquadDeploymentPlan.CollectUnmanagedCollisions(
                    workingDirectory,
                    SquadDeploymentScope.Global,
                    render.Files,
                    globalRoots));
            }
        }
        catch (SquadRenderValidationException ex)
        {
            AnsiConsole.MarkupLine(
                $"  [red]fail[/] Global unmanaged-collision scan failed: {Markup.Escape(ex.Message)}");
            return true;
        }

        if (collisions.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Global unmanaged collisions: none");
        }
        else
        {
            foreach (SquadUnmanagedPathCollision collision in collisions)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] Unmanaged global file '{Markup.Escape(collision.RelativePath)}' collides with canonical identity '{Markup.Escape(collision.Identity)}'.");
            }
        }

        return invalidGlobalRoot;
    }

    private static string GetCliVersion()
    {
        Assembly assembly = typeof(SquadDoctorCommand).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
