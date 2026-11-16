namespace KyberWeave.Arbiter.Hooks;

/// <summary>The owner lines a dispatch claim carries.</summary>
public static class DispatchClaimOwners
{
    /// <summary>The owner line of a claim taken by the project-scope hook.</summary>
    public const string Project = "project";

    /// <summary>The owner line of a claim taken by the user-level hook.</summary>
    public const string Global = "global";
}
