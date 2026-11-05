using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings for <c>arbiter uninstall --target &lt;T&gt;</c>.</summary>
/// <remarks>
/// <c>Path</c> (inherited) is the project root when <c>--project</c> is chosen; the default
/// global uninstall resolves to the user home directory unless <c>--path</c> is supplied for a
/// project root. The key is never removed unless <c>--remove-key</c> is given.
/// </remarks>
public sealed class ArbiterUninstallSettings : AnalysisSettings
{
    /// <summary>The harness target to remove Arbiter artifacts for (for example kilo).</summary>
    [CommandOption("--target <TARGET>")]
    [Description("Harness target to remove Arbiter hooks from (for example kilo).")]
    public string? Target { get; set; }

    /// <summary>The project-root override for an explicit project uninstall.</summary>
    [CommandOption("--path <PATH>")]
    [Description("Project root to uninstall from; valid only with --project.")]
    public string? PathOption { get; set; }

    /// <summary>Opt into the project-scoped uninstall; the default is the global user-home scope.</summary>
    [CommandOption("--project")]
    [Description("Uninstall from the project root instead of the global user-home scope.")]
    public bool Project { get; set; }

    /// <summary>Target the per-user global scope rather than the project root.</summary>
    [CommandOption("-g|--global")]
    [Description("Uninstall from the global user-home scope. This is the default; it cannot be combined with --project.")]
    public bool Global { get; set; }

    /// <summary>Whether to also delete the stored TypeSafe key for the endpoint origin.</summary>
    [CommandOption("--remove-key")]
    [Description("Also delete the stored TypeSafe key for the endpoint origin.")]
    public bool RemoveKey { get; set; }
}
