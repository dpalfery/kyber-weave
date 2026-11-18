namespace KyberWeave.Arbiter.Hooks.Adapters;

/// <summary>Reads the lines of a Factory session transcript, so the adapter that parses it touches no file.</summary>
public interface IFactoryTranscriptSource
{
    /// <summary>Opens a transcript for reading.</summary>
    /// <param name="path">The transcript's absolute path.</param>
    /// <param name="maxBytes">The largest file that is read; a bigger one is left unread.</param>
    /// <returns>The lines, read lazily; null when the file does not exist or is bigger than <paramref name="maxBytes"/>.</returns>
    IEnumerable<string>? ReadLines(string path, long maxBytes);
}
