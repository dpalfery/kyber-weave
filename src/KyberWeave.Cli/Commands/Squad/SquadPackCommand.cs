using System.Reflection;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Packages canonical Kyber-Squad source into APM and Agent Plugins distribution archives.
/// Maintainer-only command that must be run from the repository root.
/// </summary>
public sealed class SquadPackCommand : Command<SquadPackSettings>
{
    private readonly string? _workingDirectory;

    /// <summary>Creates a new pack command using default dependencies.</summary>
    public SquadPackCommand()
    {
    }

    /// <summary>Creates a new pack command using injectable process executor and working directory.</summary>
    internal SquadPackCommand(IProcessExecutor? executor = null, string? workingDirectory = null)
    {
        _ = executor;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.Equals(settings.Format, "apm", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.Format, "plugins", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.Format, "all", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]Invalid pack format '{Markup.Escape(settings.Format)}'. Valid formats: apm, plugins, all.[/]");
            return 2;
        }

        string workingDirectory = _workingDirectory ?? Directory.GetCurrentDirectory();
        string? sourcePath = SquadPackSourceLocator.Resolve(workingDirectory);
        if (sourcePath is null)
        {
            AnsiConsole.MarkupLine("[red]kyber-weave squad pack is maintainer-only and must be run from the repository root.[/]");
            AnsiConsole.MarkupLine("Expected markers [bold]KyberWeave.sln[/] and [bold]products/kyber-squad/squad.yml[/] were not found in the current working directory.");
            AnsiConsole.MarkupLine("To deploy agents and skills to your project or user environment, run [bold]kyber-weave squad install[/] instead.");
            return 1;
        }

        // Packing writes the canonical source tree deterministically (SquadPacker) with no
        // external toolchain in the loop, so there is nothing left to gate here — loading
        // still validates the source itself (malformed manifests, profiles, agents, or
        // skills throw SquadSourceValidationException before anything is packed).
        _ = SquadSourceLoader.Load(sourcePath);

        string version = !string.IsNullOrWhiteSpace(settings.Version)
            ? ReleaseVersion.Normalize(settings.Version)
            : ResolveVersion();
        string outDir = string.IsNullOrWhiteSpace(settings.Out)
            ? Path.Combine(workingDirectory, "artifacts")
            : Path.GetFullPath(settings.Out, workingDirectory);

        try
        {
            if (string.Equals(settings.Format, "apm", StringComparison.OrdinalIgnoreCase))
            {
                string apmArchive = SquadPacker.PackApm(sourcePath, outDir, version);
                AnsiConsole.MarkupLine($"[green]Successfully packed APM archive to [bold]{Markup.Escape(apmArchive)}[/].[/]");
            }
            else if (string.Equals(settings.Format, "plugins", StringComparison.OrdinalIgnoreCase))
            {
                string pluginsArchive = SquadPacker.PackPlugins(sourcePath, outDir, version);
                AnsiConsole.MarkupLine($"[green]Successfully packed Agent Plugins archive to [bold]{Markup.Escape(pluginsArchive)}[/].[/]");
            }
            else
            {
                (string apmArchive, string pluginsArchive, string checksumPath) = SquadPacker.PackAll(sourcePath, outDir, version);
                AnsiConsole.MarkupLine($"[green]Successfully packed APM archive to [bold]{Markup.Escape(apmArchive)}[/].[/]");
                AnsiConsole.MarkupLine($"[green]Successfully packed Agent Plugins archive to [bold]{Markup.Escape(pluginsArchive)}[/].[/]");
                AnsiConsole.MarkupLine($"[green]Generated checksums at [bold]{Markup.Escape(checksumPath)}[/].[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(SquadPackCommand).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+');
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        Version? v = assembly.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";
    }
}
