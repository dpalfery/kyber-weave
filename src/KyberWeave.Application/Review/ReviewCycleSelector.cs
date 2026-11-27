using System.ComponentModel;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Finds the review folder and the recorded findings a command works from, over the recorded cycle folders.</summary>
/// <param name="cycles">Reads the recorded review cycle folders.</param>
/// <param name="git">Names the current branch and lists what changed since a commit.</param>
/// <param name="files">Reads the prior cycle's findings document.</param>
public sealed class ReviewCycleSelector(IReviewCycleStore cycles, IGitChangeReader git, IFileSystem files) : IReviewCycleSelector
{
    /// <inheritdoc />
    public ReviewTaskChoice ChooseTask(string tree, string scratchpad, string? given)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchpad);

        if (!string.IsNullOrWhiteSpace(given))
        {
            string task = TaskSlug.FindFolder(cycles.TaskFolders(scratchpad), given) ?? TaskSlug.Normalise(given);
            return task.Length > 0
                ? new ReviewTaskChoice(task, null, null)
                : new ReviewTaskChoice(string.Empty, null, $"{ReviewDiagnosticCodes.InvalidInput}: --task '{given}' has no letters or digits to name a folder with.");
        }

        IReadOnlyList<string> folders = cycles.TaskFolders(scratchpad);
        string branch = TaskSlug.Normalise(CurrentBranch(tree));
        string[] named = [.. folders.Where(folder => branch.Length > 0 && TaskSlug.Normalise(folder) == branch)];
        if (folders.Count == 1 || named.Length == 1)
        {
            string task = named.Length == 1 ? named[0] : folders[0];
            string note = $"task: {task} (the only review folder{(named.Length == 1 ? " for this branch" : string.Empty)}; pass --task to choose another)";
            return new ReviewTaskChoice(task, note, null);
        }

        if (folders.Count == 0 && branch.Length > 0)
            return new ReviewTaskChoice(branch, $"task: {branch} (no review folder yet; named for the current branch)", null);

        string error = $"{ReviewDiagnosticCodes.InvalidInput}: --task is needed: " + (folders.Count == 0
            ? "there is no review folder and no current branch."
            : $"several review folders exist ({string.Join(", ", folders)}).");
        return new ReviewTaskChoice(string.Empty, null, error);
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? ChangedSince(string tree, string commit)
    {
        try
        {
            return git.ResolveSince(tree, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public (bool Ok, IReadOnlyList<ReviewFinding> Recheck) ReadRecheck(
        ReviewCycleRecord prior,
        ReviewPolicy policy,
        DiagnosticReport report,
        string unreadableCode,
        Severity unreadableSeverity,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(files.ReadAllText(prior.FindingsPath));
            return (true, [.. findings.Findings.Where(policy.FixRoundTakes)]);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or InvalidOperationException)
        {
            report.Add(new Diagnostic(
                unreadableCode,
                unreadableSeverity,
                $"Could not read '{prior.FindingsPath}': {ex.Message}",
                subject,
                prior.FindingsPath,
                Hint: "Compute the prior cycle's verdict again with 'review verdict --out', then ask again."));
            return (false, []);
        }
    }

    /// <summary>The current branch, or an empty string when git cannot say.</summary>
    private string CurrentBranch(string root)
    {
        try
        {
            return git.CurrentBranch(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            return string.Empty;
        }
    }
}
