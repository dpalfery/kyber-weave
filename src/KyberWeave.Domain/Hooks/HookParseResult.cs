namespace KyberWeave.Domain.Hooks;

/// <summary>The outcome of reading hook JSON: an edit, a clean skip, or a problem worth telling the agent about.</summary>
/// <param name="Edit">The edited file, or null when the input is not an edit or is invalid.</param>
/// <param name="Problem">Why the input could not be read, or null when it was read or is simply not an edit.</param>
public sealed record HookParseResult(HookEditEvent? Edit, string? Problem)
{
    /// <summary>Gets the result for well-formed input that names no edited file.</summary>
    public static HookParseResult NotAnEdit { get; } = new(null, null);

    /// <summary>Creates the result for input that could not be read.</summary>
    /// <param name="problem">What was wrong, in one sentence.</param>
    /// <returns>A result carrying <paramref name="problem"/>.</returns>
    public static HookParseResult Invalid(string problem) => new(null, problem);
}
