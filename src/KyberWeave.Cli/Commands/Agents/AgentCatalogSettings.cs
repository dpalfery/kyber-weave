using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentCatalogSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Root project directory containing harness agent folders (.codex, .cursor, .claude, etc.). Defaults to current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("-c|--config <PATH>")]
    [Description("Path to kyber-weave.yml. Defaults to <path>/kyber-weave.yml when present.")]
    public string? Config { get; set; }
}
