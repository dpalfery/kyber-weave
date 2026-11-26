using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;
using YamlDotNet.Core;

namespace KyberWeave.Application.Review;

/// <summary>
/// Writes one ready-to-send fix packet per owning group when the verdict sends findings to a fix round, and returns
/// the dispatch block that prints for each packet.
/// </summary>
/// <remarks>
/// <para>
/// The conductor used to paraphrase the verdict's <c>fix:</c> lines to fix workers, so the workers never read the
/// findings themselves and one run rewrote the same code four times. A packet carries the whole record of every
/// finding for one owner, the earlier cycles' findings on the same files, and the commits already made there, so the
/// block is sent unchanged.
/// </para>
/// <para>
/// A degraded read never loses the packets that could still be written: each one is reported as a note the caller
/// prints as a warning, and a packet that fails to write keeps the blocks already written.
/// </para>
/// </remarks>
internal static class FixPacketWriter
{
    /// <summary>The group whose findings are on Markdown files and are fixed by the documentation agent.</summary>
    internal const string DocsGroup = "docs";

    /// <summary>The group for a file no task lists, and for failed blocking gates.</summary>
    internal const string UnassignedGroup = "unassigned";

    private const string PacketsFolder = "fix-packets";
    private const string DefaultBase = "origin/HEAD";
    private const string Markdown = ".md";
    private const string TaskPrefix = "task-";

    private const string Header = """
        Read every finding here before you change anything.
        1. Make one design that satisfies all of these findings together, and the earlier ones in the history, not one patch per finding.
        2. Make the smallest change that does it. Prefer what the framework or library already provides over new mechanism.
        3. Do not change an approved plan decision unless a finding here is a regression that decision caused: then fix the regression and name the decision you overrode in your digest. A finding that only disagrees with a plan decision never reaches this packet.
        4. Fixed means the finding's whole class is fixed: name the class in one line for each finding in your digest.
        5. Each fix of a finding that is not a nitpick ships with a test that fails without it.
        6. The fix edits the current tree; never restore a file to base and re-apply.
        7. A test you add or change is deterministic: it controls time and ordering (fake clocks, awaited events, explicit waits on a condition) and never depends on a sleep, a fixed timeout or a race with a timer.
        """;

    /// <summary>Writes the fix packets of one fix round and returns their dispatch blocks.</summary>
    /// <param name="input">What the round took and where it belongs.</param>
    /// <param name="cycles">Reads the earlier cycles one at a time.</param>
    /// <param name="git">Lists the commits made on the packet's files since its first readable cycle.</param>
    /// <param name="files">Writes each packet into the cycle folder.</param>
    internal static FixPacketOutcome Write(
        FixPacketInput input, IReviewCycleStore cycles, IGitChangeReader git, IWritableFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(files);

        string folder = Path.Combine(input.CycleFolder, PacketsFolder);
        List<string> notes = [];
        try
        {
            IReadOnlyList<string>? changed = ChangedPaths(input.Tree, input.BaseRef, git);
            if (changed is null)
                notes.Add(NoTaskNote("git could not list the change"));

            string? plan = changed is null ? null : FindPlan(input, changed, files, notes);
            IReadOnlyList<PlanTaskFileEntry> tasks = plan is null ? [] : ReadTasks(input.Tree, plan, files, notes);
            Earlier earlier = ReadEarlierCycles(input.PriorFolder, cycles, files, notes);
            List<Packet> packets = Group(input.Findings, input.Gates, tasks);

            files.CreateDirectory(folder);
            Dictionary<Packet, string> names = PacketNames(packets);
            RemoveStalePackets(folder, names.Values, files, notes);

            List<string> blocks = [];
            bool wroteOtherBlocks = false;
            foreach (Packet packet in packets)
            {
                string name = names[packet];
                string path = Path.Combine(folder, name + Markdown);
                try
                {
                    files.WriteAllText(path, Text(packet, earlier, input.Tree, git));
                    blocks.Add(DispatchBlock(packet, plan, path, afterOtherBlocks: wroteOtherBlocks));
                    wroteOtherBlocks |= packet.Kind != PacketKind.Docs;
                }
                catch (Exception ex) when (FileFailure.Is(ex))
                {
                    notes.Add(
                        $"fix packets: '{name}{Markdown}' could not be written, so its dispatch block is missing " +
                        $"({FirstLine(ex.Message)}).");
                }
            }

            return new FixPacketOutcome(blocks, notes);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            notes.Add($"fix packets: could not be written to '{folder}': {FirstLine(ex.Message)}");
            return new FixPacketOutcome([], notes);
        }
    }

