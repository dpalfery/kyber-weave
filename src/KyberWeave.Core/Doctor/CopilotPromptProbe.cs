using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KyberWeave.Core.CopilotPrompt;

namespace KyberWeave.Core.Doctor;

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

    /// <summary>Lists the directory's entries one level at a time; a link is not followed. See <c>docs/standards/csharp</c>.</summary>
    private static readonly EnumerationOptions OneLevel = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };

    /// <summary>
    /// Runs the Copilot system-prompt checks for one user.
    /// </summary>
    /// <param name="mode">The doctor run mode, which decides how strictly findings are reported.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose VS Code layout applies.</param>
    /// <returns>The checks in report order. Empty when the override is not in use.</returns>
    /// <remarks>
    /// Without an installed prompt file there is nothing to check, so a repository that never opted in sees no
    /// new output. In <see cref="DoctorMode.Ensure"/> every failure becomes a warning that passes, as the other
    /// doctor checks do for a repository that opted out of stopping.
    /// </remarks>
    public static IReadOnlyList<DoctorCheck> Run(DoctorMode mode, string homeDirectory, string? appDataDirectory, OSPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);

        string promptPath = CopilotPromptFile.InstalledPath(homeDirectory);
        if (!File.Exists(promptPath))
            return [];

        List<DoctorCheck> checks = [];
        bool anyFlavourPoints = false;
        foreach (VsCodeFlavour flavour in Enum.GetValues<VsCodeFlavour>())
        {
            string settingsFile = VsCodeUserSettingsPaths.SettingsFile(flavour, homeDirectory, appDataDirectory, platform);
            CopilotOverrideState? state = ReadState(settingsFile);
            if (state?.StarPromptFile is null || !string.Equals(state.StarPromptFile, promptPath, StringComparison.Ordinal))
                continue;

            anyFlavourPoints = true;
            string label = flavour == VsCodeFlavour.Insiders ? "insiders" : "stable";
            string userData = VsCodeUserSettingsPaths.UserDataDirectory(flavour, homeDirectory, appDataDirectory, platform);
            checks.Add(new DoctorCheck($"copilot.prompt.{label}.setting", true, null, null));
            checks.Add(FileCheck(label, promptPath));
            checks.Add(LogCheck(label, userData));
        }

        if (!anyFlavourPoints)
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

    private static CopilotOverrideState? ReadState(string settingsFile)
    {
        if (!File.Exists(settingsFile))
            return null;

        try
        {
            return CopilotOverrideSettings.Read(File.ReadAllBytes(settingsFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Checks that the installed prompt file is readable and valid YAML with the allowed keys.</summary>
    private static DoctorCheck FileCheck(string label, string promptPath)
    {
        string id = $"copilot.prompt.{label}.file";
        string text;
        try
        {
            text = File.ReadAllText(promptPath);
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
    private static DoctorCheck LogCheck(string label, string userDataDirectory)
    {
        string id = $"copilot.prompt.{label}.log";
        try
        {
            string? newest = NewestLogFolder(Path.Combine(userDataDirectory, LogFolderName));
            if (newest is null)
                return Inconclusive(id, "no AgentHost log folder was found, so the prompt in use is not proven");

            string? mode = LastResolvedMode(AgentHostLogs(newest));
            if (mode is null)
                return Inconclusive(id, "the newest log has no 'Resolved system message' line yet");

            return mode == ReplaceMode
                ? new DoctorCheck(id, true, null, null)
                : new DoctorCheck(id, false, $"the newest log's last system message is {mode}, not {ReplaceMode}; restart VS Code and run kyber-weave doctor again", "kyber-weave doctor");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Inconclusive(id, "the AgentHost log could not be read");
        }
    }

    /// <summary>The timestamp folder with the greatest name. The names sort chronologically, so the newest wins.</summary>
    private static string? NewestLogFolder(string logsRoot)
    {
        if (!Directory.Exists(logsRoot))
            return null;

        string? newest = null;
        foreach (FileSystemInfo entry in new DirectoryInfo(logsRoot).EnumerateFileSystemInfos("*", OneLevel))
        {
            // A link is skipped, not followed: the log root must not send the probe outside the user-data directory.
            if (entry is not DirectoryInfo || entry.LinkTarget is not null)
                continue;
            if (newest is null || string.CompareOrdinal(entry.Name, newest) > 0)
                newest = entry.Name;
        }

        return newest is null ? null : Path.Combine(logsRoot, newest);
    }

    /// <summary>Every <c>agenthost*.log</c> under the folder, at any depth, in path order. Links are not followed.</summary>
    private static List<string> AgentHostLogs(string root)
    {
        List<string> found = [];
        Stack<(string Folder, int Depth)> pending = new();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (string folder, int depth) = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", OneLevel))
            {
                if (entry.LinkTarget is not null)
                    continue;
                if (entry is DirectoryInfo)
                {
                    if (depth < MaximumLogDepth)
                        pending.Push((entry.FullName, depth + 1));
                }
                else if (IsAgentHostLog(entry.Name))
                {
                    found.Add(entry.FullName);
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool IsAgentHostLog(string name) =>
        name.StartsWith("agenthost", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>mode=</c> token of the last matching line across the files, or null when none matches.</summary>
    private static string? LastResolvedMode(IEnumerable<string> logFiles)
    {
        string? mode = null;
        foreach (string file in logFiles)
        {
            foreach (string line in File.ReadLines(file))
            {
                if (!line.Contains("Resolved system message:", StringComparison.Ordinal))
                    continue;
                Match match = ResolvedModeLine().Match(line);
                if (match.Success)
                    mode = match.Groups["mode"].Value;
            }
        }

        return mode;
    }

    [GeneratedRegex(@"Resolved system message: (?<mode>mode=\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ResolvedModeLine();
}
