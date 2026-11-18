using System.ComponentModel;
using System.Text;

using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Core;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Settings for <c>kyber-weave review migrate-task-gates</c>.</summary>
/// <remarks>
/// Without <see cref="Write"/> the run is a preview: it computes and prints the rewrite and touches no byte. A
/// migration edits the file the whole team shares, so writing is named rather than assumed.
/// </remarks>
public sealed class ReviewMigrateTaskGatesSettings : CommandSettings
{
    /// <summary>The repository root holding the configuration to migrate.</summary>
    [CommandArgument(0, "[path]")]
    [Description("Repository root holding the configuration to migrate. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The configuration file, when the host keeps it somewhere other than the shipped location.</summary>
    [CommandOption("-c|--config <PATH>")]
    [Description("Configuration file, relative to the path or absolute. Defaults to .kyber-weave/kyber-weave.yml, then a root kyber-weave.yml.")]
    public string? Config { get; set; }

    /// <summary>Whether the printed rewrite is written.</summary>
    [CommandOption("--write")]
    [Description("Write the rewrite. Without it the change is printed and nothing is written.")]
    public bool Write { get; set; }
}

/// <summary>
///     Rewrites a host's agent-keyed task gates as technology-keyed ones, so the gates keep running when the agent
///     that wrote them is deleted.
/// </summary>
/// <remarks>
/// <para>
///     The removal phase deletes the fourteen technology agents. A host's <c>review.task-gates.&lt;agent&gt;</c>
///     entries keep resolving through the shipped map for two minor releases, and this verb is how the operator
///     moves them to <c>review.technology-gates.&lt;technology&gt;</c> before that window closes. The warning
///     <c>docs validate</c> prints for such an entry under the tiered roster names this command.
/// </para>
/// <para>
///     What it leaves alone is the point of the acceptance criteria: an entry moves only when the shipped map names
///     a technology for its agent, the host has not declared that technology already, and no other entry that moved
///     holds a different gate list. Everything else stays under its own key and is reported with the reason, because
///     an entry that is left is still an entry that resolves.
/// </para>
/// <para>
///     Exit codes: <c>0</c> for a preview, a completed migration, or nothing to do; <c>1</c> when the configuration
///     does not load, when no configuration is found, or when the rewrite would not load as a configuration — in
///     which case no byte is written.
/// </para>
/// </remarks>
/// <param name="configs">Resolves the configuration file the repository already has.</param>
/// <param name="files">Reads the file's bytes and writes the rewrite.</param>
/// <param name="tree">Answers whether a directory exists and whether a link leads out of the repository.</param>
public sealed class ReviewMigrateTaskGatesCommand(
    IKyberWeaveConfigRepository configs,
    IWritableFileSystem files,
    IRepositoryTreeProbe tree)
    : Command<ReviewMigrateTaskGatesSettings>
{
    /// <summary>The marker that turns a line of the printed change into a removed line.</summary>
    private const char Removed = '-';

    /// <summary>The marker that turns a line of the printed change into an added line.</summary>
    private const char Added = '+';

    /// <summary>The marker on a line the migration does not touch, printed for context.</summary>
    private const char Unchanged = ' ';

    /// <summary>The marker on a run of unchanged lines the change left out of the printed diff.</summary>
    private const char Elided = '~';

    /// <summary>How many untouched lines are printed beside a changed one.</summary>
    private const int Context = 3;

    /// <summary>The most diff cells computed; beyond it the change is printed as one removed and one added run.</summary>
    private const int MaxDiffCells = 4_000_000;

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        ReviewMigrateTaskGatesSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tree);

        string root = Path.GetFullPath(settings.Path);
        string? path = ResolveConfigPath(root, settings.Config);
        if (path is null)
        {
            return Refuse(
                $"no kyber-weave.yml was found under '{root}'. Name the repository that holds one, or pass --config.");
        }

        string text;
        bool bom;
        try
        {
            byte[] bytes = files.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse($"'{path}' could not be read. {exception.Message}");
        }

        KyberWeaveConfig before;
        try
        {
            before = KyberWeaveConfigAssembler.FromRepositoryYaml(text, root, Facts());
        }
        catch (YamlException exception)
        {
            return Refuse(
                $"'{path}' does not load, so there is no configuration to migrate from. {exception.Message}");
        }

        TaskGateMigrationPlan plan = ReviewTaskGateMigration.Plan(text, before.Review);
        bool reordered = false;

        // A file that keeps some agent-keyed entries gains a second mapping, and the two mappings can be written in
        // either order. Only a YAML anchor can tell them apart: an alias has to follow its definition. So the first
        // order that loads is the one written, and one that keeps an anchor valid is reported as such.
        if (plan.Changes && Load(plan.Text, root, Facts()) is null && plan.Kept.Count > 0)
        {
            TaskGateMigrationPlan retry = ReviewTaskGateMigration.Plan(text, before.Review, technologyBlockFirst: true);
            if (Load(retry.Text, root, Facts()) is not null)
            {
                plan = retry;
                reordered = true;
            }
        }

        PrintHeader(path, settings.Write, plan);

        if (reordered)
        {
            AnsiConsole.MarkupLine(
                "[grey]The technology block is written above review.task-gates, which is the order the file's YAML " +
                "anchors load in.[/]");
        }

        PrintPlan(plan);

        if (!plan.Changes)
        {
            AnsiConsole.MarkupLine(
                "[grey]nothing to do: no review.task-gates entry can move to a technology key. " +
                "An entry that stays resolves under its own key for two minor releases.[/]");
            return 0;
        }

        if (Check(plan, before, root, Facts()) is { } refusal)
            return Refuse(refusal);

        AnsiConsole.WriteLine();
        PrintChange(text, plan.Text);
        AnsiConsole.WriteLine();

        if (!settings.Write)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Nothing was written. Add --write to rewrite {Markup.Escape(path)}.[/]");
            return 0;
        }

