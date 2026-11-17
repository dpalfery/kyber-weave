using KyberWeave.Core.Review;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Core.Configuration;

/// <summary>One <c>review.task-gates</c> entry that becomes a <c>review.technology-gates</c> entry.</summary>
/// <param name="Agent">The agent slug the entry was written under.</param>
/// <param name="Technology">The technology the entry is written under instead.</param>
public sealed record TaskGateMove(string Agent, string Technology);

/// <summary>One <c>review.task-gates</c> entry the migration leaves where it is, and why.</summary>
/// <param name="Agent">The agent slug the entry is written under.</param>
/// <param name="Reason">Why no technology key takes the entry.</param>
public sealed record TaskGateKeep(string Agent, string Reason);

/// <summary>What a migration writes, which entries it folds into another, and which it leaves alone.</summary>
/// <param name="Text">The configuration as it stands after the migration; equal to the input when nothing moved.</param>
/// <param name="Moved">Entries written under a technology key, in the order they appear in the file.</param>
/// <param name="Folded">Entries dropped because an entry that moved already holds the same gates, in file order.</param>
/// <param name="Kept">Entries left under their agent key, in file order.</param>
public sealed record TaskGateMigrationPlan(
    string Text,
    IReadOnlyList<TaskGateMove> Moved,
    IReadOnlyList<TaskGateMove> Folded,
    IReadOnlyList<TaskGateKeep> Kept)
{
    /// <summary>Whether the migration rewrites any entry.</summary>
    public bool Changes => Moved.Count > 0;
}

/// <summary>
/// Rewrites a host's agent-keyed task gates as technology-keyed ones, as text.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite works on the file's own lines rather than loading it into a YAML document and emitting it again. A
/// round trip would lose every comment, every blank line, and the anchor-and-alias shorthand this repository's own
/// configuration uses to hold one gate list in one place, and the command's contract is that it changes nothing
/// else. So the only lines that differ are those of the mappings the migration reads and writes.
/// </para>
/// <para>
/// An entry moves only when the shipped <see cref="TechnologyAgentMap"/> names a technology for its agent, the host
/// has not already declared that technology, and no entry that moved already claimed it with a different gate list.
/// Everything else stays where it is: an entry that cannot move still resolves (so no host stops gating), and a
/// migration that guessed which of two unequal gate lists was meant would silently drop the other one.
/// </para>
/// </remarks>
public static class ReviewTaskGateMigration
{
    private const string ReviewKey = "review";
    private const string TaskGatesKey = "task-gates";
    private const string TechnologyGatesKey = "technology-gates";

    /// <summary>Plans the migration of one configuration file's text.</summary>
    /// <param name="yaml">The configuration's text, exactly as it is on disk.</param>
    /// <param name="review">The <c>review:</c> section that same text parses to.</param>
    /// <param name="technologyBlockFirst">
    /// Whether a file that keeps some agent-keyed entries writes the technology block above them instead of below.
    /// The two orders are the same configuration; only a YAML anchor's requirement that its definition come first
    /// distinguishes them, so a caller that cannot load one retries with the other.
    /// </param>
    /// <returns>The text to write, and what the plan does to each entry.</returns>
    public static TaskGateMigrationPlan Plan(string yaml, ReviewConfig review, bool technologyBlockFirst = false)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(review);

        string[] lines = yaml.Split('\n');
        if (FindMapping(lines, 0, lines.Length, 0, ReviewKey) is not { } reviewBlock
            || FindChild(lines, reviewBlock, TaskGatesKey) is not { } taskGates)
        {
            return new TaskGateMigrationPlan(yaml, [], [], []);
        }

        Mapping? technologyGates = FindChild(lines, reviewBlock, TechnologyGatesKey);

        Block block = ReadBlock(lines, taskGates);
        if (block.Chunks.Count == 0)
            return new TaskGateMigrationPlan(yaml, [], [], []);

