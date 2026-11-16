namespace KyberWeave.Domain.Hooks;

/// <summary>What is known about the project a changed file belongs to.</summary>
/// <param name="Root">The absolute repository root; commands run here.</param>
/// <param name="Marker">The repository-relative solution file above the changed file, or null.</param>
/// <param name="Project">The repository-relative project file nearest the changed file, or null.</param>
/// <param name="TestProject">The repository-relative project holding the covering tests, or null.</param>
/// <param name="TestClasses">Fully qualified names of test classes covering the file; empty when none.</param>
public sealed record ProjectFacts(
    string Root,
    string? Marker,
    string? Project,
    string? TestProject,
    IReadOnlyList<string> TestClasses);
