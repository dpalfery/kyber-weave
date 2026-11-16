using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Kernel.FileSystem;
namespace KyberWeave.Domain.Doctor;

/// <summary>
/// Doctor checks for the Copilot system-prompt override: whether the prompt file is installed and
/// valid, and whether each VS Code build's settings point at it.
/// </summary>
public static partial class CopilotPromptProbe
{
    private const string FixCommand = "kyber-weave squad copilot-prompt install --yes";
    private const string ReplaceMode = "mode=replace";
    private const string LogFolderName = "logs";
    private const int MaximumLogDepth = 8;
    private const long LogTailBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Runs the Copilot system-prompt checks for one user.
    /// </summary>
    /// <param name="mode">The doctor run mode, which decides how strictly findings are reported.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose VS Code layout applies.</param>
    /// <param name="xdgConfigHome">The <c>XDG_CONFIG_HOME</c> value, which moves the VS Code user data on Linux.</param>
    /// <param name="fileSystem">Reads the file system. Production registers the physical adapter; a test injects a fake.</param>
    /// <returns>The checks in report order. Empty when the override is not in use.</returns>
    /// <remarks>
    /// Without an installed prompt file there is nothing to check, so a repository that never opted in sees no
    /// new output. In <see cref="DoctorMode.Ensure"/> every failure becomes a warning that passes, as the other
    /// doctor checks do for a repository that opted out of stopping.
    /// </remarks>
    public static IReadOnlyList<DoctorCheck> Run(
        DoctorMode mode,
        string homeDirectory,
        string? appDataDirectory,
        OSPlatform platform,
        IDoctorFileSystem fileSystem,
        string? xdgConfigHome = null)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        IDoctorFileSystem files = fileSystem;

        string promptPath = CopilotPromptFile.InstalledPath(homeDirectory);
        if (!files.FileExists(promptPath))
            return [];

        List<DoctorCheck> checks = [];
        bool anyFlavourPoints = false;
        bool anyUnreadable = false;
        foreach (VsCodeFlavour flavour in Enum.GetValues<VsCodeFlavour>())
        {
            string settingsFile = VsCodeUserSettingsPaths.SettingsFile(flavour, homeDirectory, appDataDirectory, platform, xdgConfigHome);
            string label = flavour == VsCodeFlavour.Insiders ? "insiders" : "stable";
            SettingsFileRead read = CopilotOverrideSettings.ReadFile(
                settingsFile,
                files.DirectoryExists,
                files.IsPresentFile,
                files.ReadAllBytes);
            if (read.Error is not null)
            {
                // Present but unusable is its own fault: "no settings file points at it" would send the person to reinstall.
                anyUnreadable = true;
                checks.Add(new DoctorCheck(
                    $"copilot.prompt.{label}.setting",
                    false,
                    $"the settings file {settingsFile} could not be read: {read.Error}",
                    $"fix the permissions or type of {settingsFile}, then run kyber-weave doctor"));
                continue;
            }

            CopilotOverrideState? state = read.Document is { } document ? CopilotOverrideSettings.Read(document) : null;
            if (read.Document is not null && state is null)
            {
                // Present but not a JSON object: install and uninstall refuse this file, so reinstalling is not the fix.
                anyUnreadable = true;
                checks.Add(new DoctorCheck(
                    $"copilot.prompt.{label}.setting",
                    false,
                    $"the settings file {settingsFile} is not a readable JSON object",
                    $"fix the JSON in {settingsFile}, then run kyber-weave doctor"));
                continue;
            }

            if (state?.StarPromptFile is null || !CopilotPromptFile.SamePath(state.StarPromptFile, promptPath))
                continue;

            anyFlavourPoints = true;
            string userData = VsCodeUserSettingsPaths.UserDataDirectory(flavour, homeDirectory, appDataDirectory, platform, xdgConfigHome);
            checks.Add(new DoctorCheck($"copilot.prompt.{label}.setting", true, null, null));
            checks.Add(FileCheck(label, promptPath, files));
            checks.Add(LogCheck(label, userData, files));
        }

        if (!anyFlavourPoints && !anyUnreadable)
        {
            checks.Add(new DoctorCheck(
                "copilot.prompt.setting",
                false,
                "no VS Code settings file points its '*' entry at the installed prompt file",
                FixCommand));
        }