        List<TaskGateMove> moved = [];
        List<TaskGateMove> folded = [];
        List<TaskGateKeep> kept = [];
        Decision[] decisions = Decide(block, review, moved, folded, kept);
        if (moved.Count == 0)
            return new TaskGateMigrationPlan(yaml, moved, folded, kept);

        string suffix = lines[taskGates.Header].EndsWith('\r') ? "\r" : string.Empty;
        Dictionary<string, string> technologies =
            moved.ToDictionary(move => move.Agent, move => move.Technology, StringComparer.Ordinal);
        List<List<string>> sections = Sections(block, decisions, technologies, block.EntryIndent, suffix);
        bool everyEntryMoved = !decisions.Contains(Decision.Keep);

        List<Edit> edits = [];
        if (technologyGates is not null)
        {
            edits.Add(Insertion(
                lines, technologyGates, everyEntryMoved ? [.. block.HeaderTrivia] : [], sections, suffix));
            edits.Add(new Edit(
                taskGates.Header,
                taskGates.End,
                everyEntryMoved ? [] : KeptRegion(lines, block, decisions, [])));
        }
        else if (everyEntryMoved)
        {
            edits.Add(new Edit(taskGates.Header, taskGates.End, RenamedRegion(block, sections, suffix)));
        }
        else if (technologyBlockFirst)
        {
            List<string> above =
            [
                new string(' ', taskGates.Indent) + TechnologyGatesKey + ":" + suffix,
                .. Flatten(sections, suffix),
                suffix
            ];

            // Two edits that start on the same line: the insertion is applied first, so the kept block keeps every
            // line it had and the technology block lands above it.
            edits.Add(new Edit(taskGates.Header, taskGates.Header, above));
            edits.Add(new Edit(taskGates.Header, taskGates.End, KeptRegion(lines, block, decisions, [])));
        }
        else
        {
            List<string> below =
            [
                new string(' ', taskGates.Indent) + TechnologyGatesKey + ":" + suffix,
                .. Flatten(sections, suffix)
            ];

            edits.Add(new Edit(taskGates.Header, taskGates.End, KeptRegion(lines, block, decisions, below)));
        }

