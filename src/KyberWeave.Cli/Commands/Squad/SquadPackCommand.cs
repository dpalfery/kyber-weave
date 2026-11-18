using System.Reflection;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Packaging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Packages canonical Kyber-Squad source into APM and Agent Plugins distribution archives.
/// Maintainer-only command that must be run from the repository root.
/// </summary>
/// <param name="packs">Validates the source and writes the archives.</param>
/// <param name="console">Where the command writes its report.</param>
public sealed class SquadPackCommand(ISquadPackService packs, IAnsiConsole console) : Command<SquadPackSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadPackSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadPackOutcome outcome = packs.Pack(new SquadPackRequest(
            settings.Format,
            settings.Version,
            settings.Out,
            ResolveVersion()));

        foreach (string warning in outcome.Warnings)
        {
            console.MarkupLine($"[yellow]kyber-weave squad: warning: {Markup.Escape(warning)}[/]");
        }

        switch (outcome.Status)
        {
            case SquadPackStatus.InvalidFormat:
                console.MarkupLine($"[red]Invalid pack format '{Markup.Escape(settings.Format)}'. Valid formats: apm, plugins, all.[/]");
                return 2;
            case SquadPackStatus.NotRepositoryRoot:
                console.MarkupLine("[red]kyber-weave squad pack is maintainer-only and must be run from the repository root.[/]");
                console.MarkupLine("Expected markers [bold]KyberWeave.sln[/] and [bold]products/kyber-squad/squad.yml[/] were not found in the current working directory.");
                console.MarkupLine("To deploy agents and skills to your project or user environment, run [bold]kyber-weave squad install[/] instead.");
                return 1;
            case SquadPackStatus.Failed:
                console.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(outcome.Error ?? string.Empty)}[/]");
                return 1;
        }

        if (outcome.ApmArchive is not null)
        {
            console.MarkupLine($"[green]Successfully packed APM archive to [bold]{Markup.Escape(outcome.ApmArchive)}[/].[/]");
        }

        if (outcome.PluginsArchive is not null)
        {
            console.MarkupLine($"[green]Successfully packed Agent Plugins archive to [bold]{Markup.Escape(outcome.PluginsArchive)}[/].[/]");
        }

        if (outcome.ChecksumFile is not null)
        {
            console.MarkupLine($"[green]Generated checksums at [bold]{Markup.Escape(outcome.ChecksumFile)}[/].[/]");
        }

        return 0;
    }

    public int Execute(CommandContext context, SquadPackSettings settings) => Execute(context, settings, CancellationToken.None);

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(SquadPackCommand).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        Version? v = assembly.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";
    }
}
