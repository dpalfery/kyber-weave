using System.Text;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Reads the operator channel overlay from a Squad source tree.</summary>
public static class SquadOperatorChannelOverlayFile
{
    /// <summary>
    /// Reads the overlay, substitutes the validated server name for its placeholder, and returns the tool entry
    /// and the body text. A missing overlay is a source defect, so it fails the render rather than shipping a
    /// conductor that promises a channel it does not describe.
    /// </summary>
    public static SquadOperatorChannelOverlay LoadOverlay(string sourceDirectory, string serverName)
    {
        string path = Path.Combine(
            sourceDirectory,
            SquadOperatorChannel.OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // The read itself is the check. An exists-then-read pair would race the file system, so the failure
        // is taken from the read and translated into a render error that names the path and the cause.
        string raw;
        try
        {
            raw = File.ReadAllText(path, Encoding.UTF8);
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

        string text = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(SquadOperatorChannel.ServerPlaceholder, serverName, StringComparison.Ordinal)
            .TrimEnd();
        return new SquadOperatorChannelOverlay($"{serverName}/*", text);
    }
}
