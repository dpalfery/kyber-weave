using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Model;
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
    private readonly IProcessExecutor? _executor;
    private readonly string? _workingDirectory;

    /// <summary>Creates a new pack command using default dependencies.</summary>
    public SquadPackCommand()
    {
    }

    /// <summary>Creates a new pack command using injectable dependencies.</summary>
    public SquadPackCommand(IProcessExecutor? executor = null, string? workingDirectory = null)
    {
        _executor = executor;
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

        SquadSource source = SquadSourceLoader.Load(sourcePath);

        // Toolchain qualification gate (Gate G1)
        if (source.Toolchain.ValidatedRelease is null)
        {
            AnsiConsole.MarkupLine("[red]kyber-weave: error: Gate G1: toolchain qualification requirement is not met.[/]");
            AnsiConsole.MarkupLine($"The toolchain definition at [bold]{Markup.Escape(source.Toolchain.SourcePath)}[/] has validated-release set to null.");
            AnsiConsole.MarkupLine("Packaging requires a validated upstream APM toolchain release.");
            return 1;
        }

        return 0;
    }
}
