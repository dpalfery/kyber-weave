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
