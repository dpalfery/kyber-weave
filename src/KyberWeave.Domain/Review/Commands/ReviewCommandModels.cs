using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Review;

/// <summary>What a review command's use case hands back: the exit code, the report to render, and the lines after it.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Report">The report to render, or null when the run produced none.</param>
/// <param name="Epilogue">Lines printed after the report is rendered.</param>
/// <param name="ClientError">A refused input the caller formats in its own style; the run did nothing else.</param>
public sealed record ReviewCommandResult(
    int ExitCode,
    DiagnosticReport? Report = null,
    IReadOnlyList<string>? Epilogue = null,
    string? ClientError = null)
{
    /// <summary>The exit code for an input the command refuses before anything runs.</summary>
    public const int ClientInputExitCode = 2;

}

/// <summary>The options of <c>review gates</c>, as the use case reads them.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="Config">An explicit <c>--config</c> path, or null.</param>
/// <param name="Out">The <c>--out</c> path as given, or null.</param>
/// <param name="Agent">The <c>--agent</c> name, or null.</param>
/// <param name="BaseRef">The <c>--base</c> ref, or null.</param>
/// <param name="Paths">The <c>--paths</c> given.</param>
/// <param name="Gates">The <c>--gate</c> ids given.</param>
/// <param name="All">Whether <c>--all</c> was given.</param>
/// <param name="Deadline">Whether <c>--deadline</c> was given.</param>
/// <param name="Council">Whether <c>--council</c> was given.</param>
/// <param name="StopOnFailure">Whether <c>--stop-on-failure</c> was given.</param>
/// <param name="Full">Whether <c>--full</c> was given.</param>
/// <param name="TaskSlug">The <c>--task</c> slug, or null.</param>
/// <param name="Fix">Whether <c>--fix</c> was given.</param>
/// <param name="Red">Whether <c>--red</c> was given.</param>
/// <param name="HumanOutput">Whether the output format is meant for a person, so the run prints its guidance lines.</param>
/// <param name="Interactive">Whether the process has a person's terminal on standard input.</param>
public sealed record ReviewGatesRequest(
    string Path,
    string? Config,
    string? Out,
    string? Agent,
    string? BaseRef,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Gates,
    bool All,
    bool Deadline,
    bool Council,
    bool StopOnFailure,
    bool Full,
    string? TaskSlug,
    bool Fix,
    bool Red,
    bool HumanOutput,
    bool Interactive)
{
    /// <summary>Whether an option that only makes sense for one agent was given.</summary>
    public bool HasAgentOnlyOptions => TaskSlug is not null || Fix || Red;
}

/// <summary>The options of <c>review seats</c>.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="Config">An explicit <c>--config</c> path, or null.</param>
/// <param name="Task">The <c>--task</c> id, or null.</param>
/// <param name="BaseRef">The <c>--base</c> ref, or null.</param>
/// <param name="HandBack">Whether each block names the seat file.</param>
/// <param name="Full">Whether a later cycle seats every applicable lens.</param>
/// <param name="NoDecisionModel">Whether the decision model is left out.</param>
public sealed record ReviewSeatsRequest(
    string Path,
    string? Config,
    string? Task,
    string? BaseRef,
    bool HandBack,
    bool Full,
    bool NoDecisionModel);

/// <summary>The options of <c>review cycle</c>.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="Config">An explicit <c>--config</c> path, or null.</param>
/// <param name="Task">The <c>--task</c> id, or null.</param>
/// <param name="BaseRef">The <c>--base</c> ref, or null.</param>
/// <param name="Json">Whether the answer is a JSON document rather than lines.</param>
public sealed record ReviewCycleRequest(string Path, string? Config, string? Task, string? BaseRef, bool Json);

/// <summary>The options of <c>review duplicates</c>.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="Config">An explicit <c>--config</c> path, or null.</param>
/// <param name="Out">The <c>--out</c> path as given, or null.</param>
public sealed record ReviewDuplicatesRequest(string Path, string? Config, string? Out);

/// <summary>The options of <c>review suppressions</c>.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="BaseRef">The <c>--base</c> ref, or null.</param>
public sealed record ReviewSuppressionsRequest(string Path, string? BaseRef);

/// <summary>The options of <c>review catalogue</c>.</summary>
/// <param name="Path">The repository path as given.</param>
/// <param name="Config">An explicit <c>--config</c> path, or null.</param>
/// <param name="Technologies">The <c>--technology</c> slugs given.</param>
public sealed record ReviewCatalogueRequest(string Path, string? Config, IReadOnlyList<string> Technologies);
