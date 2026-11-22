namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Retired Squad skills that <c>squad doctor</c> still warns about.
/// </summary>
/// <remarks>
/// A retired skill's directory can still hold a copy <c>squad update</c> left behind: one the
/// operator edited, or one the deployment never managed. Both keep feeding workers rules that
/// moved elsewhere, so doctor names every one it finds. <c>second-brain</c> moved to the
/// Kyber-Docs skills deployed by <c>docs init</c>. The technology skills' rules moved into the
/// standards templates a worker receives through
/// <c>docs_get_coding_standard_for_technology</c>. The remedy is the same either way: delete the
/// stale directory, then run <c>squad update</c> to reconcile the receipt.
/// </remarks>
public static class SquadRetiredSkills
{
    /// <summary>The retired Squad skill that moved to Kyber-Docs.</summary>
    public const string SecondBrain = "second-brain";

    /// <summary>The Kyber-Docs skill prefix that replaces <see cref="SecondBrain"/>.</summary>
    public const string NewHomeSkillPrefix = "kyber-docs-";

    /// <summary>The Kyber-Docs skill that now owns the retired skill's workflow.</summary>
    public const string NewHomeSkill = "kyber-docs-onboarding-and-assessment";

    private const string SecondBrainNewHome =
        "The workflow moved to the Kyber-Docs skills (`" + NewHomeSkillPrefix + "*`, see `" +
        NewHomeSkill + "`); run `docs init` to deploy them.";

    /// <summary>Every retired skill doctor reports, each with where its content went.</summary>
    public static IReadOnlyList<SquadRetiredSkill> RetiredSkills { get; } =
    [
        new SquadRetiredSkill(SecondBrain, SecondBrainNewHome),
        TechnologySkill("csharp-dev", "csharp"),
        TechnologySkill("dal-dev", "data-access-layer"),
        TechnologySkill("maui-dev", "maui"),
        TechnologySkill("typescript-dev", "typescript"),
        TechnologySkill("rust-dev", "rust"),
        TechnologySkill("python-dev", "python"),
        TechnologySkill("go-dev", "go"),
        TechnologySkill("github-devops", "github-actions", "csharp"),
        TechnologySkill("csp-security", "csharp")
    ];

    /// <summary>Every retired skill name doctor reports.</summary>
    public static IReadOnlyList<string> RetiredSkillNames { get; } =
        [.. RetiredSkills.Select(skill => skill.Name)];

    /// <summary>Returns the retired skill named <paramref name="name"/>, or null.</summary>
    public static SquadRetiredSkill? Find(string name) =>
        RetiredSkills.FirstOrDefault(skill => string.Equals(skill.Name, name, StringComparison.Ordinal));

    /// <summary>Returns the retired skill a receipt-relative path belongs to, or null.</summary>
    /// <remarks>
    /// True only when one full path segment equals a retired skill name. Substring matching would
    /// flag lookalikes such as <c>csharp-dev-extra</c> as retired state.
    /// </remarks>
    public static SquadRetiredSkill? FindByPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        foreach (string segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Find(segment) is { } skill)
            {
                return skill;
            }
        }

        return null;
    }

    /// <summary>Returns the receipt files that still claim a retired skill, each with that skill.</summary>
    public static IReadOnlyList<SquadRetiredSkillClaim> FindRetainedReceiptPaths(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        List<SquadRetiredSkillClaim> retained = [];
        foreach (SquadOwnedFile file in receipt.Files)
        {
            if (FindByPath(file.RelativePath) is { } skill)
            {
                retained.Add(new SquadRetiredSkillClaim(skill, file.RelativePath));
            }
        }

        return retained;
    }

    /// <summary>Returns whether a receipt-relative path belongs to a retired skill.</summary>
    public static bool IsRetiredSkillPath(string relativePath) => FindByPath(relativePath) is not null;

    /// <summary>
    /// Builds a technology skill's retirement entry. Its rules live in one or more standards
    /// templates, so the sentence names every template and the command that adopts them.
    /// </summary>
    private static SquadRetiredSkill TechnologySkill(string name, params string[] technologies)
    {
        string templates = technologies.Length == 1
            ? $"the `{technologies[0]}` standards template"
            : $"the {JoinWithAnd(technologies.Select(technology => $"`{technology}`"))} standards templates";
        string adoptBe = technologies.Length == 1 ? "it" : "them";
        string standards = technologies.Length == 1
            ? technologies[0]
            : $"\"{string.Join(' ', technologies)}\"";
        return new SquadRetiredSkill(
            name,
            $"The rules moved to {templates}, which a worker receives through " +
            $"`docs_get_coding_standard_for_technology`; adopt {adoptBe} with " +
            $"`kyber-weave docs init . --standards {standards}`.");
    }

    private static string JoinWithAnd(IEnumerable<string> values)
    {
        string[] items = [.. values];
        return items.Length switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => $"{string.Join(", ", items[..^1])}, and {items[^1]}"
        };
    }
}
