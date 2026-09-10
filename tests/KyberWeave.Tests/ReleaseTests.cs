using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the two halves of the release-pipeline contract that this side of the
/// implementation owns:
///
///   (a) the OS / architecture -> RID mapping the release workflow and the
///       installer share, and
///   (b) the SHA256SUMS.txt verification the installer runs before placing
///       any binary on disk.
///
/// <c>scripts/install.sh</c> is the single source of truth for both. The
/// library-mode sourcing gated by <c>KYBER_WEAVE_INSTALL_LIB=1</c> exposes
/// the helpers without running the installer, so what these tests exercise is
/// the same shell code the user runs.
///
/// Task 12.1 ("Add a test covering checksum verification and the
/// platform-identifier resolution"). The installer tests are deliberately
/// POSIX-only; the test suite already skips POSIX-only assertions on Windows
/// runners.
/// </summary>
public sealed class ReleaseTests
{
    private static string InstallShPath => Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "install.sh");

    private static ProcessStartInfo CreateShellStartInfo(string script)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        // Library mode is critical: it tells install.sh to only define the
        // helpers and return, instead of executing the installer main flow.
        startInfo.Environment["KYBER_WEAVE_INSTALL_LIB"] = "1";
        return startInfo;
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell sourcing tests are not run on Windows.");
        }
    }

    // ---- platform -> Kyber-Weave RID and Kyber-Weave -> KyberDash RID ----

    [Theory]
    [InlineData("Linux", "x86_64", "linux-x64")]
    [InlineData("Linux", "amd64", "linux-x64")]
    [InlineData("Linux", "arm64", "linux-arm64")]
    [InlineData("Linux", "aarch64", "linux-arm64")]
    [InlineData("Darwin", "x86_64", "osx-x64")]
    [InlineData("Darwin", "amd64", "osx-x64")]
    [InlineData("Darwin", "arm64", "osx-arm64")]
    [InlineData("Darwin", "aarch64", "osx-arm64")]
    public void ResolveRidMapsPlatformIdentifierToSupportedRid(
        string unameOs,
        string unameArch,
        string expectedRid)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_resolve_rid '" + unameOs + "' '" + unameArch + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedRid, result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("osx-x64", "darwin-x64")]
    [InlineData("osx-arm64", "darwin-arm64")]
    [InlineData("linux-x64", "linux-x64")]
    [InlineData("linux-arm64", "linux-arm64")]
    [InlineData("win-x64", "win-x64")]
    // An RID that has no KyberDash counterpart demonstrates the helper
    // returns it verbatim rather than fabricating a publishable name.
    [InlineData("unknown-rid", "unknown-rid")]
    public void KyberDashRidReMapsOsxOnlyNodeSeaRid(
        string kyberWeaveRid,
        string expectedKyberDashRid)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_kyberdash_rid '" + kyberWeaveRid + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedKyberDashRid, result.StandardOutput.Trim());
    }

    // ---- KyberDash version floor ----
    //
    // install.sh ships unversioned from the default branch, so it must stay
    // installable against every tag it can resolve. Releases before
    // KYBERDASH_MIN_VERSION publish no kyberdash asset at all; asking for one
    // there 404s, and because every archive is verified before any is
    // installed, that aborts the whole install — CLI and MCP included.

    [Theory]
    // The floor itself, and the release below it that the documented one-line
    // install resolves by default.
    [InlineData("0.1.6", "0.1.7-rc.9", "-1")]
    [InlineData("0.1.7-rc.9", "0.1.7-rc.9", "0")]
    [InlineData("0.2.0", "0.1.7-rc.9", "1")]
    // String comparison sorts this pair the wrong way round.
    [InlineData("0.1.7-rc.10", "0.1.7-rc.9", "1")]
    [InlineData("0.1.9", "0.1.10", "-1")]
    // A pre-release ranks below the release it precedes.
    [InlineData("0.1.7-rc.9", "0.1.7", "-1")]
    [InlineData("1.0.0", "1.0.0-rc.1", "1")]
    // A leading v and build metadata carry no precedence.
    [InlineData("v0.1.7-rc.9", "0.1.7-rc.9+abc123", "0")]
    // SemVer 2.0.0's own precedence chain.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", "-1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", "-1")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", "-1")]
    // A missing field reads as zero.
    [InlineData("1.2", "1.2.0", "0")]
    public void SemverCompareFollowsSemVerPrecedence(string left, string right, string expected)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_semver_compare '" + left + "' '" + right + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput.Trim());
    }

    [Theory]
    // Every published release below the floor, including the rc line the floor
    // sits on, resolves to "skip".
    [InlineData("0.1.1", false)]
    [InlineData("0.1.6", false)]
    [InlineData("0.1.7-rc.7", false)]
    // The floor and everything above it resolves to "install".
    [InlineData("0.1.7-rc.9", true)]
    [InlineData("0.1.7-rc.10", true)]
    [InlineData("0.1.7", true)]
    [InlineData("0.2.0", true)]
    [InlineData("1.0.0", true)]
    public void ReleaseHasKyberDashGatesOnThePublishedFloor(string version, bool expected)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_release_has_kyberdash '" + version + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(expected ? 0 : 1, result.ExitCode);
    }

    [Fact]
    public void KyberDashMinVersionIsTheTagThatFirstPublishedTheAsset()
    {
        SkipOnWindows();

        // Pinned so raising the floor is a deliberate edit: bumping it silently
        // would stop installing KyberDash for everyone on the releases in between.
        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "printf '%s' \"$KYBERDASH_MIN_VERSION\"");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0.1.7-rc.9", result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("openbsd")]
    [InlineData("Plan9")]
    [InlineData("Solaris")]
    public void ResolveRidRejectsUnsupportedOs(string unsupportedOs)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_resolve_rid '" + unsupportedOs + "' x86_64");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.StandardOutput.Trim()),
            "unsupported OS should not produce a RID");
    }

    [Theory]
    [InlineData("powerpc")]
    [InlineData("sparc")]
    [InlineData("riscv64")]
    public void ResolveRidRejectsUnsupportedArchitecture(string unsupportedArch)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_resolve_rid 'Linux' '" + unsupportedArch + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrEmpty(result.StandardOutput.Trim()),
            "unsupported architecture should not produce a RID");
    }

    // ------------------- SHA256SUMS.txt verification cases ------------------
    //
    // These exercise the installer's verify_checksum helper, which is the same
    // logic the installer applies when fetching a real GitHub release. The
    // fixture-shaped sums file is built here in C# so the test never depends
    // on the network.

    [Fact]
    public void VerifyChecksumAcceptsMatchingHash()
    {
        SkipOnWindows();

        using Sandbox sandbox = new Sandbox();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        sandbox.AppendSumsLine(archive, nameHint: "kyberdash-linux-x64.tar.gz");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_verify_checksum '" + sandbox.SumsPath + "' '" + archive + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void VerifyChecksumRejectsTamperedArchive()
    {
        SkipOnWindows();

        using Sandbox sandbox = new Sandbox();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        sandbox.AppendSumsLine(archive, nameHint: "kyberdash-linux-x64.tar.gz");
        // Tamper after the sums line is written; the helper should detect the
        // divergence and return non-zero.
        File.AppendAllText(archive, "tampered");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_verify_checksum '" + sandbox.SumsPath + "' '" + archive + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(1, result.ExitCode);
        // The helper writes the diff to stderr so a user running the
        // installer can see what they were trying to install.
        Assert.Contains("SHA256 mismatch", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyChecksumRejectsMissingEntry()
    {
        SkipOnWindows();

        using Sandbox sandbox = new Sandbox();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        // Sums file lists a different asset; the basename of the archive
        // being verified is therefore absent.
        sandbox.AppendSumsLine(archive, nameHint: "kyber-weave-linux-x64.tar.gz");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_verify_checksum '" + sandbox.SumsPath + "' '" + archive + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void VerifyChecksumRejectsMalformedHashLine()
    {
        SkipOnWindows();

        using Sandbox sandbox = new Sandbox();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        // Manifest has a row that names the asset but with a non-hex hash —
        // the awk filter inside the helper must reject it as a structural
        // invariant, not a value mismatch.
        File.WriteAllText(sandbox.SumsPath, "garbagehash  kyberdash-linux-x64.tar.gz\n");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_verify_checksum '" + sandbox.SumsPath + "' '" + archive + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void VerifyChecksumIgnoresOtherAssetsHashesWhenBaselineMatches()
    {
        SkipOnWindows();

        using Sandbox sandbox = new Sandbox();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        string otherArchive = sandbox.WriteArchive(
            "kyberdash-linux-arm64.tar.gz",
            "kyberdash binary content for arm64");
        // Include a non-matching hash for the other-plaform asset so the awk
        // not only finds the right line but also confirms it ignores the wrong
        // one.
        string correctHash = Sha256(archive);
        string wrongHash = Sha256(otherArchive);
        File.WriteAllText(sandbox.SumsPath,
            wrongHash + "  kyberdash-linux-arm64.tar.gz\n" +
            correctHash + "  kyberdash-linux-x64.tar.gz\n");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_verify_checksum '" + sandbox.SumsPath + "' '" + archive + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
    }

    // ------------------------------------------------------------- Sandbox

    /// <summary>Per-test scratch directory for sums files and dummy archives.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "kyber-weave-release-test-" + Guid.NewGuid().ToString("N"));
        public string SumsPath => Path.Combine(_dir, "SHA256SUMS.txt");
        public Sandbox() { Directory.CreateDirectory(_dir); }

        public string WriteArchive(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void AppendSumsLine(string archivePath, string nameHint)
        {
            string hash = Sha256(archivePath);
            File.AppendAllText(SumsPath, hash + "  " + nameHint + "\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // best-effort cleanup; sandbox lives under %TEMP% and is
                // guaranteed-unique so a leak is bounded.
            }
        }
    }

    private static string Sha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream fs = File.OpenRead(path);
        byte[] bytes = sha.ComputeHash(fs);
        StringBuilder sb = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
