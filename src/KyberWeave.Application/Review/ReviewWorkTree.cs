namespace KyberWeave.Core.Review;

/// <summary>Where a review's state lives, from names alone: the default base ref and the scratchpad folder of a work tree.</summary>
public static class ReviewWorkTree
{
    /// <summary>The ref the change's base is resolved against when none is named.</summary>
    public const string DefaultBase = "origin/HEAD";

    /// <summary>The git entry that marks a work tree's root.</summary>
    public const string GitEntry = ".git";

    private const string ScratchpadFolder = ".agent_scratchpad";

    /// <summary>The <c>.agent_scratchpad</c> folder of <paramref name="workTree"/>.</summary>
    public static string Scratchpad(string workTree) => Path.Combine(workTree, ScratchpadFolder);
}
