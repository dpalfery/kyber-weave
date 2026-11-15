using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     Model token resolution logic for Squad agents and harnesses.
/// </summary>
public static class SquadModelResolution
{
    private const string InheritSentinel = "inherit";

    /// <summary>
    ///     Resolves the concrete model token for a category and harness target.
    /// </summary>
    /// <param name="profiles">The model profiles to resolve against.</param>
    /// <param name="category">The profile category name.</param>
    /// <param name="target">The harness target name.</param>
    /// <returns>The resolved model token, or <c>null</c> if inherited or undefined.</returns>
    public static string? ResolveToken(SquadModelProfiles profiles, string category, string target)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        if (!profiles.Profiles.TryGetValue(category, out SquadModelProfile? profile)) return null;

        string? token;
        if (profile.HarnessModels.TryGetValue(target, out string? targetModel))
            token = targetModel;
        else
            token = profile.Default;

        if (token is null || string.Equals(token, InheritSentinel, StringComparison.OrdinalIgnoreCase)) return null;

        return token;
    }

    /// <summary>
    ///     Resolves the concrete model token for a category and harness target enum.
    /// </summary>
    /// <param name="profiles">The model profiles to resolve against.</param>
    /// <param name="category">The profile category name.</param>
    /// <param name="target">The harness target enum value.</param>
    /// <returns>The resolved model token, or <c>null</c> if inherited or undefined.</returns>
    public static string? ResolveToken(SquadModelProfiles profiles, string category, SquadTarget target)
    {
        return ResolveToken(profiles, category, SquadTargetCatalog.GetToken(target));
    }
}
