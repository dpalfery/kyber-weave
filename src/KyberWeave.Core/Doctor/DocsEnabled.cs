using KyberWeave.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     The "docs is enabled" predicate: governed docs need both the host config and an existing docs root.
/// </summary>
/// <remarks>
///     A config alone is a review-only adoption, and a docs folder alone is not governed docs. Requiring both keeps
///     the run-start step from firing in a repository that has no governed docs at all.
/// </remarks>
public static class DocsEnabled
{
    /// <summary>The docs root assumed when the config does not declare one.</summary>
    private const string DefaultDocsRoot = "docs";

    /// <summary>Whether the repository at <paramref name="root"/> has governed docs.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="readText">Reads a file's text, or returns null when it is absent. Injected so the caller decides the source.</param>
    public static bool IsEnabled(string root, Func<string, string?> readText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(readText);

        string? config = readText(Path.Combine(root, ".kyber-weave", "kyber-weave.yml"));
        if (config is null)
            return false;

        string docsRoot = DeclaredDocsRoot(config) ?? DefaultDocsRoot;
        return TryResolveContained(root, docsRoot, out string directory) && Directory.Exists(directory);
    }

    /// <summary>
    ///     Resolves the declared root against the repository, refusing one that leaves it.
    /// </summary>
    /// <remarks>
    ///     <see cref="Path.Combine(string, string)"/> returns its second argument outright when that argument is
    ///     rooted, and <c>..</c> walks upward, so an unchecked declaration makes this predicate answer "enabled" for
    ///     a directory the repository does not govern. The corpus loader already refuses those values through
    ///     <see cref="DocsRootPath.Normalize"/>; this reads the same key, so it applies the same rule. A declaration
    ///     that cannot be a repository-relative path is not governed docs.
    /// </remarks>
    private static bool TryResolveContained(string root, string docsRoot, out string directory)
    {
        directory = string.Empty;
        string relative;
        try
        {
            relative = DocsRootPath.Normalize(docsRoot, "ontology.docs-root");
        }
        catch (ArgumentException)
        {
            return false;
        }

        directory = relative.Length == 0 ? root : Path.Combine(root, relative);
        return true;
    }

    /// <summary>
    ///     Reads <c>ontology.docs-root</c>, which is where the host config declares it, and falls back to a
    ///     top-level <c>docs-root</c> for a config written before the ontology nested it. A config that does not
    ///     parse declares nothing, so the default applies rather than the step being skipped on a parse error.
    /// </summary>
    private static string? DeclaredDocsRoot(string config)
    {
        try
        {
            Dictionary<string, object?>? document = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Dictionary<string, object?>>(config);

            if (document is null)
                return null;

            if (document.TryGetValue("ontology", out object? ontology)
                && ReadKey(ontology, "docs-root") is { } nested)
                return nested;

            return ReadKey(document, "docs-root");
        }
        catch (YamlException)
        {
            return null;
        }
    }

    /// <summary>Reads a non-blank string key from a deserialised YAML mapping, or null when it is absent or not a string.</summary>
    private static string? ReadKey(object? mapping, string key)
    {
        object? value = mapping switch
        {
            IReadOnlyDictionary<string, object?> typed => typed.TryGetValue(key, out object? found) ? found : null,
            IReadOnlyDictionary<object, object> untyped => untyped.TryGetValue(key, out object? found) ? found : null,
            _ => null,
        };

        return value is string text && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }
}
