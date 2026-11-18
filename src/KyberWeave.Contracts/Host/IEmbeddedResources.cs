namespace KyberWeave.Contracts.Host;

/// <summary>The resources compiled into an assembly, read by manifest name.</summary>
/// <remarks>
/// Code that ships a template or a standard reads it here instead of calling
/// <see cref="System.Reflection.Assembly.GetManifestResourceStream(string)"/>, so a unit test supplies the text.
/// </remarks>
public interface IEmbeddedResources
{
    /// <summary>Opens a resource for reading.</summary>
    /// <param name="name">The manifest resource name.</param>
    /// <returns>A readable stream positioned at the start; the caller disposes it.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or whitespace.</exception>
    /// <exception cref="MissingEmbeddedResourceException">No resource has that name.</exception>
    Stream OpenRead(string name);

    /// <summary>Reads a resource as UTF-8 text.</summary>
    /// <param name="name">The manifest resource name.</param>
    /// <returns>The whole resource text.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or whitespace.</exception>
    /// <exception cref="MissingEmbeddedResourceException">No resource has that name.</exception>
    string ReadAllText(string name);
}
