namespace KyberWeave.Core.Review;

/// <summary>The verdict as a command's summary prints it.</summary>
public static class ReviewVerdictLabel
{
    /// <summary>The verdict's name; an undefined value prints as an invalid verdict rather than throwing.</summary>
    /// <remarks>
    /// The exit code already carries the invalid-verdict signal (2). A readable summary keeps the caller's own
    /// rendering and exit code reachable instead of crashing the command.
    /// </remarks>
    /// <param name="verdict">The verdict.</param>
    public static string Summary(ReviewVerdict verdict)
    {
        try
        {
            return Name(verdict);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"INVALID VERDICT ({verdict})";
        }
    }

    private static string Name(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "APPROVE",
        ReviewVerdict.RequestChanges => "REQUEST CHANGES",
        _ => throw new ArgumentOutOfRangeException(
            nameof(verdict),
            verdict,
            "The review verdict is not a defined value.")
    };
}
