using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the content-hash receipt that lets an unchanged <c>ci.yml</c> job skip its work.
/// </summary>
/// <remarks>
/// <para>
/// Each eligible job hashes the inputs it reads, looks for a receipt under that fingerprint, and
/// decides a verdict from the receipt's age. A receipt is data, not proof, so the verdict validates
/// it before trusting it, and every way of failing to read one — no cache entry, a fork pull
/// request's isolated cache, an eviction, an unparsable or stale receipt — runs the job in full.
/// </para>
/// <para>
/// The failure this suite exists for is the opposite one: a fingerprint that misses an input lets a
/// job skip over a commit it never tested. That is why the whole-tree set is checked against the
/// tracked tree rather than against a list inside the workflow, and why every step after the verdict
/// must gate on it — a receipt can only skip work its job declared skippable.
/// </para>
/// <para>
/// It reads files, and lists the tracked tree through <c>git ls-tree</c> so build output present in a
/// working tree cannot change the answer.
/// </para>
/// </remarks>
public sealed class CiContentHashSkipTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string CiWorkflowPath = ".github/workflows/ci.yml";

    /// <summary>The ceiling a receipt may reach, in seconds: four hours.</summary>
    private const string ReceiptMaxAgeSeconds = "14400";

    /// <summary>The condition every step after the verdict composes onto its own.</summary>
    private const string SkipGuard = "steps.verdict.outputs.skip != 'true'";

    /// <summary>
    ///     The guard that keeps a receipt, and an attestation, out of a fork's pull request: its run
    ///     cannot write to the base repository's cache or attestations.
    /// </summary>
    private const string SameRepositoryGuard =
        "github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository";

    private const string FingerprintOutput = "${{ steps.fp.outputs.hash }}";

    private const string CacheRestorePin =
        "actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0";

    private const string CacheSavePin = "actions/cache/save@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0";

    private const string AttestPin = "actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2";

    /// <summary>Every job that carries a receipt, the fingerprint set it hashes, and its key's leg.</summary>
    private static readonly EligibleJob[] EligibleJobs =
    [
        new("build-test", "W", "${{ runner.os }}"),
        new("integration-test", "W", "${{ runner.os }}"),
        new("squad-filesystem-contract", "W", "${{ matrix.os }}"),
        new("sbom", "S", "${{ runner.os }}"),
        new("publish-smoke", "W", "${{ runner.os }}"),
        new("update-loop", "W", "${{ matrix.os }}"),
        new("skill-docs-gate", "W", "${{ runner.os }}"),
        new("ts-gates", "D", "${{ runner.os }}"),
        new("dash-e2e", "D", "${{ runner.os }}"),
        new("tray-rust", "T", "${{ matrix.os }}"),
        new("codeql", "W", "${{ matrix.language }}"),
        new("trivy", "W", "${{ runner.os }}"),
        new("semgrep", "W", "${{ runner.os }}"),
        new("gitleaks", "W", "${{ runner.os }}"),
    ];

    /// <summary>Jobs that decide whether to run from the path filter, never from a receipt.</summary>
    private static readonly string[] JobsWithoutReceipts = ["changes", "pr-naming", "ci-summary"];

    /// <summary>Jobs that decide their own run from the path filter, and must have no job-level if.</summary>
    private static readonly string[] JobsWithNoJobLevelCondition =
        ["changes", "build-test", "integration-test", "skill-docs-gate", "gitleaks"];

    /// <summary>
    ///     The whole tracked tree: every input a job that reads anywhere can read. The completeness
    ///     test checks it against <c>git ls-tree</c>, so a new top-level entry cannot leave it stale.
    /// </summary>
    private static readonly string[] WholeTree =
    [
        ".agents/**", ".apm/**", ".claude/**", ".codex/**", ".commandcode/**", ".cursor/**",
        ".devin/**", ".factory/**", ".github/**", ".kilo/**", ".kyber-weave/**", ".opencode/**",
        ".pi/**", ".playwright-cli/**", ".vscode/**", "analyzers/**", "dash/**", "docs/**",
        "homebrew/**",
        "npm/**", "products/**", "samples/**", "scripts/**", "src/**", "templates/**", "tests/**",
        ".editorconfig", ".gitattributes", ".gitignore", ".gitleaks.toml", ".mcp.json",
        "AGENTS.md", "CLAUDE.md", "CODE_OF_CONDUCT.md", "CONTRIBUTING.md", "Directory.Build.props",
        "HANDOVER-WAVE4.md", "HANDOVER.md", "KyberWeave.Unit.slnf", "KyberWeave.sln",
        "KyberWeave.sln.DotSettings", "LICENSE", "NOTICE", "README.md", "REFACTOR-LAYERS.md",
        "REVIEW.md", "SECURITY.md", "apm.yml", "global.json", "kilo.json",
    ];

    /// <summary>The SBOM inputs: every dependency manifest the <c>changes</c> job's sbom filter names.</summary>
    private static readonly string[] SbomInputs =
    [
        "**/package-lock.json", "**/Cargo.lock", "**/packages.lock.json", "**/*.csproj",
        "**/Directory.*.props", "global.json", "scripts/generate-sbom.sh", "scripts/validate-sbom.py",
        "scripts/syft-bootstrap.sh", "dash/tray/vendor/**", ".github/workflows/release.yml",
        ".github/workflows/ci.yml",
    ];

    /// <summary>
    ///     The KyberDash inputs: the dash package, the SDK and props its server builds under, and
    ///     <c>analyzers/**</c>, which <c>Directory.Build.props</c> imports into that build.
    /// </summary>
    private static readonly string[] DashInputs =
        ["dash/**", "global.json", "Directory.Build.props", "analyzers/**", ".github/workflows/ci.yml"];

    /// <summary>The tray inputs: the crate, the Node pin outside <c>dash/tray/</c>, and this workflow.</summary>
    private static readonly string[] TrayInputs = ["dash/tray/**", "dash/.nvmrc", ".github/workflows/ci.yml"];

    /// <summary>
    ///     Each alternation of the <c>changes</c> job's sbom filter, and the fingerprint arguments that
    ///     cover it. A new alternation fails the coverage test until it is named here.
    /// </summary>
    private static readonly SbomAlternation[] SbomFilterCoverage =
    [
        new("package-lock\\.json", ["**/package-lock.json"]),
        new("Cargo\\.lock", ["**/Cargo.lock"]),
        new("packages\\.lock\\.json", ["**/packages.lock.json"]),
        new("\\.csproj", ["**/*.csproj"]),
        new("Directory\\..*\\.props", ["**/Directory.*.props"]),
        new("^global\\.json", ["global.json"]),
        new("^scripts/(generate|validate)-sbom", ["scripts/generate-sbom.sh", "scripts/validate-sbom.py"]),
        new("^scripts/syft-bootstrap\\.sh", ["scripts/syft-bootstrap.sh"]),
        new("^\\.github/workflows/release\\.yml", [".github/workflows/release.yml"]),
        new("^dash/tray/vendor/", ["dash/tray/vendor/**"]),
    ];

    /// <summary>
    ///     Each job that attests a build product, and the product's paths. Only a build product is
    ///     digested and attested; test reports and SARIF files are neither digested nor attested.
    /// </summary>
    private static readonly AttestedProduct[] AttestedProducts =
    [
        new("build-test", ["artifacts/nupkg/*.nupkg"]),
        new(
            "publish-smoke",
            [
                "artifacts/cli-linux-x64/kyber-weave",
                "artifacts/mcp-linux-x64/kyber-weave-mcp",
                "artifacts/arbiter-linux-x64/kyber-weave-arbiter",
            ]),
        new("sbom", ["sbom-out/kyber-weave-sbom-*.cdx.json"]),
    ];

    /// <summary>The ceiling the verdict reads is one workflow-level variable, and it is four hours.</summary>
    [Fact]
    public void TheReceiptCeilingIsOneWorkflowVariable()
    {
        YamlMappingNode workflow = LoadWorkflow();
        YamlMappingNode environment = Assert.IsType<YamlMappingNode>(Required(workflow, "env", "ci.yml"));

        Assert.Equal(
            ReceiptMaxAgeSeconds,
            ScalarValue(Required(environment, "RECEIPT_MAX_AGE_SECONDS", "ci.yml's env")));

        List<string> overrides = [];
        foreach (EligibleJob job in EligibleJobs)
        {
            if (JobsOf(workflow)[job.Job].Children.TryGetValue(new YamlScalarNode("env"), out YamlNode? node) &&
                ((YamlMappingNode)node).Children.ContainsKey(new YamlScalarNode("RECEIPT_MAX_AGE_SECONDS")))
            {
                overrides.Add(job.Job);
            }
        }

        Assert.True(
            overrides.Count == 0,
            $"These jobs override RECEIPT_MAX_AGE_SECONDS: {string.Join(", ", overrides)}. The ceiling is one " +
            "workflow variable, so tightening it never means editing fourteen jobs.");
    }

    /// <summary>
    ///     Every eligible job has a fingerprint step, an exact-key restore under
    ///     <c>receipt-&lt;job&gt;-&lt;leg&gt;-&lt;hash&gt;</c>, and a verdict step to read it.
    /// </summary>
    [Fact]
    public void EveryEligibleJobCarriesTheReceiptStepsUnderOneKey()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach (EligibleJob job in EligibleJobs)
        {
            if (!jobs.TryGetValue(job.Job, out YamlMappingNode? node))
            {
                problems.Add($"'{job.Job}' is not a job in ci.yml.");
                continue;
            }

            IReadOnlyList<Step> steps = StepsOf(node);
            Step? fingerprint = steps.FirstOrDefault(step => step.Id == "fp");
            if (fingerprint is null)
            {
                problems.Add($"{job.Job}: no 'id: fp' fingerprint step.");
            }
            else if (!RunOf(fingerprint).Contains("hash=", StringComparison.Ordinal))
            {
                problems.Add($"{job.Job}: the fingerprint step does not write 'hash' to GITHUB_OUTPUT.");
            }

            problems.AddRange(ReceiptKeyProblems(job, steps));

            if (!steps.Any(step => step.Id == "verdict"))
                problems.Add($"{job.Job}: no 'id: verdict' step.");
        }

        Assert.True(problems.Count == 0, "ci.yml receipt problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    ///     Every job is either eligible or receipt-free. A job the path filter decides must not gain a
    ///     receipt, and a job added later must not quietly run without one.
    /// </summary>
    [Fact]
    public void OnlyTheEligibleJobsCarryAReceipt()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> unclassified = [];
        List<string> unexpected = [];

        foreach ((string name, YamlMappingNode node) in jobs)
        {
            bool eligible = EligibleJobs.Any(job => string.Equals(job.Job, name, StringComparison.Ordinal));
            bool excluded = JobsWithoutReceipts.Contains(name, StringComparer.Ordinal);

            if (!eligible && !excluded)
                unclassified.Add(name);

            if (eligible || !excluded)
                continue;

            unexpected.AddRange(StepsOf(node)
                .Where(IsReceiptStep)
                .Select(step => $"{name}: '{step.Name}'"));
        }

        Assert.True(
            unclassified.Count == 0,
            $"These jobs are neither eligible for a receipt nor named as receipt-free: {string.Join(", ", unclassified)}.");
        Assert.True(
            unexpected.Count == 0,
            "These receipt steps sit in a job whose run is decided by the path filter, not by a receipt: " +
            string.Join(", ", unexpected));
    }

    /// <summary>
    ///     Each job hashes exactly the set the plan fixes for it, so two jobs that read the same inputs
    ///     cannot drift into hashing different ones.
    /// </summary>
    [Fact]
    public void EveryJobHashesTheFingerprintSetItDeclares()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach (EligibleJob job in EligibleJobs)
        {
            string[] expected = ArgumentsOf(job.FingerprintSet);
            string[] actual = FingerprintArguments(jobs[job.Job], job.Job);

            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                problems.Add(
                    $"'{job.Job}' hashes set {job.FingerprintSet}, which is:\n  {string.Join(", ", expected)}\n" +
                    $"but the job asks hashFiles for:\n  {string.Join(", ", actual)}");
            }
        }

        Assert.True(problems.Count == 0, "ci.yml fingerprint problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    ///     The whole-tree set names every tracked top-level entry, directories as <c>&lt;dir&gt;/**</c> and
    ///     root files by exact name, and nothing else.
    /// </summary>
    /// <remarks>
    ///     A bare root glob would hash the same entries without proving the enumeration complete, and a
    ///     missing entry is the silent skip this mechanism prevents: the job would reuse a receipt from
    ///     a run that never read the file. The tracked tree is the authority, so a new top-level
    ///     directory or file fails here until it is added.
    /// </remarks>
    [Fact]
    public void TheWholeTreeFingerprintNamesEveryTrackedTopLevelEntry()
    {
        (IReadOnlyList<string> directories, IReadOnlyList<string> files) = TrackedTopLevelEntries();

        string[] expected =
        [
            .. directories.Select(directory => directory + "/**"),
            .. files,
        ];
        string[] actual = FingerprintArguments(JobsOf(LoadWorkflow())["build-test"], "build-test");

        Assert.Equal(
            [.. expected.Order(StringComparer.Ordinal)],
            [.. actual.Order(StringComparer.Ordinal)]);
        Assert.DoesNotContain(actual, argument => argument is "*" or ".*" or "**" or "**/*");
    }

    /// <summary>
    ///     The SBOM set covers every alternation of the <c>changes</c> job's sbom filter, so a change the
    ///     filter treats as SBOM-relevant always invalidates the receipt the sbom job would skip on.
    /// </summary>
    [Fact]
    public void TheSbomFingerprintCoversEveryAlternationOfTheChangesFilter()
    {
        Step classification = StepsOf(JobsOf(LoadWorkflow())["changes"]).Single(step => step.Id == "areas");

        string[] alternations = SbomFilterAlternations(RunOf(classification));
        Assert.Equal(
            [.. SbomFilterCoverage.Select(coverage => coverage.Alternation).Order(StringComparer.Ordinal)],
            [.. alternations.Order(StringComparer.Ordinal)]);

        string[] sbom = FingerprintArguments(JobsOf(LoadWorkflow())["sbom"], "sbom");
        List<string> uncovered = [];
        foreach (SbomAlternation coverage in SbomFilterCoverage)
        {
            uncovered.AddRange(coverage.Arguments.Except(sbom, StringComparer.Ordinal));
        }

        Assert.True(
            uncovered.Count == 0,
            "The changes job's sbom filter matches these inputs but the sbom fingerprint does not hash them: " +
            string.Join(", ", uncovered));
    }

    /// <summary>
    ///     Restore is exact-key only. A <c>restore-keys</c> prefix would hand a job the receipt of a
    ///     different tree, which is the one way this mechanism could skip work it never proved.
    /// </summary>
    [Fact]
    public void NoReceiptRestoreAcceptsANearMissKey()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> prefixes = [];

        foreach (EligibleJob job in EligibleJobs)
        {
            foreach (Step step in StepsOf(jobs[job.Job]).Where(IsCacheRead))
            {
                YamlMappingNode with = Assert.IsType<YamlMappingNode>(Required(step.Node, "with", step.Name));
                if (with.Children.ContainsKey(new YamlScalarNode("restore-keys")))
                    prefixes.Add($"{job.Job}: '{step.Name}' declares restore-keys.");
            }
        }

        Assert.True(prefixes.Count == 0, string.Join('\n', prefixes));
    }

    /// <summary>A cache key names the job, its leg and the fingerprint — never an age.</summary>
    [Fact]
    public void NoCacheKeyCarriesAnAge()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        string[] aged = ["RECEIPT_MAX_AGE_SECONDS", "epoch", "week", "steps.verdict"];
        List<string> found = [];

        foreach (EligibleJob job in EligibleJobs)
        {
            foreach (Step step in StepsOf(jobs[job.Job]).Where(step => IsCacheRead(step) || IsCacheWrite(step)))
            {
                string key = KeyOf(step);
                if (aged.Any(token => key.Contains(token, StringComparison.Ordinal)))
                    found.Add($"{job.Job}: '{key}'");
            }
        }

        Assert.True(
            found.Count == 0,
            "A cache key carries an age, so the ceiling could only be tightened by invalidating every key: " +
            string.Join(", ", found.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     Every receipt script runs under <c>bash</c>. Two eligible jobs — <c>squad-filesystem-contract</c>
    ///     and <c>tray-rust</c> — have a Windows leg whose <c>pwsh</c> default breaks <c>date +%s</c> and
    ///     the verdict's arithmetic.
    /// </summary>
    [Fact]
    public void EveryReceiptScriptRunsUnderBash()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> wrong = [];
        int inspected = 0;

        foreach (EligibleJob job in EligibleJobs)
        {
            foreach (Step step in StepsOf(jobs[job.Job]).Where(step => step.Id is "fp" or "verdict" or "digest" or "save"))
            {
                inspected++;
                if (!string.Equals(Scalar(step.Node, "shell"), "bash", StringComparison.Ordinal))
                    wrong.Add($"{job.Job}: '{step.Name}'");
            }
        }

        Assert.True(
            inspected >= 3 * EligibleJobs.Length,
            $"Only {inspected} receipt scripts were found: every job runs a fingerprint, a verdict and a save.");
        Assert.True(
            wrong.Count == 0,
            $"These receipt steps have no 'shell: bash', so a Windows leg runs them under pwsh: {string.Join(", ", wrong)}.");
    }

    /// <summary>
    ///     The digest step writes one <c>name=value</c> line. <c>jq</c> pretty-prints by default, and a
    ///     newline in <c>GITHUB_OUTPUT</c> is not a legal output line, so the step fails before the
    ///     receipt is saved.
    /// </summary>
    [Fact]
    public void TheDigestStepWritesCompactJsonToTheStepOutput()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach (AttestedProduct product in AttestedProducts)
        {
            if (!jobs.TryGetValue(product.Job, out YamlMappingNode? node))
            {
                problems.Add($"{product.Job}: is not a job, so its digest is never written.");
                continue;
            }

            Step? digest = StepsOf(node).SingleOrDefault(step => step.Id == "digest");
            if (digest is null)
            {
                problems.Add($"{product.Job}: has no digest step.");
                continue;
            }

            string run = RunOf(digest);
            if (!run.Contains("jq -c ", StringComparison.Ordinal))
            {
                problems.Add($"{product.Job}: the digest jq is not compact, so its JSON spans lines.");
            }
        }

        Assert.True(problems.Count == 0, "Digest output problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    ///     The verdict validates the receipt before it trusts it, and compares its age against the
    ///     ceiling before any skip is set. A stale, future, unparsable or foreign receipt is a miss.
    /// </summary>
    [Fact]
    public void TheVerdictValidatesTheReceiptBeforeItSkips()
    {
        string run = VerdictRun();
        List<string> problems = [];

        foreach (string required in new[]
                 {
                     "saved_at_epoch", "input_hash", "RECEIPT_MAX_AGE_SECONDS", "date -u +%s",
                     "jq -er '.saved_at_epoch'", "jq -er '.input_hash'", "[!0-9]", "${GITHUB_STEP_SUMMARY}",
                 })
        {
            if (!run.Contains(required, StringComparison.Ordinal))
                problems.Add($"the verdict never reads '{required}'.");
        }

        int skip = run.IndexOf("finish true", StringComparison.Ordinal);
        if (skip < 0 || run.IndexOf("finish true", skip + 1, StringComparison.Ordinal) >= 0)
            problems.Add("the verdict has no single place that sets skip=true.");

        foreach (string gate in new[] { "saved_at_epoch", "RECEIPT_MAX_AGE_SECONDS", "input_hash", "date -u +%s" })
        {
            if (skip >= 0 && run.IndexOf(gate, StringComparison.Ordinal) > skip)
                problems.Add($"'{gate}' is checked after skip=true is set.");
        }

        Assert.True(problems.Count == 0, "The verdict step:\n" + string.Join('\n', problems));

        Assert.Contains("no prior receipt; running in full", run, StringComparison.Ordinal);
        Assert.Contains("cache isolation", run, StringComparison.Ordinal);
        Assert.Contains("future", run, StringComparison.Ordinal);
        Assert.Contains(
            run.Split('\n'),
            line => line.Contains("${age}", StringComparison.Ordinal) &&
                line.Contains("${RECEIPT_MAX_AGE_SECONDS}", StringComparison.Ordinal) &&
                line.Contains("ceiling", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Every step that runs after the verdict composes the skip guard onto its own condition, so a
    ///     fresh receipt can skip only work the job declared skippable.
    /// </summary>
    [Fact]
    public void EveryStepAfterTheVerdictGatesOnTheSkipOutput()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> ungated = [];
        int gated = 0;

        foreach (EligibleJob job in EligibleJobs)
        {
            IReadOnlyList<Step> steps = StepsOf(jobs[job.Job]);
            int verdict = IndexOf(steps, step => step.Id == "verdict");
            Assert.True(verdict >= 0, $"'{job.Job}' has no verdict step for its later steps to gate on.");

            foreach (Step step in steps.Skip(verdict + 1))
            {
                if (step.If?.Contains(SkipGuard, StringComparison.Ordinal) == true)
                {
                    gated++;
                    continue;
                }

                ungated.Add($"{job.Job}: '{step.Name}'");
            }
        }

        Assert.True(
            ungated.Count == 0,
            "These steps run even when the receipt is fresh, so the job's work is not actually skipped: " +
            string.Join(", ", ungated));
        Assert.True(gated > 40, $"Only {gated} steps gate on the verdict; the job bodies lost their guard.");
    }

    /// <summary>
    ///     A save step is unreachable on a hit, so a hit never refreshes the receipt's age, and it is
    ///     best-effort: a cache-reserve race must not fail a job that was green.
    /// </summary>
    [Fact]
    public void TheSaveStepsAreMissOnlyBestEffortAndSameRepository()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach (EligibleJob job in EligibleJobs)
        {
            IReadOnlyList<Step> steps = StepsOf(jobs[job.Job]);
            Step[] saves = [.. steps.Where(step => step.Id == "save" || IsCacheWrite(step))];
            if (saves.Length == 0)
            {
                problems.Add($"{job.Job}: no receipt save.");
                continue;
            }

            foreach (Step step in saves)
            {
                problems.AddRange(GuardProblems(job.Job, step));
                if (!string.Equals(Scalar(step.Node, "continue-on-error"), "true", StringComparison.Ordinal))
                    problems.Add($"{job.Job}: '{step.Name}' is not continue-on-error, so a cache race fails a green job.");

                if (!(step.If ?? string.Empty).Contains("success()", StringComparison.Ordinal))
                    problems.Add($"{job.Job}: '{step.Name}' does not require success().");

                if (step.Id == "save")
                    problems.AddRange(SavePayloadProblems(job.Job, step));
            }
        }

        Assert.True(problems.Count == 0, "Save-step problems:\n" + string.Join('\n', problems));
    }

    /// <summary>Only a job that mints a token attests, and it attests a build product in provenance mode.</summary>
    [Fact]
    public void OnlyJobsThatMintATokenAttestAndOnlyBuildProductsAreAttested()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        string[] lines = ReadCi().Split('\n');
        List<string> problems = [];

        foreach ((string name, YamlMappingNode node) in jobs)
        {
            Step[] attestations =
            [
                .. StepsOf(node).Where(step =>
                    step.Uses?.StartsWith("actions/attest", StringComparison.Ordinal) == true),
            ];
            bool mints = Permissions(node).ContainsKey("id-token");

            if (mints != attestations.Length > 0)
                problems.Add($"{name}: mints a token = {mints} but carries {attestations.Length} attestation(s).");

            if (!mints)
                continue;

            IReadOnlyDictionary<string, string> permissions = Permissions(node);
            foreach (string permission in new[] { "id-token", "attestations", "artifact-metadata" })
            {
                if (!permissions.TryGetValue(permission, out string? value) ||
                    !string.Equals(value, "write", StringComparison.Ordinal))
                {
                    problems.Add($"{name}: '{permission}' is not write, so the attestation cannot be stored.");
                }
            }

            if (!permissions.TryGetValue("contents", out string? contents) ||
                !string.Equals(contents, "read", StringComparison.Ordinal))
            {
                problems.Add($"{name}: 'contents' is not the read floor every job keeps.");
            }

            string comment = CommentAbove(Required(node, "permissions", $"{name}'s job"), lines);
            if (!comment.Contains("id-token", StringComparison.Ordinal) ||
                !comment.Contains("Sigstore", StringComparison.Ordinal))
            {
                problems.Add($"{name}: the permissions block has no comment naming why it mints a token.");
            }

            AttestedProduct? product =
                AttestedProducts.FirstOrDefault(entry => string.Equals(entry.Job, name, StringComparison.Ordinal));
            if (product is null)
            {
                problems.Add($"{name}: attests a product this test does not name.");
                continue;
            }

            problems.AddRange(AttestationProblems(name, attestations, product));
        }

        foreach (AttestedProduct product in AttestedProducts)
        {
            if (!jobs.TryGetValue(product.Job, out YamlMappingNode? node))
            {
                problems.Add($"{product.Job}: is not a job in ci.yml, so its product is never attested.");
            }
            else if (!StepsOf(node).Any(step =>
                step.Uses?.StartsWith("actions/attest", StringComparison.Ordinal) == true))
            {
                problems.Add(
                    $"{product.Job}: attests {string.Join(", ", product.SubjectPaths)} but carries no attestation step.");
            }
        }

        Assert.True(problems.Count == 0, "Attestation problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    ///     The four security scans keep their existing steps, SARIF uploads and advisory state: the
    ///     receipt composes the skip guard and changes nothing else, so a skipped scan still reads as a
    ///     success and its upload still runs after a failed scan.
    /// </summary>
    [Fact]
    public void TheSecurityScansKeepTheirUploadsAndTheirAdvisoryState()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach ((string name, string scanStep) in new[]
                 {
                     ("trivy", "Scan repository with Trivy"), ("semgrep", "Scan repository with Semgrep"),
                     ("gitleaks", "Run gitleaks"),
                 })
        {
            YamlMappingNode node = jobs[name];
            if (!string.Equals(Scalar(node, "continue-on-error"), "true", StringComparison.Ordinal))
                problems.Add($"{name}: no longer advisory (continue-on-error: true is gone).");

            if (!StepsOf(node).Any(step => string.Equals(step.Name, scanStep, StringComparison.Ordinal)))
                problems.Add($"{name}: the scan step '{scanStep}' is gone.");

            Step[] uploads =
            [
                .. StepsOf(node).Where(step =>
                    step.Uses?.StartsWith("github/codeql-action/upload-sarif", StringComparison.Ordinal) == true),
            ];
            if (uploads.Length != 1)
            {
                problems.Add($"{name}: {uploads.Length} SARIF upload steps, expected one.");
                continue;
            }

            string uploadIf = uploads[0].If ?? string.Empty;
            if (!uploadIf.Contains("always()", StringComparison.Ordinal) ||
                !uploadIf.Contains("hashFiles(", StringComparison.Ordinal))
            {
                problems.Add($"{name}: the upload no longer runs after a failed scan ('{uploadIf}').");
            }
        }

        Assert.True(problems.Count == 0, "Security-scan problems:\n" + string.Join('\n', problems));
    }

    /// <summary>No job decides from a receipt at the job level; the skip happens inside the job.</summary>
    [Fact]
    public void NoJobLevelConditionSkippedAJobFromAReceipt()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> problems = [];

        foreach ((string name, YamlMappingNode node) in jobs)
        {
            string? jobIf = Scalar(node, "if");
            if (jobIf is not null &&
                (jobIf.Contains("verdict", StringComparison.Ordinal) ||
                 jobIf.Contains("receipt", StringComparison.Ordinal) ||
                 jobIf.Contains("fp.outputs", StringComparison.Ordinal)))
            {
                problems.Add($"{name}: the job-level if: reads the receipt ('{jobIf}').");
            }

            if (jobIf is null && !JobsWithNoJobLevelCondition.Contains(name, StringComparer.Ordinal))
                problems.Add($"{name}: lost its job-level if:, so its path filter no longer applies.");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));

        foreach (string name in JobsWithNoJobLevelCondition)
        {
            Assert.True(jobs.ContainsKey(name), $"ci.yml declares no '{name}' job.");
            Assert.Null(Scalar(jobs[name], "if"));
        }
    }

    /// <summary>
    ///     A receipt step's <c>run:</c> block never interpolates the event payload: the values it needs
    ///     arrive through <c>env:</c>, so a hostile ref cannot reach the shell.
    /// </summary>
    [Fact]
    public void NoReceiptRunBlockInterpolatesTheGitHubContextIntoShell()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        List<string> interpolating = [];
        int inspected = 0;

        foreach (EligibleJob job in EligibleJobs)
        {
            foreach (Step step in StepsOf(jobs[job.Job])
                .Where(step => IsReceiptStep(step) && step.Node.Children.ContainsKey(new YamlScalarNode("run"))))
            {
                inspected++;
                if (RunOf(step).Contains("${{ github.", StringComparison.Ordinal))
                    interpolating.Add($"{job.Job}: '{step.Name}'");
            }
        }

        Assert.True(
            inspected >= 3 * EligibleJobs.Length,
            $"Only {inspected} receipt run blocks were found: every job runs a fingerprint, a verdict and a save.");
        Assert.True(
            interpolating.Count == 0,
            $"These receipt run blocks interpolate the github context into shell: {string.Join(", ", interpolating)}.");
    }

    /// <summary>Every new action reuses a pin the repository already carries, with its version comment.</summary>
    [Fact]
    public void EveryNewActionReusesTheRepositoryPin()
    {
        string ci = ReadCi();

        Assert.Contains(CacheRestorePin, ci, StringComparison.Ordinal);
        Assert.Contains(CacheSavePin, ci, StringComparison.Ordinal);
        Assert.Contains(AttestPin, ci, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ReceiptKeyProblems(EligibleJob job, IReadOnlyList<Step> steps)
    {
        List<string> problems = [];
        Step? receipt = steps.FirstOrDefault(IsCacheRead);
        if (receipt is null)
        {
            problems.Add($"{job.Job}: no receipt lookup (actions/cache/restore).");
        }
        else
        {
            if (!string.Equals(receipt.Id, "receipt", StringComparison.Ordinal))
                problems.Add($"{job.Job}: the receipt lookup step has id '{receipt.Id}', not 'receipt'.");

            YamlMappingNode with = Assert.IsType<YamlMappingNode>(Required(receipt.Node, "with", receipt.Name));
            if (!string.Equals(ScalarValue(Required(with, "path", receipt.Name)), ".receipt", StringComparison.Ordinal))
                problems.Add($"{job.Job}: the receipt lookup does not restore the '.receipt' path.");

            string expected = ExpectedKey(job);
            if (!string.Equals(ScalarValue(Required(with, "key", receipt.Name)), expected, StringComparison.Ordinal))
                problems.Add($"{job.Job}: the receipt key is not '{expected}'.");
        }

        foreach (Step write in steps.Where(IsCacheWrite))
        {
            YamlMappingNode with = Assert.IsType<YamlMappingNode>(Required(write.Node, "with", write.Name));
            if (with.Children.TryGetValue(new YamlScalarNode("key"), out YamlNode? key) &&
                !string.Equals(ScalarValue(key), ExpectedKey(job), StringComparison.Ordinal))
            {
                problems.Add($"{job.Job}: '{write.Name}' stores under another key than the lookup reads.");
            }
        }

        return problems;
    }

    private static string ExpectedKey(EligibleJob job) =>
        $"receipt-{job.Job}-{job.Leg}-{FingerprintOutput}";

    private static IReadOnlyList<string> SavePayloadProblems(string job, Step step)
    {
        string run = RunOf(step);
        List<string> problems = [];

        foreach (string field in new[]
                 {
                     "schema", "job", "input_hash", "commit_sha", "run_id", "run_url", "artifact_digests",
                     "attested", "saved_at", "saved_at_epoch",
                 })
        {
            if (!run.Contains(field + ":", StringComparison.Ordinal))
                problems.Add($"{job}: the receipt has no '{field}' field.");
        }

        if (!run.Contains(".receipt/receipt.json", StringComparison.Ordinal))
            problems.Add($"{job}: the save step writes no .receipt/receipt.json.");

        if (!run.Contains("date -u +%s", StringComparison.Ordinal))
            problems.Add($"{job}: the save step records no epoch second.");

        if (!run.Contains("jq -n", StringComparison.Ordinal))
            problems.Add($"{job}: the save step does not build the receipt with jq.");

        return problems;
    }

    private static IReadOnlyList<string> AttestationProblems(
        string job,
        IReadOnlyList<Step> attestations,
        AttestedProduct product)
    {
        List<string> problems = [];
        if (attestations.Count != 1)
        {
            problems.Add($"{job}: {attestations.Count} attestation steps, one per product set.");
            return problems;
        }

        Step step = attestations[0];
        YamlMappingNode with = Assert.IsType<YamlMappingNode>(Required(step.Node, "with", step.Name));

        if (with.Children.ContainsKey(new YamlScalarNode("sbom-path")))
            problems.Add($"{job}: 'sbom-path' makes this an SBOM attestation, not build provenance.");

        if (with.Children.ContainsKey(new YamlScalarNode("predicate")))
            problems.Add($"{job}: a custom predicate replaces build provenance.");

        string subject = with.Children.TryGetValue(new YamlScalarNode("subject-path"), out YamlNode? path)
            ? ScalarValue(path)
            : string.Empty;

        foreach (string expected in product.SubjectPaths)
        {
            if (!subject.Contains(expected, StringComparison.Ordinal))
                problems.Add($"{job}: the attestation's subject-path does not name '{expected}'.");
        }

        problems.AddRange(GuardProblems(job, step));
        return problems;
    }

    private static IReadOnlyList<string> GuardProblems(string job, Step step)
    {
        string guard = step.If ?? string.Empty;
        List<string> problems = [];

        if (!guard.Contains(SkipGuard, StringComparison.Ordinal))
            problems.Add($"{job}: '{step.Name}' is reachable on a fresh receipt.");

        if (!guard.Contains(SameRepositoryGuard, StringComparison.Ordinal))
            problems.Add($"{job}: '{step.Name}' has no same-repository guard, so a fork would write it.");

        return problems;
    }

    private static string VerdictRun()
    {
        IReadOnlyDictionary<string, YamlMappingNode> jobs = JobsOf(LoadWorkflow());
        string[] runs =
        [
            .. EligibleJobs.Select(job =>
                RunOf(StepsOf(jobs[job.Job]).Single(step => step.Id == "verdict"))),
        ];

        Assert.True(
            runs.All(run => string.Equals(run, runs[0], StringComparison.Ordinal)),
            "The verdict script differs between jobs; it is one script, repeated so no job can drift.");

        return runs[0];
    }

    private static string[] ArgumentsOf(string fingerprintSet) => fingerprintSet switch
    {
        "W" => WholeTree,
        "S" => SbomInputs,
        "D" => DashInputs,
        "T" => TrayInputs,
        var other => throw new InvalidOperationException($"Unknown fingerprint set '{other}'."),
    };

    private static string[] FingerprintArguments(YamlMappingNode job, string name)
    {
        Step fingerprint = StepsOf(job).SingleOrDefault(step => step.Id == "fp") ??
            throw new InvalidOperationException($"'{name}' has no 'id: fp' step, so it has no fingerprint.");

        YamlMappingNode environment =
            Assert.IsType<YamlMappingNode>(Required(fingerprint.Node, "env", fingerprint.Name));
        string expression = ScalarValue(Required(environment, "INPUT_HASH", fingerprint.Name));

        Match match = Regex.Match(
            expression,
            @"^\$\{\{ hashFiles\((?<arguments>.*)\) \}\}$",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"'{name}' fingerprints with '{expression}', which is not one hashFiles() call.");

        return
        [
            .. Regex.Matches(match.Groups["arguments"].Value, "'(?<argument>[^']*)'", RegexOptions.CultureInvariant)
                .Select(argument => argument.Groups["argument"].Value),
        ];
    }

    private static string[] SbomFilterAlternations(string run)
    {
        Match match = Regex.Match(run, "sbom=\"\\$\\(has '(?<pattern>.+)'\\)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The changes job states no sbom path filter.");

        string pattern = match.Groups["pattern"].Value;
        if (pattern.StartsWith('(') && MatchingParen(pattern) == pattern.Length - 1)
            pattern = pattern[1..^1];

        List<string> alternations = [];
        StringBuilder current = new();
        int depth = 0;
        foreach (char character in pattern)
        {
            switch (character)
            {
                case '(':
                    depth++;
                    current.Append(character);
                    break;
                case ')':
                    depth--;
                    current.Append(character);
                    break;
                case '|' when depth == 0:
                    alternations.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        alternations.Add(current.ToString());
        return [.. alternations];
    }

    private static int MatchingParen(string pattern)
    {
        int depth = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            depth += pattern[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
                return index;
        }

        return -1;
    }

    private static (IReadOnlyList<string> Directories, IReadOnlyList<string> Files) TrackedTopLevelEntries()
    {
        List<string> directories = [];
        List<string> files = [];

        // `git ls-tree HEAD` is the tracked tree, which is the scope the fingerprint must cover: a
        // working tree also holds build output and local tool state that no job reads. StdioSession is
        // the bounded spawn path, and its 60 s budget kills the child tree when it expires.
        using StdioSession session = new(
            "git",
            ["-C", Corpus.Root, "ls-tree", "HEAD"],
            TimeSpan.FromSeconds(60),
            TimeProvider.System);

        while (true)
        {
            string line;
            try
            {
                line = session.ReadLine();
            }
            catch (EndOfStreamException)
            {
                break;
            }

            string[] halves = line.Split('\t', 2);
            if (halves.Length != 2)
                continue;

            string[] metadata = halves[0].Split(' ');
            if (metadata.Length >= 2 && string.Equals(metadata[1], "tree", StringComparison.Ordinal))
                directories.Add(halves[1]);
            else
                files.Add(halves[1]);
        }

        Assert.NotEmpty(directories);
        Assert.NotEmpty(files);
        return (directories, files);
    }

    private static bool IsCacheRead(Step step) =>
        step.Uses?.StartsWith("actions/cache/restore", StringComparison.Ordinal) == true;

    private static bool IsCacheWrite(Step step) =>
        step.Uses?.StartsWith("actions/cache/save", StringComparison.Ordinal) == true;

    private static bool IsReceiptStep(Step step) =>
        step.Id is "fp" or "receipt" or "verdict" or "digest" or "save" ||
        IsCacheRead(step) ||
        IsCacheWrite(step) ||
        step.Uses?.StartsWith("actions/attest", StringComparison.Ordinal) == true;

    private static string KeyOf(Step step) =>
        step.Node.Children.TryGetValue(new YamlScalarNode("with"), out YamlNode? with) &&
        with is YamlMappingNode mapping &&
        mapping.Children.TryGetValue(new YamlScalarNode("key"), out YamlNode? key)
            ? ScalarValue(key)
            : string.Empty;

    private static IReadOnlyDictionary<string, string> Permissions(YamlMappingNode job)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("permissions"), out YamlNode? permissions))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return ((YamlMappingNode)permissions).Children.ToDictionary(
            entry => ScalarValue(entry.Key),
            entry => ScalarValue(entry.Value),
            StringComparer.Ordinal);
    }

    private static string CommentAbove(YamlNode node, string[] lines)
    {
        // A mapping node's own mark points inside the block, so walk up to the key that opens it.
        int line = Math.Min((int)node.Start.Line, lines.Length - 1);
        int limit = Math.Max(0, line - 8);
        while (line > limit && lines[line].Trim() != "permissions:")
            line--;

        Assert.Equal("permissions:", lines[line].Trim());

        List<string> comment = [];
        for (int index = line - 1; index >= 0 && lines[index].TrimStart().StartsWith('#'); index--)
            comment.Insert(0, lines[index]);

        return string.Join('\n', comment);
    }

    private static IReadOnlyList<Step> StepsOf(YamlMappingNode job)
    {
        YamlSequenceNode steps = Assert.IsType<YamlSequenceNode>(Required(job, "steps", "A job"));
        return
        [
            .. steps.Children.Select(node =>
            {
                YamlMappingNode step = Assert.IsType<YamlMappingNode>(node);
                return new Step(
                    Scalar(step, "name") ?? Scalar(step, "uses") ?? "(unnamed step)",
                    Scalar(step, "id"),
                    Scalar(step, "uses"),
                    Scalar(step, "if"),
                    step);
            }),
        ];
    }

    private static int IndexOf(IReadOnlyList<Step> steps, Func<Step, bool> predicate)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (predicate(steps[index]))
                return index;
        }

        return -1;
    }

    private static Dictionary<string, YamlMappingNode> JobsOf(YamlMappingNode workflow)
    {
        YamlMappingNode jobs = Assert.IsType<YamlMappingNode>(Required(workflow, "jobs", "ci.yml"));
        return jobs.Children.ToDictionary(
            entry => ScalarValue(entry.Key),
            entry => Assert.IsType<YamlMappingNode>(entry.Value),
            StringComparer.Ordinal);
    }

    private static YamlMappingNode LoadWorkflow()
    {
        YamlStream stream = new();
        using StringReader reader = new(ReadCi());
        stream.Load(reader);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static string RunOf(Step step) =>
        step.Node.Children.TryGetValue(new YamlScalarNode("run"), out YamlNode? run)
            ? ScalarValue(run)
            : string.Empty;

    private static YamlNode Required(YamlMappingNode node, string key, string where)
    {
        Assert.True(
            node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value),
            $"{where} has no '{key}' key.");
        return value!;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? ScalarValue(value) : null;

    private static string ScalarValue(YamlNode node) =>
        node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;

    private static string ReadCi() => Corpus.ReadText(CiWorkflowPath);

    /// <summary>A job that carries a receipt, the fingerprint set it hashes, and its key's leg.</summary>
    private sealed record EligibleJob(string Job, string FingerprintSet, string Leg);

    /// <summary>One step of a job, with the keys this suite reads lifted out.</summary>
    private sealed record Step(string Name, string? Id, string? Uses, string? If, YamlMappingNode Node);

    /// <summary>One alternation of the <c>changes</c> job's sbom filter, and the arguments covering it.</summary>
    private sealed record SbomAlternation(string Alternation, string[] Arguments);

    /// <summary>A job that attests a build product, and the product's paths.</summary>
    private sealed record AttestedProduct(string Job, string[] SubjectPaths);
}
