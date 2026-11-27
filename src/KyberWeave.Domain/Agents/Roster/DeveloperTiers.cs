namespace KyberWeave.Core.Agents.Roster;

/// <summary>
/// The one developer roster: the three tier agents, the difficulty words and aliases that choose one,
/// and the model profile and display label each difficulty carries.
/// </summary>
/// <remarks>
/// The profile values follow <c>products/kyber-squad/profiles/models.yml</c>: <c>easy</c> targets
/// <c>fast</c>, <c>medium</c> (written <c>standard</c> in the model-targeting table) targets
/// <c>general</c>, and <c>hard</c> targets <c>dev-hard</c>.
/// </remarks>
public static class DeveloperTiers
{
    /// <summary>The first tier of the step-up ladder.</summary>
    public const string Easy = "dev-easy";

    /// <summary>The tier that stands for the developer kind; a task with no rating starts here.</summary>
    public const string Medium = "dev-medium";

    /// <summary>The last tier of the step-up ladder, which a failed attempt never climbs past.</summary>
    public const string Hard = "dev-hard";

    /// <summary>The <c>easy</c> difficulty word.</summary>
    public const string EasyDifficulty = "easy";

    /// <summary>The <c>medium</c> difficulty word.</summary>
    public const string MediumDifficulty = "medium";

    /// <summary>The <c>hard</c> difficulty word.</summary>
    public const string HardDifficulty = "hard";

    /// <summary>The model-targeting table's alias for <see cref="MediumDifficulty"/>.</summary>
    public const string Standard = "standard";

    /// <summary>The three developer agents in step-up order.</summary>
    public static IReadOnlyList<string> All { get; } = [Easy, Medium, Hard];

    /// <summary>The three legal difficulty words, in order.</summary>
    public static IReadOnlyList<string> Difficulties { get; } =
        [EasyDifficulty, MediumDifficulty, HardDifficulty];

    /// <summary>
    /// The model profile each tier targets, keyed by <see cref="EasyDifficulty"/>, <see cref="Standard"/> and
    /// <see cref="HardDifficulty"/>: <c>ModelTargetingRules</c> maps the <see cref="MediumDifficulty"/> rating to
    /// <see cref="Standard"/> before the lookup.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Profiles { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EasyDifficulty] = "fast",
            [Standard] = "general",
            [HardDifficulty] = "dev-hard",
        };

    /// <summary>The ordered profiles a retarget may climb, cheapest first.</summary>
    public static IReadOnlyList<string> Ladder { get; } = ["fast", "general", "dev-hard"];

    /// <summary>
    /// The developer tier for a task: <c>min(hard, rated difficulty + failed attempts)</c>. A task with no
    /// rating is a medium task, as the task file writes it, so a dispatch that belongs to no task also starts at medium.
    /// </summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    /// <param name="failedAttempts">How many attempts at this task already failed.</param>
    public static string For(string? difficulty, int failedAttempts)
    {
        int rated = difficulty?.Trim() switch
        {
            EasyDifficulty => 0,
            HardDifficulty => All.Count - 1,
            _ => 1,
        };

        // Long arithmetic: a retry count at the top of its range must clamp to the last tier, not wrap to a
        // negative index a few steps below zero.
        long climbed = Math.Min(All.Count - 1, rated + (long)Math.Max(failedAttempts, 0));
        return All[(int)climbed];
    }

    /// <summary>The architect's rating with the tier the rubric reads it as: <c>easy (standard)</c> and so on.</summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    public static string LabelFor(string? difficulty) => difficulty?.Trim() switch
    {
        EasyDifficulty => "easy (standard)",
        MediumDifficulty => "medium (intermediate)",
        HardDifficulty => "hard (advanced)",
        _ => difficulty ?? "(none)",
    };

    /// <summary>The rubric's own name for a rating's tier: <c>standard</c>, <c>intermediate</c> or <c>advanced</c>.</summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    public static string NameFor(string? difficulty) => difficulty?.Trim() switch
    {
        EasyDifficulty => "standard",
        MediumDifficulty => "intermediate",
        HardDifficulty => "advanced",
        _ => "unrated",
    };

    /// <summary>Whether <paramref name="value"/> is one of the three difficulty words.</summary>
    /// <param name="value">A difficulty value, or null.</param>
    public static bool IsDifficulty(string? value) =>
        value?.Trim() is EasyDifficulty or MediumDifficulty or HardDifficulty;
}
