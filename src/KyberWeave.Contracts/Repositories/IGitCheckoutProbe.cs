namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads what a checkout says about itself: its branch, its git directory, its revision and whether it is dirty.</summary>
public interface IGitCheckoutProbe
{
    /// <summary>
    /// A fingerprint of the local git tip and index, so a commit, merge or checkout that leaves the documents untouched
    /// still moves it. It covers <c>HEAD</c>, the <c>index</c>, the loose ref file a symbolic <c>HEAD</c> names and
    /// <c>packed-refs</c> in the git directory the repository's worktrees share, because a branch tip can move with
    /// <c>HEAD</c> unchanged. Zero when the root is not a checkout.
    /// </summary>
    /// <param name="root">The checkout root.</param>
    long Fingerprint(string root);

    /// <summary>The branch the checkout is on and the git directory it shares with the repository's other worktrees.</summary>
    /// <param name="root">The checkout root.</param>
    GitCheckoutLocation ReadLocation(string root);

    /// <summary>The short revision and the dirty marker, each <c>unavailable</c> or <c>unknown</c> when git cannot say.</summary>
    /// <param name="root">The checkout root.</param>
    GitCheckoutRevision ReadRevision(string root);
}

/// <summary>Where a checkout is: its branch and the git directory shared by every worktree of the repository.</summary>
/// <param name="Branch">The branch name, <c>detached</c>, or <c>unavailable</c>.</param>
/// <param name="Repository">The shared git directory, or <c>unavailable</c>.</param>
public sealed record GitCheckoutLocation(string Branch, string Repository);

/// <summary>The revision a checkout is at.</summary>
/// <param name="Revision">The short revision, or <c>unavailable</c>.</param>
/// <param name="Dirty"><c>yes</c>, <c>no</c> or <c>unknown</c>.</param>
public sealed record GitCheckoutRevision(string Revision, string Dirty);
