using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the source files under a repository root that the <c>{tests}</c> task-gate token resolves test classes from.</summary>
public interface ITestClassSources
{
    /// <summary>Opens the tree under a root and lists its source files.</summary>
    /// <param name="root">The repository root; null uses the nearest enclosing repository of this process.</param>
    /// <returns>The tree; null when there is no root, it is not a directory, or it cannot be named.</returns>
    /// <exception cref="InvalidOperationException">The root exists but is not accessible.</exception>
    TestSourceTree? Open(string? root);

    /// <summary>Reads the namespace a source file declares.</summary>
    /// <param name="filePath">The source file's path.</param>
    /// <returns>The namespace; null when the file declares none this reader can see, or cannot be read.</returns>
    string? ReadDeclaredNamespace(string filePath);
}
