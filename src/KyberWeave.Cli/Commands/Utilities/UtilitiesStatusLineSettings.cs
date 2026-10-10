using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Settings for the <c>utilities statusline deploy</c> command.</summary>
public sealed class UtilitiesStatusLineDeploySettings : CommandSettings
{
    /// <summary>The harness to deploy the status-line artifact for. Defaults to all three.</summary>
    [CommandOption("-t|--target <TARGET>")]
    [Description("The harness to deploy the status-line artifact for: claude, agy, or pi. Defaults to all three.")]
    public string? Target { get; set; }

    /// <summary>Preview the planned files and the activation guidance without writing anything.</summary>
    [CommandOption("--dry-run")]
    [Description("Preview the planned files and the activation guidance without writing anything.")]
    public bool DryRun { get; set; }
}

/// <summary>
///     Settings for the <c>utilities statusline status</c>, <c>doctor</c>, and <c>remove</c> commands.
/// </summary>
/// <remarks>
///     Empty by design: every one of the three operates on all three harnesses, so there is no target
///     selection to bind and no flag to add. The type exists because Spectre.Console.Cli requires a
///     settings type per command, and sharing one keeps the three from drifting apart.
/// </remarks>
public sealed class UtilitiesStatusLineSettings : CommandSettings
{
}
