using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Settings for <c>arbiter install --target &lt;T&gt;</c>.</summary>
/// <remarks>
/// <c>Path</c> (inherited) is the deployment root the target's Arbiter wiring is
/// installed into. The install is project-scoped: there is no global install, so
/// <c>--global</c> is rejected before any write. The key travels in the OS
/// credential store only and never appears in configuration, receipts, or output.
/// </remarks>
public sealed class ArbiterInstallSettings : AnalysisSettings
{
    /// <summary>The harness target to install Arbiter hooks for (for example kilo).</summary>
    [CommandOption("--target <TARGET>")]
    [Description("Harness target to install Arbiter hooks for (for example kilo).")]
    public string? Target { get; set; }

    /// <summary>The project-root override for an explicit project install.</summary>
    [CommandOption("--path <PATH>")]
    [Description("Project root to install into; valid only with --project.")]
    public string? PathOption { get; set; }

    /// <summary>Opt into the project-scoped install; the default is the global user-home scope.</summary>
    [CommandOption("--project")]
    [Description("Install into the project root instead of the global user-home scope.")]
    public bool Project { get; set; }

    /// <summary>The provider kind: none or systemone. Omitted keeps the configured provider; an explicit empty value is none.</summary>
    [CommandOption("--provider <KIND>")]
    [Description("Provider kind: none or systemone. Omit to keep the configured provider (none on a fresh machine); an empty value is none.")]
    public string? Provider { get; set; }

    /// <summary>The TypeSafe key, stored in the OS credential store for the endpoint origin.</summary>
    [CommandOption("--arbiter-key <KEY>")]
    [Description("TypeSafe key to store in the OS credential store. Visible in argv and shell history; prefer --key-stdin or TYPESAFE_API_KEY.")]
    public string? ArbiterKey { get; set; }

    /// <summary>Whether to read the TypeSafe key from stdin rather than a masked prompt.</summary>
    [CommandOption("--key-stdin")]
    [Description("Read the TypeSafe key from stdin instead of a masked prompt.")]
    public bool KeyStdin { get; set; }

    /// <summary>Target the per-user global scope rather than the project root.</summary>
    [CommandOption("-g|--global")]
    [Description("Install into the global user-home scope. This is the default; it cannot be combined with --project.")]
    public bool Global { get; set; }

    /// <summary>Preview the planned changes without writing anything.</summary>
    [CommandOption("--dry-run")]
    [Description("Preview the planned changes without writing anything.")]
    public bool DryRun { get; set; }

    /// <summary>Skip the interactive confirmation prompt before the install.</summary>
    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt before the install.")]
    public bool Yes { get; set; }
}
