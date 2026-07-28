using System.ComponentModel;
using KyberWeave.Cli.Commands;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentScanSettings : AgentCommandSettings
{
    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Severity that fails the run: critical | error | warning. Default critical.")]
    [DefaultValue("critical")]
    public string FailOn { get; set; } = "critical";
}
