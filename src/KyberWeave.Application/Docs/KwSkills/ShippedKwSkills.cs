using KyberWeave.Core.FileSystem;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>The KW skills embedded in the product: what the manifest lists, and a copy of one on disk.</summary>
/// <remarks>
/// A skill's reference files are read by an agent from a folder, so a shipped skill is written out once.
/// The folder name carries a hash of the skill's files, so a changed skill lands in a new folder and an
/// existing identical folder is reused as it stands. Everything is written under the operator's home
/// directory, inside <c>.kyber-weave/kw-skills</c>, and no path is built from anything but the manifest.
/// </remarks>
internal sealed class ShippedKwSkills(
    IEmbeddedResources resources,
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment)
{
    private readonly ShippedKwSkillPool _pool = new(resources);

    /// <summary>Gets the shipped skill names, in the order the manifest lists them.</summary>
    public IReadOnlyList<string> Names => _pool.Names;

    /// <summary>Gets whether a shipped skill has that name.</summary>
    public bool Contains(string name) => _pool.Contains(name);

    /// <summary>Reads a shipped skill's <c>SKILL.md</c>.</summary>
    public string ReadSkillFile(string name) => _pool.ReadSkillFile(name);

    /// <summary>
    /// Writes a shipped skill under the home folder unless an identical copy is already there, and
    /// returns the folder.
    /// </summary>
    /// <param name="name">A name the manifest lists.</param>
    /// <exception cref="IOException">The copy could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The home folder refuses the write.</exception>
    /// <exception cref="InvalidDataException">The manifest names a file that would land outside the folder.</exception>
    public string Materialize(string name)
    {
        IReadOnlyList<string> listed = _pool.FilesOf(name);
        List<(string Relative, byte[] Bytes)> contents = [];
        using SHA256 hash = SHA256.Create();
        foreach (string relative in listed)
        {
            using Stream stream = _pool.Open(name, relative);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            byte[] bytes = copy.ToArray();
            contents.Add((relative, bytes));
            byte[] label = Encoding.UTF8.GetBytes(relative);
            hash.TransformBlock(label, 0, label.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        byte[] hashBytes = hash.Hash
            ?? throw new InvalidOperationException("The skill digest was not finalised.");
        string digest = Convert.ToHexStringLower(hashBytes)[..12];

        string home = environment.HomeDirectory;
        string root = paths.GetFullPath(UserStatePaths.KwSkillsDirectory(home), home);
        string folder = paths.GetFullPath(Path.Combine(root, $"{name}-{digest}"), root);
        if (!paths.IsWithin(root, folder))
        {
            throw new InvalidDataException($"The skill folder for '{name}' is not inside {root}.");
        }

        List<(string Path, byte[] Bytes)> targets = [];
        foreach ((string relative, byte[] bytes) in contents)
        {
            string target = paths.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)), folder);
            if (!paths.IsWithin(folder, target))
            {
                throw new InvalidDataException($"The manifest file '{relative}' of skill '{name}' is not inside its folder.");
            }

            targets.Add((target, bytes));
        }

        // Content, not just length: a file that was truncated and padded, or tampered with, must
        // not be served as the skill this folder's digest names.
        if (targets.TrueForAll(t => files.FileExists(t.Path)
            && files.ReadAllBytes(t.Path).AsSpan().SequenceEqual(t.Bytes)))
        {
            return folder;
        }

        foreach ((string target, byte[] bytes) in targets)
        {
            string directory = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException($"The manifest file '{target}' has no containing folder.");
            files.CreateDirectory(directory);
            files.WriteAllBytesAtomic(target, bytes);
        }

        return folder;
    }
}
