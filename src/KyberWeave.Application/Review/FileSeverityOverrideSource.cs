using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads the severity overrides from the review guidance file on the file system.</summary>
public sealed class FileSeverityOverrideSource(IFileSystem files) : ISeverityOverrideSource
{
    /// <inheritdoc />
    public string? GuidancePath(string root, ConfigRegConfig configReg)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(configReg);
        string declared = configReg.Additions
            .FirstOrDefault(entry => string.Equals(entry.Name, "review-guidance", StringComparison.Ordinal))?.Path
            ?? SeverityOverrides.DefaultGuidancePath;
        string path = Path.GetFullPath(Path.Combine(root, declared));
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
            return SeverityOverrideRules.Parse(files.ReadAllText(path), Path.GetRelativePath(root, path).Replace('\\', '/'));
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
