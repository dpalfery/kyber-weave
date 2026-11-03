using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins how <c>squad update</c> retires the <c>response-format</c> skill from installs that already hold it.
///     The skill is no longer part of the product, so an update must delete the copies the operator never
///     edited, keep an edited copy and the receipt claim on it, and leave the home response guidance alone.
/// </summary>
/// <remarks>
///     The prior release is seeded here rather than installed from the product tree. The product no longer
///     ships the skill once it is retired, so an install from it could not produce the prior state this test
///     starts from. The seeded release is a copy of the synthetic corpus with its own inline skill.
/// </remarks>
public sealed class SquadResponseFormatRetirementTests : IDisposable
{
    private const string SeededSkillBody = "Seeded response-format body, shipped by the prior release.\n";
    private const string EditedSkillBody = "Operator-edited response-format copy that update must keep.\n";
    private const string HomeGuidanceBody = "Operator guidance that the retirement must never rewrite.\n";

    private static readonly SquadTarget[] Targets = [SquadTarget.Claude, SquadTarget.Cursor, SquadTarget.Factory];

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     An update from a prior install that owns the skill on several targets deletes every unedited copy,
    ///     keeps the one the operator edited with its receipt claim and a preserved-file warning, and leaves
    ///     the home guidance byte-identical. Without the delete mutation, the skill keeps shipping in installs
    ///     after the product dropped it.
    /// </summary>
    [Fact]
    public async Task UpdateDeletesUneditedResponseFormatCopiesAndKeepsTheEditedOneOnEveryTarget()
    {
        string root = Path.Combine(_temp.Path, "retire-response-format");
        string anchor = Path.Combine(root, "anchor");
        string globalRoots = Path.Combine(root, "global-roots");
        string home = Path.Combine(root, "home");
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(globalRoots);
        Directory.CreateDirectory(home);

        SquadStateStore store = new(new HomeUserPaths(Path.Combine(root, "app-data"), home));
        TempGlobalRoots roots = new(globalRoots);
        string guidancePath = Path.Combine(home, ".kyber-weave", "response-guidance.md");
        Directory.CreateDirectory(Path.GetDirectoryName(guidancePath)!);
        byte[] homeGuidance = Encoding.UTF8.GetBytes(HomeGuidanceBody);
        await File.WriteAllBytesAsync(guidancePath, homeGuidance);

        using SeededPriorReleaseSource prior = new(root);
        using SyntheticValidSquadReleaseSource synthetic = new();
        SquadLifecycleService priorLifecycle =
            new(prior, SquadCommandComposition.ResolveRenderer(), store, globalRoots: roots);
        SquadLifecycleResult install = await priorLifecycle.InstallAsync(new SquadInstallRequest(
            anchor,
            SquadDeploymentScope.Global,
            Targets,
            Version: "1.2.3"));
        Assert.True(install.Success, DescribeFailure(install));

        SquadReceipt priorReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(anchor, SquadDeploymentScope.Global));
        string[] priorTargets = priorReceipt.Files
            .Where(file => IsResponseFormatPath(file.RelativePath))
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            priorTargets.Length >= 2,
            "The prior install must own the response-format skill on several targets, or the update proves nothing " +
            $"about retirement. Owned targets: {string.Join(", ", priorTargets)}.");

        string[] deployed = Directory
            .EnumerateFiles(globalRoots, "*", SearchOption.AllDirectories)
            .Where(path => IsResponseFormatPath(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(deployed.Length >= 2, "The prior install must have written response-format copies to disk.");

        string edited = deployed[0];
        string[] unedited = deployed.Skip(1).ToArray();
        string editedTarget = Path.GetRelativePath(globalRoots, edited).Split(Path.DirectorySeparatorChar)[0];
        await File.WriteAllTextAsync(edited, EditedSkillBody, Encoding.UTF8);

        SquadLifecycleService updateLifecycle = new(
            synthetic,
            SquadCommandComposition.ResolveRenderer(),
            store,
            globalRoots: roots);
        SquadLifecycleResult update = await updateLifecycle.UpdateAsync(new SquadUpdateRequest(
            anchor,
            SquadDeploymentScope.Global,
            Targets,
            Version: "1.2.4"));

        Assert.True(update.Success, DescribeFailure(update));
        foreach (string path in unedited)
        {
            Assert.False(
                File.Exists(path),
                $"The unedited response-format copy '{path}' must be deleted: the product no longer ships the skill.");
        }

        Assert.True(File.Exists(edited), $"The locally edited response-format copy '{edited}' must be kept.");
        Assert.Equal(EditedSkillBody, await File.ReadAllTextAsync(edited, Encoding.UTF8));

        SquadReceipt? updatedReceipt = store.ReadReceipt(anchor, SquadDeploymentScope.Global);
        Assert.NotNull(updatedReceipt);
        Assert.Equal(
            new[] { editedTarget },
            updatedReceipt.Files.Where(file => IsResponseFormatPath(file.RelativePath)).Select(file => file.Target));

        Assert.Contains(
            update.Warnings ?? Array.Empty<string>(),
            warning => warning.Contains("Kept the locally edited deployed file", StringComparison.Ordinal) &&
                       warning.Contains("response-format", StringComparison.Ordinal));

        byte[] homeAfterUpdate = await File.ReadAllBytesAsync(guidancePath);
        Assert.Equal(homeGuidance, homeAfterUpdate);
    }

    private static bool IsResponseFormatPath(string path)
    {
        return path.Replace('\\', '/').Contains("/skills/response-format/", StringComparison.Ordinal) ||
               path.Replace('\\', '/').StartsWith("skills/response-format/", StringComparison.Ordinal);
    }

    private static string DescribeFailure(SquadLifecycleResult result)
    {
        return result.Errors is { Count: > 0 }
            ? string.Join(Environment.NewLine, result.Errors)
            : result.Warnings is { Count: > 0 }
                ? string.Join(Environment.NewLine, result.Warnings)
                : "No diagnosis was reported.";
    }

    /// <summary>
    ///     A release that carries the response-format skill as the prior version shipped it. It is the
    ///     synthetic corpus plus an inline skill and a bundle that lists it, so it does not read the product tree.
    /// </summary>
    private sealed class SeededPriorReleaseSource : ISquadReleaseSource
    {
        private const string AssetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        private readonly string _corpusRoot;

        public SeededPriorReleaseSource(string root)
        {
            _corpusRoot = Path.Combine(root, "prior-corpus");
            CopyTree(SyntheticValidSquadReleaseSource.SyntheticCorpusRoot, _corpusRoot);

            File.WriteAllText(
                Path.Combine(_corpusRoot, "bundles", "full.yml"),
                """
                schema: kyber-squad.bundle/v1
                name: full
                agents:
                  - conductor
                  - architect
                skills:
                  - response-format
                  - task-gate
                  - test-dev
                """,
                new UTF8Encoding(false));

            string skillRoot = Path.Combine(_corpusRoot, "skills", "response-format");
            Directory.CreateDirectory(skillRoot);
            File.WriteAllText(
                Path.Combine(skillRoot, "SKILL.md"),
                "---\nname: response-format\ndescription: \"Seeded prior response-format skill.\"\n---\n" +
                SeededSkillBody,
                new UTF8Encoding(false));
        }

        public Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            CopyTree(_corpusRoot, request.DestinationPath);
            List<string> extracted = Directory
                .EnumerateFiles(request.DestinationPath, "*", SearchOption.AllDirectories)
                .Select(path =>
                    Path.GetRelativePath(request.DestinationPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToList();

            string assetName = $"kyber-squad-{request.Version}.zip";
            return Task.FromResult(new SquadReleaseResult(
                request.Version,
                new SquadReleaseAsset(
                    assetName,
                    new Uri(
                        $"https://github.com/{request.Repository}/releases/download/v{request.Version}/{assetName}"),
                    1024),
                new SquadReleaseChecksum(assetName, AssetSha256),
                Path.GetFullPath(request.DestinationPath),
                extracted));
        }

        public void Dispose()
        {
            // The corpus lives under the test's own temp directory, which the test disposes.
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string directory in Directory.EnumerateDirectories(source))
                CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private sealed class HomeUserPaths(string applicationDataDirectory, string homeDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;
        public string HomeDirectory { get; } = homeDirectory;
    }

    private sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(root, SquadTargetCatalog.GetToken(target));
        }
    }
}
