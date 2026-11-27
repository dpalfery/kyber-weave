using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
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
/// <param name="briefs">Builds the brief from the installed reference files.</param>
/// <param name="configs">Reads the host configuration the brief follows.</param>
/// <param name="output">Where the brief and the refusals are written.</param>
/// <param name="environment">Names the working directory the repository root is found from.</param>
/// <param name="plans">Finds the repository root a plan argument belongs to.</param>
/// <param name="roots">Finds the repository root above a directory.</param>
public sealed class SquadBriefCommand(
    ISquadBriefBuilder briefs,
    IKyberWeaveConfigRepository configs,
    SquadConsole output,
    IProcessEnvironment environment,
    PlanArguments plans,
    ArbiterRepoRoot roots) : Command<SquadBriefSettings>
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

        string current = environment.CurrentDirectory;
        string root = string.IsNullOrWhiteSpace(settings.Plan) ? roots.Find(current) : plans.RepositoryRootFor(settings.Plan);
        DiagnosticReport report = new();
        KyberWeaveConfig? config = CommandHelpers.TryLoadConfig(configs, root, null, report, out KyberWeaveConfig loaded) ? loaded : null;

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

        if (!briefs.TryBuild(request, out BriefResult? result, out string error))
        {
            return Refuse(error);
        }

        BriefResult built = result
            ?? throw new InvalidOperationException("A built brief result must be present.");
        output.Out.Write(json ? BriefFormatter.ToJson(built) + "\n" : BriefFormatter.ToText(built));
        return Printed;
    }

    private int Refuse(string message)
    {
        output.Ansi.MarkupLine($"[red]kyber-weave squad brief: error:[/] {Markup.Escape(message)}");
        return Refused;
    }
}
