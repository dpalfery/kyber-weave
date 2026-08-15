using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>Settings for the <c>squad install</c> command.</summary>
public sealed class SquadInstallSettings : CommandSettings
{
    /// <summary>The deployment root directory. Defaults to the current directory.</summary>
    [CommandArgument(0, "[path]")]
    [Description("The deployment root directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Harness target(s) to deploy: codex, cursor, claude, copilot, opencode, kilo, gemini, antigravity, warp, factory, all.</summary>
    [CommandOption("-t|--target <TARGETS>")]
    [Description("Harness target(s) to deploy: codex, cursor, claude, copilot, opencode, kilo, gemini, antigravity, warp, factory, all.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Targets { get; set; } = [];

    /// <summary>Target(s) to exclude from deployment.</summary>
    [CommandOption("-x|--exclude <TARGETS>")]
    [Description("Target(s) to exclude from deployment.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Exclusions { get; set; } = [];

    /// <summary>Target the per-user global deployment directory rather than the local project.</summary>
    [CommandOption("-g|--global")]
    [Description("Target the per-user global deployment directory rather than the local project.")]
    public bool Global { get; set; }

    /// <summary>Preview the planned mutations without making filesystem changes.</summary>
    [CommandOption("--dry-run")]
    [Description("Preview the planned mutations without making filesystem changes.")]
    public bool DryRun { get; set; }

    /// <summary>Adopt pre-existing unmanaged files whose SHA-256 matches generated content exactly.</summary>
    [CommandOption("--adopt")]
    [Description("Adopt pre-existing unmanaged files whose SHA-256 matches generated content exactly.")]
    public bool Adopt { get; set; }
}

/// <summary>Settings for the <c>squad update</c> command.</summary>
public sealed class SquadUpdateSettings : CommandSettings
{
    /// <summary>The deployment root directory. Defaults to the current directory.</summary>
    [CommandArgument(0, "[path]")]
    [Description("The deployment root directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Target the per-user global deployment directory rather than the local project.</summary>
    [CommandOption("-g|--global")]
    [Description("Target the per-user global deployment directory rather than the local project.")]
    public bool Global { get; set; }

    /// <summary>Optional harness target(s) to restrict update to.</summary>
    [CommandOption("-t|--target <TARGETS>")]
    [Description("Harness target(s) to update: codex, cursor, claude, copilot, opencode, kilo, gemini, antigravity, warp, factory, all.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Targets { get; set; } = [];

    /// <summary>Preview the planned mutations without making filesystem changes.</summary>
    [CommandOption("--dry-run")]
    [Description("Preview the planned mutations without making filesystem changes.")]
    public bool DryRun { get; set; }

    /// <summary>Overwrite locally edited receipt-owned files with canonical updates.</summary>
    [CommandOption("--replace-managed")]
    [Description("Overwrite locally edited receipt-owned files with canonical updates.")]
    public bool ReplaceManaged { get; set; }
}

/// <summary>Settings for the <c>squad uninstall</c> command.</summary>
public sealed class SquadUninstallSettings : CommandSettings
{
    /// <summary>The deployment root directory. Defaults to the current directory.</summary>
    [CommandArgument(0, "[path]")]
    [Description("The deployment root directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Target the per-user global deployment directory rather than the local project.</summary>
    [CommandOption("-g|--global")]
    [Description("Target the per-user global deployment directory rather than the local project.")]
    public bool Global { get; set; }

    /// <summary>Preview the files to be removed without making filesystem changes.</summary>
    [CommandOption("--dry-run")]
    [Description("Preview the files to be removed without making filesystem changes.")]
    public bool DryRun { get; set; }
}

/// <summary>Settings for the <c>squad status</c> command.</summary>
public sealed class SquadStatusSettings : CommandSettings
{
    /// <summary>The deployment root directory. Defaults to the current directory.</summary>
    [CommandArgument(0, "[path]")]
    [Description("The deployment root directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Inspect the per-user global deployment state rather than the local project.</summary>
    [CommandOption("-g|--global")]
    [Description("Inspect the per-user global deployment state rather than the local project.")]
    public bool Global { get; set; }
}

/// <summary>Settings for the <c>squad doctor</c> command.</summary>
public sealed class SquadDoctorSettings : CommandSettings
{
    /// <summary>The deployment root directory to inspect. Defaults to the current directory.</summary>
    [CommandArgument(0, "[path]")]
    [Description("The deployment root directory to inspect. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Inspect the per-user global deployment state rather than the local project.</summary>
    [CommandOption("-g|--global")]
    [Description("Inspect the per-user global deployment state rather than the local project.")]
    public bool Global { get; set; }
}

/// <summary>Settings for the <c>squad pack</c> command.</summary>
public sealed class SquadPackSettings : CommandSettings
{
    /// <summary>Output package format: apm, plugins, or all.</summary>
    [CommandOption("-f|--format <FORMAT>")]
    [Description("Output package format: apm, plugins, or all.")]
    [DefaultValue("all")]
    public string Format { get; set; } = "all";

    /// <summary>Destination directory for the packed archives.</summary>
    [CommandOption("-o|--out <DIRECTORY>")]
    [Description("Destination directory for the packed archives.")]
    public string Out { get; set; } = string.Empty;
}
