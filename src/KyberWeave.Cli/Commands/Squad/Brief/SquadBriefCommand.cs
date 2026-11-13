using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Briefs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad.Brief;

/// <summary>
/// Prints only the parts of an agent's installed reference files that apply to a run, so a run reads
/// the instructions for its own route, autonomy and mode instead of every reference whole.
/// </summary>
/// <remarks>
/// Read-only. Exit codes: <c>0</c> the brief was printed (an unmapped heading is included, never an
/// error), <c>2</c> refused input or reference files that cannot be found or read.
/// </remarks>
public sealed class SquadBriefCommand : Command<SquadBriefSettings>
{
    private const int Printed = 0;
    private const int Refused = 2;

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadBriefSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool json = string.Equals(settings.Format, "json", StringComparison.OrdinalIgnoreCase);
        if (!json && !string.Equals(settings.Format, "text", StringComparison.OrdinalIgnoreCase))
        {
            return Refuse($"'{settings.Format}' is not a format. Use text or json.");
        }

        string current = Directory.GetCurrentDirectory();
        string root = string.IsNullOrWhiteSpace(settings.Plan) ? ArbiterRepoRoot.Find(current) : PlanArguments.RepositoryRootFor(settings.Plan);
        DiagnosticReport report = new();
        KyberWeaveConfig? config = CommandHelpers.TryLoadConfig(root, null, report, out KyberWeaveConfig loaded) ? loaded : null;

        SquadBriefRequest request = new(
            settings.Agent,
            settings.Path,
            settings.Autonomy,
            settings.Mode,
            settings.Plan,
            settings.Harness,
            settings.From,
            root,
            [root],
            config);

        if (!SquadBriefBuilder.TryBuild(request, out BriefResult? result, out string error))
        {
            return Refuse(error);
        }

        Console.Out.Write(json ? BriefFormatter.ToJson(result!) + "\n" : BriefFormatter.ToText(result!));
        return Printed;
    }

    private static int Refuse(string message)
    {
        AnsiConsole.MarkupLine($"[red]kyber-weave squad brief: error:[/] {Markup.Escape(message)}");
        return Refused;
    }
}