        // The configuration is committed and team-shared, so it is written where the repository
        // holds it: a link leading out of the tree would put the rewrite somewhere no one else
        // committed it.
        string relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || tree.LeadsOutsideThroughSymlink(root, relative))
        {
            return Refuse(
                $"'{path}' is, or is reached through, a symbolic link that leads outside '{root}'. " +
                "A repository-scope configuration is written inside the repository, so nothing was written.");
        }

        try
        {
            byte[] body = new UTF8Encoding(false).GetBytes(plan.Text);
            files.WriteAllBytesAtomic(path, bom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refuse($"'{path}' could not be written. {exception.Message}");
        }

        AnsiConsole.MarkupLine($"[green]written[/] {Markup.Escape(path)}");
        return 0;
    }

    private ReviewWorkingDirectoryFacts Facts() =>
        new(tree.DirectoryExists, tree.LeadsOutsideThroughSymlink);

    /// <summary>
    /// Checks the rewrite before it can reach the disk: it has to load, and every entry has to still hold the gates it
    /// held before, whether it moved, folded into another entry, or stayed where it was.
    /// </summary>
    /// <returns>The reason the rewrite must not be written, or null when it may be.</returns>
    private static string? Check(
        TaskGateMigrationPlan plan,
        KyberWeaveConfig before,
        string root,
        ReviewWorkingDirectoryFacts facts)
    {
        KyberWeaveConfig? after = Load(plan.Text, root, facts);
        if (after is null)
        {
            return "The rewrite would not load as a configuration, so nothing was written. An anchor definition a " +
                   "folded entry held, or an alias an entry still in the file points at, may be the reason.";
        }

        foreach (TaskGateMove move in plan.Moved.Concat(plan.Folded))
        {
            if (!after.Review.TechnologyGates.TryGetValue(move.Technology, out IReadOnlyList<ReviewTaskGate>? gates)
                || !before.Review.TaskGates.TryGetValue(move.Agent, out IReadOnlyList<ReviewTaskGate>? expected)
                || !ReviewTaskGateMigration.SameGates(gates, expected))
            {
                return $"The rewrite does not hold {move.Agent}'s gates under review.technology-gates.{move.Technology}, " +
                       "so its gates would change. Nothing was written.";
            }
        }

        foreach (TaskGateKeep keep in plan.Kept)
        {
            if (!after.Review.TaskGates.TryGetValue(keep.Agent, out IReadOnlyList<ReviewTaskGate>? gates)
                || !before.Review.TaskGates.TryGetValue(keep.Agent, out IReadOnlyList<ReviewTaskGate>? expected)
                || !ReviewTaskGateMigration.SameGates(gates, expected))
            {
                return $"The rewrite changes {keep.Agent}'s own entry, which it must not. Nothing was written.";
            }
        }

        return null;
    }

    /// <summary>The configuration the rewritten text parses to, or null when it does not load.</summary>
    private static KyberWeaveConfig? Load(string text, string root, ReviewWorkingDirectoryFacts facts)
    {
        try
        {
            return KyberWeaveConfigAssembler.FromRepositoryYaml(text, root, facts);
        }
        catch (YamlException)
        {
            return null;
        }
    }

    private static void PrintHeader(string path, bool write, TaskGateMigrationPlan plan)
    {
        string run = write ? "writing the rewrite" : "preview, so nothing will be written";

        AnsiConsole.MarkupLine(
            $"[bold]Agent-keyed task gates to technology-keyed task gates[/] for {Markup.Escape(path)}: " +
            $"{Markup.Escape(run)}.");
        AnsiConsole.MarkupLine(write
            ? "[grey]review.gates and every key outside review.task-gates are left as they are.[/]"
            : $"[grey]Add --write to rewrite the file. {plan.Moved.Count} entr{(plan.Moved.Count == 1 ? "y" : "ies")} " +
              "would move.[/]");
        AnsiConsole.WriteLine();
    }

    private static void PrintPlan(TaskGateMigrationPlan plan)
    {
        foreach (TaskGateMove move in plan.Moved)
            AnsiConsole.MarkupLine($"  [yellow]moved[/] {Markup.Escape(move.Agent)} to {Markup.Escape(move.Technology)}");

        foreach (TaskGateMove fold in plan.Folded)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]folded[/] {Markup.Escape(fold.Agent)} into {Markup.Escape(fold.Technology)}: " +
                "its gates are the same as an entry that moved");
        }

        foreach (TaskGateKeep keep in plan.Kept)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]kept[/] {Markup.Escape(keep.Agent)}: {Markup.Escape(TerminalText.Line(keep.Reason))}");
        }
    }

    /// <summary>Prints the change the rewrite would make, as removed and added lines.</summary>
    private static void PrintChange(string before, string after)
    {
        foreach (DiffLine line in Condense(Diff(before.Split('\n'), after.Split('\n'))))
        {
            if (line.Mark == Elided)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line.Text)}[/]");
                continue;
            }

            string escaped = Markup.Escape(TerminalText.Line(line.Text));
            string rendered = $"{line.Mark}{escaped}";

            AnsiConsole.MarkupLine(line.Mark switch
            {
                Removed => $"[red]{rendered}[/]",
                Added => $"[green]{rendered}[/]",
                _ => $"[grey]{rendered}[/]"
            });
        }
    }

    /// <summary>
    /// The diff cut down to its changed lines and a few lines beside them, so a one-block rewrite of a long file reads
    /// as the block it changes rather than as the whole file.
    /// </summary>
    private static List<DiffLine> Condense(List<DiffLine> lines)
    {
        List<DiffLine> condensed = [];
        int index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Mark != Unchanged)
            {
                condensed.Add(lines[index]);
                index++;
                continue;
            }

            int start = index;
            while (index < lines.Count && lines[index].Mark == Unchanged)
                index++;

            int run = index - start;
            bool afterChange = condensed.Count > 0;
            bool beforeChange = index < lines.Count;

            if (afterChange && beforeChange && run > 2 * Context)
            {
                condensed.AddRange(lines.Skip(start).Take(Context));
                condensed.Add(new DiffLine(Elided, $"... {run - (2 * Context)} unchanged lines"));
                condensed.AddRange(lines.Skip(index - Context).Take(Context));
            }
            else if (afterChange && beforeChange)
            {
                condensed.AddRange(lines.Skip(start).Take(run));
            }
            else if (afterChange)
            {
                condensed.AddRange(lines.Skip(start).Take(Context));
            }
            else if (beforeChange)
            {
                condensed.AddRange(lines.Skip(index - Math.Min(Context, run)).Take(Math.Min(Context, run)));
            }
        }

        return condensed;
    }

    /// <summary>The lines that differ, with the unchanged lines that surround them, in file order.</summary>
    /// <remarks>
    /// A longest-common-subsequence walk, so the operator reads which lines moved rather than two copies of the whole
    /// block. The cost is quadratic, which is why the cell count is capped: past the cap the change is printed as one
    /// removed run followed by one added run, which is still the truth, only less tidy.
    /// </remarks>
    private static List<DiffLine> Diff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        if ((long)before.Count * after.Count > MaxDiffCells)
        {
            return
            [
                .. before.Select(line => new DiffLine(Removed, line)),
                .. after.Select(line => new DiffLine(Added, line))
            ];
        }

        int[][] common = new int[before.Count + 1][];
        for (int i = 0; i < common.Length; i++)
            common[i] = new int[after.Count + 1];

        for (int i = before.Count - 1; i >= 0; i--)
        {
            for (int j = after.Count - 1; j >= 0; j--)
            {
                common[i][j] = Trim(before[i]) == Trim(after[j])
                    ? common[i + 1][j + 1] + 1
                    : Math.Max(common[i + 1][j], common[i][j + 1]);
            }
        }

        List<DiffLine> lines = [];
        int left = 0;
        int right = 0;
        while (left < before.Count && right < after.Count)
        {
            if (Trim(before[left]) == Trim(after[right]))
            {
                lines.Add(new DiffLine(Unchanged, before[left]));
                left++;
                right++;
            }
            else if (common[left + 1][right] >= common[left][right + 1])
            {
                lines.Add(new DiffLine(Removed, before[left]));
                left++;
            }
            else
            {
                lines.Add(new DiffLine(Added, after[right]));
                right++;
            }
        }

        while (left < before.Count)
            lines.Add(new DiffLine(Removed, before[left++]));

        while (right < after.Count)
            lines.Add(new DiffLine(Added, after[right++]));

        return lines;
    }

    /// <summary>A line without its line ending, which is what makes two spellings of the same line compare equal.</summary>
    private static string Trim(string line) => line.TrimEnd('\r');

    private string? ResolveConfigPath(string root, string? config)
    {
        if (!string.IsNullOrWhiteSpace(config))
        {
            string named = Path.IsPathRooted(config) ? config : Path.Combine(root, config);
            return files.FileExists(named) ? Path.GetFullPath(named) : null;
        }

        return configs.FindConfigPath(root);
    }

    private static int Refuse(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(KyberWeaveConfigDiagnosticCodes.LoadError)}[/] " +
                               Markup.Escape(TerminalText.Line(message)));
        return 1;
    }

    private sealed record DiffLine(char Mark, string Text);
}
