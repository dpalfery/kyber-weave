namespace KyberWeave.Core.Review;

/// <summary>Which task's review folder a command works in, and how that folder was chosen.</summary>
/// <param name="Task">The folder name, empty when none could be chosen.</param>
/// <param name="Note">A line saying how the folder was chosen when the caller did not name it, or null.</param>
/// <param name="Error">Why none could be chosen, or null when one was.</param>
public sealed record ReviewTaskChoice(string Task, string? Note, string? Error)
{
    /// <summary>Whether a folder was chosen.</summary>
    public bool Chosen => Error is null;
}