        return mode == DoctorMode.Ensure ? [.. checks.Select(AsWarning)] : checks;
    }

    /// <summary>Turns a failed check into a passing one whose reason says it is a warning.</summary>
    private static DoctorCheck AsWarning(DoctorCheck check) =>
        check.Passed ? check : new DoctorCheck(check.Id, true, $"warning: {check.Reason}", null);

    /// <summary>A check that cannot prove the state either way passes, with its reason prefixed by <c>warning:</c>.</summary>
    private static DoctorCheck Inconclusive(string id, string reason) =>
        new(id, true, $"warning: {reason}", null);

    /// <summary>Checks that the installed prompt file is readable and valid YAML with the allowed keys.</summary>
    private static DoctorCheck FileCheck(string label, string promptPath, IDoctorFileSystem files)
    {
        string id = $"copilot.prompt.{label}.file";
        string text;
        try
        {
            text = files.ReadAllText(promptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck(id, false, "the installed prompt file could not be read", FixCommand);
        }

        string? problem = CopilotPromptFile.Validate(text);
        return problem is null
            ? new DoctorCheck(id, true, null, null)
            : new DoctorCheck(id, false, $"the installed prompt file is not usable: {problem}", FixCommand);
    }

    /// <summary>
    /// Checks the newest AgentHost log: its last <c>Resolved system message</c> line must name <c>mode=replace</c>.
    /// </summary>
    /// <remarks>
    /// VS Code logs the mode it actually applied, which is the only proof the override is live: the settings file
    /// can be right while the file it names is ignored. A log that does not yet hold the line is inconclusive.
    /// </remarks>
    private static DoctorCheck LogCheck(string label, string userDataDirectory, IDoctorFileSystem files)
    {
        string id = $"copilot.prompt.{label}.log";
        try
        {
            string? newest = NewestLogFolder(Path.Combine(userDataDirectory, LogFolderName), files);
            if (newest is null)
                return Inconclusive(id, "no AgentHost log folder was found, so the prompt in use is not proven");

            string? mode = LastResolvedMode(AgentHostLogs(newest, files), files);
            if (mode is null)
                return Inconclusive(id, "the newest log has no 'Resolved system message' line yet");

            return mode == ReplaceMode
                ? new DoctorCheck(id, true, null, null)
                : new DoctorCheck(id, false, $"the newest log's last system message is {mode}, not {ReplaceMode}; restart VS Code and run kyber-weave doctor again", "kyber-weave doctor");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Inconclusive(id, "the AgentHost log could not be read");
        }
    }

    /// <summary>The timestamp folder with the greatest name. The names sort chronologically, so the newest wins.</summary>
    private static string? NewestLogFolder(string logsRoot, IDoctorFileSystem files)
    {
        if (!files.DirectoryExists(logsRoot))
            return null;

        string? newest = null;
        foreach (DoctorDirectoryEntry entry in files.ListEntries(logsRoot))
        {
            // A link is skipped, not followed: the log root must not send the probe outside the user-data directory.
            if (!entry.IsDirectory || entry.IsLink)
                continue;
            if (newest is null || string.CompareOrdinal(entry.Name, newest) > 0)
                newest = entry.Name;
        }

        return newest is null ? null : Path.Combine(logsRoot, newest);
    }

    /// <summary>Every <c>agenthost*.log</c> under the folder, at any depth, in path order. Links are not followed.</summary>
    private static List<string> AgentHostLogs(string root, IDoctorFileSystem files)
    {
        List<string> found = [];
        Stack<(string Folder, int Depth)> pending = new();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (string folder, int depth) = pending.Pop();
            foreach (DoctorDirectoryEntry entry in files.ListEntries(folder))
            {
                if (entry.IsLink)
                    continue;
                if (entry.IsDirectory)
                {
                    if (depth < MaximumLogDepth)
                        pending.Push((entry.FullPath, depth + 1));
                }
                else if (IsAgentHostLog(entry.Name))
                {
                    found.Add(entry.FullPath);
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool IsAgentHostLog(string name) =>
        name.StartsWith("agenthost", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>mode=</c> token of the newest matching line across the files, or null when it cannot be known.
    /// </summary>
    /// <remarks>
    /// Files are read newest-first (the reverse of their sorted order) and the first file with a match wins. Only the
    /// last <see cref="LogTailBytes"/> of a file is read. A file that was cut and has no match in its tail stops the
    /// search with null: its mode line may sit before the tail, so an older file's mode would be stale.
    /// </remarks>
    private static string? LastResolvedMode(IReadOnlyList<string> logFiles, IDoctorFileSystem files)
    {
        for (int index = logFiles.Count - 1; index >= 0; index--)
        {
            string? mode = LastModeInTail(logFiles[index], files, out bool cut);
            if (mode is not null)
                return mode;
            if (cut)
                return null;
        }

        return null;
    }

    /// <summary>
    /// The last <c>mode=</c> token in the final <see cref="LogTailBytes"/> of one log, streamed line by line.
    /// </summary>
    private static string? LastModeInTail(string file, IDoctorFileSystem files, out bool cut)
    {
        string tail = files.ReadTail(file, LogTailBytes, out bool partialFirstLine);
        cut = partialFirstLine;
        using StringReader reader = new(tail);
        if (partialFirstLine)
            reader.ReadLine();

        string? mode = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("Resolved system message:", StringComparison.Ordinal))
                continue;
            Match match = ResolvedModeLine().Match(line);
            if (match.Success)
                mode = match.Groups["mode"].Value;
        }

        return mode;
    }

    [GeneratedRegex(@"Resolved system message: (?<mode>mode=\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ResolvedModeLine();
}
