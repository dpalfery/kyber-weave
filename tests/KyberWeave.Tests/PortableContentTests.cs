using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The portable instructions — the Squad roster under <c>products/kyber-squad/</c> and the skills under
/// <c>.apm/skills/</c> — carry policy for every repository that installs them, so no line in them may name
/// this repository's projects.
/// </summary>
/// <remarks>
/// <para>
/// <c>KyberWeave.Tests</c> and <c>src/KyberWeave</c> are facts about this checkout, not about the product. A
/// host that installs the roster was being told to run a test assembly path that does not exist there, and a
/// worker that followed it ran nothing. That knowledge belongs in the host's <c>review.gates</c>,
/// <c>review.task-gates</c> and <c>## Completion</c> sections, which is where this repository's own copies of
/// it now live.
/// </para>
/// <para>
/// The scan is deliberately unscoped: it covers every file under both roots, in every language, not only the
/// files that fail today. Narrowing it to a list of known offenders would make it a snapshot of one cleanup
/// rather than a rule, and the next sentence added to a portable file would pass.
/// </para>
/// </remarks>
public sealed class PortableContentTests
{
    /// <summary>Roots whose contents ship to every host that installs this product.</summary>
    private static readonly string[] PortableRoots = ["products/kyber-squad", ".apm/skills"];

    /// <summary>The project names a portable file must not carry, because they only exist in this checkout.</summary>
    private static readonly string[] ProjectMarkers = ["KyberWeave.Tests", "src/KyberWeave"];

    /// <summary>No portable file names this repository's test project or its CLI project.</summary>
    [Fact]
    public void NoPortableFileNamesThisRepositorysProjectPaths()
    {
        List<string> offenders = [];
        List<string> scanned = [];

        foreach (string root in PortableRoots)
        {
            foreach (string file in Files(Path.Combine(KyberWeaveTestPaths.ToolRoot, root)))
            {
                string relative = Path.GetRelativePath(KyberWeaveTestPaths.ToolRoot, file).Replace('\\', '/');
                scanned.Add(relative);

                string[] lines = ReadLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    foreach (string marker in ProjectMarkers)
                    {
                        if (!lines[index].Contains(marker, StringComparison.Ordinal))
                            continue;

                        offenders.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
                        break;
                    }
                }
            }
        }

        // A walk that found nothing would pass every assertion below while proving nothing, so the
        // enumeration is asserted before the result it produces.
        Assert.True(
            scanned.Count > 0,
            $"No file was read under {string.Join(" or ", PortableRoots)}; the scan covered nothing.");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} portable line(s) name this repository's project paths. Portable files ship to " +
            "every host, so this repository's test assembly and CLI paths belong in its review gates and " +
            "coding standards instead:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every file under the two roots is read as text, in one level at a time so a directory symbolic link
    /// cannot lead the scan out of the tree.
    /// </summary>
    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        Stack<string> pending = new([root]);
        while (pending.Count > 0)
        {
            DirectoryInfo directory = new(pending.Pop());
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                if (entry.LinkTarget is not null && entry is DirectoryInfo)
                    continue;

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                    continue;
                }

                yield return entry.FullName;
            }
        }
    }

    /// <summary>
    /// The lines of a file, or none when it is not text. A binary file has no instruction lines to check, and
    /// reading one could decode to nonsense that matches a marker by accident.
    /// </summary>
    private static string[] ReadLines(string path)
    {
        try
        {
            string content = File.ReadAllText(path);
            return content.Any(character => character == '\0')
                ? []
                : content.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"Unreadable portable file '{path}': {ex.Message}");
            return [];
        }
    }
}
