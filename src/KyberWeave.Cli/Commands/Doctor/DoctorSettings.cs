using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Doctor;

/// <summary>Settings for the <c>doctor</c> verb.</summary>
public sealed class DoctorSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("A directory inside the repository to check. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("--check")]
    [Description("Read-only: detect CodeGraph and the index and prove the MCP server. Never runs init or index.")]
    public bool Check { get; set; }

    [CommandOption("--provision")]
    [Description("Provision the CodeGraph index: init when .codegraph/ is absent, index otherwise. Never installs anything.")]
    public bool Provision { get; set; }

    [CommandOption("--index-timeout <SECONDS>")]
    [Description("The bound on one codegraph init or index run, and how long a second doctor waits for the lock. Default 600.")]
    public int IndexTimeoutSeconds { get; set; } = 600;

    [CommandOption("--mcp-timeout <SECONDS>")]
    [Description("The total budget of the live MCP session. Default 20.")]
    public int McpTimeoutSeconds { get; set; } = 20;

    [CommandOption("--json")]
    [Description("Write the report as JSON instead of check lines.")]
    public bool Json { get; set; }
}
