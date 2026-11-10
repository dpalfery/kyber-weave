using System.Runtime.InteropServices;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Options shared by the Copilot system-prompt override commands.
/// </summary>
public sealed class CopilotPromptSettings : CommandSettings
{
    /// <summary>The VS Code build to target: <c>stable</c> or <c>insiders</c>.</summary>
    [CommandOption("--flavour <FLAVOUR>")]
    public string? Flavour { get; init; }

    /// <summary>Reports what would change without writing any file.</summary>
    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }

    /// <summary>Skips the confirmation prompt.</summary>
    [CommandOption("--yes")]
    public bool Yes { get; init; }
}

/// <summary>
/// The real environment a Copilot system-prompt command runs in: the home and application-data
/// directories, the platform, whether a person can answer a prompt, and how answers are read.
/// </summary>
internal sealed record CopilotPromptEnvironment(
    string HomeDirectory,
    string? AppDataDirectory,
    OSPlatform Platform,
    bool IsInteractive,
    Func<string, bool> ReadAnswer);

/// <summary>
/// Writes the Copilot system-prompt override file and points VS Code's setting at it.
/// </summary>
public sealed class CopilotPromptInstallCommand : Command<CopilotPromptSettings>
{
    private readonly CopilotPromptEnvironment? _environment;

    /// <summary>Creates the command for the real environment.</summary>
    public CopilotPromptInstallCommand()
    {
    }

    /// <summary>Creates the command for an injected environment.</summary>
    internal CopilotPromptInstallCommand(CopilotPromptEnvironment environment)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, CopilotPromptSettings settings, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Removes the Copilot system-prompt override setting from VS Code and deletes the installed prompt file.
/// </summary>
public sealed class CopilotPromptUninstallCommand : Command<CopilotPromptSettings>
{
    private readonly CopilotPromptEnvironment? _environment;

    /// <summary>Creates the command for the real environment.</summary>
    public CopilotPromptUninstallCommand()
    {
    }

    /// <summary>Creates the command for an injected environment.</summary>
    internal CopilotPromptUninstallCommand(CopilotPromptEnvironment environment)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, CopilotPromptSettings settings, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Reports whether the Copilot system-prompt override file is installed and whether each VS Code build points at it.
/// </summary>
public sealed class CopilotPromptStatusCommand : Command<CopilotPromptSettings>
{
    private readonly CopilotPromptEnvironment? _environment;

    /// <summary>Creates the command for the real environment.</summary>
    public CopilotPromptStatusCommand()
    {
    }

    /// <summary>Creates the command for an injected environment.</summary>
    internal CopilotPromptStatusCommand(CopilotPromptEnvironment environment)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, CopilotPromptSettings settings, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
