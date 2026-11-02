namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Retired Squad skills that <c>squad doctor</c> still warns about.
/// </summary>
/// <remarks>
/// <c>second-brain</c> moved out of Squad to the Kyber-Docs skills deployed by
/// <c>docs init</c>. A deployed target tree can still hold its directory without
/// receipt cover (unmanaged), or a receipt can still claim its files (retained);
/// both are warnings rather than failures because the stale copy still works.
/// The remedy is the same either way: delete the stale directory, then run
/// <c>squad update</c> to reconcile the receipt.
/// </remarks>
public static class SquadRetiredSkills
{
    /// <summary>The retired Squad skill that moved to Kyber-Docs.</summary>
    public const string SecondBrain = "second-brain";

    /// <summary>Every retired skill name doctor reports.</summary>
    public static IReadOnlyList<string> RetiredSkillNames { get; } = [SecondBrain];

    /// <summary>The Kyber-Docs skill prefix that replaces the retired skills.</summary>
    public const string NewHomeSkillPrefix = "kyber-docs-";

    /// <summary>The Kyber-Docs skill that now owns the retired skill's workflow.</summary>
    public const string NewHomeSkill = "kyber-docs-onboarding-and-assessment";

    /// <summary>Returns the receipt files that still claim a retired skill.</summary>
    public static IReadOnlyList<string> FindRetainedReceiptPaths(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        List<string> retained = [];
        foreach (SquadOwnedFile file in receipt.Files)
        {
            foreach (string retired in RetiredSkillNames)
            {
                if (IsRetiredSkillPath(file.RelativePath, retired))
                {
                    retained.Add(file.RelativePath);
                    break;
                }
            }
        }

        return retained;
    }

    /// <summary>Returns whether a receipt-relative path belongs to a retired skill.</summary>
    public static bool IsRetiredSkillPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return RetiredSkillNames.Any(retired => IsRetiredSkillPath(relativePath, retired));
    }

    /// <summary>
    /// True when one full path segment equals the retired skill name. Substring
    /// matching would flag lookalikes such as <c>my-second-brainstorm</c> as retained
    /// <c>second-brain</c> state.
    /// </summary>
    private static bool IsRetiredSkillPath(string relativePath, string retired) =>
        relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(retired, StringComparison.Ordinal));
}
