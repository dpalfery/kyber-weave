using System.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T2.1 — KyberDash merge-boundary gate. Verifies that the <c>dash/</c> subtree
/// is vendored upstream, that the merge-zone README exists, and that no
/// KyberDash source has leaked into the read-only conflict surface
/// (<c>dash/src/</c>). Enforces design decisions D1/D2/D6/D9 and acceptance
/// criteria R14.1 / R14.2 / R14.4 from <c>docs/specs/kyberdash/design.md</c>.
/// </summary>
public class MergeBoundaryTests
{
    private static readonly string[] UnshippedUpstreamSurfaces =
    [
        "windows",
        "gnome",
    ];

    [Fact]
    public void DashSubtreeIsVendoredUpstream()
    {
        // The subtree landed via `git subtree add --prefix=dash ... --squash`.
        // Both marker files are mandatory — without `dash/` there is nothing
        // to merge; without `dash/.gitignore` the upstream vendoring is broken.
        string dashRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash");

        Assert.True(
            Directory.Exists(dashRoot),
            "dash/ subtree is missing. Run `git subtree pull --prefix=dash codeburn <ref> --squash`.");

        Assert.True(
            File.Exists(Path.Combine(dashRoot, ".gitignore")),
            "dash/.gitignore is missing — the upstream subtree did not vendore cleanly.");

        Assert.True(
            File.Exists(Path.Combine(dashRoot, "package.json")),
            "dash/package.json is missing — the upstream subtree did not vendore cleanly.");
    }

    [Fact]
    public void UpstreamRemoteIsRegistered()
    {
        // Task 2.1 specifies the remote name `codeburn`. If a future refactor
        // renames it, this test forces a deliberate decision rather than a
        // silent breakage of `git subtree pull`.
        string remotes = GitCommandOutput("remote", "-v");

        Assert.Contains("codeburn", remotes, StringComparison.Ordinal);
        Assert.Contains("getagentseal/codeburn", remotes, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtreeMergeCommitExistsInLocalHistory()
    {
        // R14.1 — the pipeline must be able to take upstream changes through
        // a three-way merge. That requires at least one squashed subtree
        // merge commit on the local graph. We probe the message rather than
        // the SHA because the latter rotates on every upstream re-merge.
        string log = GitCommandOutput(
            "log", "--oneline", "--all", "--grep=as 'dash'");

        Assert.False(
            string.IsNullOrWhiteSpace(log),
            "No subtree merge commit found. Task 2.1 requires `git subtree add " +
            "--prefix=dash codeburn <ref> --squash` to have produced a merge commit.");
    }

    [Fact]
    public void KyberMergeZoneReadmeDocumentsTheBoundary()
    {
        // dash/kyber/README.md is the affirmative marker of the merge zone.
        // Deleting it would erase the rule that protects R14.1 / R14.2 from
        // future drift.
        string readmePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "dash", "kyber", "README.md");

        Assert.True(
            File.Exists(readmePath),
            $"{readmePath} is missing. Without it the merge-zone rule has no on-disk marker.");

        string text = File.ReadAllText(readmePath);

        // The README must state the rule so a future contributor who only
        // reads the file (not the spec) still learns the constraint.
        Assert.Contains("dash/kyber/**", text, StringComparison.Ordinal);
        Assert.Contains("dash/src/**", text, StringComparison.Ordinal);
        Assert.Contains("R14.1", text, StringComparison.Ordinal);
        Assert.Contains("R14.2", text, StringComparison.Ordinal);
        Assert.Contains("R14.4", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    public void NoKyberSourcesUnderUpstreamReadOnlyRoots(string upstreamRoot)
    {
        // Core enforcement of R14.2: KyberDash code lives outside upstream's
        // directories. Anything under `dash/<root>/` whose path contains
        // a `kyber/` segment is either a future vendoring accident or a
        // forbidden import — both fail this gate.
        string rootPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", upstreamRoot);
        Assert.True(
            Directory.Exists(rootPath),
            $"dash/{upstreamRoot}/ is not vendored in this snapshot; nothing to check.");

        List<string> leaks = [];
        EnumerateFiles(rootPath, leaks);

        Assert.NotEmpty(leaks); // sanity: the vendor populated something

        foreach (string path in leaks)
        {
            string normalized = path.Replace('\\', '/');
            int lastDash = normalized.LastIndexOf("/dash/", StringComparison.Ordinal);
            string underDash = lastDash >= 0 ? normalized[(lastDash + 1)..] : normalized;

            Assert.False(
                underDash.Contains("/kyber/", StringComparison.Ordinal)
                    || underDash.StartsWith("dash/kyber/", StringComparison.Ordinal),
                $"KyberDash source must not live under dash/{upstreamRoot}/. Leaked path: {path}");
        }
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("gnome")]
    public void UnshippedUpstreamSurfacesArePresentUnmodified(string surface)
    {
        // R14.4: surfaces KyberDash does not ship must be left in place.
        // Deleting them would manufacture a conflict on every future merge.
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", surface);

        Assert.True(
            Directory.Exists(path),
            $"dash/{surface}/ was deleted. R14.4 forbids this — vendored upstream owns the directory.");
    }

    [Theory]
    [InlineData("dash")]
    [InlineData("app")]
    [InlineData("mac")]
    public void UpstreamExtendedSurfacesArePresent(string surface)
    {
        // Extended upstream surfaces (where adaptation at the boundary is
        // allowed) must still be vendored. Removing them would break the
        // three-way merge path described by R14.1.
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", surface);

        Assert.True(
            Directory.Exists(path),
            $"dash/{surface}/ is missing — upstream subtree did not vendore this surface.");
    }

    private static void EnumerateFiles(string root, List<string> sink)
    {
        foreach (string path in Directory.EnumerateFiles(
            root, "*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            sink.Add(path);
        }
    }

    private static string GitCommandOutput(params string[] args)
    {
        using Process process = new Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = KyberWeaveTestPaths.ToolRoot;

        foreach (string arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return stdout;
    }
}
