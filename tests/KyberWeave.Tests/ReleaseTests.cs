using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the release-pipeline contract this test project owns:
///
///   (a) the OS / architecture -&gt; RID mapping the release workflow and the
///       installer share,
///   (b) the SHA256SUMS.txt verification the installer runs before placing
///       any binary on disk, and
///   (c) <c>KYBER_WEAVE_RELEASE_ORIGIN</c> — loopback <c>http</c>/<c>https</c>
///       only, the curl and wget split, and a pinned install from
///       <c>scripts/local-release-server.py</c>.
///
/// <c>scripts/install.sh</c> is the single source of truth. Library-mode
/// sourcing (<c>KYBER_WEAVE_INSTALL_LIB=1</c>) exposes helpers without running
/// the installer. Full-install facts do not set that variable: library mode
/// returns before download, checksum, and <c>install_binary</c>.
///
/// Task 12.1 ("Add a test covering checksum verification and the
/// platform-identifier resolution"). The installer tests are deliberately
/// POSIX-only; the test suite already skips POSIX-only assertions on Windows
/// runners.
/// </summary>
public sealed class ReleaseTests
{
    private static readonly char[] OperandsSeparators = { ' ', '\t' };

    private static string InstallShPath => Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "install.sh");

    private static string ReleaseWorkflowPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows", "release.yml");

    private static ProcessStartInfo CreateShellStartInfo(string script)
    {
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
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

        using Sandbox sandbox = new();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        sandbox.AppendSumsLine(archive, "kyberdash-linux-x64.tar.gz");

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

        using Sandbox sandbox = new();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        sandbox.AppendSumsLine(archive, "kyberdash-linux-x64.tar.gz");
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

        using Sandbox sandbox = new();
        string archive = sandbox.WriteArchive(
            "kyberdash-linux-x64.tar.gz",
            "kyberdash binary content for the matcher");
        // Sums file lists a different asset; the basename of the archive
        // being verified is therefore absent.
        sandbox.AppendSumsLine(archive, "kyber-weave-linux-x64.tar.gz");

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

        using Sandbox sandbox = new();
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

    /// <summary>Checksum verification reads only this asset's line, ignoring other assets' hashes in the sums file.</summary>
    [Fact]
    public void VerifyChecksumIgnoresOtherAssetsHashesWhenBaselineMatches()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
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
        "kyberdash-tray-win-x64-setup.exe"
    ];

    /// <summary>The macOS credentials the job requires before it builds anything.</summary>
    private static readonly string[] RequiredSigningSecrets =
    [
        "APPLE_CERTIFICATE",
        "APPLE_CERTIFICATE_PASSWORD",
        "APPLE_SIGNING_IDENTITY",
        "APPLE_API_KEY",
        "APPLE_API_ISSUER",
        "APPLE_API_KEY_P8"
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
        "secrets.APPLE_DEVELOPER_ID_P12_PASSWORD"
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
    /// The local release loop builds kyberdash from a copy of the <c>build-kyberdash</c>
    /// job's steps, because the job cannot run off a runner. The job stays the authority,
    /// so the values a copy could drift on are read from it here. A drifted copy would pass
    /// the loop with a binary the release never ships.
    /// </summary>
    [Fact]
    public void LocalKyberDashBuildMatchesTheReleaseJob()
    {
        string job = ReadBuildKyberDashJob();
        string local = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "release-local.sh"));

        // release-local.sh reads its Node version from dash/.nvmrc rather than repeating it.
        Match nodeVersion = Regex.Match(job, "NODE_VERSION: \"([^\"]+)\"");
        Assert.True(nodeVersion.Success, "build-kyberdash no longer pins NODE_VERSION.");
        string nvmrc = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", ".nvmrc")).Trim()
            .TrimStart('v');
        Assert.True(
            nodeVersion.Groups[1].Value == nvmrc,
            $"build-kyberdash builds on Node {nodeVersion.Groups[1].Value} but dash/.nvmrc, which the local build reads, says {nvmrc}.");

        Match fuse = Regex.Match(job, "--sentinel-fuse (NODE_SEA_FUSE_[0-9a-f]+)");
        Match postject = Regex.Match(job, "(postject@[0-9A-Za-z.-]+)");
        Assert.True(fuse.Success, "build-kyberdash no longer names a sentinel fuse.");
        Assert.True(postject.Success, "build-kyberdash no longer pins a postject version.");

        string[] shared =
        [
            fuse.Groups[1].Value,
            postject.Groups[1].Value,
            "--macho-segment-name NODE_SEA",
            "tsup --config tsup.sea.config.ts",
            "src/sea-shim.cjs",
            "\"useSnapshot\": false",
            "\"disableExperimentalSEAWarning\": true",
            "\"main.js\":",
            "\"package.json\":",
            "codesign --sign - --force",
            "THIRD_PARTY_NOTICES.md",
            "npm --prefix web ci",
            "npm --prefix web run build",
            "scripts/pack-sea-web.mjs",
            "\"web.json\":",
            "scripts/sea-web-smoke.mjs"
        ];
        foreach (string value in shared)
        {
            Assert.True(job.Contains(value, StringComparison.Ordinal),
                $"build-kyberdash no longer contains {value}; update this list with the job.");
            Assert.True(
                local.Contains(value, StringComparison.Ordinal),
                $"scripts/release-local.sh does not use {value}, which build-kyberdash does. Make the same change there.");
        }
    }

    /// <summary>
    /// Issue #157, defect 1: the web dashboard is embedded in the released binary.
    /// The release job and release-local.sh must build, embed, and smoke-test the dashboard
    /// in the correct order: tsup comes first, then web build, then pack, then SEA config.
    /// The smoke test runs after the blob is injected.
    /// </summary>
    [Fact]
    public void BuildKyberDashEmbedsTheWebDashboard()
    {
        string job = ReadBuildKyberDashJob();
        string local = ScopeBuildKyberDashFunction();

        // Collect violations from both sources so one message reports both.
        List<string> violations = [];

        AssertEmbedsWebDashboard("release.yml", job, violations);
        AssertEmbedsWebDashboard("release-local.sh", local, violations);

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// Reads the build-kyberdash job from release.yml, normalized to LF.
    /// The job is the section from "\n  build-kyberdash:\n" to the next two-space-indented key.
    /// </summary>
    private static string ReadBuildKyberDashJob()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath).ReplaceLineEndings("\n");
        int start = workflow.IndexOf("\n  build-kyberdash:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "release.yml has no build-kyberdash job.");
        // The job runs to the next two-space-indented key, which is the next job.
        Match next = Regex.Match(workflow[(start + 1)..], @"\n  [A-Za-z0-9_-]+:\n");
        return next.Success ? workflow.Substring(start, next.Index + 1) : workflow[start..];
    }

    /// <summary>
    /// Extracts the build_kyberdash() function from release-local.sh, then strips comments.
    /// Scoped from "\nbuild_kyberdash() {\n" to the following "\nif [ -z \"$NO_KYBERDASH\" ]".
    /// Both markers must exist.
    /// </summary>
    private static string ScopeBuildKyberDashFunction()
    {
        string local = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "release-local.sh"));
        int start = local.IndexOf("\nbuild_kyberdash() {\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "release-local.sh has no build_kyberdash() function.");
        int end = local.IndexOf("\nif [ -z \"$NO_KYBERDASH\" ]", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "release-local.sh build_kyberdash() function has no terminating marker.");

        string scoped = local.Substring(start, end - start);
        // Strip comments: lines whose trimmed text starts with #
        string[] lines = scoped.Split('\n');
        IEnumerable<string> filtered = lines.Where(line => !line.TrimStart().StartsWith('#'));
        return string.Join("\n", filtered);
    }

    /// <summary>
    /// Asserts that the given text (from release.yml job or release-local.sh) embeds the web dashboard
    /// in the correct order, and that archive commands name only the expected operands.
    /// Strips comments before checking, and collects violations into the list.
    /// </summary>
    private static void AssertEmbedsWebDashboard(string label, string text, List<string> violations)
    {
        // Strip comments before checking
        string[] lines = text.Split('\n');
        IEnumerable<string> filtered = lines.Where(line => !line.TrimStart().StartsWith('#'));
        string textNoComments = string.Join("\n", filtered);

        // Check ordering:
        // tsup comes before web run build.
        int tsupIndex = textNoComments.IndexOf("tsup --config tsup.sea.config.ts", StringComparison.Ordinal);
        if (tsupIndex < 0)
        {
            violations.Add(
                $"{label}: 'tsup --config tsup.sea.config.ts' not found; the dashboard embedding order starts there.");
            return;
        }

        int webBuildIndex = textNoComments.IndexOf("npm --prefix web run build", StringComparison.Ordinal);
        if (webBuildIndex < 0)
        {
            violations.Add(
                $"{label}: 'npm --prefix web run build' not found; add it to the 'Bundle dash CLI' step after tsup.");
            return;
        }

        if (tsupIndex >= webBuildIndex)
        {
            violations.Add(
                $"{label}: 'tsup --config tsup.sea.config.ts' must come before 'npm --prefix web run build'. Make the same change there.");
            return;
        }

        // web run build comes before pack-sea-web.mjs
        int packIndex = textNoComments.IndexOf("scripts/pack-sea-web.mjs", StringComparison.Ordinal);
        if (packIndex < 0)
        {
            violations.Add(
                $"{label}: 'scripts/pack-sea-web.mjs' not found; add it after 'npm --prefix web run build'.");
            return;
        }

        if (webBuildIndex >= packIndex)
        {
            violations.Add(
                $"{label}: 'npm --prefix web run build' must come before 'scripts/pack-sea-web.mjs'. Make the same change there.");
            return;
        }

        // pack-sea-web.mjs comes before --experimental-sea-config
        int seaConfigIndex = textNoComments.IndexOf("--experimental-sea-config", StringComparison.Ordinal);
        if (seaConfigIndex < 0)
        {
            violations.Add($"{label}: '--experimental-sea-config' not found.");
            return;
        }

        if (packIndex >= seaConfigIndex)
        {
            violations.Add(
                $"{label}: 'scripts/pack-sea-web.mjs' must come before '--experimental-sea-config'. Make the same change there.");
            return;
        }

        // Check sea-web-smoke.mjs comes after NODE_SEA_BLOB injection
        int blobIndex = textNoComments.IndexOf("NODE_SEA_BLOB", StringComparison.Ordinal);
        if (blobIndex < 0)
        {
            violations.Add($"{label}: 'NODE_SEA_BLOB' not found.");
            return;
        }

        int smokeIndex = textNoComments.IndexOf("scripts/sea-web-smoke.mjs", StringComparison.Ordinal);
        if (smokeIndex < 0)
        {
            violations.Add($"{label}: 'scripts/sea-web-smoke.mjs' not found; add it after the blob injection.");
            return;
        }

        if (blobIndex >= smokeIndex)
        {
            violations.Add(
                $"{label}: 'scripts/sea-web-smoke.mjs' must come after 'NODE_SEA_BLOB' injection. Make the same change there.");
            return;
        }

        // Check archive still names exactly the expected operands.
        // Use line-anchored regex with Multiline to capture full lines.
        AssertArchiveOperands(label, textNoComments, violations);
    }

    /// <summary>
    /// Issue #157 regression: each archive command's destination path is quoted
    /// (e.g. <c>"${BIN_DIR}/kyberdash-${RID}.tar.gz"</c>), so a closing <c>"</c> sits
    /// between the destination-path token and the operand list that follows. The three
    /// <c>AssertArchiveOperands</c> patterns required <c>\s+</c> to immediately follow that
    /// token with no tolerance for the quote, so <c>Regex.Match</c> never succeeded against
    /// the real files — meaning the operand-count/name check below it was silently
    /// unreachable, regardless of what the real archive commands contained. This pins the
    /// match, and the operands it extracts, against the real, unmodified release artifacts.
    /// </summary>
    [Fact]
    public void ArchiveOperandRegexesMatchTheQuotedDestinationPathInTheRealReleaseArtifacts()
    {
        string job = StripCommentLines(ReadBuildKyberDashJob());
        string local = ScopeBuildKyberDashFunction();

        // Match through the exact same Regex fields AssertArchiveOperands uses — a copied
        // literal here would pin a frozen pattern instead of the one production evaluates.
        Match releaseTar = TarArchiveOperandsRegex.Match(job);
        Assert.True(
            releaseTar.Success,
            "release.yml tar archive: the destination-path regex does not match the real, quoted command line.");
        Assert.Equal("\"kyberdash${EXE}\" THIRD_PARTY_NOTICES.md", releaseTar.Groups[2].Value.Trim());

        Match releaseZip = ZipArchiveOperandsRegex.Match(job);
        Assert.True(
            releaseZip.Success,
            "release.yml zip archive: the destination-path regex does not match the real, quoted command line.");
        Assert.Equal(
            "\"${FINAL_BIN}\" \"${BIN_DIR}/THIRD_PARTY_NOTICES.md\"", releaseZip.Groups[2].Value.Trim());

        Match localTar = TarArchiveOperandsRegex.Match(local);
        Assert.True(
            localTar.Success,
            "release-local.sh tar archive: the destination-path regex does not match the real, quoted command line.");
        Assert.Equal("kyberdash THIRD_PARTY_NOTICES.md", localTar.Groups[2].Value.Trim());
    }

    /// <summary>Strips comment lines the same way <see cref="AssertEmbedsWebDashboard"/> does.</summary>
    private static string StripCommentLines(string text)
    {
        string[] lines = text.Split('\n');
        IEnumerable<string> filtered = lines.Where(line => !line.TrimStart().StartsWith('#'));
        return string.Join("\n", filtered);
    }

    /// <summary>
    /// Matches a tar archive command's destination path and trailing operand list. A single
    /// field so <see cref="AssertArchiveOperands"/> and
    /// <see cref="ArchiveOperandRegexesMatchTheQuotedDestinationPathInTheRealReleaseArtifacts"/>
    /// exercise the same compiled pattern rather than two independently-maintained copies that
    /// could silently drift apart.
    /// </summary>
    private static readonly Regex TarArchiveOperandsRegex =
        new(@"tar\s+.*?-czf\s+.*?/(kyberdash-[^/\s]+\.\w+)""?\s+(.+)$", RegexOptions.Multiline);

    /// <summary>Same purpose as <see cref="TarArchiveOperandsRegex"/>, for the zip archive command.</summary>
    private static readonly Regex ZipArchiveOperandsRegex =
        new(@"zip\s+.*?/(kyberdash-[^/\s]+\.zip)""?\s+(.+)$", RegexOptions.Multiline);

    /// <summary>
    /// Asserts that archive commands in the text name only the expected operands.
    /// For release.yml: tar → ["kyberdash${EXE}", "THIRD_PARTY_NOTICES.md"], zip → ["${FINAL_BIN}", "${BIN_DIR}/THIRD_PARTY_NOTICES.md"]
    /// For release-local.sh: tar → ["kyberdash", "THIRD_PARTY_NOTICES.md"]
    /// Collects violations into the list. A regex that fails to match is itself a violation —
    /// not a silently-skipped block — so a future edit that breaks the pattern again cannot
    /// leave this method dark while still reporting a clean result.
    /// </summary>
    private static void AssertArchiveOperands(string label, string text, List<string> violations)
    {
        if (label == "release.yml")
        {
            // Check tar command: tar -C "${BIN_DIR}" -czf "${BIN_DIR}/kyberdash-${RID}.tar.gz" "kyberdash${EXE}" THIRD_PARTY_NOTICES.md
            // The destination path is quoted, so a closing " sits before the whitespace that
            // separates it from the operand list; "? tolerates that quote without requiring it.
            Match tarMatch = TarArchiveOperandsRegex.Match(text);
            if (tarMatch.Success)
            {
                string operands = tarMatch.Groups[2].Value.Trim();
                string[] parts = operands.Split(OperandsSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    // Trim quotes from operands
                    string op1 = parts[0].Trim('"');
                    string op2 = parts[1].Trim('"');
                    if (op1 != "kyberdash${EXE}" || op2 != "THIRD_PARTY_NOTICES.md")
                    {
                        violations.Add(
                            $"{label} tar archive: expected operands [\"kyberdash${{EXE}}\", \"THIRD_PARTY_NOTICES.md\"] but got [\"{op1}\", \"{op2}\"]. Update this list with the job.");
                    }
                }
                else
                {
                    // Exactly two operands are expected — the binary and THIRD_PARTY_NOTICES.md, nothing
                    // more. A third operand (e.g. a stray debug-symbols file) must fail here rather than
                    // silently widening the archive contract that install.sh and SelfUpdater.cs rely on.
                    violations.Add(
                        $"{label} tar archive: expected exactly 2 operands [\"kyberdash${{EXE}}\", \"THIRD_PARTY_NOTICES.md\"] but got {parts.Length} (\"{operands}\"). Update this list with the job.");
                }
            }
            else
            {
                // A non-matching regex must not silently skip the operand check above: that is
                // exactly the defect this test project already regressed on once (Issue #157).
                violations.Add(
                    $"{label} tar archive: destination-path regex did not match; the operand check below it did not run.");
            }

            // Check zip command: zip -9 -j "${BIN_DIR}/kyberdash-${RID}.zip" "${FINAL_BIN}" "${BIN_DIR}/THIRD_PARTY_NOTICES.md"
            // The destination path is quoted; "? tolerates the closing quote the same way as tar above.
            Match zipMatch = ZipArchiveOperandsRegex.Match(text);
            if (zipMatch.Success)
            {
                string operands = zipMatch.Groups[2].Value.Trim();
                string[] parts = operands.Split(OperandsSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    // Trim quotes from operands
                    string op1 = parts[0].Trim('"');
                    string op2 = parts[1].Trim('"');
                    if (op1 != "${FINAL_BIN}" || op2 != "${BIN_DIR}/THIRD_PARTY_NOTICES.md")
                    {
                        violations.Add(
                            $"{label} zip archive: expected operands [\"${{FINAL_BIN}}\", \"${{BIN_DIR}}/THIRD_PARTY_NOTICES.md\"] but got [\"{op1}\", \"{op2}\"]. Update this list with the job.");
                    }
                }
                else
                {
                    // Exactly two operands are expected — the binary and THIRD_PARTY_NOTICES.md, nothing
                    // more. A third operand (e.g. a stray debug-symbols file) must fail here rather than
                    // silently widening the archive contract that install.sh and SelfUpdater.cs rely on.
                    violations.Add(
                        $"{label} zip archive: expected exactly 2 operands [\"${{FINAL_BIN}}\", \"${{BIN_DIR}}/THIRD_PARTY_NOTICES.md\"] but got {parts.Length} (\"{operands}\"). Update this list with the job.");
                }
            }
            else
            {
                violations.Add(
                    $"{label} zip archive: destination-path regex did not match; the operand check below it did not run.");
            }
        }
        else if (label == "release-local.sh")
        {
            // Check tar command: tar -C "${stage}/out" -czf "${DEST}/kyberdash-${kyberdash_rid}.tar.gz" kyberdash THIRD_PARTY_NOTICES.md
            // The destination path is quoted; "? tolerates the closing quote the same way as above.
            Match tarMatch = TarArchiveOperandsRegex.Match(text);
            if (tarMatch.Success)
            {
                string operands = tarMatch.Groups[2].Value.Trim();
                string[] parts = operands.Split(OperandsSeparators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    // Trim quotes from operands
                    string op1 = parts[0].Trim('"');
                    string op2 = parts[1].Trim('"');
                    if (op1 != "kyberdash" || op2 != "THIRD_PARTY_NOTICES.md")
                    {
                        violations.Add(
                            $"{label} tar archive: expected operands [\"kyberdash\", \"THIRD_PARTY_NOTICES.md\"] but got [\"{op1}\", \"{op2}\"]. Make the same change there.");
                    }
                }
                else
                {
                    // Exactly two operands are expected — the binary and THIRD_PARTY_NOTICES.md, nothing
                    // more. A third operand (e.g. a stray debug-symbols file) must fail here rather than
                    // silently widening the archive contract that install.sh and SelfUpdater.cs rely on.
                    violations.Add(
                        $"{label} tar archive: expected exactly 2 operands [\"kyberdash\", \"THIRD_PARTY_NOTICES.md\"] but got {parts.Length} (\"{operands}\"). Make the same change there.");
                }
            }
            else
            {
                violations.Add(
                    $"{label} tar archive: destination-path regex did not match; the operand check below it did not run.");
            }
        }
    }

    /// <summary>
    /// Regression for the missing-<c>else</c> gap fixed alongside the regex above: when the
    /// destination-path regex fails to match — the exact defect that was silently invisible
    /// before this fix — <see cref="AssertArchiveOperands"/> must record a violation instead of
    /// leaving <see cref="BuildKyberDashEmbedsTheWebDashboard"/> green with nothing to show for it.
    /// </summary>
    [Fact]
    public void AssertArchiveOperandsReportsAViolationWhenTheDestinationPathRegexDoesNotMatch()
    {
        List<string> violations = [];

        AssertArchiveOperands("release.yml", "tar --help\nzip --help\n", violations);

        Assert.Equal(2, violations.Count);
        Assert.All(
            violations,
            violation => Assert.Contains("destination-path regex did not match", violation, StringComparison.Ordinal));
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

    // ------------------------------------------------- loopback release origin
    //
    // KYBER_WEAVE_RELEASE_ORIGIN is http or https, with no userinfo, and host
    // 127.0.0.1, localhost, or [::1], plus an optional port. Anything else,
    // including other 127.* addresses, is refused. These facts call fetch the
    // way the installer does. That function is defined only after the
    // KYBER_WEAVE_INSTALL_LIB return, so on an unchanged script the call fails
    // with "fetch: not found" before a checksum comparison and before the
    // installer body can contact GitHub. Full-install invocations below do not
    // set the library variable; they run only after that helper call has
    // returned.

    private const string PinnedVersion = "0.1.0";

    private const string ReleaseOwner = "dpalfery";

    private const string ReleaseRepo = "kyber-weave";

    /// <summary>Below <c>KYBERDASH_MIN_VERSION</c>, so <c>--no-mcp</c> is the only archive the contract's command line skips.</summary>
    private const string InstallPayload = "kyber-weave-local-origin";

    private static readonly string[] ProxyVariables =
    [
        "http_proxy",
        "https_proxy",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "all_proxy"
    ];

    private const string RedirectServerScript = """
                                                from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

                                                class Handler(BaseHTTPRequestHandler):
                                                    protocol_version = "HTTP/1.1"

                                                    def do_GET(self):
                                                        self.send_response(302)
                                                        self.send_header("Location", "http://example.com/not-loopback")
                                                        self.send_header("Content-Length", "0")
                                                        self.end_headers()

                                                    def log_message(self, fmt, *args):
                                                        return

                                                server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
                                                print(server.server_address[1], flush=True)
                                                server.serve_forever()
                                                """;

    /// <summary>
    /// An unset origin still refuses plain HTTP. The dest file is the download
    /// <c>fetch</c> would have written; a refusal leaves it absent.
    /// </summary>
    [Fact]
    public void ReleaseOriginUnsetRefusesPlainHttpAndWritesNoFile()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string destination = Path.Combine(sandbox.Root, "download");
        string log = Path.Combine(sandbox.Root, "curl-args");
        ProcessResult result = InvokeFetch(
            null,
            "http://127.0.0.1/dpalfery/kyber-weave/releases/download/v0.1.0/SHA256SUMS.txt",
            destination,
            PrependDownloader(sandbox, "curl", log),
            false,
            false);

        Assert.Contains("refusing non-HTTPS URL", result.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(destination), Describe(result));
        Assert.False(DownloaderWasInvoked(log), Describe(result));
    }

    /// <summary>
    /// A legal origin is only <c>http</c> or <c>https</c>, with no userinfo, and
    /// host <c>127.0.0.1</c>, <c>localhost</c>, or <c>[::1]</c>, with an optional
    /// port. While that override is active, curl uses <c>--proto '=http,https'</c>
    /// for <c>http</c> and keeps <c>--proto '=https'</c> for <c>https</c>. Both
    /// keep <c>--proto-redir '=https'</c>. <c>fetch_stdout</c> takes the same flags.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1", "=http,https")]
    [InlineData("http://127.0.0.1:9", "=http,https")]
    [InlineData("http://127.0.0.1/", "=http,https")]
    [InlineData("http://localhost", "=http,https")]
    [InlineData("http://localhost:80", "=http,https")]
    [InlineData("http://[::1]", "=http,https")]
    [InlineData("http://[::1]:9", "=http,https")]
    [InlineData("https://127.0.0.1", "=https")]
    [InlineData("https://127.0.0.1:443", "=https")]
    [InlineData("https://localhost", "=https")]
    [InlineData("https://localhost:9", "=https")]
    [InlineData("https://[::1]", "=https")]
    [InlineData("https://[::1]:443", "=https")]
    public void ReleaseOriginAcceptsLoopbackHttpOrHttps(string origin, string proto)
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string destination = Path.Combine(sandbox.Root, "download");
        string log = Path.Combine(sandbox.Root, "curl-args");
        string url = SumsUrl(origin);
        ProcessResult result = InvokeFetch(
            origin,
            url,
            destination,
            PrependDownloader(sandbox, "curl", log),
            false,
            true);

        Assert.True(result.ExitCode == 0, Describe(result));
        List<string[]> invocations = ReadDownloaderInvocations(log);
        Assert.Equal(2, invocations.Count);
        foreach (string[] args in invocations)
        {
            AssertArgSequence(args, "--proto", proto, "--proto-redir", "=https");
            Assert.Contains("-fsSL", args);
            Assert.Contains(url, args);
            if (proto == "=https")
            {
                Assert.DoesNotContain("=http,https", args);
            }
        }
    }

    /// <summary>
    /// Any other host, including other <c>127.*</c> addresses, userinfo, and a
    /// scheme other than <c>http</c> or <c>https</c>, is refused. A legal origin
    /// does not unlock an <c>http</c> URL whose host is not loopback. Nothing is
    /// written and the downloader is not started.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.2", "")]
    [InlineData("http://127.0.0.2:8080", "")]
    [InlineData("http://127.1.0.1", "")]
    [InlineData("http://127.0.0.10", "")]
    [InlineData("https://127.0.0.2", "")]
    [InlineData("http://example.com", "")]
    [InlineData("https://example.com", "")]
    [InlineData("https://github.com", "")]
    [InlineData("http://user:pass@127.0.0.1:9", "")]
    [InlineData("https://user:pass@127.0.0.1", "")]
    [InlineData("http://user@localhost", "")]
    [InlineData("ftp://127.0.0.1", "")]
    [InlineData("http://[::2]", "")]
    [InlineData("http://[::2]:9", "")]
    [InlineData("http://0.0.0.0", "")]
    [InlineData("http://127.0.0.1.example.com", "")]
    [InlineData("http://localhost.localdomain", "")]
    [InlineData("http://2130706433", "")]
    [InlineData("http://[::ffff:127.0.0.1]", "")]
    [InlineData("http://::1", "")]
    [InlineData("http://127.0.0.1:abc", "")]
    [InlineData("http://127.0.0.1:", "")]
    [InlineData("http://127.0.0.1:9", "http://example.com/outside")]
    [InlineData("https://example.com", "http://127.0.0.1/dpalfery/kyber-weave/releases/download/v0.1.0/SHA256SUMS.txt")]
    public void ReleaseOriginRefusesNonLoopbackUserinfoAndForeignHosts(string origin, string explicitTarget)
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string destination = Path.Combine(sandbox.Root, "download");
        string log = Path.Combine(sandbox.Root, "curl-args");
        string url = explicitTarget.Length == 0 ? SumsUrl(origin) : explicitTarget;
        ProcessResult result = InvokeFetch(
            origin,
            url,
            destination,
            PrependDownloader(sandbox, "curl", log),
            false,
            false);

        Assert.Contains("kyber-weave: error:", result.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(destination), Describe(result));
        Assert.False(DownloaderWasInvoked(log), Describe(result));
    }

    /// <summary>
    /// An <c>http</c> origin refuses wget. Curl is absent on purpose, so the
    /// installer would otherwise select wget and then must stop before it runs.
    /// </summary>
    [Fact]
    public void ReleaseOriginHttpRefusesWget()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string destination = Path.Combine(sandbox.Root, "download");
        string log = Path.Combine(sandbox.Root, "wget-args");
        const string origin = "http://127.0.0.1:9";
        ProcessResult result = InvokeFetch(
            origin,
            SumsUrl(origin),
            destination,
            IsolatedWgetDirectory(sandbox, log),
            true,
            false);

        Assert.Contains("wget", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("refusing", result.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(destination), Describe(result));
        Assert.False(DownloaderWasInvoked(log), Describe(result));
    }

    /// <summary>
    /// An <c>https</c> loopback origin keeps today's wget flags (<c>--https-only</c>).
    /// </summary>
    [Fact]
    public void ReleaseOriginHttpsKeepsWgetHttpsOnly()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string destination = Path.Combine(sandbox.Root, "download");
        string log = Path.Combine(sandbox.Root, "wget-args");
        const string origin = "https://127.0.0.1:9";
        string url = SumsUrl(origin);
        ProcessResult result = InvokeFetch(
            origin,
            url,
            destination,
            IsolatedWgetDirectory(sandbox, log),
            true,
            false);

        Assert.True(result.ExitCode == 0, Describe(result));
        List<string[]> invocations = ReadDownloaderInvocations(log);
        string[] wgetArgs = Assert.Single(invocations);
        AssertArgSequence(wgetArgs, "--https-only");
        Assert.Contains(url, wgetArgs);
    }

    /// <summary>
    /// With a legal loopback origin, <c>install.sh --install-dir --version --no-mcp</c>
    /// downloads from <c>scripts/local-release-server.py</c> and installs.
    /// <c>KYBER_WEAVE_INSTALL_LIB</c> is not set on that invocation.
    /// </summary>
    [Fact]
    public void ReleaseOriginInstallsPinnedVersionFromTheLocalReleaseServer()
    {
        SkipOnWindows();

        using PublishedRelease release = PublishedRelease.Create(false);
        string fetchDest = Path.Combine(release.Sandbox.Root, "fetched-sums.txt");
        ProcessResult fetch = InvokeFetch(
            release.Origin,
            SumsUrl(release.Origin),
            fetchDest,
            null,
            false,
            false);

        // The helper has to accept the loopback URL before the installer body
        // runs. Until it is defined, this fails closed and the body — which
        // would otherwise ignore the origin and contact GitHub — does not start.
        Assert.True(fetch.ExitCode == 0, Describe(fetch));
        Assert.Contains(release.AssetName, File.ReadAllText(fetchDest), StringComparison.Ordinal);

        ProcessResult install = RunFullInstall(release.InstallDirectory, release.Origin);
        Assert.True(install.ExitCode == 0, Describe(install));
        string installed = Path.Combine(release.InstallDirectory, "kyber-weave");
        Assert.Equal(InstallPayload, File.ReadAllText(installed));
        Assert.False(File.Exists(Path.Combine(release.InstallDirectory, "kyber-weave-mcp")));
    }

    /// <summary>
    /// A byte flipped in the asset after <c>SHA256SUMS.txt</c> is written exits
    /// non-zero, reports a SHA-256 mismatch, and leaves the install directory
    /// without <c>kyber-weave</c>. The live install path must be the thing that
    /// fails: a green helper-only checksum test would hide a second parser in
    /// <c>verify_and_extract</c>.
    /// </summary>
    [Fact]
    public void ReleaseOriginRejectsByteFlippedAfterTheChecksumIsWritten()
    {
        SkipOnWindows();

        using PublishedRelease release = PublishedRelease.Create(true);
        string fetchDest = Path.Combine(release.Sandbox.Root, "fetched-sums.txt");
        ProcessResult fetch = InvokeFetch(
            release.Origin,
            SumsUrl(release.Origin),
            fetchDest,
            null,
            false,
            false);
        Assert.True(fetch.ExitCode == 0, Describe(fetch));

        ProcessResult install = RunFullInstall(release.InstallDirectory, release.Origin);
        Assert.Contains("SHA256 mismatch", install.StandardError, StringComparison.Ordinal);
        Assert.NotEqual(0, install.ExitCode);
        Assert.False(
            File.Exists(Path.Combine(release.InstallDirectory, "kyber-weave")),
            Describe(install));
    }

    /// <summary>
    /// An HTTP redirect away from loopback is not installed. The loopback URL
    /// itself must have been accepted; a scheme refusal of that URL is not this
    /// outcome. The logging curl records the URL the installer actually requested.
    /// </summary>
    [Fact]
    public void ReleaseOriginHttpRedirectAwayFromLoopbackIsNotInstalled()
    {
        SkipOnWindows();

        using Sandbox sandbox = new();
        string script = Path.Combine(sandbox.Root, "redirect.py");
        File.WriteAllText(script, RedirectServerScript);
        using ListeningServer server = ListeningServer.Start(script);
        string origin = "http://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        string fetchDest = Path.Combine(sandbox.Root, "fetched");
        ProcessResult fetch = InvokeFetch(
            origin,
            SumsUrl(origin),
            fetchDest,
            null,
            false,
            false);

        Assert.True(
            fetch.ExitCode != 127
            && !fetch.StandardError.Contains("fetch: not found", StringComparison.Ordinal),
            Describe(fetch));

        string log = Path.Combine(sandbox.Root, "curl-args");
        string installDir = Path.Combine(sandbox.Root, "install");
        Directory.CreateDirectory(installDir);
        ProcessResult install = RunFullInstall(
            installDir,
            origin,
            PrependRecordingCurl(sandbox, log));

        Assert.NotEqual(0, install.ExitCode);
        Assert.False(File.Exists(Path.Combine(installDir, "kyber-weave")), Describe(install));
        string recorded = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
        Assert.Contains(origin, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "refusing non-HTTPS URL: " + origin,
            install.StandardError,
            StringComparison.Ordinal);
    }

    private static string SumsUrl(string origin)
    {
        string prefix = origin.EndsWith('/') ? origin[..^1] : origin;
        return prefix
               + "/"
               + ReleaseOwner
               + "/"
               + ReleaseRepo
               + "/releases/download/v"
               + PinnedVersion
               + "/SHA256SUMS.txt";
    }

    private static string Describe(ProcessResult result)
    {
        return "exit "
               + result.ExitCode.ToString(CultureInfo.InvariantCulture)
               + "\nstderr:\n"
               + result.StandardError
               + "\nstdout:\n"
               + result.StandardOutput;
    }

    private static void AssertArgSequence(IReadOnlyList<string> args, params string[] expected)
    {
        int from = 0;
        foreach (string token in expected)
        {
            int index = -1;
            for (int i = from; i < args.Count; i++)
            {
                if (string.Equals(args[i], token, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            Assert.True(
                index >= 0,
                "missing '" + token + "' in [" + string.Join(' ', args) + "]");
            from = index + 1;
        }
    }

    /// <summary>
    /// Sources <c>install.sh</c> in library mode and calls <c>fetch</c>.
    /// <paramref name="origin"/> null removes <c>KYBER_WEAVE_RELEASE_ORIGIN</c>.
    /// </summary>
    private static ProcessResult InvokeFetch(
        string? origin,
        string url,
        string destination,
        string? toolDirectory,
        bool replacePath,
        bool includeStdout)
    {
        string body = "fetch \"$KYBER_WEAVE_TEST_URL\" \"$KYBER_WEAVE_TEST_DEST\"\n";
        if (includeStdout)
        {
            body += "fetch_stdout \"$KYBER_WEAVE_TEST_URL\"\n";
        }

        ProcessStartInfo startInfo = CreateShellStartInfo(". \"" + InstallShPath + "\"\n" + body);
        if (origin is null)
        {
            startInfo.Environment.Remove("KYBER_WEAVE_RELEASE_ORIGIN");
        }
        else
        {
            startInfo.Environment["KYBER_WEAVE_RELEASE_ORIGIN"] = origin;
        }

        startInfo.Environment["KYBER_WEAVE_TEST_URL"] = url;
        startInfo.Environment["KYBER_WEAVE_TEST_DEST"] = destination;
        if (toolDirectory is not null)
        {
            startInfo.Environment["PATH"] = replacePath
                ? toolDirectory
                : toolDirectory + ":" + startInfo.Environment["PATH"];
        }

        ClearProxies(startInfo);
        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Runs the installer the way a user would. Does not set
    /// <c>KYBER_WEAVE_INSTALL_LIB</c>: that gate returns before any download.
    /// </summary>
    private static ProcessResult RunFullInstall(string installDir, string origin, string? prependPath = null)
    {
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(InstallShPath);
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(PinnedVersion);
        startInfo.ArgumentList.Add("--no-mcp");
        startInfo.Environment["KYBER_WEAVE_RELEASE_ORIGIN"] = origin;
        startInfo.Environment.Remove("KYBER_WEAVE_INSTALL_LIB");
        if (prependPath is not null)
        {
            startInfo.Environment["PATH"] = prependPath + ":" + startInfo.Environment["PATH"];
        }

        ClearProxies(startInfo);
        if (startInfo.Environment.TryGetValue("KYBER_WEAVE_INSTALL_LIB", out string? library) && library == "1")
        {
            throw new InvalidOperationException(
                "Full-install facts must not set KYBER_WEAVE_INSTALL_LIB; library mode returns before download.");
        }

        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
    }

    private static void ClearProxies(ProcessStartInfo startInfo)
    {
        foreach (string name in ProxyVariables)
        {
            startInfo.Environment.Remove(name);
        }
    }

    private static string CurrentInstallRid()
    {
        ProcessStartInfo startInfo = CreateShellStartInfo(
            ". \"" + InstallShPath + "\"; kyber_weave_resolve_rid \"$(uname -s)\" \"$(uname -m)\"");
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput.Trim();
    }

    private static string PrependDownloader(Sandbox sandbox, string toolName, string logPath)
    {
        string directory = Path.Combine(sandbox.Root, toolName + "-bin");
        Directory.CreateDirectory(directory);
        WriteDownloader(Path.Combine(directory, toolName), logPath, null);
        return directory;
    }

    private static string PrependRecordingCurl(Sandbox sandbox, string logPath)
    {
        string directory = Path.Combine(sandbox.Root, "curl-bin");
        Directory.CreateDirectory(directory);
        WriteDownloader(Path.Combine(directory, "curl"), logPath, ResolveExecutable("curl"));
        return directory;
    }

    /// <summary>
    /// PATH that contains wget, tar, and a checksum tool, and not curl, so the
    /// installer selects wget before the library return. <c>install.sh</c> accepts
    /// <c>sha256sum</c> or <c>shasum</c>. macOS ships the latter, and this directory
    /// replaces PATH, so the host tool is invisible unless it is linked here.
    /// </summary>
    private static string IsolatedWgetDirectory(Sandbox sandbox, string logPath)
    {
        string directory = Path.Combine(sandbox.Root, "wget-only");
        Directory.CreateDirectory(directory);
        WriteDownloader(Path.Combine(directory, "wget"), logPath, null);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(directory, "tar"), ResolveExecutable("tar"));
            string? checksum = TryResolveExecutable("sha256sum") ?? TryResolveExecutable("shasum");
            if (checksum is null)
            {
                throw SkipException.ForSkip(
                    "wget origin facts need sha256sum or shasum on PATH.");
            }

            File.CreateSymbolicLink(Path.Combine(directory, Path.GetFileName(checksum)), checksum);
        }

        return directory;
    }

    private static void WriteDownloader(string path, string logPath, string? execRealTool)
    {
        string body = "#!/bin/sh\nprintf '%s\\n' \"$@\" >> '"
                      + logPath
                      + "'\nprintf '\\n' >> '"
                      + logPath
                      + "'\n";
        if (execRealTool is null)
        {
            body += "exit 0\n";
        }
        else
        {
            body += "exec '" + execRealTool + "' \"$@\"\n";
        }

        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool DownloaderWasInvoked(string logPath)
    {
        return File.Exists(logPath) && File.ReadAllText(logPath).Trim().Length > 0;
    }

    private static List<string[]> ReadDownloaderInvocations(string logPath)
    {
        List<string[]> invocations = [];
        if (!File.Exists(logPath))
        {
            return invocations;
        }

        string[] blocks = File.ReadAllText(logPath).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (string block in blocks)
        {
            string[] args = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length > 0)
            {
                invocations.Add(args);
            }
        }

        return invocations;
    }

    private static string ResolveExecutable(string name)
    {
        string? path = TryResolveExecutable(name);
        Assert.True(path is not null, "command -v " + name + " failed");
        return path;
    }

    /// <summary>
    /// Absolute path of <paramref name="name"/>, or null when it is not on PATH.
    /// Does not add interpreters such as perl; <c>shasum</c> on macOS uses a fixed
    /// <c>/usr/bin/perl</c> shebang.
    /// </summary>
    private static string? TryResolveExecutable(string name)
    {
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("command -v " + name);
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0)
        {
            return null;
        }

        string path = result.StandardOutput.Trim();
        return path.Length == 0 ? null : path;
    }

    // ------------------------------------------------------------- Sandbox

    /// <summary>Per-test scratch directory for sums files and dummy archives.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly string _dir;

        public string Root => _dir;

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
                Directory.Delete(_dir, true);
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
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// A <c>v0.1.0</c> tree served by <c>scripts/local-release-server.py</c>.
    /// The checksum file is written before a corrupt archive is flipped, which
    /// is the order the mismatch fact has to observe.
    /// </summary>
    private sealed class PublishedRelease : IDisposable
    {
        private readonly Sandbox _sandbox;
        private readonly ListeningServer _server;

        private PublishedRelease(Sandbox sandbox, ListeningServer server, string assetName)
        {
            _sandbox = sandbox;
            _server = server;
            AssetName = assetName;
            InstallDirectory = Path.Combine(sandbox.Root, "install");
            Directory.CreateDirectory(InstallDirectory);
            Origin = "http://127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture);
        }

        public Sandbox Sandbox => _sandbox;

        public string Origin { get; }

        public string InstallDirectory { get; }

        public string AssetName { get; }

        public static PublishedRelease Create(bool corruptArchive)
        {
            Sandbox sandbox = new();
            ListeningServer? server = null;
            try
            {
                string rid = CurrentInstallRid();
                string assetName = "kyber-weave-" + rid + ".tar.gz";
                string tagDir = Path.Combine(sandbox.Root, "v" + PinnedVersion);
                Directory.CreateDirectory(tagDir);
                string stage = Path.Combine(sandbox.Root, "stage");
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "kyber-weave"), InstallPayload);
                string archive = Path.Combine(tagDir, assetName);

                ProcessStartInfo tar = new(ResolveExecutable("tar"))
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                tar.ArgumentList.Add("-czf");
                tar.ArgumentList.Add(archive);
                tar.ArgumentList.Add("-C");
                tar.ArgumentList.Add(stage);
                tar.ArgumentList.Add("kyber-weave");
                ProcessResult tarResult = ProcessRunner.Run(tar, string.Empty, TimeSpan.FromSeconds(30));
                Assert.True(tarResult.ExitCode == 0, tarResult.StandardError);

                string hash = Sha256(archive);
                File.WriteAllText(Path.Combine(tagDir, "SHA256SUMS.txt"), hash + "  " + assetName + "\n");
                if (corruptArchive)
                {
                    byte[] bytes = File.ReadAllBytes(archive);
                    bytes[0] ^= 0xFF;
                    File.WriteAllBytes(archive, bytes);
                }

                server = ListeningServer.Start(
                    Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "local-release-server.py"),
                    "--root",
                    sandbox.Root,
                    "--port",
                    "0");
                return new PublishedRelease(sandbox, server, assetName);
            }
            catch
            {
                server?.Dispose();
                sandbox.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _server.Dispose();
            _sandbox.Dispose();
        }
    }

    /// <summary>A Python HTTP server that prints its ephemeral port and then serves until disposed.</summary>
    private sealed class ListeningServer : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;

        private ListeningServer(Process process, Task<string> stderr, int port)
        {
            _process = process;
            _stderr = stderr;
            Port = port;
        }

        public int Port { get; }

        public static ListeningServer Start(string scriptPath, params string[] arguments)
        {
            ProcessStartInfo startInfo = new(ResolveExecutable("python3"))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(scriptPath);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process process = new() { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string?> readLine = process.StandardOutput.ReadLineAsync();
            if (!readLine.Wait(TimeSpan.FromSeconds(10)))
            {
                string err = KillAndRead(process, stderr);
                throw new InvalidOperationException("server did not print a port. " + err);
            }

            string? line = readLine.Result;
            if (line is null
                || !int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            {
                string err = KillAndRead(process, stderr);
                throw new InvalidOperationException("server port line was '" + line + "'. " + err);
            }

            return new ListeningServer(process, stderr, port);
        }

        public void Dispose()
        {
            KillAndRead(_process, _stderr);
        }

        private static string KillAndRead(Process process, Task<string> stderr)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited. The stderr read still has to finish so the pipe closes.
            }

            string error = stderr.Wait(TimeSpan.FromSeconds(5)) ? stderr.Result : string.Empty;
            process.Dispose();
            return error;
        }
    }
}
