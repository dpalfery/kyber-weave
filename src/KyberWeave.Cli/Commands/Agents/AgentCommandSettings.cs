using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

/// <summary>
/// Shared settings for agent commands. Path is always the project root; harness trees are
/// discovered as <c>.harnessname/agents</c> under that root.
/// </summary>
public class AgentCommandSettings : AnalysisSettings
{
    [CommandOption("--harness <NAME>")]
    [Description("Optional harness filter (codex|cursor|claude|github|opencode|kilo). Default: all .*/agents under the project root.")]
    public string? Harness { get; set; }
}
