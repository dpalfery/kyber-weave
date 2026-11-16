namespace KyberWeave.Core.RunStart;

/// <summary>Where a run creates the branch its work belongs on.</summary>
public enum RunStartLocation
{
    /// <summary>The checkout the run was started in, which is clean and which no other worktree shares.</summary>
    InPlace,

    /// <summary>A worktree of its own, because that checkout is dirty or another worktree shares it.</summary>
    InWorktree
}

/// <summary>
///     The decisions a <c>kyber-weave run start</c> reaches before it touches git: the branch to name, the
///     base to start it from, and whether it may take the operator's checkout.
/// </summary>
public sealed record RunStartPlan(
    string Branch,
    string BaseBranch,
    string BaseRef,
    bool Resumed,
    RunStartLocation Location);
