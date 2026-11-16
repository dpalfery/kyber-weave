namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the test classes that cover a changed file.</summary>
public interface ITestClassResolver
{
    /// <summary>Resolves covering test classes.</summary>
    /// <param name="repositoryRoot">The absolute repository root.</param>
    /// <param name="relativePath">The repository-relative changed file.</param>
    /// <returns>The classes found, or <see cref="TestClassResolution.None"/>; never a guess that names a missing class.</returns>
    TestClassResolution Resolve(string repositoryRoot, string relativePath);
}
