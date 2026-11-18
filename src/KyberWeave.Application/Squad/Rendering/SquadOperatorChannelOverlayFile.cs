using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Reads the operator channel overlay from a Squad source tree.</summary>
/// <param name="files">Reads the overlay file.</param>
public sealed class SquadOperatorChannelOverlayFile(IFileSystem files) : ISquadOperatorChannelOverlayFile
{
    /// <summary>
    /// Reads the overlay file's text. A missing overlay is a source defect, so it fails the render rather than shipping a
    /// conductor that promises a channel it does not describe.
    /// </summary>
    /// <param name="sourceDirectory">The Squad source root.</param>
    /// <returns>The file's text.</returns>
    /// <exception cref="SquadRenderValidationException">The overlay is missing or cannot be read.</exception>
    public string ReadText(string sourceDirectory)
    {
        string path = Path.Combine(
            sourceDirectory,
            SquadOperatorChannel.OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // The read itself is the check. An exists-then-read pair would race the file system, so the failure
        // is taken from the read and translated into a render error that names the path and the cause.
        try
        {
            return files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SquadRenderValidationException(
                $"The operator channel overlay is missing from the Squad source at '{path}'.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SquadRenderValidationException(
                $"The operator channel overlay at '{path}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the overlay, substitutes the validated server name for its placeholder, and returns the tool entry
    /// and the body text.
    /// </summary>
    /// <param name="sourceDirectory">The Squad source root.</param>
    /// <param name="serverName">The validated server name.</param>
    /// <returns>The overlay, ready to render.</returns>
    public SquadOperatorChannelOverlay LoadOverlay(string sourceDirectory, string serverName) =>
        SquadOperatorChannelOverlay.FromFileText(ReadText(sourceDirectory), serverName);
}
