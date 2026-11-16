namespace KyberWeave.Core.Review;

/// <summary>What probing a repository root found: whether it is a directory, and why it could not be listed.</summary>
/// <param name="Exists">Whether the root is an existing directory.</param>
/// <param name="AccessError">Why an existing directory could not be listed (a permission or I/O failure); null when it could.</param>
public sealed record RepositoryRootAccess(bool Exists, string? AccessError = null)
{
    /// <summary>A root that exists and can be listed.</summary>
    public static RepositoryRootAccess Accessible { get; } = new(true);
}
