using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What kind of gap a wiring finding reports.</summary>
public enum McpWiringKind
{
    /// <summary>The harness is in play and does not configure the declared server.</summary>
    NotWired,

    /// <summary>The harness configures the server, but with a different launch command.</summary>
    CommandDiffers,

    /// <summary>The harness configures the server, but its launch binary is not resolvable.</summary>
    BinaryAbsent,

    /// <summary>The harness is not in play, or has no file in the tree to check.</summary>
    NotDetectable
}

/// <summary>Whether the repair can close a wiring gap.</summary>
public enum McpWiringFixability
{
    /// <summary>Kyber-Weave may write this harness's repository-scope configuration.</summary>
    Fixable,

    /// <summary>
    /// Kyber-Weave never writes this harness's configuration: the entry body is unverified, or
    /// there is no file to write. The gap is reported with the snippet to paste instead.
    /// </summary>
    NotFixable
}

/// <summary>One gap between a declared MCP server and a harness's repository-scope wiring.</summary>
/// <param name="Kind">What kind of gap this is.</param>
/// <param name="Severity">
/// The declared server's level: <c>required</c> warns, <c>recommended</c> informs. A
/// not-detectable line is always informational, whatever the level.
/// </param>
/// <param name="Harness">The harness slug.</param>
/// <param name="Server">
/// The server name as the declaration keys it. Empty on a not-detectable line: that line is about
/// the harness, and one is emitted per harness however many servers are declared.
/// </param>
/// <param name="Path">
/// The repository-relative configuration file the finding is about, or null when the harness has
/// no file in the tree.
/// </param>
/// <param name="Code">The stable <c>KW-*</c> rule id this finding carries.</param>
/// <param name="Message">One sentence naming the harness, file, server and gap.</param>
/// <param name="Hint">The remedy, as the command to run — not a description of one.</param>
/// <param name="Fixable">Whether the repair can close it.</param>
public sealed record McpWiringFinding(
    McpWiringKind Kind,
    Severity Severity,
    string Harness,
    string Server,
    string? Path,
    string Code,
    string Message,
    string Hint,
    McpWiringFixability Fixable);

/// <summary>Every finding one inspection produced, in report order.</summary>
/// <param name="Findings">
/// Ordered by harness in catalog order, then by server name (ordinal), so the same tree yields the
/// same report on every run and a diff of two reports means something.
/// </param>
public sealed record McpWiringReport(IReadOnlyList<McpWiringFinding> Findings);

/// <summary>
/// Checks whether each server a repository declares reaches the harnesses it is in play for, and
/// whether it would launch if it did.
/// </summary>
/// <remarks>
/// <para>
/// The check reads the repository tree and nothing else. No user-scope configuration under the
/// user's home directory is opened, for any harness, and nothing is written: this runs inside
/// <c>docs init</c>, whose writes are the ones scaffolding asked for. A repaired file appearing
/// from a check would be an unwritten consent.
/// </para>
/// <para>
/// Two properties are the reason it exists. The first is honesty about detection — a harness with
/// neither a configuration file nor a marker is reported at <c>info</c> with the marker that was
/// looked for, never as a warning, so a repository is not failed for a harness nobody uses. The
/// second is that an uncertain read never resolves toward "wired": a Codex document the scanner
/// refuses, or an entry naming a binary that does not resolve, is a gap rather than a pass.
/// </para>
/// </remarks>
public static class McpWiringInspector
{
    /// <summary>A declared server is not wired for an in-play harness.</summary>
    public const string NotWiredCode = "KW-MCP-WIRE-001";

    /// <summary>A wired entry's launch command differs from the declaration.</summary>
    public const string CommandDiffersCode = "KW-MCP-WIRE-002";

    /// <summary>A wired entry's launch binary is not resolvable on the search path.</summary>
    public const string BinaryAbsentCode = "KW-MCP-WIRE-003";

