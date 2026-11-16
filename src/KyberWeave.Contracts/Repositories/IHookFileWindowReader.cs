namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads a few lines of a working-tree file, for the lines around a finding.</summary>
public interface IHookFileWindowReader
{
    /// <summary>Reads a window of lines.</summary>
    /// <param name="path">The file's absolute path.</param>
    /// <param name="maxBytes">The largest file that is read.</param>
    /// <param name="skip">How many lines to skip.</param>
    /// <param name="take">How many lines to take.</param>
    /// <returns>The lines; null when the file is missing, larger than <paramref name="maxBytes"/> or unreadable.</returns>
    IReadOnlyList<string>? ReadWindow(string path, long maxBytes, int skip, int take);
}
