using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Pins the Copilot global-scope resource-link contract for the update path: the update must
/// render with the same resolved global roots as the install, otherwise the rewritten
/// conductor silently reverts to unanchored relative links.
/// </summary>
public sealed class SquadHotshotLifecycleCopilotGlobalUpdateLinksTests : IDisposable
{
    private const string ConductorPath = "agents/conductor.agent.md";

    private static readonly string[] ReferenceFiles =
    [
        "plan-path.md",
        "spec-path.md",
        "intake-path.md",
        "autonomy.md",
    ];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// After a global install and a version-bump update, the rewritten conductor must link each
    /// declared reference by an absolute path under the resolved Copilot root, and the receipt
    /// digest must describe exactly the bytes on disk.
    /// </summary>
    [Fact]
    public async Task GlobalCopilotUpdateRewritesAbsoluteReferenceLinksAndReceiptDigestMatchesWrittenBytes()
    {
        // The install is only the starting point here: the update's own rendering is what this test
        // asserts. Copy the run-wide prepared Copilot install into this test's home instead of
        // installing again, and use that home as the target root the copy's receipt names.
        string home = Path.Combine(_temp.Path, "links-update-home");
        Directory.CreateDirectory(home);
        using InstalledSquadHome template = InstalledSquadHome.Build([SquadTarget.Copilot]);
        SquadReceipt? copied = template.CopyInto(home);
        Assert.NotNull(copied);
        string targetRoot = home;
        SquadGlobalRoots globalRoots = new(_ => null, home);
        SquadStateStore stateStore = new(new SquadHostDefaults.CliSquadUserPaths(home, home));
        using CorpusSquadReleaseSource releaseSource = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
            releaseSource,
            SquadHostDefaults.ResolveRenderer(),
            stateStore,
            globalRoots: globalRoots);

        string root = globalRoots.ResolveGlobalRoot(SquadTarget.Copilot);
        string rootForwardSlashed = root.Replace('\\', '/').TrimEnd('/');
        string conductorFile = Path.Combine(root, "agents", "conductor.agent.md");

        // Drift the deployment to an older, unanchored rendering that the receipt still owns,
        // so the update has genuine managed bytes to replace. The copied bytes still name the
        // template's root, so match whatever absolute root they carry rather than the copy's.
        string installed = await File.ReadAllTextAsync(conductorFile);
        string older = Regex.Replace(
            installed,
            @"\]\([^)]*/agents/conductor/references/",
            "](conductor/references/");
        Assert.Contains("](conductor/references/", older, StringComparison.Ordinal);
        await File.WriteAllTextAsync(conductorFile, older);
        SquadReceipt installReceipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Global));
        string olderDigest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(conductorFile)));
        SquadReceipt driftedReceipt = installReceipt with
        {
            Files = installReceipt.Files
                .Select(file => string.Equals(file.RelativePath, ConductorPath, StringComparison.Ordinal)
                    && string.Equals(file.Target, "copilot", StringComparison.Ordinal)
                        ? file with { Sha256 = olderDigest }
                        : file)
                .ToArray(),
        };
        string receiptFile = stateStore.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global);
        await File.WriteAllTextAsync(receiptFile, stateStore.SerializeReceipt(driftedReceipt));

        SquadLifecycleResult update = await service.UpdateAsync(new SquadUpdateRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Global,
            Version: "1.2.3"));
        Assert.True(update.Success);

        string content = await File.ReadAllTextAsync(conductorFile);
        foreach (string reference in ReferenceFiles)
        {
            string expectedLink = $"]({rootForwardSlashed}/agents/conductor/references/{reference})";
            Assert.True(
                content.Contains(expectedLink, StringComparison.Ordinal),
                $"Updated conductor does not contain the absolute link '{expectedLink}'.");
        }

        Assert.DoesNotContain("](conductor/references/", content, StringComparison.Ordinal);

        SquadReceipt receipt = Assert.IsType<SquadReceipt>(
            stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Global));
        SquadOwnedFile owned = Assert.Single(
            receipt.Files,
            file => string.Equals(file.RelativePath, ConductorPath, StringComparison.Ordinal)
                && string.Equals(file.Target, "copilot", StringComparison.Ordinal));
        string writtenDigest = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(conductorFile)));
        Assert.Equal(writtenDigest, owned.Sha256);
    }
}