    /// <summary>The harness is not in play, or cannot be detected at all.</summary>
    public const string NotDetectableCode = "KW-MCP-WIRE-004";

    /// <summary>The marker that identifies a git worktree, walked to as ZCode does.</summary>
    private const string WorktreeMarker = ".git";

    private const string InstallRepairCommand = "kyber-weave update";

    private const string WireCommand = "kyber-weave docs mcp-wire --target";

    /// <summary>Inspects a repository against this process's <c>PATH</c>.</summary>
    /// <param name="repositoryRoot">The repository whose repository-scope files are read.</param>
    /// <param name="config">The servers the repository declares.</param>
    public static McpWiringReport Inspect(string repositoryRoot, McpConfig config) =>
        Inspect(repositoryRoot, config, LaunchCommandResolver.FromEnvironment());

    /// <summary>Inspects a repository against an injected search path.</summary>
    /// <param name="repositoryRoot">The repository whose repository-scope files are read.</param>
    /// <param name="config">The servers the repository declares.</param>
    /// <param name="resolver">Resolves a launch command to a file, without launching it.</param>
    public static McpWiringReport Inspect(
        string repositoryRoot,
        McpConfig config,
        LaunchCommandResolver resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resolver);

        // No declaration, no report: a repository that declares no servers has nothing to check,
        // and printing one "not detectable" line per harness for it would be pure noise.
        if (config.RequiredServers.Count == 0)
            return new McpWiringReport([]);

        string root = Path.GetFullPath(repositoryRoot);

        List<RequiredMcpServer> declared =
        [
            .. config.RequiredServers.Values.OrderBy(server => server.Name, StringComparer.Ordinal)
        ];

        List<McpWiringFinding> findings = [];
        foreach (HarnessMcpEntry entry in HarnessMcpCatalog.Rows)
            AddHarnessFindings(findings, root, entry, declared, resolver);

