using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Review;

/// <summary>The one way a review command writes its report document.</summary>
internal static class ReviewReportWriter
{
    /// <summary>
    /// Writes a report document to <paramref name="path"/>. A write failure is recorded as a diagnostic rather than
    /// thrown: the analysis already ran, and losing its findings to a filesystem error is a worse outcome than losing
    /// the file.
    /// </summary>
    /// <param name="files">Writes the file.</param>
    /// <param name="paths">Resolves the path the caller spelled.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="path">Where to write it, or null for nowhere.</param>
    /// <param name="content">The document.</param>
    /// <param name="code">The rule id a failed write is reported under.</param>
    /// <param name="report">Receives the failure.</param>
    internal static void TryWrite(
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        string? path,
        string content,
        string code,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(report);

        if (path is null)
            return;

        // An empty --out names no file; resolving it would throw rather than report.
        if (string.IsNullOrWhiteSpace(path))
        {
            report.Add(new Diagnostic(
                code,
                Severity.Error,
                "--out was given an empty value, so the report was not written.",
                "review",
                Hint: "Name the file to write, or drop --out."));
            return;
        }

        string full = paths.GetFullPath(path, environment.CurrentDirectory);
        try
        {
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
                files.CreateDirectory(directory);

            files.WriteAllText(full, content);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            report.Add(new Diagnostic(
                code,
                Severity.Error,
                $"Could not write the report to '{full}': {ex.Message}",
                "review",
                full,
                Hint: "Check that the directory is writable, or drop --out."));
        }
    }
}
