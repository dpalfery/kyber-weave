using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings for <c>arbiter uninstall --target &lt;T&gt;</c>.</summary>
/// <remarks>
/// <c>Path</c> (inherited) is the deployment root the target's Arbiter artifacts are removed from.
/// There is no <c>--global</c>: a global run has no project hooks to remove. The key is never
/// removed unless <c>--remove-key</c> is given.
/// </remarks>
public sealed class ArbiterUninstallSettings : AnalysisSettings
{
    /// <summary>The harness target to remove Arbiter artifacts for (for example kilo).</summary>
    [CommandOption("--target <TARGET>")]
    [Description("Harness target to remove Arbiter hooks from (for example kilo).")]
    public string? Target { get; set; }

    /// <summary>Whether to also delete the stored TypeSafe key for the endpoint origin.</summary>
    [CommandOption("--remove-key")]
    [Description("Also delete the stored TypeSafe key for the endpoint origin.")]
    public bool RemoveKey { get; set; }
}
