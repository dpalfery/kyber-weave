using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the release-notes contract in two halves:
///
///   (a) <c>scripts/next-release-version.sh --notes-base --for &lt;tag&gt;</c> prints the previous
///       tag the release notes compare against (<c>PREVIOUS_TAG=</c>), relative to the tag
///       actually being released. A stable release compares with the highest lower stable tag,
///       spanning any release candidates in between. A release candidate compares with the
///       highest lower stable or candidate tag. Dev tags are ignored, and the major-version
///       filter is widened for this mode only, for candidates below the released tag, so
///       v1.0.0-rc.2 compares with v1.0.0-rc.1 rather than with the 0.x stable line. A tag
///       above the released tag, such as a mistyped major, is never a base. The default and
///       <c>--prod</c> outputs must not change.
///   (b) <c>.github/workflows/release.yml</c> resolves that tag in the version job, passes the
///       computed TAG to <c>--notes-base --for</c>, generates the notes through the
///       <c>generate-notes</c> API, places the changes section before the install heading,
///       publishes with <c>--notes-file</c>, verifies the published notes, and fails closed
///       when generation fails after exactly two attempts.
///
/// The tag-set cases run the real script inside a throwaway git repository with no
/// <c>origin</c> remote, so the script reads its tags from <c>git tag -l</c>. Nothing is
/// written into the repository tree.
///
/// Tests in section (a) that exercise <c>--notes-base</c> are expected to fail until that mode
/// exists: the script rejects the flag as an unknown option. The default and <c>--prod</c>
/// pins are expected to pass today.
/// </summary>
public sealed class ReleaseNotesTests
{
    private const string NotesBaseFlag = "--notes-base";
    private const string ForFlag = "--for";
    private const string PreviousTagKey = "PREVIOUS_TAG";