        return new McpWiringReport(findings);
    }

    private static void AddHarnessFindings(
        List<McpWiringFinding> findings,
        string root,
        HarnessMcpEntry entry,
        IReadOnlyList<RequiredMcpServer> declared,
        LaunchCommandResolver resolver)
    {
        if (entry.Shape == McpConfigurationShape.RepositorySettings)
        {
            findings.Add(NotDetectableFinding(entry));
            return;
        }

        IReadOnlyList<ConfiguredFile> files = ConfiguredFiles(root, entry);
        bool markerPresent = entry.MarkerDirectories.Any(
            marker => Directory.Exists(Path.Combine(root, marker)));

        if (files.Count == 0 && !markerPresent)
        {
            findings.Add(NotDetectableFinding(entry));
            return;
        }

        string namedPath = NamedPath(entry, files);

        foreach (RequiredMcpServer server in declared)
        {
            McpWiringFinding? finding = InspectServer(entry, server, files, namedPath, resolver);
            if (finding is not null)
                findings.Add(finding);
        }
    }

    private static McpWiringFinding? InspectServer(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        IReadOnlyList<ConfiguredFile> files,
        string namedPath,
        LaunchCommandResolver resolver)
    {
        Detection detection = Detect(entry, server, files);

        if (detection.Wired &&
            detection.LaunchCommand is { Count: > 0 } wiredCommand &&
            !wiredCommand.SequenceEqual(server.Command, StringComparer.Ordinal))
        {
            return new McpWiringFinding(
                McpWiringKind.CommandDiffers,
                SeverityFor(server),
                entry.Harness,
                server.Name,
                namedPath,
                CommandDiffersCode,
                $"server '{server.Name}' is wired in {namedPath} with a different launch command: declared "
                    + $"'{Describe(server.Command)}', wired '{Describe(wiredCommand)}'",
                CommandDiffersHint(entry, server, namedPath, wiredCommand),
                entry.Fixability);
        }

        // What a launch would actually run: the wired entry's executable when its body could be
        // read, the declaration's otherwise. A Codex or ZCode entry is name-only, so the
        // declaration is the only command the check can honestly test.
        IReadOnlyList<string> launch = detection.LaunchCommand ?? server.Command;
        string? executable = launch.Count > 0 ? launch[0] : null;

        // An absent binary is reported whether or not the server is wired. Pointing an operator
        // at a repair command that would refuse to write — because the entry could not launch —
        // hides the one fact they need: the binary is missing, and here is how to install it.
        if (!string.IsNullOrWhiteSpace(executable) && resolver.Resolve(executable) is null)
        {
            return new McpWiringFinding(
                McpWiringKind.BinaryAbsent,
                SeverityFor(server),
                entry.Harness,
                server.Name,
                namedPath,
                BinaryAbsentCode,
                detection.Wired
                    ? $"server '{server.Name}' is wired in {namedPath} but '{executable}' is not on PATH"
                    : $"server '{server.Name}' is not wired ({namedPath}) and '{executable}' is not on PATH",
                BinaryAbsentHint(server, executable),
                McpWiringFixability.NotFixable);
        }

        if (detection.Wired)
            return null;

        return new McpWiringFinding(
            McpWiringKind.NotWired,
            SeverityFor(server),
            entry.Harness,
            server.Name,
            namedPath,
            NotWiredCode,
            NotWiredMessage(server, namedPath, detection),
            NotWiredHint(entry, server, namedPath),
            entry.Fixability);
    }

    /// <summary>
    /// Reads one harness's configured files and answers whether the server is wired, and — when
    /// the entry body can be read — with which launch command.
    /// </summary>
    private static Detection Detect(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        IReadOnlyList<ConfiguredFile> files)
    {
        string? refusal = null;

        foreach (ConfiguredFile file in files)
        {
            switch (entry.Shape)
            {
                case McpConfigurationShape.Toml:
                    string? tomText = ReadText(file.AbsolutePath);
                    if (tomText is null)
                    {
                        refusal = $"{file.RelativePath} could not be read";
                        continue;
                    }

                    TomlScanReport scan = TomlTableScanner.Scan(tomText);
                    if (scan.Status == TomlScanStatus.Refused)
                    {
                        refusal = scan.Reason;
                        continue;
                    }

                    if (scan.Servers.Contains(server.Name, StringComparer.Ordinal))
                        return new Detection(true, null, null);

                    continue;

                case McpConfigurationShape.Json:
                    using (JsonDocument? document = ReadJson(file.AbsolutePath))
                    {
                        if (document is null)
                        {
                            refusal = $"{file.RelativePath} could not be read as JSON";
                            continue;
                        }

                        if (!TryFindEntry(document.RootElement, entry.ServerKeyPath, server.Name, out JsonElement wired))
                            continue;

                        return new Detection(
                            true,
                            ReadLaunchCommand(wired, entry.CommandShape),
                            null);
                    }

                default:
                    continue;
            }
        }

        return new Detection(false, null, refusal);
    }

    /// <summary>Reads the servers a JSON document declares at a key path.</summary>
    private static bool TryFindEntry(
        JsonElement root,
        IReadOnlyList<string> keyPath,
        string name,
        out JsonElement entry)
    {
        entry = default;
        JsonElement current = root;

        foreach (string key in keyPath)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(key, out current) ||
                current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }

        return current.TryGetProperty(name, out entry);
    }

    /// <summary>
    /// The launch command a wired entry names, or null when this catalog row does not pin the
    /// entry body. Null means "compare by name only", never "no command".
    /// </summary>
    private static IReadOnlyList<string>? ReadLaunchCommand(JsonElement entry, McpEntryCommandShape shape)
    {
        if (shape == McpEntryCommandShape.Undetermined || entry.ValueKind != JsonValueKind.Object)
            return null;

        if (!entry.TryGetProperty("command", out JsonElement command))
            return null;

        List<string> commandLine = [];

        if (shape == McpEntryCommandShape.SingleArray)
        {
            if (command.ValueKind == JsonValueKind.String)
            {
                commandLine.Add(command.GetString() ?? string.Empty);
                return commandLine;
            }

            if (command.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement item in command.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return null;

                commandLine.Add(item.GetString() ?? string.Empty);
            }

            return commandLine;
        }

        if (command.ValueKind != JsonValueKind.String)
            return null;

        commandLine.Add(command.GetString() ?? string.Empty);

        if (entry.TryGetProperty("args", out JsonElement args))
        {
            if (args.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement argument in args.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String)
                    return null;

                commandLine.Add(argument.GetString() ?? string.Empty);
            }
        }

        return commandLine;
    }

    /// <summary>
    /// The files this harness actually has. Every row but ZCode has fixed repository-relative
    /// paths; ZCode's project tiers are walked as ZCode walks them — from the working directory up
    /// to the git worktree root, and no further, with the user tier never touched.
    /// </summary>
    private static IReadOnlyList<ConfiguredFile> ConfiguredFiles(string root, HarnessMcpEntry entry)
    {
        List<ConfiguredFile> files = [];

        if (entry.WalksToWorktreeRoot)
        {
            foreach (string directory in ZCodeProjectDirectories(root))
            {
                foreach (string relativePath in entry.Paths)
                {
                    string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                        files.Add(new ConfiguredFile(path, RelativeTo(root, path)));
                }
            }

            return files;
        }

        foreach (string relativePath in entry.Paths)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                files.Add(new ConfiguredFile(path, relativePath));
        }

        return files;
    }

    /// <summary>
    /// Walks from the repository root up to the git worktree root, mirroring the verified
    /// <c>ZCodeMcpConfiguration</c> semantics: with no worktree marker anywhere above, the walk
    /// contributes the starting directory alone rather than every ancestor.
    /// </summary>
    private static IReadOnlyList<string> ZCodeProjectDirectories(string root)
    {
        List<string> directories = [];
        string current = root;

        while (true)
        {
            directories.Add(current);

            string marker = Path.Combine(current, WorktreeMarker);
            if (Directory.Exists(marker) || File.Exists(marker))
                return directories;

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                return [root];

            current = parent;
        }
    }

    /// <summary>
    /// The file a finding names: the first that exists, else the harness's canonical one. A marker
    /// alone still puts a harness in play with the server unwired, so the report has to name the
    /// file the operator would write even when it is not there yet.
    /// </summary>
    private static string NamedPath(HarnessMcpEntry entry, IReadOnlyList<ConfiguredFile> files)
    {
        foreach (ConfiguredFile file in files)
        {
            if (!Path.IsPathRooted(file.RelativePath) &&
                !file.RelativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return file.RelativePath;
            }
        }

        return entry.Paths.Count > 0 ? entry.Paths[0] : entry.InPlaySignal;
    }

    private static McpWiringFinding NotDetectableFinding(HarnessMcpEntry entry) =>
        new(
            McpWiringKind.NotDetectable,
            Severity.Info,
            entry.Harness,
            string.Empty,
            null,
            NotDetectableCode,
            NotDetectableMessage(entry),
            NotDetectableHint(entry),
            McpWiringFixability.NotFixable);

    private static string NotDetectableMessage(HarnessMcpEntry entry)
    {
        if (entry.Shape == McpConfigurationShape.RepositorySettings)
            return "not checked (its configuration is repository settings, not a file in the tree)";

        string file = entry.Paths.Count > 0 ? entry.Paths[0] : entry.InPlaySignal;

        return entry.MarkerDirectories.Count == 0
            ? $"not checked (no '{file}' and no marker to detect it)"
            : $"not checked (no '{entry.InPlaySignal}' and no '{file}')";
    }

    private static string NotDetectableHint(HarnessMcpEntry entry)
    {
        if (entry.Shape == McpConfigurationShape.RepositorySettings)
        {
            return "Add the server to the repository's Copilot MCP configuration under 'mcpServers' "
                + "(type and tools are required) and add the install step to "
                + ".github/workflows/copilot-setup-steps.yml: the coding agent reads MCP "
                + "configuration from repository settings only, and installs nothing without that step.";
        }

        if (entry.Fixability == McpWiringFixability.Fixable)
            return $"No action unless this repository uses {entry.Harness}; if it does, run: {WireCommand} {entry.Harness}";

        string file = entry.Paths.Count > 0 ? entry.Paths[0] : entry.InPlaySignal;
        return $"No action unless this repository uses {entry.Harness}; if it does, add the server to {file} by hand.";
    }

    private static string NotWiredMessage(
        RequiredMcpServer server,
        string namedPath,
        Detection detection) =>
        detection.Refusal is null
            ? $"server '{server.Name}' is not wired ({namedPath})"
            : $"server '{server.Name}' is not wired ({namedPath}): {detection.Refusal}";

    private static string NotWiredHint(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        string namedPath)
    {
        if (entry.Fixability == McpWiringFixability.Fixable)
            return $"Fix: {WireCommand} {entry.Harness}";

        string snippet = entry.Shape == McpConfigurationShape.Toml
            ? $"[{string.Join('.', entry.ServerKeyPath)}.{server.Name}] with command and args"
            : $"\"{server.Name}\" under {string.Join('.', entry.ServerKeyPath)}";

        return $"Kyber-Weave does not write {entry.Harness} configuration yet; add {snippet} to "
            + $"{namedPath} by hand.";
    }

    private static string CommandDiffersHint(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        string namedPath,
        IReadOnlyList<string> wiredCommand) =>
        entry.Fixability == McpWiringFixability.Fixable
            ? $"Fix: {WireCommand} {entry.Harness} (it refuses an entry that already exists; "
                + $"remove the existing one or edit {namedPath} to '{Describe(server.Command)}')"
            : $"Edit {namedPath} so the entry runs '{Describe(server.Command)}' rather than "
                + $"'{Describe(wiredCommand)}'; Kyber-Weave does not write {entry.Harness} configuration.";

    private static string BinaryAbsentHint(RequiredMcpServer server, string executable)
    {
        if (server.Provenance == McpProvenance.FirstParty)
            return $"This Kyber-Weave installation is broken; run: {InstallRepairCommand}";

        return string.IsNullOrWhiteSpace(server.Install)
            ? $"Install '{executable}' yourself and make sure it is on PATH; Kyber-Weave never installs external binaries."
            : $"Install it yourself; Kyber-Weave never installs external binaries: {server.Install}";
    }

    private static Severity SeverityFor(RequiredMcpServer server) =>
        server.Level == McpRequirementLevel.Required ? Severity.Warning : Severity.Info;

    private static string Describe(IReadOnlyList<string> command) => string.Join(' ', command);

    private static string RelativeTo(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Reads a file's text, or null when it is absent or unreadable. An unreadable file is not an
    /// exception here: the check reports a gap, and one permission error in a repository's tree
    /// must not abort a <c>docs init</c>.
    /// </summary>
    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a JSON configuration leniently — comments and trailing commas are what editors leave
    /// behind — and returns null when it cannot be read at all.
    /// </summary>
    private static JsonDocument? ReadJson(string path)
    {
        string? text = ReadText(path);
        if (text is null)
            return null;

        try
        {
            return JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What reading one harness's files established about one server.</summary>
    /// <param name="Wired">Whether the harness configures the server.</param>
    /// <param name="LaunchCommand">
    /// The command the wired entry names, or null when the entry body is not pinned or unreadable.
    /// </param>
    /// <param name="Refusal">Why a document could not be classified, when one could not.</param>
    private readonly record struct Detection(
        bool Wired,
        IReadOnlyList<string>? LaunchCommand,
        string? Refusal);

    /// <summary>A configuration file that exists: where to read it, and how to name it.</summary>
    private readonly record struct ConfiguredFile(string AbsolutePath, string RelativePath);
}
