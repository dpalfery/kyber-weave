using KyberWeave.Contracts.Paths;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// The deployment's view of the path seam: every answer comes from an <see cref="IPathResolver" />, with the
/// canonical spellings and containment rules a deployment needs.
/// </summary>
/// <remarks>
/// The four members of <see cref="IFileSystemPathSemantics" /> are a role over <see cref="IPathResolver" />, kept
/// so the deployment code that already takes this interface keeps compiling; the one difference is the exception
/// type, because a deployment that cannot bind its root is a deployment conflict rather than a missing directory.
/// The instance the product uses reads the real disk; a test passes a resolver over its own file system.
/// </remarks>
public sealed class SquadFileSystemPathSemantics : IFileSystemPathSemantics, IPathResolver
{
    /// <summary>The resolver over the real disk, shared by the adapters that need one.</summary>
    public static SquadFileSystemPathSemantics Instance { get; } = new();

    private readonly IPathResolver _resolver;

    /// <summary>Creates the deployment view over the real disk.</summary>
    public SquadFileSystemPathSemantics()
        : this(new PhysicalPathResolver(new PhysicalFileSystem()))
    {
    }

    /// <summary>Creates the deployment view over a resolver the caller supplies.</summary>
    /// <param name="resolver">The path seam whose answers this view reports.</param>
    public SquadFileSystemPathSemantics(IPathResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    ///     Reuses the parent listings a preflight reads while it inspects a tree it does not change. Scoped to the
    ///     caller: the cache dies with the returned scope, so no answer outlives the operation that read it. A
    ///     caller must not mutate the tree while the scope is open.
    /// </summary>
    /// <returns>The scope that restores the previous listing cache when it is disposed.</returns>
    internal static IDisposable BeginListingScope() => FileSystemPathResolver.BeginListingScope();

    /// <inheritdoc />
    public bool AreSame(string first, string second) => _resolver.AreSame(first, second);

    /// <inheritdoc />
    public bool IsWithin(string root, string candidate) => _resolver.IsWithin(root, candidate);

    /// <inheritdoc />
    public string Canonicalize(string path) => _resolver.Canonicalize(path);

    /// <inheritdoc />
    public string GetFullPath(string path, string baseDirectory) => _resolver.GetFullPath(path, baseDirectory);

    /// <inheritdoc />
    public string ResolvePhysicalRoot(string targetRoot)
    {
        try
        {
            return _resolver.ResolvePhysicalRoot(targetRoot);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new SquadDeploymentConflictException(exception.Message, exception);
        }
    }
}
