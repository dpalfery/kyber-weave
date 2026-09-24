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

    private static string ReleaseWorkflowPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows", "release.yml");

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

    /// <summary>
    /// The Releases API lists newest-created first, so creation order must not win; and a
    /// tag that is not a release version (<c>9.0.0/preview</c>) must never outrank one that
    /// is, however high its core parses.
    /// </summary>
    [Theory]
    [InlineData("0.1.7-rc.9\n0.1.7-rc.10\n0.1.6-rc.8\n", "0.1.7-rc.10")]
    [InlineData("0.1.7-rc.2\n0.1.8-rc.1\n", "0.1.8-rc.1")]
    [InlineData("0.1.7-rc.1\n", "0.1.7-rc.1")]
    [InlineData("9.0.0/preview\n0.1.7-rc.13\nlatest\n", "0.1.7-rc.13")]
    public void HighestVersionPicksSemVerMaximumNotFirstListed(string listed, string expected)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; kyber_weave_highest_version");

        ProcessResult result = ProcessRunner.Run(startInfo, listed);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput.Trim());
    }

    /// <summary>
    /// The Releases API pages at 100. A pre-release on a later page must still win, and
    /// drafts, stable releases and tags that are not versions must not, wherever they sit.
    /// </summary>
    [Fact]
    public void NewestPrereleaseReadsEveryPageAndSkipsDraftsAndStableReleases()
    {
        SkipOnWindows();

        const string stub = """
            stub() {
                case "$1" in
                    *page=1)
                        i=1
                        printf '['
                        while [ "$i" -le 100 ]; do
                            printf '{"tag_name":"v0.1.%s-rc.1","draft":false,"prerelease":true}' "$i"
                            if [ "$i" -lt 100 ]; then printf ','; fi
                            i=$((i + 1))
                        done
                        printf ']' ;;
                    *page=2)
                        printf '[{"tag_name":"v9.0.0/preview","draft":false,"prerelease":true},'
                        printf '{"tag_name":"v0.3.0-rc.1","draft":true,"prerelease":true},'
                        printf '{"tag_name":"v0.3.0","draft":false,"prerelease":false},'
                        printf '{"tag_name":"v0.2.0-rc.1","draft":false,"prerelease":true}]' ;;
                    *) printf '[]' ;;
                esac
            }
            """;
        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"\n" + stub +
            "\nkyber_weave_newest_prerelease https://api.invalid/releases stub");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0.2.0-rc.1", result.StandardOutput.Trim());
    }

    /// <summary>
    /// A failed later page must fail the whole scan: the first page alone is an incomplete
    /// answer that would install a lower version than the one the missing page holds.
    /// </summary>
    [Fact]
    public void NewestPrereleaseFailsWhenALaterPageCannotBeFetched()
    {
        SkipOnWindows();

        const string stub = """
            stub() {
                case "$1" in
                    *page=1)
                        i=1
                        printf '['
                        while [ "$i" -le 100 ]; do
                            printf '{"tag_name":"v0.1.%s-rc.1","draft":false,"prerelease":true}' "$i"
                            if [ "$i" -lt 100 ]; then printf ','; fi
                            i=$((i + 1))
                        done
                        printf ']' ;;
                    *) return 22 ;;
                esac
            }
            """;
        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"\n" + stub +
            "\nkyber_weave_newest_prerelease https://api.invalid/releases stub");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
    }

    /// <summary>Nothing to choose from is a failure, not an empty version.</summary>
    [Fact]
    public void HighestVersionFailsOnEmptyInput()
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; kyber_weave_highest_version");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
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
    // ---- build-tray release job (task 9.4, Requirements 12.1-12.4, 12.9) ----

    /// <summary>
    /// The asset names the <c>build-tray</c> job publishes, which are the same
    /// names <c>trayArtifactName</c> in <c>dash/src/install/origin.ts</c> asks
    /// for. A rename on either side leaves `kyberdash menubar` downloading an
    /// asset the release does not carry, and nothing else would catch it.
    /// </summary>
    private static readonly string[] TrayAssetNames =
    [
        "kyberdash-tray-darwin-arm64.zip",
        "kyberdash-tray-darwin-x64.zip",
        "kyberdash-tray-win-x64-setup.exe",
    ];

    /// <summary>The macOS credentials the job requires before it builds anything.</summary>
    private static readonly string[] RequiredSigningSecrets =
    [
        "APPLE_CERTIFICATE",
        "APPLE_CERTIFICATE_PASSWORD",
        "APPLE_SIGNING_IDENTITY",
        "APPLE_API_KEY",
        "APPLE_API_ISSUER",
        "APPLE_API_KEY_P8",
    ];

    /// <summary>
    /// The `secrets.*` the job actually reads, which are not the Tauri
    /// environment variable names above. Asserting only the variable names
    /// would pass against a workflow wired to a secret that does not exist —
    /// which is exactly what it was, until the certificate pair was pointed at
    /// the names the `release` environment really uses.
    /// </summary>
    private static readonly string[] RequiredSecretReferences =
    [
        "secrets.APPLE_DEVELOPER_ID_P12_BASE64",
        "secrets.APPLE_DEVELOPER_ID_P12_PASSWORD",
    ];

    [Fact]
    public void BuildTrayPublishesTheAssetNamesTheInstallerAsksFor()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        foreach (string asset in TrayAssetNames)
        {
            Assert.True(
                workflow.Contains(asset, StringComparison.Ordinal),
                $"release.yml does not publish {asset}, which kyberdash menubar downloads.");
        }

        // And the installer still asks for exactly these.
        string origin = File.ReadAllText(
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", "src", "install", "origin.ts"));
        Assert.Contains("kyberdash-tray-darwin-", origin, StringComparison.Ordinal);
        Assert.Contains("kyberdash-tray-win-x64-setup.exe", origin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Requirements 12.2 and 12.3. Tauri's bundler warns and skips notarization
    /// when credentials are missing rather than failing, so the job must check
    /// for itself — before the build, and again on the result.
    /// </summary>
    [Fact]
    public void BuildTrayFailsTheMacOsLegsBeforeBuildingWhenASecretIsMissing()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        foreach (string secret in RequiredSigningSecrets)
        {
            Assert.True(
                workflow.Contains(secret, StringComparison.Ordinal),
                $"release.yml never reads {secret}, so a missing one would not be caught.");
        }

        foreach (string reference in RequiredSecretReferences)
        {
            Assert.True(
                workflow.Contains(reference, StringComparison.Ordinal),
                $"release.yml does not read {reference}, the name the release environment "
                    + "actually holds, so the certificate would be empty at signing time.");
        }

        int presenceCheck = workflow.IndexOf("Require the signing secrets", StringComparison.Ordinal);
        int build = workflow.IndexOf("Build and sign the tray (macOS)", StringComparison.Ordinal);
        Assert.True(presenceCheck >= 0, "release.yml has no secrets-presence step.");
        Assert.True(build > presenceCheck, "the secrets check must run before the build.");

        // The bundler's success is not evidence; each property is asserted.
        foreach (string assertion in new[] { "spctl --assess", "stapler validate", "TeamIdentifier" })
        {
            Assert.True(
                workflow.Contains(assertion, StringComparison.Ordinal),
                $"release.yml does not check {assertion} on the built app.");
        }

        Assert.Contains("J2UNNQ466J", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Requirement 12.9: every action is pinned to a commit, not a tag. A tag
    /// is a moving reference, so a pin that is not a full 40-character SHA is
    /// not a pin at all.
    /// </summary>
    [Fact]
    public void EveryWorkflowActionIsPinnedToAFullCommitSha()
    {
        string workflowsDir = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows");
        List<string> unpinned = [];

        foreach (string file in Directory.EnumerateFiles(workflowsDir, "*.yml"))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("uses:", StringComparison.Ordinal)) continue;

                string reference = trimmed["uses:".Length..].Trim();
                // A local composite action is a path, not a pinned reference.
                if (reference.StartsWith('.')) continue;

                int at = reference.IndexOf('@', StringComparison.Ordinal);
                string sha = at < 0 ? string.Empty : reference[(at + 1)..].Split(' ')[0];
                bool pinned =
                    sha.Length == 40
                    && sha.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));

                if (!pinned)
                {
                    unpinned.Add($"{Path.GetFileName(file)}: {reference}");
                }
            }
        }

        Assert.True(
            unpinned.Count == 0,
            "Requirement 12.9: pin each of these to a 40-character commit SHA verified "
                + $"against its repository:\n  {string.Join("\n  ", unpinned)}");
    }

    /// <summary>Requirement 12.4: the Windows SmartScreen warning is explained in the notes.</summary>
    [Fact]
    public void ReleaseNotesCarryTheWindowsSmartScreenLine()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        Assert.Contains("SmartScreen", workflow, StringComparison.Ordinal);
        Assert.Contains("More info", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tray assets have to reach the checksum manifest, which they do by
    /// being artifacts the release job downloads before it runs sha256sum.
    /// </summary>
    [Fact]
    public void TrayAssetsJoinTheChecksumManifest()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        Assert.Contains("name: tray-${{ matrix.rid }}", workflow, StringComparison.Ordinal);
        Assert.Contains("sha256sum *", workflow, StringComparison.Ordinal);

        // The release job cannot publish what has not been built.
        Assert.Contains(
            "needs: [version, build, build-kyberdash, build-tray, pack-squad]",
            workflow,
            StringComparison.Ordinal);
    }

    // ---- --with-menubar (task 9.2, Requirement 12.7) ----

    /// <summary>The argv --with-menubar must produce, asserted by two tests.</summary>
    private static readonly string[] ExpectedMenubarArgv = ["menubar", "--force"];

    /// <summary>
    /// The tray installer is a <c>kyberdash</c> subcommand, so
    /// <c>--with-menubar</c> has to invoke that binary. It previously invoked
    /// <c>kyber-weave menubar</c>, which is not a command this repository
    /// ships — the flag installed nothing.
    /// </summary>
    [Fact]
    public void WithMenubarInvokesTheKyberdashBinaryWithForce()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        // A stand-in for the installed CLI that records how it was called.
        string recorder = sandbox.WriteExecutable(
            "kyberdash",
            "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/argv.txt\"\n");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_run_menubar '" + Path.GetDirectoryName(recorder) + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        string[] argv = File.ReadAllLines(
            Path.Combine(Path.GetDirectoryName(recorder)!, "argv.txt"));
        Assert.Equal(ExpectedMenubarArgv, argv);
    }

    /// <summary>
    /// An install directory with a space in it must reach the binary as one
    /// path, not two arguments.
    /// </summary>
    [Fact]
    public void WithMenubarQuotesAnInstallDirectoryContainingASpace()
    {
        SkipOnWindows();

        using Sandbox sandbox = new("with space");
        string recorder = sandbox.WriteExecutable(
            "kyberdash",
            "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/argv.txt\"\n");

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_run_menubar \"" + Path.GetDirectoryName(recorder) + "\"");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ExpectedMenubarArgv,
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(recorder)!, "argv.txt")));
    }

    /// <summary>
    /// Requirement 12.7: the two flags contradict each other, and the
    /// contradiction is reported before anything is downloaded rather than as
    /// a "not found" after the CLI and MCP are already on disk.
    /// </summary>
    [Theory]
    [InlineData("1", "1", 2)]
    [InlineData("1", "", 0)]
    [InlineData("", "1", 0)]
    [InlineData("", "", 0)]
    public void WithMenubarConflictsWithNoKyberdash(
        string withMenubar,
        string noKyberdash,
        int expectedExitCode)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; " +
            "kyber_weave_menubar_conflict '" + withMenubar + "' '" + noKyberdash + "'");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (expectedExitCode != 0)
        {
            Assert.Contains("--with-menubar", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("--no-kyberdash", result.StandardError, StringComparison.Ordinal);
        }
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _dir;
        public string SumsPath => Path.Combine(_dir, "SHA256SUMS.txt");

        public Sandbox(string suffix = "")
        {
            _dir = Path.Combine(
                Path.GetTempPath(),
                "kyber-weave-release-test-" + Guid.NewGuid().ToString("N") + suffix);
            Directory.CreateDirectory(_dir);
        }

        /// <summary>Writes a stand-in binary and makes it executable.</summary>
        public string WriteExecutable(string name, string script)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, script);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }

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
