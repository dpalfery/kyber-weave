using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the release chain receipt: one version-bound receipt lets a re-run of
/// the same release skip the build chain as a unit, while the publish jobs
/// verify the published state instead of re-publishing it.
///
/// The contract under test:
///
///   (a) the workflow declares the four-hour freshness ceiling
///       (<c>RECEIPT_MAX_AGE_SECONDS: "14400"</c>, the same value as
///       <c>ci.yml</c>), and the verdict step compares the receipt's
///       <c>saved_at_epoch</c> against it before the chain may skip,
///   (b) every build, SBOM and publish job restores the same version-bound
///       chain key, so the chain skips or runs as a unit and no job can skip
///       while a sibling still builds,
///   (c) every other step in the build chain is gated on the verdict's
///       <c>skip</c> output,
///   (d) the receipt is saved only in the <c>release</c> job, only after the
///       published release was verified, and only on a miss, with
///       <c>continue-on-error: true</c> so a cache-reserve race never fails a
///       green publish,
///   (e) the publish jobs never gate their verification, and their hit paths
///       name concrete fail-closed commands, and
///   (f) <c>attest-sbom</c> mints SLSA build provenance for every archive
///       beside the SBOM attestations, under the existing permission block,
///   (g) the verdict rejects a future-dated <c>saved_at_epoch</c> (a negative
///       age) as a miss before comparing against the ceiling, matching the
///       guard <c>ci.yml</c>'s verdicts carry,
///   (h) the attestation verify retry loops start their flag unverified, so
///       a persistent miss reaches the exit-1 check instead of passing
///       vacuously, and
///   (i) a chain hit names the proving run even when verification fails: the
///       verdict's fresh echo carries <c>run_url</c>, and the report step runs
///       under <c>if: always()</c>, and
///   (j) the report step claims only what happened on every path: a miss
///       whose publish step completed but whose verification failed reports
///       a live, unverified release, and only a miss whose publishing never
///       completed says it published nothing.
///
/// The fail-closed exit behaviour of those shell commands is not statically
/// observable; it is review scope, not test scope. The structures above —
/// the guard's position, the flag's initialization, the step's condition —
/// are statically observable and pinned here.
/// </summary>
public sealed class ReleaseReceiptSkipTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ReleaseWorkflowPath = ".github/workflows/release.yml";

    private const string FreshnessCeiling = "RECEIPT_MAX_AGE_SECONDS: \"14400\"";

    private const string ChainKey = "key: release-chain-${{ needs.version.outputs.version }}";

    private const string MissOnlyGate = "steps.verdict.outputs.skip != 'true'";

    [Fact]
    public void TheWorkflowEnvDeclaresTheFourHourReceiptFreshnessCeiling()
    {
        string workflow = ReadWorkflow();

        Assert.Equal(1, Regex.Count(workflow, Regex.Escape(FreshnessCeiling)));

        int ceiling = workflow.IndexOf(FreshnessCeiling, StringComparison.Ordinal);
        int jobs = workflow.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobs > 0, "release.yml has no jobs: section.");
        Assert.True(
            ceiling >= 0 && ceiling < jobs,
            "RECEIPT_MAX_AGE_SECONDS must be a workflow-level env entry, declared before jobs:.");
    }

    [Fact]
    public void EveryChainAndPublishJobRestoresTheSameVersionBoundKeyAndRunsTheVerdict()
    {
        string workflow = ReadWorkflow();
        string[] chainAndPublishJobs =
        {
            "build", "build-kyberdash", "pack-squad", "build-tray", "sbom",
            "artifact-sboms", "attest-sbom", "release", "gh-packages",
        };

        foreach (string job in chainAndPublishJobs)
        {
            string jobText = ReadJob(job);
            Assert.Contains("actions/cache/restore@", jobText, StringComparison.Ordinal);

            string verdict = RequiredStep(StepBlocks(jobText), "Decide whether the release chain can skip");
            Assert.Contains("id: verdict", verdict, StringComparison.Ordinal);
            Assert.Contains(".saved_at_epoch", verdict, StringComparison.Ordinal);
            Assert.Contains("${RECEIPT_MAX_AGE_SECONDS}", verdict, StringComparison.Ordinal);
        }

        // One shared key for the whole chain: every key line in the file, the
        // restores and the save alike, is the version-bound chain key.
        foreach (Match key in Regex.Matches(workflow, @"key: release-chain-[^\n]+"))
        {
            Assert.Equal(ChainKey, key.Value);
        }

        int keyLines = Regex.Count(workflow, @"key: release-chain-");
        Assert.True(
            keyLines >= 9,
            $"expected at least nine chain-key lines (one restore per job plus the save), found {keyLines}.");
    }

    [Fact]
    public void TheChainVerdictRejectsAFutureDatedReceiptAsAMiss()
    {
        // A receipt whose saved_at_epoch lies in the future computes a
        // negative age, which satisfies `age <= RECEIPT_MAX_AGE_SECONDS`; the
        // verdict must reject it as a miss before the ceiling compare, the
        // same guard every ci.yml verdict carries.
        string[] chainAndPublishJobs =
        {
            "build", "build-kyberdash", "pack-squad", "build-tray", "sbom",
            "artifact-sboms", "attest-sbom", "release", "gh-packages",
        };

        foreach (string job in chainAndPublishJobs)
        {
            string verdict = RequiredStep(StepBlocks(ReadJob(job)), "Decide whether the release chain can skip");

            int futureGuard = verdict.IndexOf("(( age < 0 ))", StringComparison.Ordinal);
            Assert.True(
                futureGuard >= 0,
                $"the '{job}' verdict has no negative-age guard; a future-dated receipt would skip the chain.");

            int ceilingCompare = verdict.IndexOf("age <= RECEIPT_MAX_AGE_SECONDS", StringComparison.Ordinal);
            Assert.True(
                ceilingCompare > futureGuard,
                $"the '{job}' verdict must reject a negative age before comparing against the ceiling.");

            // The guard names why it rebuilt, so the log carries the same
            // evidence the other miss paths do.
            Assert.Contains(
                "chain receipt is dated in the future",
                verdict,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChainJobsGateEveryStepExceptRestoreVerdictCheckoutAndAlwaysCleanup()
    {
        string[] chainJobs =
        {
            "build", "build-kyberdash", "pack-squad", "build-tray", "sbom",
            "artifact-sboms", "attest-sbom",
        };

        foreach (string job in chainJobs)
        {
            foreach (string block in StepBlocks(ReadJob(job)))
            {
                if (IsExemptFromGating(block))
                {
                    continue;
                }

                Assert.True(
                    block.Contains(MissOnlyGate, StringComparison.Ordinal),
                    $"step '{StepName(block)}' in job '{job}' must be gated on {MissOnlyGate}.");
            }
        }
    }

    [Fact]
    public void TheChainReceiptSaveIsMissOnlyAndOnlyInReleaseAfterVerification()
    {
        string workflow = ReadWorkflow();
        Assert.Equal(1, Regex.Count(workflow, "actions/cache/save@"));

        string release = ReadJob("release");
        List<string> blocks = StepBlocks(release);

        string save = blocks.Single(b => b.Contains("actions/cache/save@", StringComparison.Ordinal));
        Assert.Contains(MissOnlyGate, save, StringComparison.Ordinal);
        Assert.Contains("continue-on-error: true", save, StringComparison.Ordinal);
        Assert.Contains(ChainKey, save, StringComparison.Ordinal);

        string record = RequiredStep(blocks, "Record the release chain receipt");
        Assert.Contains(MissOnlyGate, record, StringComparison.Ordinal);
        Assert.Contains("saved_at_epoch", record, StringComparison.Ordinal);
        Assert.Contains("run_url", record, StringComparison.Ordinal);
        Assert.Contains("package_versions", record, StringComparison.Ordinal);

        // The receipt is written only after the release job verified the
        // published release, so a half-published release leaves no receipt.
        int saveIndex = release.IndexOf("actions/cache/save@", StringComparison.Ordinal);
        foreach (string stepName in new[]
        {
            "Create tag and GitHub Release",
            "Verify published release assets",
            "Verify SBOM attestations",
            "Verify provenance attestations",
        })
        {
            int stepIndex = release.IndexOf("name: " + stepName, StringComparison.Ordinal);
            Assert.True(
                stepIndex >= 0 && stepIndex < saveIndex,
                $"the chain receipt save must come after '{stepName}'.");
        }
    }

    [Fact]
    public void ThePublishJobsVerifyThePublishedStateOnAHitWithFailClosedCommands()
    {
        string release = ReadJob("release");
        List<string> releaseBlocks = StepBlocks(release);

        // The publish steps run only on a miss; a hit never re-publishes.
        foreach (string stepName in new[]
        {
            "Download all release assets",
            "Verify and compute checksums",
            "Generate release notes",
            "Create tag and GitHub Release",
        })
        {
            Assert.Contains(MissOnlyGate, RequiredStep(releaseBlocks, stepName), StringComparison.Ordinal);
        }

        // The verification steps are never gated on the verdict: they must run
        // on a hit, where they are the only proof the release is really there.
        foreach (string stepName in new[]
        {
            "Verify published release notes",
            "Verify published release assets",
            "Verify SBOM attestations",
            "Verify provenance attestations",
            "Report the run that published this release",
        })
        {
            Assert.DoesNotContain("if: steps.verdict", RequiredStep(releaseBlocks, stepName), StringComparison.Ordinal);
        }

        string assets = RequiredStep(releaseBlocks, "Verify published release assets");
        Assert.Contains("CHAIN_HIT: ${{ steps.verdict.outputs.skip }}", assets, StringComparison.Ordinal);
        Assert.Contains(".assets[]", assets, StringComparison.Ordinal);
        Assert.Contains(".sha256sums", assets, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_SORTED", assets, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_SORTED", assets, StringComparison.Ordinal);
        Assert.Contains("exit 1", assets, StringComparison.Ordinal);
        Assert.Contains("diff", assets, StringComparison.Ordinal);
        Assert.Contains("gh release download", assets, StringComparison.Ordinal);

        string sbom = RequiredStep(releaseBlocks, "Verify SBOM attestations");
        Assert.Contains("https://cyclonedx.org/bom", sbom, StringComparison.Ordinal);

        string provenance = RequiredStep(releaseBlocks, "Verify provenance attestations");
        Assert.Contains("https://slsa.dev/provenance/v1", provenance, StringComparison.Ordinal);
        Assert.Contains("gh attestation verify", provenance, StringComparison.Ordinal);
        Assert.Contains("exit 1", provenance, StringComparison.Ordinal);

        string hitDownload = RequiredStep(releaseBlocks, "Download the published archives (chain hit)");
        Assert.Contains("if: steps.verdict.outputs.skip == 'true'", hitDownload, StringComparison.Ordinal);
        Assert.Contains("gh release download", hitDownload, StringComparison.Ordinal);

        Assert.Contains(
            "steps.verdict.outputs.run_url",
            RequiredStep(releaseBlocks, "Report the run that published this release"),
            StringComparison.Ordinal);

        // gh-packages: the publish steps are miss-only, the package-version
        // lookup is ungated and reads the receipt's recorded versions on a hit.
        List<string> packageBlocks = StepBlocks(ReadJob("gh-packages"));
        foreach (string stepName in new[] { "Setup .NET", "Pack tools", "Push to GitHub Packages (secondary channel)" })
        {
            Assert.Contains(MissOnlyGate, RequiredStep(packageBlocks, stepName), StringComparison.Ordinal);
        }

        string verify = RequiredStep(packageBlocks, "Verify the published package versions");
        Assert.DoesNotContain("if: steps.verdict", verify, StringComparison.Ordinal);
        Assert.Contains("CHAIN_HIT", verify, StringComparison.Ordinal);
        Assert.Contains("gh api", verify, StringComparison.Ordinal);
        Assert.Contains("packages/nuget/", verify, StringComparison.Ordinal);
        Assert.Contains(".package_versions", verify, StringComparison.Ordinal);
        Assert.Contains("exit 1", verify, StringComparison.Ordinal);
    }

    [Fact]
    public void AttestationVerifyRetryLoopsStartUnverifiedSoAPersistentMissFails()
    {
        // The retry loops close with `[[ "${verified}" != "true" ]] && exit 1`;
        // a flag initialized to true before the loop makes that check
        // unreachable, so three failed attempts still exit 0. The flag must be
        // false before the first attempt, and only the successful verify
        // inside the loop raises it.
        string release = ReadJob("release");
        List<string> blocks = StepBlocks(release);

        foreach (string stepName in new[] { "Verify SBOM attestations", "Verify provenance attestations" })
        {
            string step = RequiredStep(blocks, stepName);

            int startsFalse = step.IndexOf("verified=false", StringComparison.Ordinal);
            Assert.True(
                startsFalse >= 0,
                $"'{stepName}' must initialize its verified flag to false before the retry loop.");

            int retryLoop = step.IndexOf("for attempt in 1 2 3", StringComparison.Ordinal);
            Assert.True(
                retryLoop > startsFalse,
                $"'{stepName}' must initialize verified=false before the first retry attempt.");

            int exitCheck = step.IndexOf("\"${verified}\" != \"true\"", StringComparison.Ordinal);
            Assert.True(
                exitCheck > retryLoop,
                $"'{stepName}' must check the flag after the retry loop so a persistent miss exits 1.");
        }
    }

    [Fact]
    public void AChainHitNamesTheProvingRunEvenWhenVerificationFails()
    {
        // On a hit the proving run's URL is the only lead an operator has when
        // verification fails: the report step must still run after a failed
        // verify (`if: always()`), and the verdict's fresh echo must carry the
        // run_url so the log names it even then.
        string[] chainAndPublishJobs =
        {
            "build", "build-kyberdash", "pack-squad", "build-tray", "sbom",
            "artifact-sboms", "attest-sbom", "release", "gh-packages",
        };

        foreach (string job in chainAndPublishJobs)
        {
            string verdict = RequiredStep(StepBlocks(ReadJob(job)), "Decide whether the release chain can skip");
            Match freshEcho = Regex.Match(verdict, @"echo ""chain receipt is fresh[^\n]*""");
            Assert.True(
                freshEcho.Success,
                $"the '{job}' verdict has no fresh-receipt echo line.");
            Assert.Contains(
                "${run_url",
                freshEcho.Value,
                StringComparison.Ordinal);
        }

        string release = ReadJob("release");
        string report = RequiredStep(StepBlocks(release), "Report the run that published this release");
        Assert.Contains("if: always()", report, StringComparison.Ordinal);
        Assert.Contains("steps.verdict.outputs.run_url", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportStepClaimsOnlyWhatHappenedOnEveryBranch()
    {
        // A failure after the publish steps leaves a live, unverified release;
        // a blanket "published nothing" line denies it exists. The step must
        // split its failure branch on what the publish step actually did, and
        // the branch order is pinned so one claim cannot shadow another.
        string release = ReadJob("release");
        List<string> blocks = StepBlocks(release);

        // The publish step records the outcome the report step reads.
        Assert.Contains(
            "id: publish",
            RequiredStep(blocks, "Create tag and GitHub Release"),
            StringComparison.Ordinal);

        string report = RequiredStep(blocks, "Report the run that published this release");

        // Every branch input arrives through env, never interpolated into
        // the script.
        Assert.Contains("PRIOR_RUN_URL: ${{ steps.verdict.outputs.run_url }}", report, StringComparison.Ordinal);
        Assert.Contains("CHAIN_HIT: ${{ steps.verdict.outputs.skip }}", report, StringComparison.Ordinal);
        Assert.Contains("JOB_STATUS: ${{ job.status }}", report, StringComparison.Ordinal);
        Assert.Contains("PUBLISH_OUTCOME: ${{ steps.publish.outcome }}", report, StringComparison.Ordinal);

        // Order: the recorded proving run leads (the strongest lead), then
        // the hit without one, then the green miss, then the failure branch
        // split by what the publish step did.
        int priorRunUrl = report.IndexOf("-n \"${PRIOR_RUN_URL}\"", StringComparison.Ordinal);
        int chainHit = report.IndexOf("\"${CHAIN_HIT}\" == \"true\"", StringComparison.Ordinal);
        int jobStatus = report.IndexOf("\"${JOB_STATUS}\" == \"success\"", StringComparison.Ordinal);
        int publishOutcome = report.IndexOf("\"${PUBLISH_OUTCOME}\" == \"success\"", StringComparison.Ordinal);
        Assert.True(
            priorRunUrl >= 0,
            "the report step must test PRIOR_RUN_URL first: the recorded proving run is the strongest lead.");
        Assert.True(
            chainHit > priorRunUrl,
            "the report step must test CHAIN_HIT only after PRIOR_RUN_URL.");
        Assert.True(
            jobStatus > chainHit,
            "the report step must test JOB_STATUS only after CHAIN_HIT.");
        Assert.True(
            publishOutcome > jobStatus,
            "the report step must split its failure branch on PUBLISH_OUTCOME after the JOB_STATUS check.");

        // A failed run whose publish completed reports the live, unverified
        // release and names this run, instead of claiming nothing shipped.
        Assert.Contains("verification failed", report, StringComparison.Ordinal);
        Assert.Contains("unverified", report, StringComparison.Ordinal);

        // Only a run whose publishing never completed claims it published
        // nothing, and that claim is the final fail-safe branch.
        int publishedNothing = report.IndexOf("published nothing", StringComparison.Ordinal);
        Assert.True(
            publishedNothing > publishOutcome,
            "'published nothing' must be the final, fail-safe branch after the PUBLISH_OUTCOME check.");
    }

    [Fact]
    public void AttestSbomMintsProvenanceForEveryArchiveBesideTheSbomAttestations()
    {
        string attest = ReadJob("attest-sbom");

        int sbomAttestations = Regex.Count(attest, "sbom-path:");
        Assert.True(
            sbomAttestations >= 26,
            $"the per-archive SBOM attestations must stay beside the provenance attestation (found {sbomAttestations}).");

        string provenance = RequiredStep(StepBlocks(attest), "Attest build provenance for all archives");
        Assert.Contains("uses: actions/attest@", provenance, StringComparison.Ordinal);
        Assert.Contains("subject-path: attest-assets/*.tar.gz, attest-assets/*.zip, attest-assets/*.exe", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("sbom-path", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("predicate-type:", provenance, StringComparison.Ordinal);
        Assert.DoesNotContain("predicate-path:", provenance, StringComparison.Ordinal);
        Assert.Contains(MissOnlyGate, provenance, StringComparison.Ordinal);

        // The provenance attestation reuses the id-token the SBOM attestations
        // already mint; the permission block itself stays untouched.
        Assert.Contains(
            "    permissions:\n      id-token: write\n      attestations: write\n      artifact-metadata: write\n      contents: read",
            attest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsEdgesAndDisplayNamesKeepTheirContract()
    {
        // job key | sorted needs | display name — pinned so the receipt wiring
        // cannot silently change what runs after what. authorize needs nothing.
        string[] expected =
        {
            "authorize||Authorize",
            "version|authorize|Resolve version",
            "approve|version|Approve release (David Palfery must approve)",
            "build|approve,version|Publish ${{ matrix.rid }}",
            "build-kyberdash|approve,version|Build KyberDash ${{ matrix.rid }}",
            "pack-squad|approve,version|Package Kyber-Squad",
            "build-tray|approve,version|Build tray ${{ matrix.rid }}",
            "sbom|approve,version|Generate SBOM",
            "artifact-sboms|build,build-kyberdash,build-tray,pack-squad,version|Generate per-artifact SBOMs",
            "attest-sbom|artifact-sboms,build,build-kyberdash,build-tray,pack-squad,sbom,version|Attest SBOMs",
            "release|artifact-sboms,attest-sbom,build,build-kyberdash,build-tray,pack-squad,sbom,version|Publish GitHub Release",
            "gh-packages|build,version|Publish GitHub Packages",
        };

        string workflow = ReadWorkflow();
        // Only the section after jobs: carries two-space keys; the trigger
        // block's `push:` must not be read as a job header.
        int jobsStart = workflow.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobsStart > 0, "release.yml has no jobs: section.");
        List<string> actual = new();
        foreach (Match header in Regex.Matches(workflow[(jobsStart + 1)..], @"\n  ([A-Za-z0-9_-]+):\n"))
        {
            string jobKey = header.Groups[1].Value;
            string jobText = ReadJob(jobKey);
            // needs comes either as a bracket list or as a single bare name.
            Match needs = Regex.Match(jobText, @"needs: (?:\[([^\]]*)\]|(\S+))");
            string needsRaw = needs.Success
                ? (needs.Groups[1].Success ? needs.Groups[1].Value : needs.Groups[2].Value)
                : "";
            string needsJoined = needs.Success
                ? string.Join(
                    ",",
                    needsRaw.Split(',').Select(s => s.Trim()).OrderBy(s => s, StringComparer.Ordinal))
                : "";
            Match name = Regex.Match(jobText, @"^    name: (.*)$", RegexOptions.Multiline);
            Assert.True(name.Success, $"the '{jobKey}' job has no display name.");
            actual.Add($"{jobKey}|{needsJoined}|{name.Groups[1].Value.TrimEnd()}");
        }

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    private static string ReadWorkflow()
    {
        return Corpus.ReadText(ReleaseWorkflowPath).ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Reads one job section from release.yml: from <c>\n  {jobName}:\n</c> to the
    /// next two-space-indented key, so a step assertion cannot leak into a later
    /// job's text.
    /// </summary>
    private static string ReadJob(string jobName)
    {
        string workflow = ReadWorkflow();
        int start = workflow.IndexOf("\n  " + jobName + ":\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "release.yml has no " + jobName + " job.");
        Match next = Regex.Match(workflow[(start + 1)..], @"\n  [A-Za-z0-9_-]+:\n");
        return next.Success ? workflow.Substring(start, next.Index + 1) : workflow[start..];
    }

    /// <summary>
    /// Splits a job's <c>steps:</c> list into blocks: from each
    /// six-space <c>- </c> line to the next one (or to the job's end), with the
    /// comment lines above a step attached to the step that follows them only
    /// when they sit inside the list.
    /// </summary>
    private static List<string> StepBlocks(string jobText)
    {
        string[] lines = jobText.Split('\n');
        List<string> blocks = new();
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            bool isStepStart = lines[i].StartsWith("      - ", StringComparison.Ordinal);
            bool isJobKey = Regex.IsMatch(lines[i], @"^    [A-Za-z0-9_-]+:");
            if (isStepStart)
            {
                if (start >= 0)
                {
                    blocks.Add(string.Join('\n', lines[start..i]));
                }

                start = i;
            }
            else if (isJobKey && start >= 0)
            {
                blocks.Add(string.Join('\n', lines[start..i]));
                start = -1;
            }
        }

        if (start >= 0)
        {
            blocks.Add(string.Join('\n', lines[start..]));
        }

        return blocks;
    }

    private static string StepName(string block)
    {
        Match match = Regex.Match(block, @"- name: (.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.TrimEnd() : "(unnamed)";
    }

    private static string RequiredStep(List<string> blocks, string stepName)
    {
        string? block = blocks.Find(b => string.Equals(StepName(b), stepName, StringComparison.Ordinal));
        Assert.True(block is not null, $"release.yml has no step named '{stepName}'.");
        return block!;
    }

    private static bool IsExemptFromGating(string block)
    {
        return block.Contains("actions/checkout@", StringComparison.Ordinal)
            || block.Contains("actions/cache/restore@", StringComparison.Ordinal)
            || block.Contains("id: verdict", StringComparison.Ordinal)
            || block.Contains("if: always()", StringComparison.Ordinal);
    }
}