    /// <summary>The line saying why no plan task could own a finding, and where those findings went instead.</summary>
    private static string NoTaskNote(string reason) =>
        $"fix packets: {reason}, so no plan task owned a finding; every finding not on a Markdown file went to unassigned.";

    /// <summary>The one plan the change touches, or null when it touches none or several, with a note saying which.</summary>
    private static string? FindPlan(
        FixPacketInput input, IReadOnlyList<string> changed, IWritableFileSystem files, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(input.PlanFolder))
        {
            notes.Add(NoTaskNote("the host registers no plan folder"));
            return null;
        }

        IReadOnlyList<string> plans = ChangePlanLookup.Find(
            changed, input.PlanFolder, relative => ReadText(input.Tree, relative, files));
        if (plans.Count == 1)
            return plans[0];

        notes.Add(plans.Count == 0
            ? NoTaskNote("no plan was found for this change")
            : NoTaskNote(
                $"the change touches {plans.Count.ToString(CultureInfo.InvariantCulture)} plans ({string.Join(", ", plans)})"));
        return null;
    }

    /// <summary>The change's paths, or null when git could not list them.</summary>
    private static IReadOnlyList<string>? ChangedPaths(string tree, string? baseRef, IGitChangeReader git)
    {
        try
        {
            return [.. git.ResolveSinceMergeBase(tree, baseRef ?? DefaultBase).Select(change => change.Path)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The tasks the plan's task file declares, in plan order; none when the file is missing or unreadable.</summary>
    private static IReadOnlyList<PlanTaskFileEntry> ReadTasks(
        string tree, string plan, IWritableFileSystem files, List<string> notes)
    {
        string relative = Path.ChangeExtension(plan, PlanTaskFileRules.TaskFileSuffix);
        string path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
        string? text;
        try
        {
            text = files.FileExists(path) ? files.ReadAllText(path) : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            text = null;
        }

        if (text is null)
        {
            notes.Add(NoTaskNote($"the task file '{relative}' is missing or could not be read"));
            return [];
        }

        try
        {
            PlanTaskFile? file = PlanTaskFileFormat.Deserialize(relative, text);
            return [.. (file?.Tasks ?? []).Where(task => !string.IsNullOrWhiteSpace(task.Id))];
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or YamlException or InvalidOperationException)
        {
            notes.Add(NoTaskNote($"the task file '{relative}' could not be read ({FirstLine(ex.Message)})"));
            return [];
        }
    }

    /// <summary>The text of a repository-relative file, or null when it cannot be read.</summary>
    private static string? ReadText(string tree, string relative, IWritableFileSystem files)
    {
        try
        {
            string path = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
            return files.FileExists(path) ? files.ReadAllText(path) : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The earlier cycles of the change whose findings the history section reads, read one folder at a time: a cycle
    /// that cannot be read is named in the history and reported, and never hides the cycles that can.
    /// </summary>
    private static Earlier ReadEarlierCycles(
        string? priorFolder, IReviewCycleStore cycles, IWritableFileSystem files, List<string> notes)
    {
        List<EarlierCycle> readable = [];
        List<int> unreadable = [];
        if (string.IsNullOrWhiteSpace(priorFolder))
            return new Earlier(readable, unreadable);

        IReadOnlyList<(int Number, string Folder)> folders;
        try
        {
            folders = CycleFolders(priorFolder, files);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException)
        {
            notes.Add("fix packets: the earlier cycles could not be listed, so the history is empty.");
            return new Earlier(readable, unreadable);
        }

        foreach ((int number, string folder) in folders)
        {
            ReviewCycleRecord? record;
            try
            {
                record = cycles.ReadCycle(folder);
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException)
            {
                record = null;
            }

            if (record is null)
            {
                unreadable.Add(number);
                notes.Add(UnreadableCycleNote(number));
                continue;
            }

            if (!files.FileExists(record.FindingsPath))
                continue;

            try
            {
                FindingsReport findings = ReviewJson.ReadFindings(files.ReadAllText(record.FindingsPath));
                readable.Add(new EarlierCycle(record, findings.Findings));
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or InvalidOperationException)
            {
                unreadable.Add(number);
                notes.Add(UnreadableCycleNote(number));
            }
        }

        return new Earlier([.. readable.OrderBy(cycle => cycle.Cycle.Number)], [.. unreadable.Order()]);
    }

    private static string UnreadableCycleNote(int number) =>
        $"fix packets: cycle {number.ToString(CultureInfo.InvariantCulture)} could not be read, so its findings are " +
        "not in the history.";

    /// <summary>The cycle folders of <paramref name="priorFolder"/>'s review folder, oldest first, up to its number.</summary>
    private static IReadOnlyList<(int Number, string Folder)> CycleFolders(
        string priorFolder, IWritableFileSystem files)
    {
        string full = priorFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ReviewCyclePlanning.TryNumber(Path.GetFileName(full)) is not { } upTo)
            return [(0, priorFolder)];

        string parent = Path.GetDirectoryName(full) ?? full;
        List<(int Number, string Folder)> folders = [];
        foreach (string folder in files.EnumerateDirectories(parent))
        {
            if (ReviewCyclePlanning.TryNumber(Path.GetFileName(folder)) is { } number && number <= upTo)
                folders.Add((number, folder));
        }

        return [.. folders.OrderBy(entry => entry.Number)];
    }

    /// <summary>
    /// The packets of one round: each task with findings, in plan order, then <c>unassigned</c>, then <c>docs</c>, so
    /// the documentation fix is dispatched after every code fix it describes.
    /// </summary>
    private static List<Packet> Group(
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<GateResult> gates,
        IReadOnlyList<PlanTaskFileEntry> tasks)
    {
        Dictionary<string, Packet> byTask = new(StringComparer.Ordinal);
        List<ReviewFinding> docs = [];
        List<ReviewFinding> unassigned = [];
        List<GateResult> unassignedGates = [.. gates];

        foreach (ReviewFinding finding in findings)
        {
            if (finding.File.EndsWith(Markdown, StringComparison.OrdinalIgnoreCase))
            {
                docs.Add(finding);
                continue;
            }

            IReadOnlyList<PlanTaskFileEntry> owners = Owners(tasks, finding.File);
            if (owners.Count == 0)
            {
                unassigned.Add(finding);
                continue;
            }

            string id = owners[0].Id!;
            if (!byTask.TryGetValue(id, out Packet? packet))
                byTask[id] = packet = new Packet(PacketKind.Task, id, [], []);
            packet.Findings.Add(finding);

            // The first task in plan order owns the file; the others are named so a worker knows where else it is in scope.
            foreach (PlanTaskFileEntry other in owners.Skip(1))
            {
                string note = $"{Normalize(finding.File)} is also listed by task {other.Id} in the plan's task file.";
                if (!packet.Notes.Contains(note, StringComparer.Ordinal))
                    packet.Notes.Add(note);
            }
        }

        List<Packet> packets = [];
        foreach (PlanTaskFileEntry task in tasks.Where(task => task.Id is not null))
        {
            if (byTask.TryGetValue(task.Id!, out Packet? packet) && !packets.Contains(packet))
                packets.Add(packet);
        }

        if (unassigned.Count > 0 || unassignedGates.Count > 0)
            packets.Add(new Packet(PacketKind.Unassigned, UnassignedGroup, unassigned, unassignedGates));
        if (docs.Count > 0)
            packets.Add(new Packet(PacketKind.Docs, DocsGroup, docs, []));
        return packets;
    }

    /// <summary>The tasks whose file scope names the finding's file, in plan order.</summary>
    private static IReadOnlyList<PlanTaskFileEntry> Owners(IReadOnlyList<PlanTaskFileEntry> tasks, string file)
    {
        string path = Normalize(file);
        return
        [
            .. tasks.Where(task => task.Id is not null && task.Files?.Any(listed => Names(listed, path)) == true)
        ];
    }

    /// <summary>Whether a task's listed path is the file, or a directory the file sits under.</summary>
    private static bool Names(string listed, string path)
    {
        string entry = Normalize(listed).TrimEnd('/');
        return entry.Length > 0
               && (string.Equals(entry, path, StringComparison.Ordinal)
                   || path.StartsWith(entry + "/", StringComparison.Ordinal));
    }

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    /// <summary>The name of each packet's file, unique within the round.</summary>
    private static Dictionary<Packet, string> PacketNames(IReadOnlyList<Packet> packets)
    {
        Dictionary<Packet, string> names = [];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (Packet packet in packets)
        {
            string baseName = FileName(packet);
            string name = baseName;
            for (int suffix = 2; !used.Add(name); suffix++)
                name = $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            names[packet] = name;
        }

        return names;
    }

    /// <summary>
    /// The packet's file name: its group sanitized to letters, digits, dot, underscore and dash, with <c>task-</c>
    /// before a plan task's group so a task called <c>docs</c> cannot take the documentation packet's name.
    /// </summary>
    private static string FileName(Packet packet) =>
        packet.Kind == PacketKind.Task ? TaskPrefix + SafeName(packet.Group) : SafeName(packet.Group);

    private static string SafeName(string group)
    {
        StringBuilder name = new(group.Length);
        foreach (char character in group)
            name.Append(IsSafeNameCharacter(character) ? character : '-');

        return name.Length == 0 ? "packet" : name.ToString();
    }

    private static bool IsSafeNameCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    /// <summary>Deletes the packet files of an earlier round that this round does not write.</summary>
    private static void RemoveStalePackets(
        string folder, IEnumerable<string> keep, IWritableFileSystem files, List<string> notes)
    {
        HashSet<string> names = new(keep, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in files.EnumerateFiles(folder, "*" + Markdown, recursive: false))
            {
                if (!names.Contains(Path.GetFileNameWithoutExtension(path)))
                    files.DeleteFile(path);
            }
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            notes.Add(
                $"fix packets: packet files this round did not write could not be removed from '{folder}' " +
                $"({FirstLine(ex.Message)}).");
        }
    }

    /// <summary>The packet's whole text: the fixed header, its notes, findings, the history and the commits on those files.</summary>
    private static string Text(Packet packet, Earlier earlier, string tree, IGitChangeReader git)
    {
        List<string> files = [.. packet.Findings.Select(finding => Normalize(finding.File)).Distinct(StringComparer.Ordinal)];
        StringBuilder text = new();
        text.Append("# Fix packet: ").Append(packet.Group).Append("\n\n");
        text.Append(Header).Append("\n\n");
        if (packet.Notes.Count > 0)
        {
            text.Append("## Scope notes\n\n");
            foreach (string note in packet.Notes)
                text.Append("- ").Append(note).Append('\n');
            text.Append('\n');
        }

        text.Append("## Findings to fix\n\n");
        foreach (ReviewFinding finding in packet.Findings)
        {
            text.Append("### ").Append(finding.Id).Append('\n');
            Field(text, "lens", finding.Lens);
            Field(text, "severity", SeverityWord(finding.Severity));
            Field(text, "kind", finding.Kind is { } kind ? KindWord(kind) : null);
            Field(text, "basis", finding.Basis);
            Field(text, "file", $"{finding.File}:{finding.Line.ToString(CultureInfo.InvariantCulture)}");
            Field(text, "excerpt", finding.Excerpt);
            Field(text, "claim", finding.Claim);
            Field(text, "evidence", finding.Evidence);
            Field(text, "failure_scenario", finding.FailureScenario);
            Field(text, "suggestion", finding.Suggestion);
            text.Append('\n');
        }

        foreach (GateResult gate in packet.Gates)
        {
            text.Append("### gate ").Append(gate.Id).Append('\n');
            Field(text, "summary", gate.Summary);
            text.Append('\n');
        }

        text.Append(
            "## Earlier cycles' findings on these files (line numbers are from that cycle's commit; not necessarily still open)\n\n");
        List<string> history = History(earlier, files);
        if (history.Count == 0)
            text.Append("none\n");
        else
            foreach (string line in history)
                text.Append(line).Append('\n');

        text.Append('\n');
        AppendCommits(text, files, earlier, tree, git);
        return text.ToString();
    }

    /// <summary>Writes one field of a finding, omitting it when the finding does not carry it.</summary>
    private static void Field(StringBuilder text, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        string flat = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text.Append("- ").Append(name).Append(": ").Append(flat.Replace("\n", "\n  ", StringComparison.Ordinal)).Append('\n');
    }

    /// <summary>Every earlier cycle's finding on a file this packet covers, oldest cycle first, with any unreadable one named.</summary>
    private static List<string> History(Earlier earlier, IReadOnlyList<string> files)
    {
        List<(int Number, string Line)> lines = [];
        foreach (EarlierCycle entry in earlier.Cycles)
        {
            foreach (ReviewFinding finding in entry.Findings.Where(finding => Covered(files, finding.File)))
            {
                string where = $"{finding.File}:{finding.Line.ToString(CultureInfo.InvariantCulture)}";
                string claim = string.IsNullOrWhiteSpace(finding.Claim) ? string.Empty : $" — {finding.Claim}";
                lines.Add((
                    entry.Cycle.Number,
                    $"cycle {entry.Cycle.Number.ToString(CultureInfo.InvariantCulture)} · {finding.Id} " +
                    $"({SeverityWord(finding.Severity)}) {where}{claim}"));
            }
        }

        foreach (int number in earlier.Unreadable)
            lines.Add((number, $"(cycle {number.ToString(CultureInfo.InvariantCulture)} unreadable)"));

        return [.. lines.OrderBy(line => line.Number).Select(line => line.Line)];
    }

    private static bool Covered(IReadOnlyList<string> files, string file) =>
        files.Contains(Normalize(file), StringComparer.Ordinal);

    /// <summary>
    /// The commits on the packet's files since its first readable cycle, or one line saying why they are not listed:
    /// no cycle could be read, that cycle has no head, git could not answer, or the packet names no file.
    /// </summary>
    private static void AppendCommits(
        StringBuilder text, IReadOnlyList<string> files, Earlier earlier, string tree, IGitChangeReader git)
    {
        if (earlier.Cycles.Count == 0)
        {
            string why = earlier.Unreadable.Count == 0
                ? "no earlier cycle of this change is recorded"
                : "no earlier cycle of this change could be read";
            text.Append("Commits on these files: not listed — ").Append(why).Append(".\n");
            return;
        }

        ReviewCycleRecord first = earlier.Cycles[0].Cycle;
        string since = $"cycle {first.Number.ToString(CultureInfo.InvariantCulture)}";
        if (first.Head is null)
        {
            text.Append("Commits on these files since ").Append(since).Append(": not listed — ")
                .Append(since).Append(" recorded no head.\n");
            return;
        }

        if (files.Count == 0)
        {
            text.Append("Commits on these files since ").Append(since)
                .Append(": not listed — this packet names no file.\n");
            return;
        }

        IReadOnlyList<string> commits;
        try
        {
            commits = git.LogOneline(tree, first.Head, files);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // A git message can carry an absolute path, which must never reach the packet.
            text.Append("Commits on these files since ").Append(since)
                .Append(": not listed — git could not list them.\n");
            return;
        }

        text.Append("## Commits on these files since ").Append(since).Append("\n\n");
        if (commits.Count == 0)
            text.Append("none\n");
        else
            foreach (string commit in commits)
                text.Append(commit).Append('\n');
    }

    private static string FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no reason reported";

    /// <summary>
    /// The block the conductor sends unchanged: the group, its plan and task, and the packet's path. The documentation
    /// block waits for the round's other fixes when there are any, because a docs fix describes the tree the code fixes
    /// have already changed.
    /// </summary>
    private static string DispatchBlock(Packet packet, string? plan, string path, bool afterOtherBlocks)
    {
        StringBuilder block = new();
        block.Append("--- fix ").Append(packet.Group).Append(" ---\n");
        block.Append("KYBER-ARBITER: true\n");
        if (plan is not null)
            block.Append("PLAN_FILE: ").Append(plan).Append('\n');
        switch (packet.Kind)
        {
            case PacketKind.Task:
                block.Append("TASK: ").Append(packet.Group).Append('\n');
                break;
            case PacketKind.Docs:
                block.Append("Agent: docs-dev\n");
                if (afterOtherBlocks)
                    block.Append("After: every other fix block of this round is integrated\n");
                break;
            default:
                block.Append("Assign: the task that owns these files' component\n");
                break;
        }

        block.Append("Fix packet: ").Append(path).Append('\n');
        block.Append("--- end ---");
        return block.ToString();
    }

    private static string SeverityWord(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => "critical",
        ReviewSeverity.Major => "major",
        ReviewSeverity.Nitpick => "nitpick",
        _ => "minor"
    };

    private static string KindWord(FindingKind kind) => kind switch
    {
        FindingKind.Defect => "defect",
        FindingKind.Hardening => "hardening",
        _ => "plan-objection"
    };

    /// <summary>The group a packet belongs to: one plan task, the documentation agent, or nobody yet.</summary>
    private enum PacketKind
    {
        Task,
        Docs,
        Unassigned
    }

    /// <summary>One cycle the history section reads, and the findings it recorded.</summary>
    private sealed record EarlierCycle(ReviewCycleRecord Cycle, IReadOnlyList<ReviewFinding> Findings);

    /// <summary>The earlier cycles whose findings could be read, and the numbers of those that could not.</summary>
    private sealed record Earlier(List<EarlierCycle> Cycles, List<int> Unreadable);

    /// <summary>One packet being built, and the findings and gates it carries.</summary>
    private sealed record Packet(PacketKind Kind, string Group, List<ReviewFinding> Findings, List<GateResult> Gates)
    {
        /// <summary>One line for another task that also lists one of the packet's files.</summary>
        internal List<string> Notes { get; } = [];
    }
}

/// <summary>What one fix round took, and where the packets belong.</summary>
/// <param name="Tree">The work tree the change is in.</param>
/// <param name="CycleFolder">The cycle folder the packets are written under.</param>
/// <param name="PriorFolder">The previous cycle's folder, or null for a first cycle.</param>
/// <param name="BaseRef">The git ref the change's base resolves against, or null for <c>origin/HEAD</c>.</param>
/// <param name="PlanFolder">The host's plan folder, repository-relative, or null when it registers none.</param>
/// <param name="Findings">The findings the fix round took.</param>
/// <param name="Gates">The blocking gates that failed.</param>
internal sealed record FixPacketInput(
    string Tree,
    string CycleFolder,
    string? PriorFolder,
    string? BaseRef,
    string? PlanFolder,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<GateResult> Gates);

/// <summary>What one fix round wrote, the dispatch block for each packet, and what it could not do.</summary>
/// <param name="DispatchBlocks">One block per packet written, in the order the packets were written.</param>
/// <param name="Notes">One line for each degraded path: what could not be read or written, and what that left out.</param>
internal sealed record FixPacketOutcome(IReadOnlyList<string> DispatchBlocks, IReadOnlyList<string> Notes);
