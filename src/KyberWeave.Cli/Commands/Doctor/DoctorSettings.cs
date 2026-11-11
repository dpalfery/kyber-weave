using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Doctor;

/// <summary>Settings for the <c>doctor</c> verb.</summary>
public sealed class DoctorSettings : CommandSettings
{
    /// <summary>The default bound on one CodeGraph index run, in seconds. Other run-start and review commands reuse it.</summary>
    public const int DefaultIndexTimeoutSeconds = 600;

    /// <summary>The default total budget of the live MCP session, in seconds.</summary>
    public const int DefaultMcpTimeoutSeconds = 20;

    [CommandArgument(0, "[path]")]
    [Description("A directory inside the repository to check. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("--check")]
    [Description("Read-only: detect CodeGraph and the index and prove the MCP server. Never runs init, sync or index.")]
    public bool Check { get; set; }

    [CommandOption("--provision")]
    [Description("Provision the CodeGraph index: init when .codegraph/ is absent, sync otherwise. Never installs anything.")]
    public bool Provision { get; set; }

    [CommandOption("--ensure")]
    [Description(
        "Leave the tooling working in one command: sync the index (or init it), restore the tracked files the index "
        + "wrote, prove the index and the docs server with one call each, and repair once if a step fails. "
        + "Exit 3 means CodeGraph is missing; follow stop-on-tool-failure otherwise.")]
    public bool Ensure { get; set; }

    [CommandOption("--index-timeout <SECONDS>")]
    [Description("The bound on one codegraph init, sync or index run, and how long a second doctor waits for the lock. Default 600.")]
    public int IndexTimeoutSeconds { get; set; } = DefaultIndexTimeoutSeconds;

    [CommandOption("--mcp-timeout <SECONDS>")]
    [Description("The total budget of the live MCP session. Default 20.")]
    public int McpTimeoutSeconds { get; set; } = DefaultMcpTimeoutSeconds;

    [CommandOption("--json")]
    [Description("Write the report as JSON instead of check lines.")]
    public bool Json { get; set; }
}
