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
///     The prior release is seeded rather than installed from the product tree. The product no longer
///     ships the skill once it is retired, so an install from it could not produce the prior state this test
///     starts from. The seeded release is a copy of the synthetic corpus with its own inline skill. The
///     deployment it makes is built once for the run through <see cref="PreparedSquadInstalls"/> and copied
///     into this test's own home and global roots; the test then edits a deployed copy and runs the update
///     for real, because that edited deployment is the sequence under test.
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
        string globalRootsDirectory = Path.Combine(root, "global-roots");
        string home = Path.Combine(root, "home");
        string applicationData = Path.Combine(root, "app-data");
        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(globalRootsDirectory);
        Directory.CreateDirectory(home);

        HomeUserPaths userPaths = new(applicationData, home);
        SquadStateStore store = new(userPaths);
        TempGlobalRoots roots = new(globalRootsDirectory);
        string guidancePath = Path.Combine(home, ".kyber-weave", "response-guidance.md");

        PreparedPriorDeployment prior = PriorDeployment();
        prior.CopyInto(userPaths, anchor, globalRootsDirectory);
        Assert.Equal(0, prior.Record.ExitCode);

        SquadReceipt priorReceipt = Assert.IsType<SquadReceipt>(
            store.ReadReceipt(anchor, SquadDeploymentScope.Global));

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
            .EnumerateFiles(globalRootsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => IsResponseFormatPath(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(deployed.Length >= 2, "The prior install must have written response-format copies to disk.");

        string edited = deployed[0];
        string[] unedited = deployed.Skip(1).ToArray();
        string editedTarget = Path.GetRelativePath(globalRootsDirectory, edited).Split(Path.DirectorySeparatorChar)[0];
        await File.WriteAllTextAsync(edited, EditedSkillBody, Encoding.UTF8);

        using SyntheticValidSquadReleaseSource synthetic = new();
        SquadLifecycleService updateLifecycle = SquadHostDefaults.CreateLifecycleService(
            synthetic,
            SquadHostDefaults.ResolveRenderer(),
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
        Assert.Equal(prior.HomeGuidanceBeforeInstall, homeAfterUpdate);
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

    private static string RecipeKey => SquadInstallRecipe.Key(
        "squad-response-format-retirement",
        SquadDeploymentScope.Global,
        Targets,
        ("version", "1.2.3"),
        ("home-guidance", HomeGuidanceBody));

    private static PreparedPriorDeployment PriorDeployment() =>
        PreparedSquadInstalls.Get(RecipeKey, () => BuildPriorDeployment(RecipeKey));

    /// <summary>
    /// The one real prior install the class starts from: the seeded release, the real renderer and the
    /// operator's home guidance, made once for the run through the real lifecycle.
    /// </summary>
    private static PreparedPriorDeployment BuildPriorDeployment(string key)
    {
        TempDirectory? directory = new();
        try
        {
            string anchor = Path.Combine(directory.Path, "anchor");
            string home = Path.Combine(directory.Path, "home");
            string applicationData = Path.Combine(directory.Path, "app-data");
            string globalRoots = Path.Combine(directory.Path, "global-roots");
            Directory.CreateDirectory(anchor);
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(globalRoots);

            string guidancePath = Path.Combine(home, ".kyber-weave", "response-guidance.md");
            Directory.CreateDirectory(Path.GetDirectoryName(guidancePath)!);
            byte[] homeGuidance = Encoding.UTF8.GetBytes(HomeGuidanceBody);
            File.WriteAllBytes(guidancePath, homeGuidance);

            HomeUserPaths userPaths = new(applicationData, home);
            SquadStateStore store = new(userPaths);
            TempGlobalRoots roots = new(globalRoots);
            using SeededPriorReleaseSource prior = new(directory.Path);
            SquadLifecycleService lifecycle = SquadHostDefaults.CreateLifecycleService(
                prior,
                SquadHostDefaults.ResolveRenderer(),
                store,
                globalRoots: roots);

            SquadLifecycleResult result = lifecycle.InstallAsync(new SquadInstallRequest(
                    anchor,
                    SquadDeploymentScope.Global,
                    Targets,
                    Version: "1.2.3"))
                .GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"The prepared prior install failed: {string.Join("; ", result.Errors ?? [])}");
            }

            PreparedPriorDeployment prepared = new(
                key,
                directory,
                home,
                applicationData,
                globalRoots,
                homeGuidance,
                SquadInstallRecord.FromLifecycle(result, exitCode: 0));
            directory = null;
            return prepared;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    /// <summary>
    /// The shared prior install, copied into a test's own home, application data and per-target global
    /// roots. The copy rewrites the template's root in the home state, and rewrites it in the deployed
    /// files too while recording their new digests, so the copy is a faithful install at its new root:
    /// the update compares receipt digests with the bytes it finds.
    /// </summary>
    private sealed class PreparedPriorDeployment(
        string key,
        TempDirectory directory,
        string home,
        string applicationData,
        string globalRoots,
        byte[] homeGuidanceBeforeInstall,
        SquadInstallRecord record) : PreparedSquadInstall(key, directory, record)
    {
        /// <summary>The home guidance bytes the template held before its install ran.</summary>
        public byte[] HomeGuidanceBeforeInstall { get; } = homeGuidanceBeforeInstall;

        public void CopyInto(HomeUserPaths userPaths, string destinationAnchor, string destinationGlobalRoots)
        {
            ArgumentNullException.ThrowIfNull(userPaths);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationAnchor);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationGlobalRoots);

            SquadInstallCopy.Tree(home, userPaths.HomeDirectory);
            if (Directory.Exists(applicationData))
                SquadInstallCopy.Tree(applicationData, userPaths.ApplicationDataDirectory);
            SquadInstallCopy.Tree(globalRoots, destinationGlobalRoots);

            SquadInstallCopy.ReRoot(
                Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad"),
                SquadInstallCopy.Canonical(home),
                SquadInstallCopy.Canonical(userPaths.HomeDirectory));

            SquadStateStore store = new(userPaths);
            string receiptPath = store.ResolveReceiptPath(destinationAnchor, SquadDeploymentScope.Global);
            foreach (SquadTarget target in Targets)
            {
                string token = SquadTargetCatalog.GetToken(target);
                _ = SquadInstallCopy.ReRootDeployed(
                    Path.Combine(destinationGlobalRoots, token),
                    receiptPath,
                    store,
                    ForwardSlashed(Path.Combine(globalRoots, token)),
                    ForwardSlashed(Path.Combine(destinationGlobalRoots, token)),
                    token);
            }
        }

        private static string ForwardSlashed(string path) => path.Replace('\\', '/').TrimEnd('/');
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
                  - testing
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
