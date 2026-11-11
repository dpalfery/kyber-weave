using System.Text;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>A bounded window of a repository file, around one line.</summary>
/// <param name="Path">The repository-relative path the excerpt was read from.</param>
/// <param name="StartLine">The 1-based first line of the window.</param>
/// <param name="EndLine">The 1-based last line of the window that was read.</param>
/// <param name="Text">The window's lines joined with newlines, bounded by <see cref="FileTextFacts.MaxExcerptCharacters"/>.</param>
/// <param name="Truncated">True when a line or the window was cut short to stay within bounds.</param>
public sealed record FileExcerpt(string Path, int StartLine, int EndLine, string Text, bool Truncated);

/// <summary>
/// Reads the current text around a line of a repository file, so a review comment is judged against the code as it
/// is now. Reads are bounded in lines, characters per line and characters overall, and a path that leaves the
/// repository root is never read.
/// </summary>
public static class FileTextFacts
{
    /// <summary>The most characters one excerpt carries.</summary>
    public const int MaxExcerptCharacters = 4000;

    /// <summary>The most characters one line carries before it is cut.</summary>
    public const int MaxLineCharacters = 500;

    /// <summary>Lines read on each side of the cited line.</summary>
    private const int Radius = 10;

    /// <summary>
    /// The most characters one read consumes, skipped lines and the clipped tail of a long line included. A read
    /// past it is refused rather than finished, so one huge line costs at most this much. It is the document cap
    /// the section selector reads by.
    /// </summary>
    private const long MaxConsumedCharacters = SectionSelector.MaxDocumentBytes;

    /// <summary>
    /// Reads the window around <paramref name="line"/> of <paramref name="path"/>.
    /// </summary>
    /// <param name="repoRoot">The repository root; the path must resolve under it.</param>
    /// <param name="path">A path relative to the root, or an absolute path under it.</param>
    /// <param name="line">The 1-based line the comment cites.</param>
    /// <returns>The excerpt, or null when the path leaves the root, the file is absent, or the line is past the end.</returns>
    public static FileExcerpt? ReadExcerpt(string repoRoot, string path, int line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (line < 1)
        {
            return null;
        }

        string? full = ResolveContainedFile(repoRoot, path);
        if (full is null)
        {
            return null;
        }

        // A cited file that exists but cannot be opened or read is absent evidence, not a failure of the caller: the
        // comment stays real and the classification never throws on an I/O error.
        try
        {
            return ReadWindow(full, path, line);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileExcerpt? ReadWindow(string full, string path, int line)
    {
        int first = Math.Max(1, line - Radius);
        int last = line + Radius;
        StringBuilder text = new();
        StringBuilder current = new();
        bool truncated = false;
        bool capped = false;
        int lineNumber = 0;
        int lastRead = 0;
        bool reachedTarget = false;
        long budget = MaxConsumedCharacters;

        using FileStream stream = File.OpenRead(full);
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (lineNumber < last && ReadBoundedLine(reader, current, ref budget, out bool clipped))
        {
            lineNumber++;
            if (lineNumber < first)
            {
                continue;
            }

            if (lineNumber == line)
            {
                reachedTarget = true;
            }

            lastRead = lineNumber;
            truncated |= clipped;
            if (capped)
            {
                continue;
            }

            string piece = current.ToString();
            int separator = text.Length == 0 ? 0 : 1;
            if (text.Length + separator + piece.Length > MaxExcerptCharacters)
            {
                int room = MaxExcerptCharacters - text.Length - separator;
                if (room > 0)
                {
                    text.Append(separator == 1 ? "\n" : string.Empty).Append(piece, 0, room);
                }

                truncated = true;
                capped = true;
                continue;
            }

            if (separator == 1)
            {
                text.Append('\n');
            }

            text.Append(piece);
        }

        // Past the budget the read is refused, not finished: the file is absent evidence, as an unreadable one is.
        if (budget < 0 || !reachedTarget)
        {
            return null;
        }

        return new FileExcerpt(path, first, lastRead, text.ToString(), truncated);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="repoRoot"/> and returns the full path only when it stays
    /// under the root.
    /// </summary>
    /// <returns>The full path, or null when the path would leave the root.</returns>
    public static string? ResolveWithinRoot(string repoRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string root = Path.GetFullPath(repoRoot);
        string candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to an existing regular file under <paramref name="repoRoot"/>. The lexical
    /// check alone would let a symbolic link inside the repository point outside it, so every component below the
    /// root is checked for a link before the file is tested for existence.
    /// </summary>
    /// <returns>The full path, or null when the path leaves the root, crosses a link, or names no file.</returns>
    public static string? ResolveContainedFile(string repoRoot, string path)
    {
        string? full = ResolveWithinRoot(repoRoot, path);
        if (full is null)
        {
            return null;
        }

        string root = Path.GetFullPath(repoRoot);
        string relative = Path.GetRelativePath(root, full);
        return DocsRootPath.IsContainedFile(root, relative, []) ? full : null;
    }

    /// <summary>
    /// Reads one line into <paramref name="line"/>, keeping at most <see cref="MaxLineCharacters"/> characters and
    /// discarding the rest. Every character consumed is taken from <paramref name="budget"/>; once it is spent the
    /// line stops and returns false, and the caller sees the negative budget and refuses the file. Returns false at
    /// the end of the stream.
    /// </summary>
    private static bool ReadBoundedLine(TextReader reader, StringBuilder line, ref long budget, out bool clipped)
    {
        line.Clear();
        clipped = false;
        bool any = false;
        while (true)
        {
            int next = reader.Read();
            if (next < 0)
            {
                return any;
            }

            if (--budget < 0)
            {
                return false;
            }

            any = true;
            if (next == '\n')
            {
                return true;
            }

            if (next == '\r')
            {
                continue;
            }

            if (line.Length < MaxLineCharacters)
            {
                line.Append((char)next);
            }
            else
            {
                clipped = true;
            }
        }
    }
}
