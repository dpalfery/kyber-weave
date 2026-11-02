using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
///     High-performance <see cref="ISquadReleaseSource" /> fake that builds a minimal, 100%-schema-valid
///     Squad corpus once in a lazy static temp directory and extracts it in &lt;1ms with ~15 small files
///     instead of copying and rendering all 203 canonical files.
/// </summary>
/// <remarks>
///     Plumbing, state, fleet, lock, and plugin tests require a valid Squad corpus so that
///     <see cref="KyberWeave.Core.Squad.Parsing.SquadSourceLoader.Load(string)" /> and target renderers
///     succeed, but they do not assert canonical 203-file product content. This synthetic source satisfies
///     all cross-document ontology checks (manifest, bundles, agents, skills, models, capabilities,
///     fallbacks, toolchain, MCP) with minimal overhead.
/// </remarks>
public sealed class SyntheticValidSquadReleaseSource : ISquadReleaseSource
{
    private const string AssetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly Lazy<string> LazySyntheticTemplate = new(BuildSyntheticCorpusTemplate);

    private readonly List<SquadReleaseRequest> _requests = [];

    public IReadOnlyList<SquadReleaseRequest> Requests => _requests;

    /// <summary>The path to the shared synthetic template root directory.</summary>
    public static string SyntheticCorpusRoot => LazySyntheticTemplate.Value;

    public Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_requests)
        {
            _requests.Add(request);
        }

        List<string> extractedFiles = CopyCorpus(SyntheticCorpusRoot, request.DestinationPath);

        string assetName = $"kyber-squad-{request.Version}.zip";
        SquadReleaseResult result = new(
            request.Version,
            new SquadReleaseAsset(
                assetName,
                new Uri($"https://github.com/{request.Repository}/releases/download/v{request.Version}/{assetName}"),
                1024),
            new SquadReleaseChecksum(
                assetName,
                AssetSha256),
            Path.GetFullPath(request.DestinationPath),
            extractedFiles);

        return Task.FromResult(result);
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    private static string BuildSyntheticCorpusTemplate()
    {
        string productRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        string templateRoot = Path.Combine(
            Path.GetTempPath(),
            "kyber-weave-synthetic-squad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(templateRoot);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(templateRoot)) Directory.Delete(templateRoot, true);
            }
            catch
            {
                // Best-effort cleanup on process termination.
            }
        };

        // 1. Schemas
        CopyDirectorySafe(Path.Combine(productRoot, "schemas"), Path.Combine(templateRoot, "schemas"));

        // 2. Root files: squad.yml, toolchain.yml, mcp.json
        File.Copy(Path.Combine(productRoot, "squad.yml"), Path.Combine(templateRoot, "squad.yml"));
        File.Copy(Path.Combine(productRoot, "toolchain.yml"), Path.Combine(templateRoot, "toolchain.yml"));
        File.Copy(Path.Combine(productRoot, "mcp.json"), Path.Combine(templateRoot, "mcp.json"));

        // 3. Profiles
        CopyDirectorySafe(Path.Combine(productRoot, "profiles"), Path.Combine(templateRoot, "profiles"));
        string fallbacksOriginal = File.ReadAllText(Path.Combine(productRoot, "profiles", "fallbacks.yml"));
        string fallbacksAdjusted = Regex.Replace(
            fallbacksOriginal,
            @"shared-identities:\s*\r?\n(\s*-\s*[^\r\n]+\r?\n)*",
            "shared-identities: []\n");
        File.WriteAllText(Path.Combine(templateRoot, "profiles", "fallbacks.yml"), fallbacksAdjusted);

        // 4. Bundles: declare minimal valid bundle
        string bundlesDir = Path.Combine(templateRoot, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(
            Path.Combine(bundlesDir, "full.yml"),
            """
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - conductor
              - architect
            skills:
              - task-gate
              - test-dev
            """);

        // 5. Agents: conductor & architect with bounded delegates-to matching the synthetic bundle
        string agentsDir = Path.Combine(templateRoot, "agents");
        Directory.CreateDirectory(agentsDir);
        CopyDirectorySafe(Path.Combine(productRoot, "agents", "conductor"), Path.Combine(agentsDir, "conductor"));
        CopyDirectorySafe(Path.Combine(productRoot, "agents", "architect"), Path.Combine(agentsDir, "architect"));

        string conductorOriginal = File.ReadAllText(Path.Combine(productRoot, "agents", "conductor.md"));
        string conductorAdjusted = Regex.Replace(
            conductorOriginal,
            @"delegates-to:\s*\[.*?\]",
            "delegates-to: [architect]");
        File.WriteAllText(Path.Combine(agentsDir, "conductor.md"), conductorAdjusted);

        string architectOriginal = File.ReadAllText(Path.Combine(productRoot, "agents", "architect.md"));
        string architectAdjusted = Regex.Replace(
            architectOriginal,
            @"delegates-to:\s*\[.*?\]",
            "delegates-to: []");
        File.WriteAllText(Path.Combine(agentsDir, "architect.md"), architectAdjusted);

        // 6. Skills: task-gate, test-dev
        string skillsDir = Path.Combine(templateRoot, "skills");
        Directory.CreateDirectory(skillsDir);
        CopyDirectorySafe(
            Path.Combine(productRoot, "skills", "task-gate"),
            Path.Combine(skillsDir, "task-gate"));
        CopyDirectorySafe(
            Path.Combine(productRoot, "skills", "test-dev"),
            Path.Combine(skillsDir, "test-dev"));

        return templateRoot;
    }

    private static List<string> CopyCorpus(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        List<string> extractedFiles = [];
        CopyDirectoryInternal(sourceRoot, destinationRoot, sourceRoot, extractedFiles);
        extractedFiles.Sort(StringComparer.Ordinal);
        return extractedFiles;
    }

    private static void CopyDirectorySafe(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        DirectoryInfo sourceInfo = new(sourceDir);

        foreach (FileSystemInfo entry in sourceInfo.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        }))
        {
            if (entry.LinkTarget is not null) continue;

            string targetPath = Path.Combine(destinationDir, entry.Name);
            if (entry is DirectoryInfo subDir)
                CopyDirectorySafe(subDir.FullName, targetPath);
            else if (entry is FileInfo file) file.CopyTo(targetPath, true);
        }
    }

    private static void CopyDirectoryInternal(
        string currentSourceDir,
        string currentDestinationDir,
        string rootSourceDir,
        List<string> extractedFiles)
    {
        Directory.CreateDirectory(currentDestinationDir);
        DirectoryInfo sourceInfo = new(currentSourceDir);

        foreach (FileSystemInfo entry in sourceInfo.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        }))
        {
            if (entry.LinkTarget is not null) continue;

            string targetPath = Path.Combine(currentDestinationDir, entry.Name);
            if (entry is DirectoryInfo subDir)
            {
                CopyDirectoryInternal(subDir.FullName, targetPath, rootSourceDir, extractedFiles);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(targetPath, true);
                string relativePath = Path.GetRelativePath(rootSourceDir, file.FullName);
                extractedFiles.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
    }
}