    private static string NextReleaseVersionScript =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "next-release-version.sh");

    private static string ReleaseWorkflowPath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows", "release.yml");

    // ---------------------------------------------------------------------------------------
    // (a) previous-tag lookup
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The first release candidate after a stable release compares with that stable release.
    /// Without this the first RC of a new line would compare with an older line's candidates.
    /// </summary>
    [Fact]
    public void NotesBaseForFirstRcAfterStableComparesWithThatStable()
    {
        AssertNotesBase(
            tags: ["v0.1.6"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.7-rc.1"],
            expectedPreviousTag: "v0.1.6",
            because: "the next RC is 0.1.7-rc.1, and the highest lower tag is the stable 0.1.6");
    }

    /// <summary>
    /// A later release candidate compares with the immediately preceding candidate, not with
    /// the stable release that opened the line.
    /// </summary>
    [Fact]
    public void NotesBaseForLaterRcComparesWithPreviousRc()
    {
        AssertNotesBase(
            tags: ["v0.1.6", "v0.1.7-rc.1", "v0.1.7-rc.2"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.7-rc.3"],
            expectedPreviousTag: "v0.1.7-rc.2",
            because: "the next RC is 0.1.7-rc.3, and rc.2 is the highest lower RC");
    }

    /// <summary>
    /// The first stable release after several candidates compares with the previous stable,
    /// so the notes cover everything since that stable. A candidate of the same core version
    /// (0.1.7-rc.*) is never the base for the stable 0.1.7.
    /// </summary>
    [Fact]
    public void NotesBaseForFirstStableAfterRcsSpansThemToPreviousStable()
    {
        AssertNotesBase(
            tags: ["v0.1.6", "v0.1.7-rc.1", "v0.1.7-rc.2", "v0.1.7-rc.3"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.7"],
            expectedPreviousTag: "v0.1.6",
            because: "the next stable is 0.1.7, and only stable tags may be the base for a stable");
    }

    /// <summary>
    /// A first stable release with no earlier stable tag has no base. The mode reports an
    /// empty value and exits 0, so the workflow can compare with the start of history.
    /// </summary>
    [Fact]
    public void NotesBaseForStableWithNoPriorStableIsEmpty()
    {
        AssertNotesBase(
            tags: ["v0.1.0-rc.1"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.0"],
            expectedPreviousTag: string.Empty,
            because: "the only tag is an RC, and a stable release has no lower stable to compare with");
    }

    /// <summary>
    /// Dev tags are never release points. A v0.1.7-dev.3 tag must not be picked as the base
    /// for the first RC of 0.1.7, even though it sorts above v0.1.6.
    /// </summary>
    [Fact]
    public void NotesBaseIgnoresDevTags()
    {
        AssertNotesBase(
            tags: ["v0.1.6", "v0.1.7-dev.3"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.7-rc.1"],
            expectedPreviousTag: "v0.1.6",
            because: "dev prereleases are not candidates for the notes base");
    }

    /// <summary>
    /// After a major bump, the first candidate of the new major compares with the previous
    /// candidate of that major, not with the 0.x stable. The default filter would hide the
    /// v1.0.0 line here, so this mode widens it.
    /// </summary>
    [Fact]
    public void NotesBaseWidensMajorFilterSoNewMajorRcComparesWithItsOwnLine()
    {
        AssertNotesBase(
            tags: ["v0.9.5", "v1.0.0-rc.1"],
            arguments: [NotesBaseFlag, ForFlag, "v1.0.0-rc.2"],
            expectedPreviousTag: "v1.0.0-rc.1",
            because: "the next RC is 1.0.0-rc.2, which must compare with 1.0.0-rc.1 and not 0.9.5");
    }

    /// <summary>
    /// Regression for the review finding: with a mistyped v1.0.6-rc.6 tag present, the release
    /// TAG is v0.1.7-rc.10 (default mode ignores the mistyped major). The notes base for that
    /// TAG must be v0.1.7-rc.9. A base above the released tag would show the notes compared
    /// against a version that is not in the release line.
    /// </summary>
    [Fact]
    public void NotesBaseForReleasedTagIgnoresTagsAboveItWhenMajorIsMistyped()
    {
        AssertNotesBase(
            tags: ["v0.1.6", "v0.1.7-rc.9", "v1.0.6-rc.6"],
            arguments: [NotesBaseFlag, ForFlag, "v0.1.7-rc.10"],
            expectedPreviousTag: "v0.1.7-rc.9",
            because: "the released tag is 0.1.7-rc.10, so the mistyped 1.0.6-rc.6 must never be the base");
    }

    /// <summary>
    /// The base must agree with the TAG the default mode actually releases. Pins that the
    /// default output for this same tag set is v0.1.7-rc.10, so the two modes cannot drift.
    /// </summary>
    [Fact]
    public void DefaultTagForMistypedMajorSetIsTheReleasedTagTheBaseIsComputedFor()
    {
        using TempDirectory temp = new();
        CreateRepoWithTags(temp.Path, ["v0.1.6", "v0.1.7-rc.9", "v1.0.6-rc.6"]);

        ProcessResult result = RunNextReleaseVersion(temp.Path);

        Assert.True(result.ExitCode == 0, $"default mode exited {result.ExitCode}: {result.StandardError.Trim()}");
        Assert.Equal("v0.1.7-rc.10", ReadOutputValue(result.StandardOutput, "TAG"));
    }

    /// <summary>
    /// The default RC output keeps the major-version guard. A mistyped major above the stable
    /// line must not move the next RC number, so widening stays confined to <c>--notes-base</c>.
    /// </summary>
    [Fact]
    public void DefaultRcOutputKeepsMajorGuardForTagsAboveStableMajor()
    {
        using TempDirectory temp = new();
        CreateRepoWithTags(temp.Path, ["v0.9.5", "v1.0.0-rc.1"]);

        ProcessResult result = RunNextReleaseVersion(temp.Path);

        Assert.True(result.ExitCode == 0, $"default mode exited {result.ExitCode}: {result.StandardError.Trim()}");
        Assert.Equal("0.9.6-rc.1", ReadOutputValue(result.StandardOutput, "VERSION"));
        Assert.Equal("v0.9.6-rc.1", ReadOutputValue(result.StandardOutput, "TAG"));
    }

    /// <summary>
    /// Pins the default RC output so the notes-base change cannot alter the number a dispatch
    /// publishes. The 0.1.7-rc.10 answer is what the release workflow relies on today.
    /// </summary>
    [Fact]
    public void DefaultRcOutputIsUnchangedByNotesBaseMode()
    {
        using TempDirectory temp = new();
        CreateRepoWithTags(temp.Path, ["v0.1.6", "v0.1.7-rc.9"]);

        ProcessResult result = RunNextReleaseVersion(temp.Path);

        Assert.True(result.ExitCode == 0, $"default mode exited {result.ExitCode}: {result.StandardError.Trim()}");
        Assert.Equal("0.1.7-rc.10", ReadOutputValue(result.StandardOutput, "VERSION"));
        Assert.Equal("v0.1.7-rc.10", ReadOutputValue(result.StandardOutput, "TAG"));
    }

    /// <summary>
    /// Pins the <c>--prod</c> output so promoting a candidate to stable still yields 0.1.7.
    /// </summary>
    [Fact]
    public void ProdOutputIsUnchangedByNotesBaseMode()
    {
        using TempDirectory temp = new();
        CreateRepoWithTags(temp.Path, ["v0.1.6", "v0.1.7-rc.9"]);

        ProcessResult result = RunNextReleaseVersion(temp.Path, "--prod");

        Assert.True(result.ExitCode == 0, $"--prod mode exited {result.ExitCode}: {result.StandardError.Trim()}");
        Assert.Equal("0.1.7", ReadOutputValue(result.StandardOutput, "VERSION"));
        Assert.Equal("v0.1.7", ReadOutputValue(result.StandardOutput, "TAG"));
    }

    // ---------------------------------------------------------------------------------------
    // (b) release.yml layout
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The version job exports <c>notes_start_tag</c> so the release job can read the base
    /// tag without recomputing it.
    /// </summary>
    [Fact]
    public void VersionJobOutputsNotesStartTag()
    {
        string job = Job(ReadWorkflow(), "version");

        Assert.Contains(
            "notes_start_tag: ${{ steps.meta.outputs.notes_start_tag }}",
            job,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The version job computes the base with <c>--notes-base</c>, so the base follows the same
    /// tag rules as the version number it sits beside.
    /// </summary>
    [Fact]
    public void VersionJobComputesNotesStartTagWithNotesBaseMode()
    {
        string job = Job(ReadWorkflow(), "version");

        Assert.Contains("next-release-version.sh --notes-base", job, StringComparison.Ordinal);
        Assert.Contains("notes_start_tag=", job, StringComparison.Ordinal);
    }

    /// <summary>
    /// The version job passes the TAG it actually computed to <c>--notes-base --for</c>. Deriving
    /// the base from anything else (such as a second, unguarded computation of the version)
    /// lets the base and the released TAG disagree, which is the mistyped-major regression.
    /// </summary>
    [Fact]
    public void VersionJobComputesNotesBaseForTheTagBeingReleased()
    {
        string job = Job(ReadWorkflow(), "version");

        Assert.Contains("next-release-version.sh --notes-base --for \"${TAG}\"", job, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notes step calls the <c>generate-notes</c> API with <c>previous_tag_name</c> set to
    /// the base tag from the version job. Without the base the notes span the whole history.
    /// </summary>
    [Fact]
    public void ReleaseNotesStepCallsGenerateNotesWithPreviousTagName()
    {
        string step = Step(Job(ReadWorkflow(), "release"), "Generate release notes");

        Assert.Contains("releases/generate-notes", step, StringComparison.Ordinal);
        Assert.Contains("previous_tag_name", step, StringComparison.Ordinal);
        Assert.Contains("needs.version.outputs.notes_start_tag", step, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>--generate-notes</c> flag of <c>gh release create</c> is retired. It ignores the
    /// base tag and cannot be retried independently, so the notes come from the API step only.
    /// </summary>
    [Fact]
    public void ReleaseJobNoLongerUsesGenerateNotesFlag()
    {
        string job = Job(ReadWorkflow(), "release");

        Assert.DoesNotContain("--generate-notes", job, StringComparison.Ordinal);
    }

    /// <summary>
    /// The changes section is placed before the install heading. Readers see what changed
    /// before how to install it, and the install heading stays the last part of the notes.
    /// </summary>
    [Fact]
    public void ChangesSectionAppearsBeforeInstallHeadingInNotes()
    {
        string job = Job(ReadWorkflow(), "release");

        int notesStart = job.IndexOf("NOTES=\"$(cat <<EOF", StringComparison.Ordinal);
        Assert.True(notesStart >= 0, "the release job no longer assembles NOTES from a heredoc");

        string notes = job[notesStart..];
        int changes = notes.IndexOf("${CHANGES_SECTION}", StringComparison.Ordinal);
        int install = notes.IndexOf("## Kyber-Weave ${VERSION}", StringComparison.Ordinal);

        Assert.True(install >= 0, "the install heading '## Kyber-Weave ${VERSION}' is missing from NOTES");
        Assert.True(
            changes >= 0 && changes < install,
            "the changes section (${CHANGES_SECTION}) must be placed before the '## Kyber-Weave ${VERSION}' heading in NOTES");
    }

    /// <summary>
    /// Publish hands the notes to <c>gh release create</c> as a file. Passing them as an inline
    /// argument puts a multi-line body on the command line, where length and quoting can break it.
    /// </summary>
    [Fact]
    public void PublishUsesNotesFileInsteadOfInlineNotes()
    {
        string step = Step(Job(ReadWorkflow(), "release"), "Create tag and GitHub Release");

        Assert.Contains("--notes-file", step, StringComparison.Ordinal);
        Assert.DoesNotContain("--notes \"${NOTES}\"", step, StringComparison.Ordinal);
    }

    /// <summary>
    /// A verification step runs after publish and reads back the published notes, so a release
    /// that went out with an empty or wrong body is caught in the same run.
    /// </summary>
    [Fact]
    public void PublishedReleaseNotesAreVerifiedAfterPublish()
    {
        string job = Job(ReadWorkflow(), "release");
        string verify = Step(job, "Verify published release notes");

        Assert.Contains("gh release view", verify, StringComparison.Ordinal);
        Assert.True(
            job.IndexOf("name: Create tag and GitHub Release", StringComparison.Ordinal)
                < job.IndexOf("name: Verify published release notes", StringComparison.Ordinal),
            "the notes verification must run after the publish step");
    }

    /// <summary>
    /// Notes generation fails closed. The step runs before publish, retries once, and is never
    /// marked <c>continue-on-error</c>, so a failure stops the job before any release exists.
    /// </summary>
    [Fact]
    public void NotesGenerationFailsClosedBeforePublishAfterOneRetry()
    {
        string job = Job(ReadWorkflow(), "release");
        string generate = Step(job, "Generate release notes");
        string publish = Step(job, "Create tag and GitHub Release");

        Assert.True(
            job.IndexOf("name: Generate release notes", StringComparison.Ordinal)
                < job.IndexOf("name: Create tag and GitHub Release", StringComparison.Ordinal),
            "notes must be generated before the release is created");
        Assert.DoesNotContain("continue-on-error: true", generate, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error: true", publish, StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(generate, @"for\s+\w+\s+in\s+1\s+2\b|seq\s+1\s+2\b|\{1\.\.2\}"),
            "the notes step must make exactly two attempts (one retry) before it fails");
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    private static void AssertNotesBase(
        string[] tags,
        string[] arguments,
        string expectedPreviousTag,
        string because)
    {
        SkipOnWindows();

        using TempDirectory temp = new();
        CreateRepoWithTags(temp.Path, tags);

        ProcessResult result = RunNextReleaseVersion(temp.Path, arguments);

        Assert.True(
            result.ExitCode == 0,
            $"{because}: the script exited {result.ExitCode}: {result.StandardError.Trim()}");
        Assert.Equal(expectedPreviousTag, ReadOutputValue(result.StandardOutput, PreviousTagKey));
    }

    private static void CreateRepoWithTags(string path, string[] tags)
    {
        RunGit(path, "init", "-q");
        RunGit(path, "commit", "--allow-empty", "-q", "-m", "fixture");
        foreach (string tag in tags)
        {
            RunGit(path, "tag", tag);
        }
    }

    private static void RunGit(string path, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Identity and signing are set per call so the fixture never reads the operator's git config.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.name=kyber-weave-test");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("user.email=kyber-weave-test@example.invalid");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("commit.gpgSign=false");
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));

        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.StandardError.Trim()}");
    }

    private static ProcessResult RunNextReleaseVersion(string repositoryPath, params string[] arguments)
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = new("/bin/bash")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(NextReleaseVersionScript);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Returns the value of a <c>KEY=value</c> line, or null when the key is not printed. An empty
    /// value is returned as an empty string, which is how an absent base is reported.
    /// </summary>
    private static string? ReadOutputValue(string output, string key)
    {
        string prefix = key + "=";
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed.Substring(prefix.Length);
            }
        }

        return null;
    }

    private static string ReadWorkflow()
    {
        return File.ReadAllText(ReleaseWorkflowPath);
    }

    /// <summary>
    /// Returns the text of one job, from its header to the next top-level job header.
    /// </summary>
    private static string Job(string workflow, string jobId)
    {
        Match header = Regex.Match(
            workflow,
            $@"^  {Regex.Escape(jobId)}:[ \t]*$",
            RegexOptions.Multiline);
        Assert.True(header.Success, $"release.yml has no '{jobId}' job");

        int bodyStart = header.Index + header.Length;
        Match next = Regex.Match(
            workflow[bodyStart..],
            @"^  [A-Za-z][\w-]*:[ \t]*$",
            RegexOptions.Multiline);
        int bodyEnd = next.Success ? bodyStart + next.Index : workflow.Length;

        return workflow[header.Index..bodyEnd];
    }

    /// <summary>
    /// Returns the text of one named step inside a job, from its <c>- name:</c> line to the next step.
    /// </summary>
    private static string Step(string job, string stepName)
    {
        Match header = Regex.Match(
            job,
            $@"^      - name: {Regex.Escape(stepName)}[ \t]*$",
            RegexOptions.Multiline);
        Assert.True(header.Success, $"the release job has no step named '{stepName}'");

        int bodyStart = header.Index + header.Length;
        Match next = Regex.Match(job[bodyStart..], @"^      - ", RegexOptions.Multiline);
        int bodyEnd = next.Success ? bodyStart + next.Index : job.Length;

        return job[header.Index..bodyEnd];
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell and git fixture tests are not run on Windows.");
        }
    }
}
