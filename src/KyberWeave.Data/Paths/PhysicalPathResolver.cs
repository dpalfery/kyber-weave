using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Paths;

/// <summary>
/// The path seam over the real disk: the file-system adapter wrapped with the case rule this host's volume has.
/// </summary>
/// <remarks>
/// Nothing here is specific to the disk — every answer comes from the <see cref="IFileSystem" /> passed in — so the
/// only thing this type adds is the host's name rule. Pass the case rule explicitly where the volume's rule is
/// known, as the contract tests do, rather than relying on the host's default.
/// </remarks>
public sealed class PhysicalPathResolver : FileSystemPathResolver
{
    /// <summary>Creates the resolver for this host's file system.</summary>
    /// <param name="files">The file-system adapter.</param>
    public PhysicalPathResolver(IFileSystem files)
        : base(files, caseSensitiveFileSystem: OperatingSystem.IsLinux())
    {
    }

    /// <summary>Creates the resolver for a file system whose name rule the caller knows.</summary>
    /// <param name="files">The file-system adapter.</param>
    /// <param name="caseSensitiveFileSystem">True when the volume compares names case-sensitively.</param>
    public PhysicalPathResolver(IFileSystem files, bool caseSensitiveFileSystem)
        : base(files, caseSensitiveFileSystem)
    {
    }
}