        return new TaskGateMigrationPlan(Apply(lines, edits), moved, folded, kept);
    }

    /// <summary>Whether two gate lists hold the same gates, so one entry can stand for both.</summary>
    /// <param name="left">One entry's gates, in declaration order.</param>
    /// <param name="right">The other entry's gates, in declaration order.</param>
    public static bool SameGates(IReadOnlyList<ReviewTaskGate> left, IReadOnlyList<ReviewTaskGate> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            ReviewTaskGate one = left[i];
            ReviewTaskGate other = right[i];
            if (one.Id != other.Id
                || one.Os != other.Os
                || one.Blocking != other.Blocking
                || one.TimeoutSeconds != other.TimeoutSeconds
                || one.WorkingDirectory != other.WorkingDirectory
                || one.FailOnOutput != other.FailOnOutput
                || !one.Include.SequenceEqual(other.Include, StringComparer.Ordinal)
                || !one.Run.SequenceEqual(other.Run, StringComparer.Ordinal)
                || !one.Fix.SequenceEqual(other.Fix, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Decides what happens to each entry, in file order, and records what each decision was based on.</summary>
    private static Decision[] Decide(
        Block block,
        ReviewConfig review,
        List<TaskGateMove> moved,
        List<TaskGateMove> folded,
        List<TaskGateKeep> kept)
    {
        Decision[] decisions = new Decision[block.Chunks.Count];
        Dictionary<string, int> representatives = new(StringComparer.Ordinal);
        HashSet<string> conflicted = new(StringComparer.Ordinal);

        for (int i = 0; i < block.Chunks.Count; i++)
        {
            string agent = block.Chunks[i].Key;

            if (!RemovedAgentMap.IsRemoved(agent))
            {
                decisions[i] = Decision.Keep;
                kept.Add(new TaskGateKeep(
                    agent, "the removal phase does not delete this agent, so its own key keeps working"));
                continue;
            }

            if (TechnologyAgentMap.TechnologyFor(agent) is not { } technology)
            {
                decisions[i] = Decision.Keep;
                kept.Add(new TaskGateKeep(
                    agent,
                    "the shipped map names no technology for this agent, so no technology key can hold its gates; " +
                    "it keeps resolving under its own key"));
                continue;
            }

            if (review.TechnologyGates.ContainsKey(technology))
            {
                decisions[i] = Decision.Keep;
                kept.Add(new TaskGateKeep(
                    agent,
                    $"review.technology-gates.{technology} is already declared, so these gates are left for you to merge"));
                continue;
            }

            if (representatives.TryGetValue(technology, out int representative))
            {
                string other = block.Chunks[representative].Key;
                if (SameGates(Gates(review, agent), Gates(review, other)))
                {
                    decisions[i] = Decision.Drop;
                    folded.Add(new TaskGateMove(agent, technology));
                    continue;
                }

                // Two entries claim one technology with different gates. Picking either would drop the other's gates
                // without saying so, so both stay and the operator chooses.
                decisions[representative] = Decision.Keep;
                decisions[i] = Decision.Keep;
                _ = representatives.Remove(technology);
                _ = conflicted.Add(technology);
                _ = moved.RemoveAll(move => move.Agent == other);
                kept.Add(new TaskGateKeep(
                    other, $"its gates differ from '{agent}', and both map to technology '{technology}'; reconcile them"));
                kept.Add(new TaskGateKeep(
                    agent, $"its gates differ from '{other}', and both map to technology '{technology}'; reconcile them"));
                continue;
            }

            if (conflicted.Contains(technology))
            {
                decisions[i] = Decision.Keep;
                kept.Add(new TaskGateKeep(
                    agent, $"another entry's gates for technology '{technology}' differ; reconcile them"));
                continue;
            }

            representatives[technology] = i;
            decisions[i] = Decision.Move;
            moved.Add(new TaskGateMove(agent, technology));
        }

        return decisions;
    }

    private static IReadOnlyList<ReviewTaskGate> Gates(ReviewConfig review, string agent) =>
        review.TaskGates.TryGetValue(agent, out IReadOnlyList<ReviewTaskGate>? gates) ? gates : [];

    /// <summary>The technology sections to write, in the order their entries appear in the file.</summary>
    private static List<List<string>> Sections(
        Block block,
        Decision[] decisions,
        IReadOnlyDictionary<string, string> technologies,
        int entryIndent,
        string suffix)
    {
        List<List<string>> sections = [];
        HashSet<string> emitted = new(StringComparer.Ordinal);

        for (int i = 0; i < block.Chunks.Count; i++)
        {
            if (decisions[i] is not Decision.Move)
                continue;

            string technology = technologies[block.Chunks[i].Key];
            if (!emitted.Add(technology))
                continue;

            List<string> section = [.. block.Chunks[i].Trivia];
            section.Add(new string(' ', entryIndent) + technology + ":" + suffix);
            section.AddRange(block.Chunks[i].Body);
            sections.Add(section);
        }

        return sections;
    }

    /// <summary>
    /// The block's own header, renamed as the technology header, with every section under it. Renaming in place keeps
    /// the comment above the block and the block's position in the file exactly where the operator put them.
    /// </summary>
    private static List<string> RenamedRegion(Block block, List<List<string>> sections, string suffix)
    {
        List<string> region =
        [
            new string(' ', block.Mapping.Indent) + TechnologyGatesKey + ":" + suffix,
            .. block.HeaderTrivia
        ];

        AppendSections(region, sections, suffix, blankBeforeFirst: false);
        region.AddRange(block.Trailer);
        return region;
    }

    /// <summary>The block with only the entries that stay, followed by whatever <paramref name="inserted"/> adds.</summary>
    private static List<string> KeptRegion(
        string[] lines,
        Block block,
        Decision[] decisions,
        List<string> inserted)
    {
        List<string> region = [lines[block.Mapping.Header], .. block.HeaderTrivia];

        for (int i = 0; i < block.Chunks.Count; i++)
        {
            if (decisions[i] is not Decision.Keep)
                continue;

            region.AddRange(block.Chunks[i].Trivia);
            region.Add(block.Chunks[i].KeyLine);
            region.AddRange(block.Chunks[i].Body);
        }

        if (inserted.Count > 0 && region[^1].Trim().Length > 0 && inserted[0].Trim().Length > 0)
            region.Add(string.Empty);

        region.AddRange(inserted);
        region.AddRange(block.Trailer);
        return region;
    }

    /// <summary>The new sections appended to the block that already declares technologies.</summary>
    private static Edit Insertion(
        string[] lines,
        Mapping technologyGates,
        List<string> headerTrivia,
        List<List<string>> sections,
        string suffix)
    {
        List<string> insert = [.. headerTrivia];
        AppendSections(insert, sections, suffix, blankBeforeFirst: true);

        int at = ReadBlock(lines, technologyGates).LastContent + 1;
        return new Edit(at, at, insert);
    }

    /// <summary>
    /// Sections one per technology, separated by a blank line so the block reads as the blocks above it do.
    /// </summary>
    /// <param name="target">The lines the sections are appended to.</param>
    /// <param name="sections">One list of lines per technology, in the order they are written.</param>
    /// <param name="suffix">The line ending the file uses, written on the blank separator.</param>
    /// <param name="blankBeforeFirst">Whether a blank line separates the first section from the last line of the block.</param>
    private static void AppendSections(
        List<string> target,
        List<List<string>> sections,
        string suffix,
        bool blankBeforeFirst = true)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            List<string> section = sections[i];
            bool separated = i > 0 || blankBeforeFirst;
            if (separated && target.Count > 0 && target[^1].Trim().Length > 0 && section.Count > 0
                && section[0].Trim().Length > 0)
            {
                target.Add(suffix);
            }

            target.AddRange(section);
        }
    }

    private static List<string> Flatten(List<List<string>> sections, string suffix)
    {
        List<string> flat = [];
        AppendSections(flat, sections, suffix, blankBeforeFirst: false);
        return flat;
    }

    private static string Apply(string[] lines, List<Edit> edits)
    {
        // An insertion at a line sorts before the replacement of that same line, so the kept block's lines are read
        // from the cursor the insertion left behind.
        edits.Sort((left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.End.CompareTo(right.End));

        List<string> result = [];
        int cursor = 0;
        foreach (Edit edit in edits)
        {
            if (edit.Start > cursor)
                result.AddRange(lines[cursor..edit.Start]);

            result.AddRange(edit.Replacement);
            cursor = edit.End;
        }

        result.AddRange(lines[cursor..]);
        return string.Join('\n', result);
    }

    private static Block ReadBlock(string[] lines, Mapping mapping)
    {
        int entryIndent = -1;
        for (int i = mapping.Header + 1; i < mapping.End; i++)
        {
            if (!IsContent(lines[i]))
                continue;

            // The first content line under the block is an entry, so its indentation is the level the entries share.
            entryIndent = IndentOf(lines[i]);
            break;
        }

        List<int> starts = [];
        for (int i = mapping.Header + 1; i < mapping.End; i++)
        {
            if (IsEntry(lines[i], entryIndent))
                starts.Add(i);
        }

        if (starts.Count == 0)
            return new Block(mapping, [], [], [], mapping.Header, mapping.Indent + 2);

        int lastContent = mapping.End - 1;
        while (lastContent > starts[^1] && !IsContent(lines[lastContent]))
            lastContent--;

        List<int> bounds = [];
        for (int i = 0; i < starts.Count; i++)
        {
            int from = starts[i];
            if (i > 0)
            {
                int floor = starts[i - 1] + 1;
                while (from > floor && !IsContent(lines[from - 1]))
                    from--;
            }

            bounds.Add(from);
        }

        List<Chunk> chunks = [];
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? bounds[i + 1] : lastContent + 1;
            chunks.Add(new Chunk(
                [.. lines[bounds[i]..starts[i]]],
                KeyOf(lines[starts[i]]),
                lines[starts[i]],
                [.. lines[(starts[i] + 1)..end]]));
        }

        return new Block(
            mapping,
            [.. lines[(mapping.Header + 1)..starts[0]]],
            chunks,
            [.. lines[(lastContent + 1)..mapping.End]],
            lastContent,
            entryIndent);
    }

    /// <summary>A mapping whose key sits at <paramref name="indent"/> and whose body ends before <c>End</c>.</summary>
    private static Mapping? FindMapping(string[] lines, int from, int to, int indent, string key)
    {
        for (int i = from; i < to; i++)
        {
            if (!IsContent(lines[i]) || IndentOf(lines[i]) != indent || KeyOf(lines[i]) != key)
                continue;

            int end = i + 1;
            while (end < to && (!IsContent(lines[end]) || IndentOf(lines[end]) > indent))
                end++;

            return new Mapping(i, indent, end);
        }

        return null;
    }

    /// <summary>
    /// A direct child mapping of <paramref name="parent"/>, whatever indentation the file writes its children at.
    /// </summary>
    private static Mapping? FindChild(string[] lines, Mapping parent, string key)
    {
        int childIndent = -1;
        for (int i = parent.Header + 1; i < parent.End; i++)
        {
            if (!IsContent(lines[i]))
                continue;

            // The first content line under a mapping is a child, so its indentation is the level siblings share.
            if (childIndent < 0)
                childIndent = IndentOf(lines[i]);

            if (IndentOf(lines[i]) != childIndent || KeyOf(lines[i]) != key)
                continue;

            int end = i + 1;
            while (end < parent.End && (!IsContent(lines[end]) || IndentOf(lines[end]) > childIndent))
                end++;

            return new Mapping(i, childIndent, end);
        }

        return null;
    }

    private static bool IsEntry(string line, int indent) =>
        IsContent(line)
        && IndentOf(line) == indent
        && line.TrimEnd().EndsWith(':')
        && !line.TrimStart().StartsWith('-');

    private static bool IsContent(string line) => !IsBlank(line) && !IsComment(line);

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    private static int IndentOf(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;

        return indent;
    }

    /// <summary>A line's key without its colon: the agent slug or technology name a mapping declares.</summary>
    private static string KeyOf(string line) => line.Trim().TrimEnd(':');

    private enum Decision
    {
        /// <summary>The entry stays under its agent key.</summary>
        Keep,

        /// <summary>The entry becomes a technology key's entry.</summary>
        Move,

        /// <summary>An entry that moved already holds these gates.</summary>
        Drop
    }

    private sealed record Mapping(int Header, int Indent, int End);

    /// <summary>One entry: the lines above it that explain it, its key line, and its gates.</summary>
    private sealed record Chunk(IReadOnlyList<string> Trivia, string Key, string KeyLine, IReadOnlyList<string> Body);

    /// <summary>A gate mapping read as lines: its header, the comments under it, its entries, and the lines after it.</summary>
    private sealed record Block(
        Mapping Mapping,
        IReadOnlyList<string> HeaderTrivia,
        IReadOnlyList<Chunk> Chunks,
        IReadOnlyList<string> Trailer,
        int LastContent,
        int EntryIndent);

    /// <summary>Lines <c>[Start, End)</c> replaced by <c>Replacement</c>; <c>Start == End</c> inserts.</summary>
    private sealed record Edit(int Start, int End, IReadOnlyList<string> Replacement);
}
