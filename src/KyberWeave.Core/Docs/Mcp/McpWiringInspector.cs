using System.Text;
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
    /// <remarks>
    /// The root is passed to the resolver as well as to the tree walk, so a declared launch command
    /// spelled as a relative path — <c>./bin/kyber-weave-mcp</c>, which is how a repository points at
    /// the binary it builds — resolves against the repository being inspected. Resolving it against
    /// the working directory instead made the answer depend on where the operator stood when they ran
    /// the command: the same tree was wired from the repository root and unwired from anywhere else.
    /// </remarks>
    public static McpWiringReport Inspect(string repositoryRoot, McpConfig config) =>
        Inspect(repositoryRoot, config, LaunchCommandResolver.FromEnvironment(repositoryRoot));

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

        string namedPath = NamedPath(root, entry, files);

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
            !MatchesDeclaredCommand(entry, wiredCommand, server.Command))
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

                // Not fixable, whatever the harness's row says: the difference is an entry that
                // already exists, and 5.5 forbids the repair from modifying, merging, or overwriting
                // one. Offering it would ask the operator to consent to a write that is guaranteed
                // to come back as KW-MCP-WIRE-005.
                McpWiringFixability.NotFixable);
        }

        // What a launch would actually run: the wired entry's executable when the check could read
        // it, the declaration's otherwise. A ZCode entry's body is pinned nowhere in this repository,
        // so for that harness the declaration is the only command the check can honestly test.
        string? executable = detection.Executable
            ?? (server.Command.Count > 0 ? server.Command[0] : null);

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

        // A configuration this check refused to read is not one the repair may write either — it
        // refuses a link, and a file it may not inspect, for the same reason — so neither can be
        // offered as a fixable gap. An entry that exists but names no launch command is refused for
        // the same reason: 5.5 forbids overwriting an entry, so a repair offered here would come back
        // as KW-MCP-WIRE-005.
        McpLinkObstruction obstruction = detection.Obstruction.Obstruction;
        bool writable = obstruction == McpLinkObstruction.None && !detection.EntryUnlaunchable;

        string hint = obstruction switch
        {
            McpLinkObstruction.Link => LinkedHint(namedPath, detection.Obstruction.Segment!),
            McpLinkObstruction.Denied => DeniedHint(namedPath),
            _ => detection.EntryUnlaunchable
                ? UnlaunchableHint(entry, server, namedPath)
                : NotWiredHint(entry, server, namedPath)
        };

        return new McpWiringFinding(
            McpWiringKind.NotWired,
            SeverityFor(server),
            entry.Harness,
            server.Name,
            namedPath,
            NotWiredCode,
            NotWiredMessage(server, namedPath, detection),
            hint,
            writable ? entry.Fixability : McpWiringFixability.NotFixable);
    }

    /// <summary>
    /// Whether a wired launch command is the one declared, allowing the harness's own spelling of
    /// the repository root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repair substitutes the harness's documented root token when it composes an entry (5.11),
    /// so <c>${workspaceFolder}</c> in a VS Code entry and the declaration's <c>.</c> name the same
    /// root and have to compare equal. Comparing them as raw text reported a difference against the
    /// file the repair had written a moment earlier, which turned one <c>ok</c> line into a warning
    /// (3.5) and made a second <c>mcp-wire</c> refuse its own success (<c>KW-MCP-WIRE-005</c>).
    /// </para>
    /// <para>
    /// The tolerance is exactly one token: the declaration's repository-root token may be spelled as
    /// <em>that harness's</em> token. It cannot be normalised globally, because
    /// <c>${workspaceFolder}</c> in a Claude Code entry is not a root spelling at all — that harness
    /// substitutes nothing, and an agent reading it would be pointed at a directory literally named
    /// <c>${workspaceFolder}</c>. Everything else compares exactly.
    /// </para>
    /// </remarks>
    private static bool MatchesDeclaredCommand(
        HarnessMcpEntry entry,
        IReadOnlyList<string> wired,
        IReadOnlyList<string> declared)
    {
        if (wired.Count != declared.Count)
            return false;

        for (int index = 0; index < declared.Count; index++)
        {
            if (!TokenMatches(entry, declared[index], wired[index]))
                return false;
        }

        return true;
    }

    private static bool TokenMatches(HarnessMcpEntry entry, string declared, string wired) =>
        string.Equals(declared, wired, StringComparison.Ordinal)
        || (string.Equals(declared, JsonServerSplice.RepositoryRootToken, StringComparison.Ordinal)
            && string.Equals(wired, entry.RepositoryRootToken, StringComparison.Ordinal));

    /// <summary>
    /// Reads one harness's configured files and answers whether the server is wired, and — when
    /// the entry body can be read — with which launch command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every uncertainty resolves toward a gap. A name is not a launch: an entry whose body cannot
    /// be read, or which names no command at all, is reported as unwired with the reason, because
    /// that is the only answer that cannot confirm a configuration no harness could start.
    /// </para>
    /// <para>
    /// A harness can have more than one configured file, and each one that cannot be read is kept
    /// rather than replacing the last: reporting only the final problem names one obstacle out of
    /// however many stand between the operator and a working configuration.
    /// </para>
    /// </remarks>
    private static Detection Detect(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        IReadOnlyList<ConfiguredFile> files)
    {
        List<string> refusals = [];
        McpLinkProbe obstruction = default;

        foreach (ConfiguredFile file in files)
        {
            // A configuration the guard stopped at is not read through: requirement 2.6 confines the
            // check to the repository, and reading one would report a teammate's own user-scope file
            // as this repository's wiring — a confirming pass for a configuration a fresh checkout
            // lacks. A file this process may not inspect is refused for the second half of the same
            // reason: "denied" and "absent" are opposite answers, and only one of them may be read as
            // "this harness has nothing to say".
            if (file.Obstruction.Obstruction != McpLinkObstruction.None)
            {
                refusals.Add(
                    $"{McpConfigurationLink.Describe(file.RelativePath, file.Obstruction)}, so it was not read"
                        + (file.Obstruction.Obstruction == McpLinkObstruction.Denied
                            ? ": a file this process cannot inspect cannot be told from one that is absent"
                            : string.Empty));

                if (obstruction.Obstruction == McpLinkObstruction.None)
                    obstruction = file.Obstruction;

                continue;
            }

            switch (entry.Shape)
            {
                case McpConfigurationShape.Toml:
                    string? tomText = ReadText(file.AbsolutePath);
                    if (tomText is null)
                    {
                        refusals.Add($"{file.RelativePath} could not be read");
                        continue;
                    }

                    TomlScanReport scan = TomlTableScanner.Scan(tomText);
                    if (scan.Status == TomlScanStatus.Refused)
                    {
                        refusals.Add(scan.Reason ?? $"{file.RelativePath} could not be classified");
                        continue;
                    }

                    if (!scan.Servers.Contains(server.Name, StringComparer.Ordinal))
                        continue;

                    // The scanner answers which tables a document declares and nothing about their
                    // bodies, so a header alone says only that the name is there. What the check has
                    // to know is whether a launch is there, and that is read from the table.
                    return FromEntryBody(entry, server, ReadTomlEntryBody(tomText, entry.ServerKeyPath, server.Name));

                case McpConfigurationShape.Json:
                    using (JsonDocument? document = ReadJson(file.AbsolutePath))
                    {
                        if (document is null)
                        {
                            refusals.Add($"{file.RelativePath} could not be read as JSON");
                            continue;
                        }

                        if (!TryFindEntry(document.RootElement, entry.ServerKeyPath, server.Name, out JsonElement wired))
                            continue;

                        return FromEntryBody(entry, server, ReadEntryBody(wired, entry.CommandShape));
                    }

                default:
                    // Every shape the catalog can hold is named above, so this is a member a later
                    // change added without a reader here. It refuses for the same reason the
                    // scanner does: an unreadable configuration is a gap, never a pass and never a
                    // silent "not wired" with nothing to act on.
                    refusals.Add($"{file.RelativePath} was not read: this build has no reader for its configuration shape");
                    continue;
            }
        }

        return new Detection(
            false,
            null,
            refusals.Count == 0 ? null : string.Join("; ", refusals),
            obstruction);
    }

    /// <summary>
    /// The detection one entry body yields: a launch to compare, an identity-only match, or the gap
    /// an entry that names no launch command is.
    /// </summary>
    /// <remarks>
    /// A row whose <see cref="HarnessMcpEntry.CommandShape"/> is
    /// <see cref="McpEntryCommandShape.Undetermined"/> keeps only the executable: the catalog records
    /// that harness's entry body as pinned nowhere in this repository, so comparing an argv read
    /// here against the declaration would report a difference nobody verified the meaning of. The
    /// executable is enough to answer the question the body is read for — whether the binary a
    /// launch would run is there.
    /// </remarks>
    private static Detection FromEntryBody(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        EntryBody body) =>
        body.Reading switch
        {
            McpEntryReading.Readable => new Detection(
                true,
                entry.CommandShape == McpEntryCommandShape.Undetermined ? null : body.Tokens,
                null,
                Executable: body.Tokens[0]),
            McpEntryReading.Unpinned => new Detection(true, null, null),

            // The entry exists but cannot start anything. It stays a KW-MCP-WIRE-001 gap and not a
            // difference, because there is no command to compare with the declaration: the entry
            // names none, which is the whole problem the operator has to fix.
            _ => new Detection(
                false,
                null,
                body.Reading == McpEntryReading.EmptyCommand
                    ? $"its '{server.Name}' entry names an empty launch command"
                    : $"its '{server.Name}' entry names a launch command that could not be read",
                EntryUnlaunchable: true)
        };

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
    /// Reads the launch command a JSON entry body names, or reports why it names none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null used to mean two different things here — "this catalog row does not pin the entry body"
    /// and "the body is not the shape it should be" — and the caller treated both as "compare by
    /// name only", so an entry with a body nobody could read was reported as wired on the strength
    /// of its name. The two are separated because they answer different questions: one is a
    /// deliberate identity comparison for a body pinned nowhere, the other is a gap.
    /// </para>
    /// <para>
    /// The same rule covers an entry that reads cleanly and still names nothing: <c>command: []</c>
    /// parses, and a launch of it starts nothing.
    /// </para>
    /// </remarks>
    private static EntryBody ReadEntryBody(JsonElement entry, McpEntryCommandShape shape)
    {
        if (shape == McpEntryCommandShape.Undetermined)
            return EntryBody.Unpinned;

        if (shape is not (McpEntryCommandShape.CommandWithArgs or McpEntryCommandShape.SingleArray))
            return EntryBody.Unreadable;

        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("command", out JsonElement command))
            return EntryBody.Unreadable;

        List<string> commandLine = [];

        if (shape == McpEntryCommandShape.SingleArray)
        {
            if (command.ValueKind == JsonValueKind.String)
            {
                commandLine.Add(command.GetString() ?? string.Empty);
                return EntryBody.FromCommandLine(commandLine);
            }

            if (command.ValueKind != JsonValueKind.Array)
                return EntryBody.Unreadable;

            foreach (JsonElement item in command.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return EntryBody.Unreadable;

                commandLine.Add(item.GetString() ?? string.Empty);
            }

            return EntryBody.FromCommandLine(commandLine);
        }

        if (command.ValueKind != JsonValueKind.String)
            return EntryBody.Unreadable;

        commandLine.Add(command.GetString() ?? string.Empty);

        if (entry.TryGetProperty("args", out JsonElement args))
        {
            if (args.ValueKind != JsonValueKind.Array)
                return EntryBody.Unreadable;

            foreach (JsonElement argument in args.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String)
                    return EntryBody.Unreadable;

                commandLine.Add(argument.GetString() ?? string.Empty);
            }
        }

        return EntryBody.FromCommandLine(commandLine);
    }

    /// <summary>
    /// The launch command a Codex server table declares, read from a document the scanner has
    /// already classified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TomlTableScanner"/> answers which <c>[mcp_servers.&lt;name&gt;]</c> tables a
    /// document declares, and nothing else. A header is a name, not a launch, so treating one as
    /// the entry's identity reports a table that names no command as wired — a confirming pass for
    /// a configuration the harness cannot start. That path is live even though the repair never
    /// writes Codex's file: the check reads what the repair declines to touch.
    /// </para>
    /// <para>
    /// This reads one value out of the table the scanner found, so it is not a second classifier:
    /// the scanner has already established that the table exists and that every string in the
    /// document is closed, and this only has to know where that table's region ends and what it
    /// names as its command. Anything it does not recognise answers an unreadable body, which the
    /// caller reports as a gap. Arguments are deliberately not read — they do not decide whether an
    /// entry can launch, and this repository pins no full argv for the harness to compare against.
    /// </para>
    /// </remarks>
    private static EntryBody ReadTomlEntryBody(
        string text,
        IReadOnlyList<string> tablePath,
        string serverName)
    {
        string table = string.Join('.', tablePath.Append(serverName));
        bool inTable = false;

        foreach (string raw in WithoutMultilineStrings(text).Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                inTable = NamesTable(line, table);
                continue;
            }

            if (!inTable)
                continue;

            int assignment = line.IndexOf('=', StringComparison.Ordinal);
            if (assignment < 0 ||
                !string.Equals(BareKey(line[..assignment]), "command", StringComparison.Ordinal))
            {
                continue;
            }

            return ReadTomlString(line[(assignment + 1)..]) is { } command
                ? EntryBody.FromCommandLine([command])
                : EntryBody.Unreadable;
        }

        return EntryBody.Unreadable;
    }

    /// <summary>
    /// The document with the contents of every multi-line string blanked, so a note cannot
    /// impersonate a table header or a command key.
    /// </summary>
    /// <remarks>
    /// Newlines are kept, so the document stays line-aligned with the scanner's reading of it. The
    /// delimiters are the ones the scanner uses, and a document reaching here has been classified by
    /// it, so this only has to agree about where prose is: one value reader and one classifier
    /// disagreeing about that is how a note becomes a launch command.
    /// </remarks>
    private static string WithoutMultilineStrings(string text)
    {
        StringBuilder builder = new(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            string? delimiter = TripleDelimiterAt(text, index);
            if (delimiter is null)
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            builder.Append(delimiter);
            index += delimiter.Length;

            int close = text.IndexOf(delimiter, index, StringComparison.Ordinal);
            int end = close < 0 ? text.Length : close;

            for (; index < end; index++)
                builder.Append(text[index] == '\n' ? '\n' : ' ');

            if (close >= 0)
            {
                builder.Append(delimiter);
                index = close + delimiter.Length;
            }
        }

        return builder.ToString();
    }

    /// <summary>The multi-line string delimiter starting at <paramref name="index"/>, when one does.</summary>
    private static string? TripleDelimiterAt(string text, int index)
    {
        ReadOnlySpan<char> remainder = text.AsSpan(index);

        if (remainder.StartsWith("\"\"\"", StringComparison.Ordinal))
            return "\"\"\"";

        return remainder.StartsWith("'''", StringComparison.Ordinal) ? "'''" : null;
    }

    /// <summary>
    /// Whether a header line names <paramref name="table"/>, or a sub-table of it.
    /// </summary>
    /// <remarks>
    /// Quoted keys and the whitespace TOML allows around them are removed before the comparison, so
    /// a spelling the scanner accepted compares equal here too — one classifier and one value reader
    /// disagreeing about which table they are looking at would report a working entry as a gap. A
    /// sub-table counts as inside the server's region — <c>[mcp_servers.x.env]</c> implicitly
    /// creates the server's table — which is the same reading the scanner takes.
    /// </remarks>
    private static bool NamesTable(string headerLine, string table)
    {
        string path = Normalize(StripComment(headerLine).TrimStart('[').TrimEnd(']'));

        return string.Equals(path, table, StringComparison.Ordinal)
            || path.StartsWith(table + ".", StringComparison.Ordinal);
    }

    /// <summary>One quoted TOML value, or null when the text is not a quoted string this reader knows.</summary>
    /// <remarks>
    /// A basic string (<c>"…"</c>) escapes with a backslash; a literal string (<c>'…'</c>) does not,
    /// so a backslash inside one is the character it looks like. A multi-line value answers an empty
    /// string — its contents were blanked as prose — and an empty command is the gap the caller
    /// reports, not a launch.
    /// </remarks>
    private static string? ReadTomlString(string text)
    {
        string value = text.TrimStart();

        if (value.Length < 2 || value[0] is not ('"' or '\''))
            return null;

        char quote = value[0];
        StringBuilder literal = new();

        for (int index = 1; index < value.Length; index++)
        {
            char current = value[index];

            if (quote == '"' && current == '\\' && index + 1 < value.Length)
            {
                index++;
                literal.Append(value[index]);
                continue;
            }

            if (current == quote)
                return literal.ToString();

            literal.Append(current);
        }

        return null;
    }

    /// <summary>A key as it compares: unquoted, without the whitespace TOML allows around it.</summary>
    private static string BareKey(string text) => Normalize(StripComment(text));

    /// <summary>The text before a <c>#</c> comment, which ends a TOML line.</summary>
    private static string StripComment(string text)
    {
        int comment = text.IndexOf('#', StringComparison.Ordinal);
        return comment < 0 ? text : text[..comment];
    }

    /// <summary>
    /// The text with the whitespace and quotes TOML allows around a key or a table path removed, so
    /// two spellings of one name compare equal.
    /// </summary>
    private static string Normalize(string text)
    {
        StringBuilder builder = new(text.Length);

        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character) && character is not ('"' or '\''))
                builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The files this harness actually has. Every row but ZCode has fixed repository-relative
    /// paths; ZCode's project tiers are walked as ZCode walks them — from the working directory up
    /// to the git worktree root, and no further, with the user tier never touched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file reached through a symbolic link is listed with the linked segment it was found behind
    /// rather than dropped: dropping it would report the harness as unwired and offer a write the
    /// repair refuses, when what the operator needs to hear is that a link stands between this
    /// repository and a file outside it.
    /// </para>
    /// <para>
    /// Nothing here reads a path the catalog names without asking <see cref="McpRepositoryPath"/>
    /// whether this repository owns it, because the read and the write have to agree about which file
    /// they are talking about: a row naming an absolute path would otherwise be read from wherever it
    /// pointed while the repair refused to write it. The ZCode walk is exempt on purpose — it is the
    /// one place a directory above the root is read deliberately, and it reports what it finds rather
    /// than writing there.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ConfiguredFile> ConfiguredFiles(string root, HarnessMcpEntry entry)
    {
        List<ConfiguredFile> files = [];

        if (entry.WalksToWorktreeRoot)
        {
            foreach (string directory in ZCodeProjectDirectories(root))
            {
                foreach (string relativePath in entry.Paths)
                {
                    if (!McpWiringReceipt.RepositoryRelative(relativePath))
                        continue;

                    string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    McpLinkProbe probe = McpConfigurationLink.Probe(root, path);
                    if (probe.Obstruction != McpLinkObstruction.None || File.Exists(path))
                        files.Add(new ConfiguredFile(path, RelativeTo(root, path), probe));
                }
            }

            return files;
        }

        foreach (string relativePath in entry.Paths)
        {
            if (McpRepositoryPath.Contained(root, relativePath) is not { } path)
                continue;

            // The guard is asked before the existence check, which follows it, and it answers three
            // states where File.Exists answers two: a link whose target is gone is still a link, and a
            // file this process may not reach still exists — File.Exists reports it as absent, and
            // "absent" is the answer that lets the repair create a file over one it never read.
            McpLinkProbe probe = McpConfigurationLink.Probe(root, path);
            if (probe.Obstruction != McpLinkObstruction.None || File.Exists(path))
                files.Add(new ConfiguredFile(path, relativePath, probe));
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
    /// The file a finding names: the first that this repository owns, else the harness's canonical
    /// one. A marker alone still puts a harness in play with the server unwired, so the report has to
    /// name the file the operator would write even when it is not there yet.
    /// </summary>
    /// <remarks>
    /// "This repository owns it" is <see cref="McpRepositoryPath"/>'s question rather than a prefix
    /// test kept here. A ZCode row walks to the worktree root, so a file it found can sit above the
    /// repository and be named with a parent reference — and the prefix test refused every name
    /// beginning with those two characters, including the ordinary <c>..foo/mcp.json</c> a real
    /// directory inside the tree produces.
    /// </remarks>
    private static string NamedPath(string root, HarnessMcpEntry entry, IReadOnlyList<ConfiguredFile> files)
    {
        foreach (ConfiguredFile file in files)
        {
            if (McpRepositoryPath.Contained(root, file.RelativePath) is not null)
                return file.RelativePath;
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

    /// <summary>
    /// The remedy for a configuration the check refused to read. It names no wire command: the
    /// repair refuses a link for the same reason, so the only route is to put a real file in the
    /// tree, and telling the operator to run a command that will refuse again is worse than silence.
    /// </summary>
    private static string LinkedHint(string namedPath, string linkedSegment) =>
        string.Equals(linkedSegment, namedPath, StringComparison.Ordinal)
            ? $"Replace {namedPath} with a real file inside the repository: copy its content out of the linked "
                + "file, delete the link, and leave the copy in place. Kyber-Weave reads and writes "
                + "repository-scope files only, because anything else is a configuration a fresh checkout does "
                + "not have."
            : $"Replace the symbolic link '{linkedSegment}' on the path to {namedPath} with a real directory inside "
                + "the repository: Kyber-Weave reads and writes repository-scope files only, because anything else "
                + "is a configuration a fresh checkout does not have.";

    /// <summary>
    /// The remedy for a configuration this process may not inspect: a permission, on the file or on a
    /// directory above it, and nothing else.
    /// </summary>
    /// <remarks>
    /// It names no wire command, for the reason <see cref="LinkedHint"/> names none: the repair
    /// refuses the same file for the same reason, so sending the operator to a command that will
    /// refuse again is worse than silence. The two remedies stay separate sentences because they are
    /// separate problems — a link is a file outside the repository, a denial is one inside it — and a
    /// single "cannot read this" would leave the operator to guess which they have.
    /// </remarks>
    private static string DeniedHint(string namedPath) =>
        $"Check the permissions on {namedPath} and on every directory above it, then run the check again. "
            + "A file this process may not inspect cannot be told from one that is absent, so the check reports "
            + "it as a gap rather than as a configuration this repository does not have.";

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

    /// <summary>
    /// The remedy for a wired entry whose launch command differs from the declaration: edit the
    /// named file so the entry runs the declared command.
    /// </summary>
    /// <remarks>
    /// The hand edit leads even on a harness the repair may write, because this finding is never
    /// offered as a repair — the difference is an entry that already exists, and 5.5 forbids the
    /// repair from modifying one. A hint that opened with the wire command would open with a
    /// command guaranteed to come back as <c>KW-MCP-WIRE-005</c>, so the wire command appears only
    /// as the caveat explaining why the operator has to make the edit themselves.
    /// </remarks>
    private static string CommandDiffersHint(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        string namedPath,
        IReadOnlyList<string> wiredCommand) =>
        entry.Fixability == McpWiringFixability.Fixable
            ? $"Edit {namedPath} so the entry runs '{Describe(server.Command)}' rather than "
                + $"'{Describe(wiredCommand)}', or delete the entry and re-add it by hand: "
                + $"{WireCommand} {entry.Harness} refuses an entry that already exists."
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

    /// <summary>
    /// The remedy for an entry that exists and names no launch command: the entry itself has to be
    /// corrected, and the wire command cannot do it.
    /// </summary>
    /// <remarks>
    /// This leads with the edit for the same reason a differing command does — the entry already
    /// exists, and 5.5 forbids the repair from modifying one, so running the wire command as it
    /// stands would come back as <c>KW-MCP-WIRE-005</c>. Deleting the entry first is named because
    /// that is the route back to a repair that works.
    /// </remarks>
    private static string UnlaunchableHint(
        HarnessMcpEntry entry,
        RequiredMcpServer server,
        string namedPath) =>
        entry.Fixability == McpWiringFixability.Fixable
            ? $"Edit the '{server.Name}' entry in {namedPath} so it runs '{Describe(server.Command)}', "
                + $"or delete the entry and re-add it by hand: {WireCommand} {entry.Harness} refuses "
                + "an entry that already exists."
            : $"Edit the '{server.Name}' entry in {namedPath} so it runs '{Describe(server.Command)}'; "
                + $"Kyber-Weave does not write {entry.Harness} configuration.";

    private static Severity SeverityFor(RequiredMcpServer server) =>
        server.Level == McpRequirementLevel.Required ? Severity.Warning : Severity.Info;

    /// <summary>
    /// A launch command as the report shows it: one token per word, quoted where a bare join would
    /// make two tokens read as one.
    /// </summary>
    /// <remarks>
    /// A bare space join renders <c>["my-tool", "a b"]</c> and <c>["my-tool", "a", "b"]</c> the same,
    /// and those two launch differently: an operator copying the hint into a command/args split
    /// would get the launch the report could not distinguish from the other one. A token needs no
    /// quoting unless it is empty or holds whitespace, which leaves the common case —
    /// <c>kyber-weave-mcp --repo-root .</c> — exactly as readable as it was.
    /// </remarks>
    private static string Describe(IReadOnlyList<string> command) => string.Join(' ', command.Select(Quote));

    /// <summary>One token of a command, quoted when a space would split it into two.</summary>
    private static string Quote(string token) =>
        token.Length > 0 && !token.Any(char.IsWhiteSpace)
            ? token
            : "\"" + token
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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
    /// <param name="Wired">Whether the harness configures the server with a launch the check can read.</param>
    /// <param name="LaunchCommand">
    /// The command the wired entry names, or null when the entry body is not pinned or unreadable.
    /// </param>
    /// <param name="Refusal">
    /// Why a document or an entry body could not be read — every reason, when more than one file
    /// could not be.
    /// </param>
    /// <param name="Obstruction">
    /// The first file the link guard would not read through, and why — a link, or a denial. It is
    /// carried rather than folded into <paramref name="Refusal"/> because it decides two further
    /// things the message cannot: whether a repair may be offered, and which remedy the hint names.
    /// Being unable to inspect a file and being unable to traverse to it have different fixes, and the
    /// repair names the same distinction for the same file.
    /// </param>
    /// <param name="Executable">
    /// The program a launch of the wired entry would run, when the check could read it. Kept apart
    /// from <paramref name="LaunchCommand"/> because an entry body can name its executable without a
    /// full argv the catalog pins far enough to compare.
    /// </param>
    /// <param name="EntryUnlaunchable">
    /// Whether an entry exists but names no launch command this check could read. That is a gap, not
    /// a pass, and it is not one a second entry can repair.
    /// </param>
    private readonly record struct Detection(
        bool Wired,
        IReadOnlyList<string>? LaunchCommand,
        string? Refusal,
        McpLinkProbe Obstruction = default,
        string? Executable = null,
        bool EntryUnlaunchable = false);

    /// <summary>What reading one entry body established about the launch it names.</summary>
    private enum McpEntryReading
    {
        /// <summary>The body names a command a launch could start.</summary>
        Readable,

        /// <summary>
        /// The catalog row does not pin the entry body, so only the name can be compared. That is
        /// not a gap: ZCode's body is pinned nowhere in this repository, and inventing a shape to
        /// compare would produce a finding about a format nobody verified.
        /// </summary>
        Unpinned,

        /// <summary>The body reads, and names no command — <c>command: []</c> starts nothing.</summary>
        EmptyCommand,

        /// <summary>The body is not a shape this reader understands.</summary>
        Unreadable
    }

    /// <summary>The launch command an entry body names, or why it names none.</summary>
    /// <param name="Reading">What reading the body established.</param>
    /// <param name="Tokens">The command's argv, empty unless <paramref name="Reading"/> is readable.</param>
    private readonly record struct EntryBody(McpEntryReading Reading, IReadOnlyList<string> Tokens)
    {
        /// <summary>A body the catalog row does not pin, so only its name is compared.</summary>
        public static EntryBody Unpinned { get; } = new(McpEntryReading.Unpinned, []);

        /// <summary>A body whose shape this reader does not understand.</summary>
        public static EntryBody Unreadable { get; } = new(McpEntryReading.Unreadable, []);

        /// <summary>
        /// A body's command, or the empty-command gap it is. A blank program is as unlaunchable as
        /// an empty list, and a launch of either starts nothing.
        /// </summary>
        public static EntryBody FromCommandLine(IReadOnlyList<string> tokens) =>
            tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0])
                ? new EntryBody(McpEntryReading.EmptyCommand, [])
                : new EntryBody(McpEntryReading.Readable, tokens);
    }

    /// <summary>
    /// A configuration file that is in play: where to read it, how to name it, and what the link
    /// guard found on the way to it when it found anything.
    /// </summary>
    /// <param name="AbsolutePath">Where the file would be read from.</param>
    /// <param name="RelativePath">The file as an operator's repository names it.</param>
    /// <param name="Obstruction">
    /// The symbolic link or denial that stopped the guard, or the default when the whole path is made
    /// of real entries. A denied file is kept rather than dropped, because the two states have
    /// different meanings and only one of them is a harness with nothing to say.
    /// </param>
    private readonly record struct ConfiguredFile(
        string AbsolutePath,
        string RelativePath,
        McpLinkProbe Obstruction = default);
}
