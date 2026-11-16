using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Checks that the line a finding cites exists in its file, and repairs it from the excerpt when it does not.</summary>
/// <param name="files">Reads the cited files.</param>
public sealed class CitedLineChecker(IContainedFileReader files) : ICitedLineChecker
{
    /// <inheritdoc />
    public CitedLineResult Check(string repositoryRoot, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Check(repositoryRoot, [finding])[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<CitedLineResult> Check(string repositoryRoot, IReadOnlyList<ReviewFinding> findings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(findings);

        Dictionary<string, FileLines> read = new(StringComparer.Ordinal);
        List<CitedLineResult> results = new(findings.Count);
        foreach (ReviewFinding finding in findings)
        {
            if (!read.TryGetValue(finding.File, out FileLines? file))
            {
                file = ReadFile(repositoryRoot, finding.File);
                read[finding.File] = file;
            }

            results.Add(file.Lines is { } lines
                ? CitedLineCheck.Judge(finding, lines)
                : CitedLineResult.Unverified(finding, file.Problem ?? "the cited file could not be read"));
        }

        return results;
    }

    /// <summary>
    /// The cited file's lines, or why they were not read. The path comes from a seat's text, so only a file inside
    /// the repository, reached without a symbolic link, is read.
    /// </summary>
    private FileLines ReadFile(string repositoryRoot, string path)
    {
        if (!CitedLineCheck.IsRepositoryRelative(path))
            return new FileLines(null, $"{path} is not a path inside the repository, so it was not read");

        try
        {
            if (!files.IsContainedFile(repositoryRoot, path))
                return new FileLines(null, $"{path} is not a file in the repository, or is reached through a link");

            string full = Path.Combine(repositoryRoot, path);
            return new FileLines(files.ReadLines(full, ReviewSeatInput.MaxFileBytes), null);
        }
        catch (InvalidDataException)
        {
            return new FileLines(null, $"{path} is larger than {ReviewSeatInput.MaxFileBytes} bytes, so it was not read");
        }
        // A seat can write a path holding a character no file system accepts, such as a NUL from a quoted "\0", or
        // name a root the process may not look in; the one shared filter says which failures are answers.
        catch (Exception ex) when (ReviewSeatInput.IsFileFailure(ex))
        {
            // The exception message can carry the absolute path, which does not belong in a review report.
            return new FileLines(null, $"{path} could not be read ({ex.GetType().Name})");
        }
    }

    private sealed record FileLines(string[]? Lines, string? Problem);
}
