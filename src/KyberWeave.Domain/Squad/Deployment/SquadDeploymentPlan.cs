using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     One unmanaged file whose name matches a canonical Squad identity and whose
///     bytes do not match the current render. Doctor lists these; install refuses them.
/// </summary>
public sealed record SquadUnmanagedPathCollision(
    string RelativePath,
    string FullPath,
    string Target,
    string Identity);

/// <summary>
///     Whether a planned file change writes new bytes or removes a deployed file. Public so
///     command hosts can report what a plan will do without seeing the internal payload record.
/// </summary>
public enum SquadFileMutationKind
{
    Write,
    Delete
}

/// <summary>A deployed file a plan kept on trust, with the raw target token that produced it.</summary>
public sealed record SquadDegradedFile(string RelativePath, string Target);

/// <summary>
///     One planned file change without its payload, for command hosts that report what a plan
///     will do. An uninstall plan's receipt holds only the files it retains, so the removals it
///     plans are readable from these changes alone.
/// </summary>
public sealed record SquadPlannedFileChange(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind);

/// <summary>
///     A pending write or delete for one deployed file, identified by <c>(Target, RelativePath)</c>
///     so that Global scope's per-target physical roots (see <c>SquadDeploymentPlanRules.ResolvePhysicalRoot</c>)
///     can be resolved without cross-referencing the receipt by relative path alone.
/// </summary>
public sealed record SquadFileMutation(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind,
    ReadOnlyMemory<byte>? Content)
{
    public static SquadFileMutation Write(
        string relativePath,
        string target,
        ReadOnlyMemory<byte> content)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Write, content);
    }

    public static SquadFileMutation Delete(string relativePath, string target)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Delete, null);
    }
}

public enum SquadStateMutation
{
    Keep,
    Write,
    Delete
}

public enum SquadFilePreconditionKind
{
    Missing,
    ExactFile
}

public sealed record SquadFilePrecondition(
    string RelativePath,
    string Target,
    SquadFilePreconditionKind Kind,
    string? Sha256)
{
    public static SquadFilePrecondition Missing(string relativePath, string target)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.Missing, null);
    }

    public static SquadFilePrecondition Exact(string relativePath, string target, string sha256)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.ExactFile, sha256);
    }
}
