namespace KyberWeave.Contracts.Repositories;

/// <summary>The test classes that cover a changed file.</summary>
/// <param name="Classes">Fully qualified class names, each verified to exist in a source file.</param>
/// <param name="ProjectPath">The repository-relative project file that holds them, or null when there are none.</param>
public sealed record TestClassResolution(IReadOnlyList<string> Classes, string? ProjectPath)
{
    /// <summary>Gets the result for a file nothing covers.</summary>
    public static TestClassResolution None { get; } = new([], null);
}
