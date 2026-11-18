using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads the severity overrides from the review guidance file on the file system.</summary>
/// <param name="files">Answers every existence and read question.</param>
/// <param name="paths">Resolves the guidance path the configuration declares.</param>
/// <param name="environment">Names the working directory a relative root resolves against.</param>
public sealed class FileSeverityOverrideSource(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISeverityOverrideSource
{
    /// <inheritdoc />
    public string? GuidancePath(string root, ConfigRegConfig configReg)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(configReg);
        string declared = configReg.Additions
            .FirstOrDefault(entry => string.Equals(entry.Name, "review-guidance", StringComparison.Ordinal))?.Path
            ?? SeverityOverrides.DefaultGuidancePath;
        string path = paths.GetFullPath(Path.Combine(root, declared), environment.CurrentDirectory);
        return files.FileExists(path) ? path : null;
    }

    /// <inheritdoc />
    public SeverityOverrides Load(string root, ConfigRegConfig configReg)
    {
        string? path = GuidancePath(root, configReg);
        if (path is null)
            return SeverityOverrides.None;

        try
        {
            // The name a problem cites is repository-relative, so the root it is taken from is resolved the same way
            // the guidance path was: a relative root names the same file on every host.
            string resolvedRoot = paths.GetFullPath(root, environment.CurrentDirectory);
            return SeverityOverrideRules.Parse(
                files.ReadAllText(path),
                Path.GetRelativePath(resolvedRoot, path).Replace('\\', '/'));
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return SeverityOverrides.None;
        }
    }

    /// <inheritdoc />
    public DiagnosticReport Validate(string root, ConfigRegConfig configReg)
    {
        DiagnosticReport report = new();
        report.AddRange(Load(root, configReg).Problems);
        return report;
    }
}
