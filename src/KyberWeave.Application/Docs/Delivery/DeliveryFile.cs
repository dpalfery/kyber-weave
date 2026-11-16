using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>
/// Reads a text file this tool, or a person, put in the repository without loading an absurd one whole.
/// </summary>
/// <remarks>
/// When the rest of the bounded reads move out of the engine, this collapses into one shared
/// helper. Until then the limit is the same number the engine uses, stated once here so the two
/// cannot disagree about what "absurdly large" means.
/// </remarks>
internal static class DeliveryFile
{
    /// <summary>The most bytes read from one such file: far above any real template or task file.</summary>
    internal const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The file's text, through the one bounded reader. A file larger than <see cref="MaxBytes"/>
    /// is not read: it throws <see cref="InvalidDataException"/> naming the size.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/>.</exception>
    internal static string ReadAllText(string path) => BoundedFileReader.ReadAllText(path, MaxBytes);
}
