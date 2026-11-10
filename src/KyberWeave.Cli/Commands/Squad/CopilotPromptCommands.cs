using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands;
using KyberWeave.Core.CopilotPrompt;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.FileSystem;
using Spectre.Console;
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
/// directories, the platform, whether a person can answer a prompt, how answers are read, and the
/// <c>XDG_CONFIG_HOME</c> value that moves VS Code's user data on Linux.
/// </summary>
internal sealed record CopilotPromptEnvironment(
    string? HomeDirectory,
    string? AppDataDirectory,
    OSPlatform Platform,
    bool IsInteractive,
    Func<string, bool> ReadAnswer,
    string? XdgConfigHome = null);

/// <summary>One settings file as read: its bytes, null when it is missing, or the error that made it unusable.</summary>
internal sealed record SettingsRead(byte[]? Document, string? Error);

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
    /// <remarks>
    /// Every settings file is planned before anything is written. A single refusal stops the whole install, so a
    /// machine is never left with one build pointing at the prompt and another refusing it.
    /// </remarks>
    public override int Execute(CommandContext context, CopilotPromptSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CopilotPromptEnvironment environment = _environment ?? CopilotPromptCommandSupport.RealEnvironment();
        if (!CopilotPromptCommandSupport.HasHome(environment))
            return 2;

        if (!CopilotPromptCommandSupport.TryParseFlavour(settings.Flavour, out VsCodeFlavour? requested))
        {
            CopilotPromptCommandSupport.Fail("--flavour must be stable or insiders.");
            return 2;
        }

        if (!settings.DryRun && !settings.Yes && !environment.IsInteractive)
        {
            CopilotPromptCommandSupport.Fail("install changes every Copilot chat on this machine. Pass --yes to confirm when no terminal is attached.");
            return 2;
        }

        IReadOnlyList<VsCodeFlavour> targets = CopilotPromptCommandSupport.DetectedTargets(environment, requested);
        if (targets.Count == 0)
        {
            CopilotPromptCommandSupport.Fail("no VS Code user data was found for the requested build. Open VS Code once, then run this again.");
            return 1;
        }

        string promptPath = CopilotPromptFile.InstalledPath(CopilotPromptCommandSupport.Home(environment));
        string entryJson = "{\"promptOverrideFile\": " + JsonSerializer.Serialize(promptPath) + "}";
        List<(string File, byte[] Document)> writes = [];
        List<string> alreadySet = [];
        List<string> refusals = [];

        foreach (VsCodeFlavour flavour in targets)
        {
            string file = CopilotPromptCommandSupport.SettingsFileOf(flavour, environment);
            SettingsRead read = CopilotPromptCommandSupport.ReadSettings(file);
            if (read.Error is not null)
            {
                refusals.Add($"{file} could not be read, so it was left unchanged: {read.Error}");
                continue;
            }

            // A file that does not exist yet is created from an empty object: the only write that is not a splice.
            byte[] document = read.Document ?? Encoding.UTF8.GetBytes("{}\n");
            JsonSplicePlan plan = JsonServerSplice.Plan(document, [CopilotOverrideSettings.SettingId], CopilotOverrideSettings.StarEntry, entryJson);

            if (plan.Outcome == JsonSpliceOutcome.Inserted)
            {
                writes.Add((file, plan.Document));
            }
            else if (plan.Outcome == JsonSpliceOutcome.ServerAlreadyPresent)
            {
                CopilotOverrideState? state = CopilotOverrideSettings.Read(document);
                if (CopilotPromptCommandSupport.PointsAtPrompt(state, promptPath))
                    alreadySet.Add(file);
                else
                    refusals.Add($"{file} already has a '{CopilotOverrideSettings.StarEntry}' entry that is not Kyber-Weave's. Remove it by hand, then run this again.");
            }
            else
            {
                refusals.Add($"{file} cannot be changed safely: {plan.Message ?? "the document is not one VS Code can load"}");
            }
        }

        if (refusals.Count > 0)
        {
            foreach (string refusal in refusals)
                CopilotPromptCommandSupport.Fail(refusal);
            return 1;
        }

        if (settings.DryRun)
        {
            CopilotPromptCommandSupport.Say($"Dry run: would write the prompt to {Markup.Escape(promptPath)}.");
            foreach ((string file, _) in writes)
                CopilotPromptCommandSupport.Say($"Dry run: would set the override in {Markup.Escape(file)}.");
            CopilotPromptCommandSupport.Say("Dry run: nothing was written.");
            return 0;
        }

        if (!settings.Yes)
        {
            string files = string.Join(", ", writes.Select(write => write.File));
            if (writes.Count == 0)
                files = string.Join(", ", alreadySet);
            bool confirmed = environment.ReadAnswer(
                $"Set the Copilot system prompt in {Markup.Escape(files)}? It affects every Copilot chat on this machine.");
            if (!confirmed)
            {
                CopilotPromptCommandSupport.Say("No change was made.");
                return 0;
            }
        }

        try
        {
            // The prompt file goes first, so VS Code never reads a setting that points at a file that is not there yet.
            AtomicFile.WriteAllText(promptPath, CopilotPromptFile.EmbeddedText());
            foreach ((string file, byte[] document) in writes)
                AtomicFile.WriteAllBytes(file, document, writeThroughSymlink: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CopilotPromptCommandSupport.Fail($"could not write the prompt or a settings file: {ex.Message}");
            return 1;
        }

        CopilotPromptCommandSupport.Say($"Installed the Copilot system prompt at {Markup.Escape(promptPath)}.");
        foreach ((string file, _) in writes)
            CopilotPromptCommandSupport.Say($"Set the override in {Markup.Escape(file)}.");
        foreach (string file in alreadySet)
            CopilotPromptCommandSupport.Say($"The override was already set in {Markup.Escape(file)}.");
        CopilotPromptCommandSupport.Say("This affects every Copilot chat on this machine, not only this repository. Restart VS Code to apply it.");
        return 0;
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
    /// <remarks>
    /// Only an entry that names this install's prompt file is removed. A foreign <c>*</c> entry is a refusal, and a
    /// refusal deletes nothing, so the prompt file is never removed while a setting still depends on it.
    /// </remarks>
    public override int Execute(CommandContext context, CopilotPromptSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CopilotPromptEnvironment environment = _environment ?? CopilotPromptCommandSupport.RealEnvironment();
        if (!CopilotPromptCommandSupport.HasHome(environment))
            return 2;

        if (!CopilotPromptCommandSupport.TryParseFlavour(settings.Flavour, out VsCodeFlavour? requested))
        {
            CopilotPromptCommandSupport.Fail("--flavour must be stable or insiders.");
            return 2;
        }

        if (!settings.DryRun && !settings.Yes && !environment.IsInteractive)
        {
            CopilotPromptCommandSupport.Fail("uninstall changes every Copilot chat on this machine. Pass --yes to confirm when no terminal is attached.");
            return 2;
        }

        string promptPath = CopilotPromptFile.InstalledPath(CopilotPromptCommandSupport.Home(environment));
        List<(string File, byte[] Document)> removals = [];
        List<string> refusals = [];

        foreach (VsCodeFlavour flavour in CopilotPromptCommandSupport.DetectedTargets(environment, requested))
        {
            string file = CopilotPromptCommandSupport.SettingsFileOf(flavour, environment);
            SettingsRead read = CopilotPromptCommandSupport.ReadSettings(file);
            if (read.Error is not null)
            {
                refusals.Add($"{file} could not be read, so it was left unchanged: {read.Error}");
                continue;
            }

            if (read.Document is not { } document)
                continue;

            CopilotOverrideState? state = CopilotOverrideSettings.Read(document);
            if (state is null)
            {
                refusals.Add($"{file} is not readable JSON, so it was left unchanged.");
                continue;
            }

            if (!state.StarEntryPresent)
                continue;

            if (!CopilotPromptCommandSupport.PointsAtPrompt(state, promptPath))
            {
                refusals.Add($"{file} has a '{CopilotOverrideSettings.StarEntry}' entry that is not Kyber-Weave's, so it was left unchanged.");
                continue;
            }

            JsonSplicePlan plan = JsonServerSplice.PlanRemoval(document, [CopilotOverrideSettings.SettingId], CopilotOverrideSettings.StarEntry);
            if (plan.Outcome == JsonSpliceOutcome.Removed)
                removals.Add((file, plan.Document));
            else
                refusals.Add($"{file} cannot be changed safely: {plan.Message ?? "the document is not one VS Code can load"}");
        }

        if (refusals.Count > 0)
        {
            foreach (string refusal in refusals)
                CopilotPromptCommandSupport.Fail(refusal);
            return 1;
        }

        bool promptInstalled = File.Exists(promptPath);
        if (settings.DryRun)
        {
            foreach ((string file, _) in removals)
                CopilotPromptCommandSupport.Say($"Dry run: would remove the override from {Markup.Escape(file)}.");
            if (promptInstalled)
                CopilotPromptCommandSupport.Say($"Dry run: would delete {Markup.Escape(promptPath)}.");
            CopilotPromptCommandSupport.Say("Dry run: nothing was changed.");
            return 0;
        }

        if (!settings.Yes)
        {
            string files = removals.Count == 0 ? "no settings file" : string.Join(", ", removals.Select(removal => removal.File));
            bool confirmed = environment.ReadAnswer(
                $"Remove the Copilot system prompt override from {Markup.Escape(files)}? Copilot chats on this machine return to the default prompt.");
            if (!confirmed)
            {
                CopilotPromptCommandSupport.Say("No change was made.");
                return 0;
            }
        }

        try
        {
            // The settings go first, so a setting never points at a prompt file this command has already deleted.
            foreach ((string file, byte[] document) in removals)
                AtomicFile.WriteAllBytes(file, document, writeThroughSymlink: true);
            if (promptInstalled)
                File.Delete(promptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CopilotPromptCommandSupport.Fail($"could not finish the uninstall: {ex.Message}");
            return 1;
        }

        foreach ((string file, _) in removals)
            CopilotPromptCommandSupport.Say($"Removed the override from {Markup.Escape(file)}.");
        if (promptInstalled)
            CopilotPromptCommandSupport.Say($"Deleted {Markup.Escape(promptPath)}.");
        if (removals.Count == 0 && !promptInstalled)
            CopilotPromptCommandSupport.Say("Nothing to remove: the Copilot prompt override is not installed.");
        else
            CopilotPromptCommandSupport.Say("Restart VS Code to apply it.");
        return 0;
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
        ArgumentNullException.ThrowIfNull(settings);
        CopilotPromptEnvironment environment = _environment ?? CopilotPromptCommandSupport.RealEnvironment();
        if (!CopilotPromptCommandSupport.HasHome(environment))
            return 2;

        if (!CopilotPromptCommandSupport.TryParseFlavour(settings.Flavour, out VsCodeFlavour? requested))
        {
            CopilotPromptCommandSupport.Fail("--flavour must be stable or insiders.");
            return 2;
        }

        string promptPath = CopilotPromptFile.InstalledPath(CopilotPromptCommandSupport.Home(environment));
        CopilotPromptCommandSupport.Say(File.Exists(promptPath)
            ? $"Prompt file: installed at {Markup.Escape(promptPath)}."
            : $"Prompt file: not installed (expected {Markup.Escape(promptPath)}).");

        IReadOnlyList<VsCodeFlavour> detected = CopilotPromptCommandSupport.DetectedTargets(environment, null);
        foreach (VsCodeFlavour flavour in Enum.GetValues<VsCodeFlavour>())
        {
            if (requested is not null && requested.Value != flavour)
                continue;

            string label = CopilotPromptCommandSupport.FlavourLabel(flavour);
            if (!detected.Contains(flavour))
            {
                CopilotPromptCommandSupport.Say($"{label}: VS Code is not installed for this user.");
                continue;
            }

            string file = CopilotPromptCommandSupport.SettingsFileOf(flavour, environment);
            SettingsRead read = CopilotPromptCommandSupport.ReadSettings(file);
            if (read.Error is not null)
            {
                CopilotPromptCommandSupport.Say($"{label}: {Markup.Escape(file)} could not be read: {Markup.Escape(read.Error)}");
                continue;
            }

            CopilotOverrideState? state = read.Document is { } document
                ? CopilotOverrideSettings.Read(document)
                : new CopilotOverrideState(false, null, []);
            if (state is null)
            {
                CopilotPromptCommandSupport.Say($"{label}: {Markup.Escape(file)} is not readable JSON.");
                continue;
            }

            CopilotPromptCommandSupport.Say($"{label}: {Markup.Escape(file)}");
            CopilotPromptCommandSupport.Say(DescribeStarEntry(state, promptPath));
            if (state.ModelSpecificIds.Count > 0)
            {
                string models = string.Join(", ", state.ModelSpecificIds);
                CopilotPromptCommandSupport.Say(
                    $"  a model-specific entry overrides '*' for: {Markup.Escape(models)}");
            }
        }

        return 0;
    }

    private static string DescribeStarEntry(CopilotOverrideState state, string promptPath)
    {
        if (!state.StarEntryPresent)
            return "  override entry: not present";
        if (CopilotPromptCommandSupport.PointsAtPrompt(state, promptPath))
            return "  override entry: present and Kyber-Weave's";
        return state.StarPromptFile is null
            ? "  override entry: present, not Kyber-Weave's (no promptOverrideFile)"
            : $"  override entry: present, not Kyber-Weave's (promptOverrideFile {Markup.Escape(state.StarPromptFile)})";
    }
}

/// <summary>
/// Shared plumbing for the three Copilot prompt commands: the real environment, flavour selection and output.
/// </summary>
internal static class CopilotPromptCommandSupport
{
    private const string Prefix = "kyber-weave squad copilot-prompt";

    /// <summary>The environment a person's terminal runs in: their home, their roaming app data, and whether they can answer a prompt.</summary>
    internal static CopilotPromptEnvironment RealEnvironment() => EnvironmentFrom(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        !Console.IsInputRedirected,
        question => AnsiConsole.Confirm(question, false));

    /// <summary>
    /// Builds the environment from environment variables. Home resolves as doctor resolves it, so doctor finds
    /// the prompt file where install wrote it. A null home means neither variable names an absolute directory.
    /// </summary>
    internal static CopilotPromptEnvironment EnvironmentFrom(
        Func<string, string?> read,
        OSPlatform platform,
        bool isInteractive,
        Func<string, bool> readAnswer) => new(
        CopilotPromptHome.FromEnvironment(read),
        read("APPDATA"),
        platform,
        isInteractive,
        readAnswer,
        read("XDG_CONFIG_HOME"));

    /// <summary>The home directory. Throws when it is unknown, so a path is never built under the working directory.</summary>
    /// <exception cref="InvalidOperationException">No home directory is known; callers check <see cref="HasHome"/> first.</exception>
    internal static string Home(CopilotPromptEnvironment environment) =>
        string.IsNullOrWhiteSpace(environment.HomeDirectory)
            ? throw new InvalidOperationException("No home directory is known; check HasHome before building a path.")
            : environment.HomeDirectory;

    /// <summary>
    /// False, after saying why, when no home directory is known. Writing then would put the prompt file under the
    /// working directory, where doctor would never look.
    /// </summary>
    internal static bool HasHome(CopilotPromptEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.HomeDirectory))
            return true;

        Fail("no home directory is known. Set HOME (or USERPROFILE on Windows) to an absolute path and run the command again.");
        return false;
    }

    /// <summary>
    /// Reads one settings file, telling a missing file apart from one that is present but unusable.
    /// </summary>
    /// <remarks>
    /// A symbolic link whose final target does not exist is missing: <see cref="File.Exists(string)"/> is true for it,
    /// but there is nothing to read, and a write goes through it. Every probe and the read itself are guarded, so an
    /// IO or permission failure comes back as <see cref="SettingsRead.Error"/> for the caller to report, never as a crash.
    /// </remarks>
    internal static SettingsRead ReadSettings(string file)
    {
        try
        {
            FileInfo info = new(file);
            bool present = info.LinkTarget is null
                ? info.Exists
                : info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
            return present ? new SettingsRead(File.ReadAllBytes(file), null) : new SettingsRead(null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsRead(null, exception.Message);
        }
    }

    /// <summary>Parses <c>--flavour</c>. Null means the option was not given, so every detected build is in scope.</summary>
    internal static bool TryParseFlavour(string? text, out VsCodeFlavour? flavour)
    {
        flavour = null;
        if (text is null)
            return true;
        if (string.Equals(text, "stable", StringComparison.OrdinalIgnoreCase))
        {
            flavour = VsCodeFlavour.Stable;
            return true;
        }

        if (string.Equals(text, "insiders", StringComparison.OrdinalIgnoreCase))
        {
            flavour = VsCodeFlavour.Insiders;
            return true;
        }

        return false;
    }

    /// <summary>The detected builds in scope: all of them, or only the one the person named.</summary>
    internal static IReadOnlyList<VsCodeFlavour> DetectedTargets(CopilotPromptEnvironment environment, VsCodeFlavour? requested)
    {
        IReadOnlyList<VsCodeFlavour> detected = VsCodeUserSettingsPaths.Detect(Home(environment), environment.AppDataDirectory, environment.Platform, environment.XdgConfigHome);
        return requested is null ? detected : detected.Where(flavour => flavour == requested.Value).ToList();
    }

    internal static string SettingsFileOf(VsCodeFlavour flavour, CopilotPromptEnvironment environment) =>
        VsCodeUserSettingsPaths.SettingsFile(flavour, Home(environment), environment.AppDataDirectory, environment.Platform, environment.XdgConfigHome);

    internal static string FlavourLabel(VsCodeFlavour flavour) =>
        flavour == VsCodeFlavour.Insiders ? "insiders" : "stable";

    /// <summary>True when the state's <c>*</c> entry names this install's prompt file. Only such an entry is ours.</summary>
    internal static bool PointsAtPrompt(CopilotOverrideState? state, string promptPath) =>
        state?.StarPromptFile is not null
        && string.Equals(state.StarPromptFile, promptPath, StringComparison.Ordinal);

    /// <summary>Writes one line of console output. Callers escape any value from a file or the operator.</summary>
    internal static void Say(string markup) =>
        CommandHelpers.WriteUnfolded(() => AnsiConsole.MarkupLine(markup));

    /// <summary>Writes one refusal or error line, in the squad command's error style.</summary>
    internal static void Fail(string message) =>
        Say($"[red]{Prefix}: error: {Markup.Escape(message)}[/]");
}
