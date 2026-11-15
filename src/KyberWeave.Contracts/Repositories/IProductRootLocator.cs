namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds a product's artifact tree inside a repository checkout.</summary>
public interface IProductRootLocator
{
    /// <summary>Locates a product tree directly under a checkout.</summary>
    /// <param name="workingDirectory">The checkout root. Only this directory is looked in; no parent is searched.</param>
    /// <param name="productRelativePath">The tree's path relative to the checkout, with forward slashes (for example <c>products/kyber-utilities/statusline</c>).</param>
    /// <returns>The absolute path of the tree, or null when the checkout holds no solution file beside it or the tree is missing.</returns>
    string? Locate(string workingDirectory, string productRelativePath);
}
