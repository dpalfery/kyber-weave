using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;

namespace KyberWeave.Contracts.Services;

/// <summary>Previews, merges, validates and reads the one managed documentation glossary.</summary>
public interface IManagedGlossary
{
    /// <summary>Builds the exact Markdown a write would produce without changing disk.</summary>
    /// <param name="proposals">The proposed senses.</param>
    /// <returns>The merged document and what changed.</returns>
    GlossaryUpdateResult Preview(IReadOnlyList<GlossaryProposal> proposals);

    /// <summary>Atomically writes the glossary when the conservative merge changes it.</summary>
    /// <param name="proposals">The proposed senses.</param>
    /// <returns>The merged document and what changed.</returns>
    GlossaryUpdateResult Write(IReadOnlyList<GlossaryProposal> proposals);

    /// <summary>Validates the configured glossary's managed structure and approved scopes.</summary>
    /// <returns>The findings.</returns>
    DiagnosticReport Validate();

    /// <summary>Reads the glossary.</summary>
    /// <returns>The approved glossary and every term.</returns>
    ManagedGlossaryLoadResult Load();

    /// <summary>Looks one term up in the glossary on disk.</summary>
    /// <param name="term">The term.</param>
    /// <returns>The term's senses.</returns>
    GlossaryLookupResult Lookup(string term);

    /// <summary>Looks one term up in an already loaded glossary.</summary>
    /// <param name="term">The term.</param>
    /// <param name="loaded">The loaded glossary.</param>
    /// <returns>The term's senses.</returns>
    GlossaryLookupResult Lookup(string term, ManagedGlossaryLoadResult loaded);
}

/// <summary>Opens the managed glossary of one repository.</summary>
public interface IManagedGlossaryFactory
{
    /// <summary>Opens the glossary the configuration names.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <returns>The glossary.</returns>
    /// <exception cref="ArgumentException">The configured path is rooted or leaves the repository.</exception>
    /// <exception cref="InvalidOperationException">The path passes through a symbolic link.</exception>
    IManagedGlossary Open(string repositoryRoot, KyberWeaveConfig config);
}
