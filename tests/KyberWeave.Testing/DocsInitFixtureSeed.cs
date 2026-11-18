using System.Text;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests;

/// <summary>
/// Places a gate-catalogue family's clean fixture, and a host configuration declaring its technologies, into an
/// in-memory checkout, so a unit test of <c>docs init</c> arranges the repository without a disk or a git history.
/// </summary>
/// <remarks>
/// The fixture files are read from the checkout's corpus: they are the test's input data, as the approved
/// <c>expected-review.yml</c> is. Only the in-memory file system is written.
/// </remarks>
internal static class DocsInitFixtureSeed
{
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";

    /// <summary>Copies the family's clean fixture under <paramref name="repoRoot"/> and writes the host configuration.</summary>
    /// <param name="files">The in-memory file system.</param>
    /// <param name="repoRoot">The checkout root to seed.</param>
    /// <param name="family">The gate-catalogue family whose clean fixture is copied.</param>
    /// <param name="technologies">The technologies the host configuration declares.</param>
    public static void Family(InMemoryFileSystem files, string repoRoot, string family, IReadOnlyList<string> technologies)
    {
        ArgumentNullException.ThrowIfNull(files);
        ICorpusSource corpus = CheckoutCorpusSource.Checkout;
        string clean = $"tests/fixtures/gate-catalogue/{family}/clean";
        foreach (string file in corpus.EnumerateFiles(clean))
        {
            string target = Path.Combine(repoRoot, file[(clean.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
            files.CreateDirectory(Path.GetDirectoryName(target)!);
            files.WriteAllBytes(target, corpus.ReadBytes(file));
        }

        HostConfig(files, repoRoot, technologies);
    }

    /// <summary>Writes the minimal host configuration declaring <paramref name="technologies"/>.</summary>
    /// <param name="files">The in-memory file system.</param>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="technologies">The technologies to declare; none writes an empty list.</param>
    public static void HostConfig(InMemoryFileSystem files, string repoRoot, IReadOnlyList<string> technologies)
    {
        ArgumentNullException.ThrowIfNull(files);
        StringBuilder yaml = new();
        yaml.AppendLine("ontology:");
        yaml.AppendLine("  docs-root: docs");
        if (technologies.Count == 0)
        {
            yaml.AppendLine("  technologies: []");
        }
        else
        {
            yaml.AppendLine("  technologies:");
            foreach (string technology in technologies)
            {
                yaml.Append("    - ").AppendLine(technology);
            }
        }

        string path = Path.Combine(repoRoot, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        files.CreateDirectory(Path.GetDirectoryName(path)!);
        files.WriteAllText(path, yaml.ToString());
    }
}
