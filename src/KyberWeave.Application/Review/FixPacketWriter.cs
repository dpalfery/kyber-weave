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
/// The conductor used to paraphrase the verdict's <c>fix:</c> lines to fix workers, so the workers never read the
/// findings themselves and one run rewrote the same code four times. A packet carries the whole record of every
/// finding for one owner, the earlier cycles' findings on the same files, and the commits already made there, so the
/// block is sent unchanged.
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
    private const string TaskFileSuffix = ".tasks.yml";

    private const string Header = """
        Read every finding here before you change anything.
        1. Make one design that satisfies all of these findings together, and the earlier ones in the history, not one patch per finding.
        2. Make the smallest change that does it. Prefer what the framework or library already provides over new mechanism.
        3. Never change an approved plan decision. If a finding can only be fixed by changing one, leave it and report `Plan conflict: <id> — <the decision>`.
        4. Fixed means the finding's whole class is fixed: name the class in one line for each finding in your digest.
        5. Each fix of a finding that is not a nitpick ships with a test that fails without it.
        6. The fix edits the current tree; never restore a file to base and re-apply.
        """;

    /// <summary>Writes the fix packets of one fix round and returns their dispatch blocks.</summary>
    /// <param name="input">What the round took and where it belongs.</param>
    /// <param name="cycles">Lists the earlier cycles of the change.</param>
    /// <param name="git">Lists the commits made on the packet's files since cycle 1.</param>
    /// <param name="files">Writes each packet into the cycle folder.</param>
    internal static FixPacketOutcome Write(
        FixPacketInput input, IReviewCycleStore cycles, IGitChangeReader git, IWritableFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(files);

        string folder = Path.Combine(input.CycleFolder, PacketsFolder);
        try
        {
            string? plan = FindPlan(input, git, files);
            IReadOnlyList<PlanTaskFileEntry> tasks = plan is null ? [] : ReadTasks(input.Tree, plan, files);
            IReadOnlyList<ReviewCycleRecord> earlier = EarlierCycles(input.PriorFolder, cycles, files);
            List<Packet> packets = Group(input.Findings, input.Gates, tasks);

            files.CreateDirectory(folder);
            List<string> blocks = [];
            foreach (Packet packet in packets)
            {
                string path = Path.Combine(folder, FileName(packet) + Markdown);
                files.WriteAllText(path, Text(packet, earlier, input.Tree, git, files));
                blocks.Add(DispatchBlock(packet, plan, path));
            }

            return new FixPacketOutcome(blocks, null);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return new FixPacketOutcome(
                [], $"fix packets: could not be written to '{folder}': {ex.Message}");
        }
    }

    /// <summary>The one plan the change touches, or null when it touches none or several.</summary>
    private static string? FindPlan(FixPacketInput input, IGitChangeReader git, IWritableFileSystem files)
    {
        IReadOnlyList<string>? changed = ChangedPaths(input.Tree, input.BaseRef, git);
        if (changed is null || string.IsNullOrWhiteSpace(input.PlanFolder))
            return null;

        IReadOnlyList<string> plans = ChangePlanLookup.Find(
            changed, input.PlanFolder, relative => ReadText(input.Tree, relative, files));
        return plans.Count == 1 ? plans[0] : null;
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
    private static IReadOnlyList<PlanTaskFileEntry> ReadTasks(string tree, string plan, IWritableFileSystem files)
    {
        string relative = Path.ChangeExtension(plan, TaskFileSuffix);
        if (ReadText(tree, relative, files) is not { } text)
            return [];

        try
        {
            PlanTaskFile? file = PlanTaskFileFormat.Deserialize(relative, text);
            return [.. (file?.Tasks ?? []).Where(task => !string.IsNullOrWhiteSpace(task.Id))];
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or YamlException or InvalidOperationException)
        {
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

    /// <summary>The earlier cycles of the change, oldest first, whose findings the history section reads.</summary>
    private static IReadOnlyList<ReviewCycleRecord> EarlierCycles(
        string? priorFolder, IReviewCycleStore cycles, IWritableFileSystem files)
    {
        if (string.IsNullOrWhiteSpace(priorFolder))
            return [];

        try
        {
            return [.. cycles.ReadThrough(priorFolder).Where(cycle => files.FileExists(cycle.FindingsPath))];
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The packets of one round: each task with findings, in plan order, then <c>docs</c>, then <c>unassigned</c>.</summary>
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
            }
            else if (TaskFor(tasks, finding.File) is { Id: { } id })
            {
                if (!byTask.TryGetValue(id, out Packet? packet))
                    byTask[id] = packet = new Packet(PacketKind.Task, id, [], []);
                packet.Findings.Add(finding);
            }
            else
            {
                unassigned.Add(finding);
            }
        }

        List<Packet> packets = [];
        foreach (PlanTaskFileEntry task in tasks.Where(task => task.Id is not null))
        {
            if (byTask.TryGetValue(task.Id!, out Packet? packet))
                packets.Add(packet);
        }

        if (docs.Count > 0)
            packets.Add(new Packet(PacketKind.Docs, DocsGroup, docs, []));
        if (unassigned.Count > 0 || unassignedGates.Count > 0)
            packets.Add(new Packet(PacketKind.Unassigned, UnassignedGroup, unassigned, unassignedGates));
        return packets;
    }

    /// <summary>The task whose file scope names the finding's file, by exact path or by a listed directory prefix.</summary>
    private static PlanTaskFileEntry? TaskFor(IReadOnlyList<PlanTaskFileEntry> tasks, string file)
    {
        string path = Normalize(file);
        foreach (PlanTaskFileEntry task in tasks)
        {
            if (task.Files?.Any(listed => Names(listed, path)) == true)
                return task;
        }

        return null;
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

    /// <summary>The packet's file name: the group, with any path separator made safe for a file name.</summary>
    private static string FileName(Packet packet) =>
        string.Join('-', packet.Group.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']));

    /// <summary>The packet's whole text: the fixed header, its findings, the history and the commits on those files.</summary>
    private static string Text(
        Packet packet,
        IReadOnlyList<ReviewCycleRecord> earlier,
        string tree,
        IGitChangeReader git,
        IWritableFileSystem reader)
    {
        List<string> files = [.. packet.Findings.Select(finding => Normalize(finding.File)).Distinct(StringComparer.Ordinal)];
        StringBuilder text = new();
        text.Append("# Fix packet: ").Append(packet.Group).Append("\n\n");
        text.Append(Header).Append("\n\n## Findings to fix\n\n");
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

        text.Append("## History on these files\n\n");
        List<string> history = History(earlier, files, reader);
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

    /// <summary>Every earlier cycle's finding on a file this packet covers, oldest cycle first.</summary>
    private static List<string> History(
        IReadOnlyList<ReviewCycleRecord> earlier, IReadOnlyList<string> files, IWritableFileSystem reader)
    {
        List<string> lines = [];
        foreach (ReviewCycleRecord cycle in earlier)
        {
            try
            {
                FindingsReport report = ReviewJson.ReadFindings(reader.ReadAllText(cycle.FindingsPath));
                foreach (ReviewFinding finding in report.Findings.Where(finding => Covered(files, finding.File)))
                {
                    string where = $"{finding.File}:{finding.Line.ToString(CultureInfo.InvariantCulture)}";
                    string claim = string.IsNullOrWhiteSpace(finding.Claim) ? string.Empty : $" — {finding.Claim}";
                    lines.Add(
                        $"cycle {cycle.Number.ToString(CultureInfo.InvariantCulture)} · {finding.Id} " +
                        $"({SeverityWord(finding.Severity)}) {where}{claim}");
                }
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or InvalidOperationException)
            {
                // A cycle that cannot be read adds no history: the packet still carries what was read.
            }
        }

        return lines;
    }

    private static bool Covered(IReadOnlyList<string> files, string file) =>
        files.Contains(Normalize(file), StringComparer.Ordinal);

    /// <summary>
    /// The commits on the packet's files since cycle 1, or one line saying why they are not listed: cycle 1 has no
    /// head, git could not answer, or the packet names no file.
    /// </summary>
    private static void AppendCommits(
        StringBuilder text, IReadOnlyList<string> files, IReadOnlyList<ReviewCycleRecord> earlier, string tree, IGitChangeReader git)
    {
        const string Heading = "## Commits on these files since cycle 1";

        if (earlier.Count == 0)
        {
            text.Append("Commits on these files since cycle 1: not listed — no earlier cycle of this change is recorded.\n");
            return;
        }

        ReviewCycleRecord first = earlier[0];
        if (first.Head is null)
        {
            text.Append("Commits on these files since cycle 1: not listed — cycle 1 recorded no head.\n");
            return;
        }

        if (files.Count == 0)
        {
            text.Append("Commits on these files since cycle 1: not listed — this packet names no file.\n");
            return;
        }

        IReadOnlyList<string> commits;
        try
        {
            commits = git.LogOneline(tree, first.Head, files);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            text.Append("Commits on these files since cycle 1: not listed — git could not list them (")
                .Append(FirstLine(ex.Message))
                .Append(").\n");
            return;
        }

        text.Append(Heading).Append("\n\n");
        if (commits.Count == 0)
            text.Append("none\n");
        else
            foreach (string commit in commits)
                text.Append(commit).Append('\n');
    }

    private static string FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no reason reported";

    /// <summary>The block the conductor sends unchanged: the group, its plan and task, and the packet's path.</summary>
    private static string DispatchBlock(Packet packet, string? plan, string path)
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

    /// <summary>One packet being built, and the findings and gates it carries.</summary>
    private sealed record Packet(PacketKind Kind, string Group, List<ReviewFinding> Findings, List<GateResult> Gates);
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

/// <summary>The dispatch blocks a fix round prints, or the one reason no packet could be written.</summary>
/// <param name="DispatchBlocks">One block per packet, in the order the packets were written.</param>
/// <param name="Problem">Why no packet was written; null when every packet was.</param>
internal sealed record FixPacketOutcome(IReadOnlyList<string> DispatchBlocks, string? Problem);
