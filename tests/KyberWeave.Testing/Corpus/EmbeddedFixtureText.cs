using System.Reflection;

namespace KyberWeave.Tests.Corpus;

/// <summary>
/// Reads a test fixture file that a test project embeds in its own assembly, so a unit test holds its fixture
/// content in memory and never opens a file.
/// </summary>
/// <remarks>
/// A fixture is test data, not shipped content: it is not read through <see cref="ICorpusSource"/>. A project embeds
/// a fixture folder with an <c>EmbeddedResource</c> item whose logical name is <c>fixtures/</c> followed by the
/// file's path under the folder, with forward slashes.
/// </remarks>
public static class EmbeddedFixtureText
{
    private const string Prefix = "fixtures/";

    /// <summary>Reads one embedded fixture file as text.</summary>
    /// <param name="assembly">The test assembly that embeds the fixture.</param>
    /// <param name="relativePath">The file's path under the fixture folder, with forward slashes.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="FileNotFoundException">The assembly embeds no fixture at the path.</exception>
    public static string Read(Assembly assembly, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string wanted = Prefix + relativePath.Replace('\\', '/');
        string name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Replace('\\', '/'),
                wanted,
                StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"The assembly embeds no fixture at '{relativePath}'.", relativePath);

        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The fixture '{name}' could not be opened.", relativePath);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
