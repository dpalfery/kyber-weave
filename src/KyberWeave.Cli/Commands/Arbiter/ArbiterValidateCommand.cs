using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Validates the <c>arbiter:</c> section, the user override and the shipped rules.</summary>
public sealed class ArbiterValidateCommand : Command<ArbiterSettings>
{
    /// <summary>
    /// Validates the effective Arbiter configuration. Exits 0 when no error
    /// is reported (warnings such as the provider-<c>none</c> notice do not
    /// fail the run); exits 1 on any error, including hinted
    /// <c>KW-ARB-CONFIG-*</c> diagnostics for an invalid section.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out string? configPath))
        {
            CommandHelpers.Finish(report, settings, "arbiter validate", "Rule");
            return 1;
        }

        report.AddRange(RuleValidator.Validate(config.Arbiter, configPath));
        CommandHelpers.Finish(report, settings, "arbiter validate", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);
}
