using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Application.Arbiter;

/// <summary>Diagnoses the host's Arbiter installation without writing anything.</summary>
/// <remarks>
/// Includes the provider and override diagnostics, the missing-key warning, the unignored-log warning, the binary
/// warning, the guard warning and the two hook-coverage warnings. Every check that passes is reported too, so the
/// operator can see what was looked at. The host decides how each line is marked and printed.
/// </remarks>
public sealed class ArbiterDoctorService(
    IArbiterModelTargetsReader modelTargets,
    IArbiterConfigResolver configs,
    IArbiterKeyService keys,
    IArbiterUserSettings userSettings,
    IProcessEnvironment environment,
    IUserHostConfigStore userConfig,
    IArbiterRepositoryLocator locator,
    IArbiterHarnessGating gating,
    IGitCommandRunner git,
    ISquadStateStore stateStore,
    IWritableFileSystem files,
    IFileStatReader fileStats,
    IArbiterBinaryProbe probe) : IArbiterDoctorService
{
    private const string ModelTargetingOff = "off";
    private const string EnforcedGlobal = "enforced-global";
    private const string SquadUpdateHint = "Run kyber-weave squad update to rewrite the model targets.";

    /// <summary>
    /// How much older than the global Squad lock a model targets file may be before it counts as stale. This
    /// is the fallback for a table no deployment recorded, judged by write time alone. A recorded table is
    /// judged by the digest the receipt holds: an update rewrites only changed content, so a table whose
    /// bytes did not change keeps the write time of the deployment that first wrote it.
    /// </summary>
    private static readonly TimeSpan SameDeploymentWindow = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public ArbiterDoctorReport Inspect(string repositoryPath, string? configFile, DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _ = configs.TryResolve(repositoryPath, configFile, report, out KyberWeaveConfig config, out string? configPath);
        string home = environment.HomeDirectory;
        IReadOnlyList<string> globalTargets = userConfig.ReadGlobalTargets(home);
        string root = locator.ResolveRepositoryRoot(repositoryPath);
        HarnessGatingReport gatingReport = gating.Build(root, home, config, globalTargets);

        List<ArbiterDoctorLine> lines = [];
        report.AddRange(RuleValidator.Validate(config.Arbiter, configPath));
        report.AddRange(RuleValidator.CheckModelTuning(config.Arbiter));
        CheckProviderTimeout(config, home, report);
        CheckKey(config, report, lines);
        CheckLogIgnored(root, report, lines);
        CheckBinary(report, lines);
        CheckGuard(config, root, report, lines);
        CheckProjectHooks(gatingReport, report, lines);
        CheckModelTargets(config, home, globalTargets, report, lines);
        CheckAdvisoryPostDispatchTargets(lines);

        IReadOnlyList<ArbiterDoctorScopeTarget> scope = [.. globalTargets.Select(token => new ArbiterDoctorScopeTarget(TargetDisplayName(token), GlobalReason(token)))];
        return new ArbiterDoctorReport(config.Arbiter.Enabled, scope, gatingReport, lines);
    }

    /// <summary>The name the operator knows a harness token by.</summary>
    /// <param name="token">The harness token.</param>
    /// <returns>The display name, or the token itself when the catalogue does not know it.</returns>
    public static string TargetDisplayName(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return token;
        }

        return target switch
        {
            SquadTarget.Claude => "Claude",
            SquadTarget.Codex => "Codex",
            SquadTarget.Copilot => "Copilot",
            SquadTarget.Cursor => "Cursor",
            SquadTarget.OpenCode => "OpenCode",
            SquadTarget.Pi => "Pi",
            SquadTarget.Warp => "Warp",
            SquadTarget.ZCode => "ZCode",
            SquadTarget.Kilo => "Kilo",
            SquadTarget.Antigravity => "Antigravity",
            SquadTarget.Factory => "Factory",
            SquadTarget.Devin => "Devin",
            SquadTarget.CommandCode => "CommandCode",
            _ => token,
        };
    }

    /// <summary>
    /// Warns when model targeting is on and a hooked global target has no current model targets file.
    /// The file lives under each harness's own global root, where the user-scope Squad install writes it
    /// and where the harness's global hook reads it. It must be the table the last Squad update wrote:
    /// the deployment receipt records the digest of every file it owns, so a byte-identical table is
    /// current no matter how old its write time is. An update rewrites only changed content, which is why
    /// a write-time comparison would flag an unchanged table forever while the lock moved on.
    /// </summary>
    private void CheckModelTargets(
        KyberWeaveConfig config,
        string home,
        IReadOnlyList<string> globalTargets,
        DiagnosticReport report,
        List<ArbiterDoctorLine> lines)
    {
        if (string.Equals(config.Arbiter.ModelTargeting.Mode, ModelTargetingOff, StringComparison.Ordinal))
        {
            return;
        }

        SquadReceipt? receipt = TryReadGlobalReceipt(home);
        string lockPath = Path.Combine(home, ".kyber-weave", "squad", "global", "squad.lock.yml");
        foreach (string token in globalTargets)
        {
            if (!IsHooked(token) || GlobalArbiterDirectory(token, home) is not { } arbiterDirectory)
            {
                continue;
            }

            string targetsPath = Path.Combine(arbiterDirectory, $"model-targets.{token}.json");
            bool readable = modelTargets.TryRead(arbiterDirectory, token) is not null;
            if (readable && IsCurrentModelTargets(targetsPath, token, receipt, lockPath))
            {
                continue;
            }

            string reason = readable
                ? "does not match the table the last Squad update wrote"
                : "is missing or unreadable";
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Warn,
                "Model targets",
                IArbiterDoctorService.ModelTargetsMissing,
                $"the {TargetDisplayName(token)} targets file {reason}. {SquadUpdateHint}"));
            report.Add(new Diagnostic(
                IArbiterDoctorService.ModelTargetsMissing,
                Severity.Warning,
                $"Model targeting is '{config.Arbiter.ModelTargeting.Mode}' but the {TargetDisplayName(token)} model targets file {reason}.",
                targetsPath,
                Hint: SquadUpdateHint));
        }
    }

    /// <summary>
    /// Whether the table on disk is the one the last update wrote. When the global receipt records the file,
    /// the recorded digest is the answer: comparing it catches a table an older update left behind and one a
    /// person edited, and it does not care that an unchanged table was never rewritten. A table no receipt
    /// records, such as a hand-made one or one from a state that predates the record, falls back to the write
    /// time the earlier check used.
    /// </summary>
    /// <param name="targetsPath">The table's full path.</param>
    /// <param name="token">The harness token the table is for.</param>
    /// <param name="receipt">The global receipt, or null when none could be read.</param>
    /// <param name="lockPath">The global Squad lock's path, used only by the write-time fallback.</param>
    /// <returns>True when the table is the one the last update wrote, or is judged current by write time.</returns>
    private bool IsCurrentModelTargets(string targetsPath, string token, SquadReceipt? receipt, string lockPath)
    {
        SquadOwnedFile? owned = receipt?.Files.FirstOrDefault(file =>
            string.Equals(file.Target, token, StringComparison.Ordinal)
            && string.Equals(file.RelativePath, ArbiterModelTargetsFile.GlobalRelativePath(token), StringComparison.Ordinal));
        if (owned is null)
        {
            return !(TryGetWriteTimeUtc(targetsPath, out DateTime targetsWritten)
                && TryGetWriteTimeUtc(lockPath, out DateTime lockWritten)
                && targetsWritten < lockWritten - SameDeploymentWindow);
        }

        string? digest = DigestOf(targetsPath);
        return digest is not null && string.Equals(digest, owned.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private SquadReceipt? TryReadGlobalReceipt(string home)
    {
        try
        {
            return stateStore.ReadReceipt(home, SquadDeploymentScope.Global);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidDataException)
        {
            // No usable receipt means the write-time fallback decides, not a failed doctor run.
            return null;
        }
    }

    /// <summary>
    /// The SHA-256 of a table's bytes, as the receipt records it, or null when the file cannot be read. The
    /// render writes the table as UTF-8 JSON, so the text re-encodes to the bytes that were written.
    /// </summary>
    private string? DigestOf(string path)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(files.ReadAllText(path))));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// The directory holding a harness's global model targets table: <c>.kyber-weave/arbiter</c> under the
    /// harness's own global root. That is where the user-scope Squad install deploys it and where the global
    /// hook reads it, so the doctor has to look there rather than under the operator's home.
    /// </summary>
    /// <param name="token">The harness token.</param>
    /// <param name="home">The operator's home, which a global root falls back to.</param>
    /// <returns>The directory, or null when the token names no target with a global root.</returns>
    private string? GlobalArbiterDirectory(string token, string home)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return null;
        }

        try
        {
            string root = new SquadGlobalRoots(environment.GetVariable, home).ResolveGlobalRoot(target);
            return Path.Combine(root, ".kyber-weave", "arbiter");
        }
        catch (ArgumentException)
        {
            // A target with no verified global root cannot be installed at user scope, so it has no table.
            return null;
        }
    }

    /// <summary>
    /// Reads a file's last write time in UTC, or reports that there is none. A missing file reads as the
    /// file-time epoch rather than an error, and an unreadable one throws, so both come back as false and
    /// the check degrades to its warning instead of crashing <c>doctor</c>.
    /// </summary>
    /// <param name="path">The file to stat.</param>
    /// <param name="writtenUtc">The last write time in UTC when the file exists and can be stat'ed.</param>
    /// <returns>True when a real write time was read; false when the file is missing or cannot be stat'ed.</returns>
    internal bool TryGetWriteTimeUtc(string path, out DateTime writtenUtc)
    {
        writtenUtc = default;
        try
        {
            writtenUtc = fileStats.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }

        return writtenUtc != DateTime.FromFileTimeUtc(0);
    }

    /// <summary>
    /// A target is hooked when the catalogue knows it and its global reason is the enforced one. A
    /// fallback-only target carries its own degradation reason and is not hooked, so its missing
    /// targets file is not a warning: no hook would have honoured the override anyway.
    /// </summary>
    private static bool IsHooked(string token) =>
        SquadTargetCatalog.TryResolve(token, out _)
        && string.Equals(GlobalReason(token), EnforcedGlobal, StringComparison.Ordinal);

    private static string GlobalReason(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return EnforcedGlobal;
        }

        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [target],
            SquadDeploymentScope.Global);
        return records.Count == 0 ? EnforcedGlobal : (records[0].Details ?? EnforcedGlobal);
    }

    /// <summary>
    /// Raises one <c>KW-ARB-PROJECT-001</c> warning per ungated harness, and one <c>KW-ARB-GLOBAL-001</c> warning per
    /// harness the global receipt holds a hook for but the global targets do not list.
    /// </summary>
    private static void CheckProjectHooks(
        HarnessGatingReport gating,
        DiagnosticReport report,
        List<ArbiterDoctorLine> lines)
    {
        foreach (HarnessGatingRow row in gating.Rows.Where(row => row.Ungated))
        {
            string label = TargetDisplayName(row.Token);
            // The row's state says what is missing when the harness is installed but a hook is absent; a row that
            // says only "ungated" has nothing to add.
            string detail = string.Equals(row.State, "ungated", StringComparison.Ordinal)
                ? string.Empty
                : $" ({row.State})";
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Warn,
                null,
                IArbiterDoctorService.ProjectUngated,
                $"{label} is ungated{detail}; run {row.FixCommand ?? string.Empty}."));
            report.Add(new Diagnostic(
                IArbiterDoctorService.ProjectUngated,
                Severity.Warning,
                $"Project enables Arbiter, and {label} is ungated: no project hook and no live global hook gates its dispatches.",
                "arbiter.enabled",
                Hint: row.FixCommand));
        }

        foreach (string token in gating.UnlistedGlobalTokens)
        {
            string label = TargetDisplayName(token);
            string fix = $"kyber-weave arbiter install --target {token}";
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Warn,
                null,
                IArbiterDoctorService.GlobalUnlisted,
                $"the global Squad receipt holds an Arbiter hook for {label}, which arbiter.global.targets does not list; run {fix}."));
            report.Add(new Diagnostic(
                IArbiterDoctorService.GlobalUnlisted,
                Severity.Warning,
                $"The global Squad receipt holds an Arbiter hook for {label}, but {label} is not in arbiter.global.targets, so kyber-weave update never refreshes it.",
                "arbiter.global.targets",
                Hint: fix));
        }
    }

    /// <summary>
    /// Warns when the user override's provider timeout cannot finish inside the hook timeout the repository renders.
    /// A malformed override is reported by the hook and validate paths, not here.
    /// </summary>
    private void CheckProviderTimeout(KyberWeaveConfig config, string home, DiagnosticReport report)
    {
        ArbiterProviderConfig effective;
        try
        {
            effective = userSettings.ApplyTo(config.Arbiter.Provider, home);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return;
        }

        report.AddRange(RuleValidator.CheckProviderTimeout(config.Arbiter.Provider, effective));
    }

    private void CheckKey(KyberWeaveConfig config, DiagnosticReport report, List<ArbiterDoctorLine> lines)
    {
        ArbiterProviderConfig provider = config.Arbiter.Provider;
        if (provider.Kind == ArbiterProviderKind.None || ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint))
        {
            lines.Add(new ArbiterDoctorLine(ArbiterDoctorStatus.Ok, "Key", null, "none needed (none or loopback provider)."));
            return;
        }

        string? origin = ArbiterKeyResolver.GetOrigin(provider.Endpoint);
        string? key;
        try
        {
            key = keys.ResolveKey(config, provider.Endpoint);
        }
        catch (SecretServiceUnavailableException exception)
        {
            // The store cannot run on this host: the finding carries the remedy, not a generic "no key".
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Warn,
                "Key",
                ArbiterKeyResolver.MissingKeyCode,
                exception.Message));
            report.Add(new Diagnostic(
                ArbiterKeyResolver.MissingKeyCode,
                Severity.Warning,
                exception.Message,
                "arbiter.provider"));
            return;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            key = null;
        }

        if (key is not null)
        {
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Ok,
                "Key",
                null,
                $"found for {origin ?? provider.Endpoint}."));
            return;
        }

        lines.Add(new ArbiterDoctorLine(
            ArbiterDoctorStatus.Warn,
            "Key",
            ArbiterKeyResolver.MissingKeyCode,
            $"no key resolves for {origin ?? provider.Endpoint}."));
        report.Add(new Diagnostic(
            ArbiterKeyResolver.MissingKeyCode,
            Severity.Warning,
            $"A remote provider is configured, and no key resolves for origin '{origin}'.",
            "arbiter.provider",
            Hint: $"Run 'arbiter setup' or export {ArbiterKeyResolver.EnvVarName}."));
    }

    private void CheckLogIgnored(string root, DiagnosticReport report, List<ArbiterDoctorLine> lines)
    {
        if (IsIgnoredByGit(root))
        {
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Ok,
                "Log",
                null,
                "artifacts/arbiter/ is ignored by git."));
            return;
        }

        lines.Add(new ArbiterDoctorLine(
            ArbiterDoctorStatus.Warn,
            "Log",
            IArbiterDoctorService.LogNotIgnored,
            "artifacts/arbiter/ is not ignored by git."));
        report.Add(new Diagnostic(
            IArbiterDoctorService.LogNotIgnored,
            Severity.Warning,
            "artifacts/arbiter/ is not ignored by git; the ledger and decision log must not be committed.",
            "artifacts/arbiter",
            Hint: "Add artifacts/arbiter/ to .gitignore."));
    }

    private bool IsIgnoredByGit(string root)
    {
        try
        {
            GitOutcome result = git.Run(root, ["check-ignore", "-q", "artifacts/arbiter"]);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // Without git there is no evidence the log is ignored: warn, the safe direction.
            return false;
        }
    }

    private void CheckBinary(DiagnosticReport report, List<ArbiterDoctorLine> lines)
    {
        ToolProbeResult result = probe.Probe();
        string cliVersion = probe.CliVersion;

        if (result is { IsAvailable: true, Version: not null }
            && string.Equals(result.Version, cliVersion, StringComparison.Ordinal))
        {
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Ok,
                "Binary",
                null,
                $"kyber-weave-arbiter {result.Version}."));
            return;
        }

        string reason = result.FailureReason
            ?? (result.Version is null
                ? "The 'kyber-weave-arbiter' executable is not available on PATH."
                : $"The 'kyber-weave-arbiter' version '{result.Version}' differs from the CLI version '{cliVersion}'.");
        lines.Add(new ArbiterDoctorLine(
            ArbiterDoctorStatus.Warn,
            "Binary",
            IArbiterDoctorService.BinaryMissing,
            reason));
        report.Add(new Diagnostic(
            IArbiterDoctorService.BinaryMissing,
            Severity.Warning,
            $"Kyber-Weave Arbiter binary: {reason}",
            "kyber-weave-arbiter",
            Hint: "Install the kyber-weave-arbiter binary matching this CLI version."));
    }

    private void CheckGuard(
        KyberWeaveConfig config,
        string root,
        DiagnosticReport report,
        List<ArbiterDoctorLine> lines)
    {
        IReadOnlyList<ConfigRegEntry> entries = config.ConfigReg.Resolve(config.Ontology);
        string[] wanted =
        [
            ConfigRegConfig.PlanIndexProperty,
            ConfigRegConfig.SpecificationIndexProperty,
            ConfigRegConfig.TodoIndexProperty,
        ];
        bool anyResolves = wanted.Any(name => entries.Any(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal) && IndexExists(root, entry.Path)));

        if (anyResolves)
        {
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Ok,
                "Guard",
                null,
                "a plan, spec or todo index resolves."));
            return;
        }

        lines.Add(new ArbiterDoctorLine(
            ArbiterDoctorStatus.Warn,
            "Guard",
            IArbiterDoctorService.GuardWithoutIndex,
            "no plan, spec or todo index resolves."));
        report.Add(new Diagnostic(
            IArbiterDoctorService.GuardWithoutIndex,
            Severity.Warning,
            "No plan, spec or todo index is declared, so the Read guard protects nothing.",
            "arbiter",
            Hint: "Declare the plan, spec and todo indexes so the Read guard has something to protect."));
    }

    /// <summary>
    /// Notes, as information, the targets whose post-dispatch findings are advisory:
    /// on <c>copilot-cli</c>, <c>codex</c> and <c>cursor</c> a post-dispatch
    /// <c>Deny</c>, <c>PostBlock</c> or <c>PostAnnotation</c> renders only as additional
    /// context, so the conductor can ignore it. Pre-dispatch denies still block on all
    /// three. Informational only, hence no diagnostic id.
    /// </summary>
    private static void CheckAdvisoryPostDispatchTargets(List<ArbiterDoctorLine> lines)
    {
        foreach ((string label, string target, string channel) in AdvisoryPostDispatchTargets)
        {
            lines.Add(new ArbiterDoctorLine(
                ArbiterDoctorStatus.Info,
                null,
                null,
                $"{label} ({target}): post-dispatch Deny/PostBlock/PostAnnotation render as {channel} (advisory); pre-dispatch denies still block."));
        }
    }

    private static readonly (string Label, string Target, string Channel)[] AdvisoryPostDispatchTargets =
    [
        ("Copilot CLI", "copilot-cli", "additionalContext"),
        ("Codex", "codex", "additionalContext"),
        ("Cursor", "cursor", "additional_context"),
    ];

    private bool IndexExists(string root, string path)
    {
        try
        {
            string full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
            return files.PathExists(full);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return false;
        }
    }
}
