using System.Reflection;
using System.Threading;
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
        string workingDirectory = _workingDirectory ?? Directory.GetCurrentDirectory();
        string? canonicalSourcePath = SquadPackSourceLocator.Resolve(workingDirectory);
        if (canonicalSourcePath is not null)
        {
            try
            {
                SquadSource source = SquadSourceLoader.Load(canonicalSourcePath);
                AnsiConsole.MarkupLine($"  [green]ok[/] Canonical source: valid ([grey]{Markup.Escape(source.Manifest.Name)}[/], {source.Agents.Count} agents, {source.Skills.Count} skills)");
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

        if (settings.Global)
        {
            ReportGlobalCollisions(workingDirectory, canonicalSourcePath);
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

    private void ReportGlobalCollisions(string workingDirectory, string? canonicalSourcePath)
    {
        if (canonicalSourcePath is null)
        {
            AnsiConsole.MarkupLine(
                "  [grey]info[/] Global unmanaged-collision scan skipped: canonical source is not available from this working directory.");
            return;
        }

        ISquadRenderer renderer = _renderer ?? SquadCommandComposition.ResolveRenderer();
        ISquadGlobalRootResolver globalRoots = _globalRoots
            ?? new SquadGlobalRoots(
                Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        List<SquadUnmanagedPathCollision> collisions = [];
        foreach (SquadTarget target in renderer.SupportedTargets)
        {
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

        if (collisions.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]ok[/] Global unmanaged collisions: none");
            return;
        }

        foreach (SquadUnmanagedPathCollision collision in collisions)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]warn[/] Unmanaged global file '{Markup.Escape(collision.RelativePath)}' collides with canonical identity '{Markup.Escape(collision.Identity)}'.");
        }
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
