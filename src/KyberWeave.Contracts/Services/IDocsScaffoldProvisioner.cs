namespace KyberWeave.Contracts.Services;

/// <summary>Creates the documentation scaffold an install needs on a project root that has no host config yet.</summary>
public interface IDocsScaffoldProvisioner
{
    /// <summary>Creates the scaffold under <paramref name="root"/>, leaving an existing one alone.</summary>
    /// <param name="root">The project root.</param>
    void EnsureProjectScaffold(string root);
}
