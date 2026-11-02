using Xunit;
using KyberWeave.Core.Doctor;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the D14 "docs is enabled" predicate: a governed-docs repository has <c>.kyber-weave/kyber-weave.yml</c> and
/// an existing <c>docs-root</c> directory, and neither alone is enough. The run-start step depends on this exact
/// condition, so a looser predicate would run it in repositories with no governed docs.
/// </summary>
public sealed class DocsEnabledTests : IDisposable
{
    private const string ConfigText = "ontology:\n  docs-root: docs\n";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Root => _root.Path;

    /// <summary>The config reader answers for the repository config path only; any other file reads as absent.</summary>
    private static Func<string, string?> ConfigReader(string? text) =>
        path => text is not null && path.Replace('\\', '/').EndsWith(".kyber-weave/kyber-weave.yml", StringComparison.Ordinal)
            ? text
            : null;

    /// <summary>A docs folder with no config is not governed docs, so the step does not run.</summary>
    [Fact]
    public void DocsEnabled_NoConfig_IsFalse()
    {
        Directory.CreateDirectory(Path.Combine(Root, "docs"));

        bool enabled = DocsEnabled.IsEnabled(Root, ConfigReader(null));

        Assert.False(enabled);
    }

    /// <summary>A config whose docs-root does not exist is a review-only adoption, not governed docs.</summary>
    [Fact]
    public void DocsEnabled_ConfigWithoutDocsRootDirectory_IsFalse()
    {
        bool enabled = DocsEnabled.IsEnabled(Root, ConfigReader(ConfigText));

        Assert.False(enabled);
    }

    /// <summary>The root is read from <c>ontology.docs-root</c>, so a non-default root is honoured.</summary>
    [Fact]
    public void DocsEnabled_OntologyDocsRootOutsideDocs_IsTrue()
    {
        Directory.CreateDirectory(Path.Combine(Root, "documentation"));

        bool enabled = DocsEnabled.IsEnabled(
            Root,
            ConfigReader("ontology:\n  docs-root: documentation\n"));

        Assert.True(enabled);
    }

    /// <summary>
    /// A docs root that walks upwards out of the repository is not governed docs. <c>..</c> resolves to the parent
    /// directory, which exists, so an unchecked predicate answers "enabled" for a directory this repository does not
    /// govern — the same escape <c>DocsRootPath.Normalize</c> already refuses when the config is loaded.
    /// </summary>
    [Fact]
    public void DocsEnabled_DocsRootEscapesRepository_IsFalse()
    {
        Assert.True(Directory.Exists(Path.Combine(Root, "..")), "The parent directory must exist for this test to mean anything.");

        bool enabled = DocsEnabled.IsEnabled(
            Root,
            ConfigReader("ontology:\n  docs-root: ..\n"));

        Assert.False(enabled);
    }

    /// <summary>An absolute docs root is refused for the same reason: <c>Path.Combine</c> would return it outright.</summary>
    [Fact]
    public void DocsEnabled_AbsoluteDocsRoot_IsFalse()
    {
        string absolute = Path.GetFullPath(Root);

        bool enabled = DocsEnabled.IsEnabled(
            Root,
            ConfigReader($"ontology:\n  docs-root: '{absolute}'\n"));

        Assert.False(enabled);
    }

    /// <summary>A non-default root that does not exist is not governed docs, even when <c>docs/</c> exists.</summary>
    [Fact]
    public void DocsEnabled_OntologyDocsRootMissing_FallsBackToNothing()
    {
        Directory.CreateDirectory(Path.Combine(Root, "docs"));

        bool enabled = DocsEnabled.IsEnabled(
            Root,
            ConfigReader("ontology:\n  docs-root: documentation\n"));

        Assert.False(enabled);
    }

    /// <summary>Config plus an existing docs-root is the one case where the run-start step runs.</summary>
    [Fact]
    public void DocsEnabled_ConfigAndDocsRoot_IsTrue()
    {
        Directory.CreateDirectory(Path.Combine(Root, "docs"));

        bool enabled = DocsEnabled.IsEnabled(Root, ConfigReader(ConfigText));

        Assert.True(enabled);
    }
}
