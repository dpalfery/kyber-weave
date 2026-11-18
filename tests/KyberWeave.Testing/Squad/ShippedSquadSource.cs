using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>The shipped <c>products/kyber-squad</c> tree, copied once per test into an in-memory file system.</summary>
public static class ShippedSquadSource
{
    private const string SourceDirectory = "products/kyber-squad";

    /// <summary>Whether this checkout carries the shipped Squad source.</summary>
    public static bool Exists => CheckoutCorpusSource.Checkout.DirectoryExists(SourceDirectory);

    /// <summary>Copies the shipped Squad source into an in-memory file system through the corpus seam.</summary>
    /// <param name="files">The file system to copy into.</param>
    /// <param name="destinationDirectory">Where the tree lands.</param>
    /// <returns>The destination directory.</returns>
    public static string CopyInto(InMemoryFileSystem files, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ICorpusSource corpus = CheckoutCorpusSource.Checkout;
        files.CreateDirectory(destinationDirectory);
        foreach (string file in corpus.EnumerateFiles(SourceDirectory, "*", recursive: true))
        {
            string relativePath = file[(SourceDirectory.Length + 1)..];
            string destination = Path.Combine(destinationDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            files.CreateDirectory(Path.GetDirectoryName(destination)!);
            files.WriteAllBytes(destination, corpus.ReadBytes(file));
        }

        return destinationDirectory;
    }
}
