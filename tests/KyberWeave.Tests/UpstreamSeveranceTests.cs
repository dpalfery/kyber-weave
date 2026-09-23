using System.Diagnostics;
using System.Text;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Guards the one-time fork of the upstream CodeBurn repository (ADR 0020) at
/// both of its ends:
///
///   (a) the severance — no tracked file outside <c>docs/</c> names the
///       upstream in the forms that tie this repository to it
///       (<c>getagentseal</c>, <c>agentseal.org</c>, <c>codeburn.app</c>),
///       except the two attribution files Requirement 1.5 keeps, and
///   (b) the attribution — every <c>kyberdash</c> archive the
///       <c>build-kyberdash</c> release job produces ships
///       <c>dash/THIRD_PARTY_NOTICES.md</c>, and that file retains the
///       upstream MIT copyright line.
///
/// <c>docs/</c> is exempt because the decision records that document the fork
/// have to name its origin (Requirement 1.6); the exemption stops there, so a
/// new first-party file drifts this test rather than a reviewer. The scan
/// walks <c>git ls-files</c>, which is the requirement's own scope — tracked
/// files — so untracked build output and local tool state are out of bounds
/// by design.
///
/// Task 4.2 ("Guard the severance and keep the attribution").
/// </summary>
public sealed class UpstreamSeveranceTests
{
    private static string RepoRoot => KyberWeaveTestPaths.ToolRoot;

    private static string ReleaseWorkflowPath =>
        Path.Combine(RepoRoot, ".github", "workflows", "release.yml");

    private static string ThirdPartyNoticesPath =>
        Path.Combine(RepoRoot, "dash", "THIRD_PARTY_NOTICES.md");

    // The forms that tie the repository to its upstream. Matched without case:
    // a domain or org name in prose or a URL is a tie to upstream whatever its
    // casing.
    private static readonly string[] BannedMarkers =
    {
        "getagentseal",
        "agentseal.org",
        "codeburn.app",
    };

    // Requirement 1.5: the attribution files are where the upstream notice is
    // allowed — and required — to live.
    //
    // This file is exempt for a different reason: a guard has to name what it
    // forbids. Without the exemption the scan matches its own BannedMarkers
    // array and fails on itself, which is what it did from the moment it was
    // written — the test has never passed.
    private static readonly string[] AllowedPaths =
    {
        "dash/LICENSE",
        "dash/THIRD_PARTY_NOTICES.md",
        "tests/KyberWeave.Tests/UpstreamSeveranceTests.cs",
    };

    [Fact]
    public void NoTrackedFileOutsideDocsNamesTheUpstream()
    {
        List<string> offenders = new();
        foreach (string relativePath in ListTrackedFiles())
        {
            if (IsExempt(relativePath)) continue;

            string absolutePath = Path.Combine(RepoRoot, relativePath);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(absolutePath);
            }
            catch (IOException)
            {
                continue; // deleted between listing and reading; not tracked content
            }

            // Latin-1 maps every byte to a char, so the ASCII markers are
            // found in text and binary files alike without throwing on
            // invalid UTF-8 (logos, archives, zstd fixtures).
            string content = Encoding.Latin1.GetString(bytes);
            if (BannedMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Requirement 1.6: tracked files outside docs/ must not name the upstream. " +
            "Remove the reference, or move the record under docs/ where the fork's " +
            $"history is allowed to name it:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// The exemption above must not hollow the scan out: everything it exempts
    /// is exempt for a stated reason, and the markers themselves still match.
    /// A scan that matched nothing would pass just as quietly as a clean tree.
    /// </summary>
    [Fact]
    public void TheScanStillMatchesTheMarkersItExempts()
    {
        Assert.NotEmpty(BannedMarkers);

        // The guard's own file is exempt, and it is exempt because it contains
        // them — if it stopped containing them, the exemption is dead weight.
        string self = File.ReadAllText(
            Path.Combine(RepoRoot, "tests", "KyberWeave.Tests", "UpstreamSeveranceTests.cs"));
        Assert.True(
            BannedMarkers.Any(marker => self.Contains(marker, StringComparison.OrdinalIgnoreCase)),
            "UpstreamSeveranceTests.cs no longer names the markers it bans, so its " +
            "entry in AllowedPaths can go.");

        foreach (string allowed in AllowedPaths)
        {
            Assert.True(
                IsExempt(allowed),
                $"{allowed} is listed as allowed but IsExempt does not exempt it.");
        }

        // A path that is neither an attribution file nor under docs/ is not exempt.
        Assert.False(IsExempt("dash/src/cli/report.ts"));
        Assert.True(IsExempt("docs/adr/0020-one-time-fork.md"));
    }

    [Fact]
    public void KyberdashArchivesShipThirdPartyNotices()
    {
        string notices = File.ReadAllText(ThirdPartyNoticesPath);
        Assert.Contains("AgentSeal", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIT", notices, StringComparison.OrdinalIgnoreCase);

        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        // The build-kyberdash job stages the notices next to the binary and
        // packs that staged copy into both archive forms the RID matrix
        // produces (tar.gz everywhere, zip for win-x64).
        string[] requiredFragments =
        {
            "cp THIRD_PARTY_NOTICES.md \"${BIN_DIR}/THIRD_PARTY_NOTICES.md\"",
            "tar -C \"${BIN_DIR}\" -czf \"${BIN_DIR}/kyberdash-${RID}.tar.gz\" \"kyberdash${EXE}\" THIRD_PARTY_NOTICES.md",
            "zip -9 -j \"${BIN_DIR}/kyberdash-${RID}.zip\" \"${FINAL_BIN}\" \"${BIN_DIR}/THIRD_PARTY_NOTICES.md\"",
        };
        foreach (string fragment in requiredFragments)
        {
            Assert.True(
                workflow.Contains(fragment, StringComparison.Ordinal),
                $"Requirement 1.5: release.yml must ship THIRD_PARTY_NOTICES.md in every " +
                $"kyberdash archive. Missing fragment:\n  {fragment}");
        }
    }

    private static bool IsExempt(string repoRelativePath)
    {
        string normalized = repoRelativePath.Replace('\\', '/');
        if (AllowedPaths.Contains(normalized, StringComparer.Ordinal)) return true;
        return normalized == "docs" || normalized.StartsWith("docs/", StringComparison.Ordinal);
    }

    private static List<string> ListTrackedFiles()
    {
        ProcessStartInfo start = new("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started.");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"git ls-files failed: {process.StandardError.ReadToEnd()}");

        // -z separates entries with NUL, so paths with spaces or quotes stay
        // one entry each.
        return stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
