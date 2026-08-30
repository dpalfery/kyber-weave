using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Parameters for a Kyber-Squad install lifecycle operation.</summary>
public sealed record SquadInstallRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    IReadOnlyList<SquadTarget> Targets,
    IReadOnlyList<string>? Exclusions = null,
    string? Version = null,
    string Bundle = "full",
    string Translation = "best-effort",
    bool Adopt = false,
    bool DryRun = false,
    string Repository = "dpalfery/kyber-weave");

/// <summary>Parameters for a Kyber-Squad update lifecycle operation.</summary>
public sealed record SquadUpdateRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    IReadOnlyList<SquadTarget>? Targets = null,
    IReadOnlyList<string>? Exclusions = null,
    string? Version = null,
    string Bundle = "full",
    string Translation = "best-effort",
    bool ReplaceManaged = false,
    bool DryRun = false,
    string Repository = "dpalfery/kyber-weave");

/// <summary>Parameters for a Kyber-Squad uninstall lifecycle operation.</summary>
public sealed record SquadUninstallRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    bool DryRun = false);

/// <summary>The result of executing a Kyber-Squad lifecycle operation.</summary>
public sealed record SquadLifecycleResult(
    bool Success,
    SquadDeploymentPlan? Plan = null,
    SquadReceipt? Receipt = null,
    SquadLock? Lock = null,
    IReadOnlyList<SquadDegradation>? Degradations = null,
    IReadOnlyList<string>? Errors = null,
    bool DryRun = false);

/// <summary>
/// Orchestrates install, update, and uninstall lifecycle operations for Kyber-Squad.
/// </summary>
public sealed class SquadLifecycleService
{
    private const string LockSchema = "kyber-squad.lock/v1";

    private readonly ISquadReleaseSource _releaseSource;
    private readonly ISquadRenderer _renderer;
    private readonly SquadStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly ISquadTransactionObserver? _observer;

    public SquadLifecycleService(
        ISquadReleaseSource releaseSource,
        ISquadRenderer renderer,
        SquadStateStore stateStore,
        TimeProvider? timeProvider = null,
        ISquadTransactionObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(releaseSource);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(stateStore);
        _releaseSource = releaseSource;
        _renderer = renderer;
        _stateStore = stateStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer;
    }

    /// <summary>
    /// Performs an installation of Kyber-Squad into the target root.
    /// </summary>
    public async Task<SquadLifecycleResult> InstallAsync(
        SquadInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        if (request.Targets is null || request.Targets.Count == 0)
        {
            throw new ArgumentException("At least one deployment target must be specified.", nameof(request));
        }

        RequireRenderableTargets(request.Targets);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt? existingReceipt = _stateStore.ReadReceipt(targetRoot, request.Scope);
        if (existingReceipt is not null)
        {
            HashSet<string> existingTargets = existingReceipt.Files
                .Select(f => f.Target)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> requestedTargets = request.Targets
                .Select(SquadTargetCatalog.GetToken)
                .ToHashSet(StringComparer.Ordinal);

            if (!existingTargets.SetEquals(requestedTargets))
            {
                throw new SquadDeploymentConflictException(
                    $"Kyber-Squad is already installed at '{targetRoot}' with targets ({string.Join(", ", existingTargets)}). " +
                    $"Requested targets ({string.Join(", ", requestedTargets)}) do not match. Use 'update' to modify targets.");
            }
        }

        string version = request.Version ?? ResolveDefaultVersion();
        string tempExtractDir = Path.Combine(
            Path.GetTempPath(),
            "kyber-squad-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            SquadReleaseResult releaseResult = await _releaseSource.DownloadAndExtractAsync(
                new SquadReleaseRequest(request.Repository, version, tempExtractDir),
                cancellationToken).ConfigureAwait(false);

            string? userScopeDir = request.Scope == SquadDeploymentScope.Global
                ? _stateStore.UserPaths.ApplicationDataDirectory
                : null;

            SquadRenderRequest renderRequest = new(
                SourceDirectory: releaseResult.ExtractionRoot,
                Targets: request.Targets,
                Scope: request.Scope,
                UserScopeDirectory: userScopeDir,
                TranslationMode: request.Translation);

            SquadRenderResult renderResult = await _renderer.RenderAsync(renderRequest, cancellationToken).ConfigureAwait(false);
            if (!renderResult.Success || renderResult.Errors.Count > 0)
            {
                string error = renderResult.Errors.Count > 0
                    ? string.Join(Environment.NewLine, renderResult.Errors)
                    : "Squad render failed.";
                throw new SquadRenderValidationException(error);
            }

            SquadLock squadLock = BuildLock(
                version: releaseResult.Version,
                bundle: request.Bundle,
                targets: request.Targets,
                exclusions: request.Exclusions ?? Array.Empty<string>(),
                translation: request.Translation,
                assetDigest: releaseResult.Checksum.Sha256,
                extractionRoot: releaseResult.ExtractionRoot);

            IReadOnlyList<SquadDegradation> degradations = renderResult.Degradations
                .Select(d => new SquadDegradation(d.Target, d.CanonicalIdentity, d.Code))
                .ToArray();

            SquadDeploymentPlan plan;
            if (existingReceipt is null)
            {
                plan = SquadDeploymentPlan.CreateInstall(
                    targetRoot: targetRoot,
                    scope: request.Scope,
                    squadLock: squadLock,
                    renderedFiles: renderResult.Files,
                    degradations: degradations,
                    adopt: request.Adopt,
                    timeProvider: _timeProvider);
            }
            else
            {
                plan = SquadDeploymentPlan.CreateUpdate(
                    targetRoot: targetRoot,
                    scope: request.Scope,
                    squadLock: squadLock,
                    renderedFiles: renderResult.Files,
                    previousReceipt: existingReceipt,
                    degradations: degradations,
                    replaceManaged: false,
                    timeProvider: _timeProvider);
            }

            if (request.DryRun)
            {
                return new SquadLifecycleResult(
                    Success: true,
                    Plan: plan,
                    Receipt: plan.Receipt,
                    Lock: plan.Lock,
                    Degradations: plan.Receipt.Degradations,
                    DryRun: true);
            }

            SquadTransaction transaction = new(_stateStore, _observer);
            transaction.Execute(plan);

            return new SquadLifecycleResult(
                Success: true,
                Plan: plan,
                Receipt: plan.Receipt,
                Lock: plan.Lock,
                Degradations: plan.Receipt.Degradations,
                DryRun: false);
        }
        finally
        {
            CleanupTempDirectory(tempExtractDir);
        }
    }

    /// <summary>
    /// Performs an update of an existing Kyber-Squad deployment in the target root.
    /// </summary>
    public async Task<SquadLifecycleResult> UpdateAsync(
        SquadUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt previousReceipt = _stateStore.ReadReceipt(targetRoot, request.Scope)
            ?? throw new SquadDeploymentConflictException(
                $"No Kyber-Squad deployment found at '{targetRoot}'. Run 'squad install' first.");

        IReadOnlyList<SquadTarget> targets;
        if (request.Targets is { Count: > 0 })
        {
            targets = request.Targets;
        }
        else
        {
            HashSet<string> targetTokens = previousReceipt.Files
                .Select(f => f.Target)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            targets = SquadTargetCatalog.Parse(targetTokens);
            if (targets.Count == 0)
            {
                throw new SquadDeploymentConflictException("No targets found in previous deployment receipt.");
            }
        }

        RequireRenderableTargets(targets);

        string version = request.Version ?? ResolveDefaultVersion();
        string tempExtractDir = Path.Combine(
            Path.GetTempPath(),
            "kyber-squad-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            SquadReleaseResult releaseResult = await _releaseSource.DownloadAndExtractAsync(
                new SquadReleaseRequest(request.Repository, version, tempExtractDir),
                cancellationToken).ConfigureAwait(false);

            string? userScopeDir = request.Scope == SquadDeploymentScope.Global
                ? _stateStore.UserPaths.ApplicationDataDirectory
                : null;

            SquadRenderRequest renderRequest = new(
                SourceDirectory: releaseResult.ExtractionRoot,
                Targets: targets,
                Scope: request.Scope,
                UserScopeDirectory: userScopeDir,
                TranslationMode: request.Translation);

            SquadRenderResult renderResult = await _renderer.RenderAsync(renderRequest, cancellationToken).ConfigureAwait(false);
            if (!renderResult.Success || renderResult.Errors.Count > 0)
            {
                string error = renderResult.Errors.Count > 0
                    ? string.Join(Environment.NewLine, renderResult.Errors)
                    : "Squad render failed.";
                throw new SquadRenderValidationException(error);
            }

            SquadLock squadLock = BuildLock(
                version: releaseResult.Version,
                bundle: request.Bundle,
                targets: targets,
                exclusions: request.Exclusions ?? Array.Empty<string>(),
                translation: request.Translation,
                assetDigest: releaseResult.Checksum.Sha256,
                extractionRoot: releaseResult.ExtractionRoot);

            IReadOnlyList<SquadDegradation> degradations = renderResult.Degradations
                .Select(d => new SquadDegradation(d.Target, d.CanonicalIdentity, d.Code))
                .ToArray();

            SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
                targetRoot: targetRoot,
                scope: request.Scope,
                squadLock: squadLock,
                renderedFiles: renderResult.Files,
                previousReceipt: previousReceipt,
                degradations: degradations,
                replaceManaged: request.ReplaceManaged,
                timeProvider: _timeProvider);

            if (request.DryRun)
            {
                return new SquadLifecycleResult(
                    Success: true,
                    Plan: plan,
                    Receipt: plan.Receipt,
                    Lock: plan.Lock,
                    Degradations: plan.Receipt.Degradations,
                    DryRun: true);
            }

            SquadTransaction transaction = new(_stateStore, _observer);
            transaction.Execute(plan);

            return new SquadLifecycleResult(
                Success: true,
                Plan: plan,
                Receipt: plan.Receipt,
                Lock: plan.Lock,
                Degradations: plan.Receipt.Degradations,
                DryRun: false);
        }
        finally
        {
            CleanupTempDirectory(tempExtractDir);
        }
    }

    /// <summary>
    /// Performs an uninstall of an existing Kyber-Squad deployment from the target root.
    /// </summary>
    public Task<SquadLifecycleResult> UninstallAsync(
        SquadUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        // The body is synchronous, so the token had no observation point and was silently
        // ignored. Honouring it at the boundary is the whole contract an *Async method
        // taking a token owes its caller.
        cancellationToken.ThrowIfCancellationRequested();

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(request.TargetRoot);
        string targetRoot = identity.PhysicalPath;

        SquadReceipt? receipt = _stateStore.ReadReceipt(targetRoot, request.Scope);
        if (receipt is null)
        {
            return Task.FromResult(new SquadLifecycleResult(
                Success: true,
                Receipt: null,
                DryRun: request.DryRun));
        }

        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            targetRoot: targetRoot,
            scope: request.Scope,
            receipt: receipt);

        if (request.DryRun)
        {
            return Task.FromResult(new SquadLifecycleResult(
                Success: true,
                Plan: plan,
                Receipt: plan.Receipt,
                Lock: plan.Lock,
                Degradations: plan.Receipt.Degradations,
                DryRun: true));
        }

        SquadTransaction transaction = new(_stateStore, _observer);
        transaction.Execute(plan);

        return Task.FromResult(new SquadLifecycleResult(
            Success: true,
            Plan: plan,
            Receipt: plan.ReceiptMutation == SquadStateMutation.Write ? plan.Receipt : null,
            Lock: plan.LockMutation == SquadStateMutation.Write ? plan.Lock : null,
            Degradations: plan.Receipt.Degradations,
            DryRun: false));
    }

    /// <summary>
    /// Rejects any requested target with no registered renderer before the release is
    /// downloaded — a target with a renderer already existing per-target on
    /// <see cref="_renderer"/> is a cheap property read, so there is no reason to spend a
    /// network round trip on a request that is going to fail regardless.
    /// </summary>
    private void RequireRenderableTargets(IReadOnlyList<SquadTarget> targets)
    {
        IReadOnlyList<SquadTarget> unsupported = targets
            .Where(target => !_renderer.SupportedTargets.Contains(target))
            .ToArray();
        if (unsupported.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", unsupported.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
        string supported = string.Join(
            ", ",
            _renderer.SupportedTargets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal));
        throw new SquadRenderValidationException(
            $"No renderer is implemented yet for target(s): {names}. " +
            $"See docs/todo/<target>.md for what is needed to add support. " +
            $"Targets available today: {supported}.");
    }

    private static SquadLock BuildLock(
        string version,
        string bundle,
        IReadOnlyList<SquadTarget> targets,
        IReadOnlyList<string> exclusions,
        string translation,
        string assetDigest,
        string extractionRoot)
    {
        string cliVersion = GetAssemblyVersion();
        string bundleDigest = ComputeBundleDigest(extractionRoot, bundle);
        SquadApmIdentity apmIdentity = ReadApmIdentity(extractionRoot);

        return new SquadLock(
            Schema: LockSchema,
            SquadVersion: version,
            CliVersion: cliVersion,
            McpVersion: cliVersion,
            Bundle: bundle,
            Targets: targets.Select(SquadTargetCatalog.GetToken).ToArray(),
            Exclusions: exclusions.ToArray(),
            Translation: translation,
            BundleDigest: bundleDigest,
            AssetDigest: assetDigest,
            Apm: apmIdentity);
    }

    private static string GetAssemblyVersion()
    {
        Assembly assembly = typeof(SquadLifecycleService).Assembly;
        string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            int plusIdx = infoVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    private static string ResolveDefaultVersion() => GetAssemblyVersion();

    private static string ComputeBundleDigest(string extractionRoot, string bundle)
    {
        string bundlePath = Path.Combine(extractionRoot, "bundles", $"{bundle}.yml");
        if (File.Exists(bundlePath))
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(bundlePath)));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle)));
    }

    /// <summary>
    /// Reads the pinned upstream-toolchain identity recorded in a Squad release's
    /// <c>toolchain.yml</c>, if any.
    /// </summary>
    /// <remarks>
    /// Vestigial since rendering stopped shelling out to an external toolchain: canonical
    /// <c>toolchain.yml</c> no longer declares a <c>validated-release</c>, so this reads
    /// "unverified" on every install today. The lock field and this reader stay rather
    /// than forcing a lock schema bump (and the golden-file churn across
    /// <c>SquadDeploymentStateTests</c> that implies) for a field that was already
    /// optional and whose absence this code already handled correctly.
    /// </remarks>
    private static SquadApmIdentity ReadApmIdentity(string extractionRoot)
    {
        string toolchainPath = Path.Combine(extractionRoot, "toolchain.yml");
        if (!File.Exists(toolchainPath))
        {
            return SquadApmIdentity.None;
        }

        try
        {
            string yamlText = File.ReadAllText(toolchainPath);
            YamlStream yaml = new();
            yaml.Load(new StringReader(yamlText));
            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root)
            {
                if (root.Children.TryGetValue(new YamlScalarNode("validated-release"), out YamlNode? valRelNode))
                {
                    if (valRelNode is YamlMappingNode valRel)
                    {
                        string apmVersion = string.Empty;
                        string apmTagCommit = string.Empty;
                        string apmDigest = string.Empty;

                        if (valRel.Children.TryGetValue(new YamlScalarNode("version"), out YamlNode? vNode) &&
                            vNode is YamlScalarNode vScalar && !string.IsNullOrWhiteSpace(vScalar.Value))
                        {
                            apmVersion = vScalar.Value;
                        }

                        if (valRel.Children.TryGetValue(new YamlScalarNode("tag-commit"), out YamlNode? tcNode) &&
                            tcNode is YamlScalarNode tcScalar && !string.IsNullOrWhiteSpace(tcScalar.Value))
                        {
                            apmTagCommit = tcScalar.Value;
                        }

                        if (valRel.Children.TryGetValue(new YamlScalarNode("asset-sha256"), out YamlNode? shaNode) &&
                            shaNode is YamlScalarNode shaScalar && !string.IsNullOrWhiteSpace(shaScalar.Value))
                        {
                            apmDigest = shaScalar.Value;
                        }

                        if (string.IsNullOrWhiteSpace(apmVersion) ||
                            string.IsNullOrWhiteSpace(apmTagCommit) ||
                            apmDigest.Length != 64 ||
                            !apmDigest.All(char.IsAsciiHexDigit))
                        {
                            throw new SquadRenderValidationException(
                                $"'{toolchainPath}' declares an invalid APM validated-release. A 64-character hex asset-sha256, version, and tag-commit are required.");
                        }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
                        return new SquadApmIdentity(apmVersion, apmTagCommit, apmDigest.ToLowerInvariant());
#pragma warning restore CA1308
                    }
                    else if (valRelNode is not YamlScalarNode scalar || (scalar.Value is not null && scalar.Value != "null" && scalar.Value != "~" && scalar.Value.Length > 0))
                    {
                        throw new SquadRenderValidationException(
                            $"'{toolchainPath}' declares an invalid APM validated-release shape. A mapping with version, tag-commit, and asset-sha256 (or null) is required.");
                    }
                }
            }
        }
        catch (SquadRenderValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SquadRenderValidationException($"Failed to parse toolchain file at '{toolchainPath}': {ex.Message}", ex);
        }

        return SquadApmIdentity.None;
    }

    private static void CleanupTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Suppress cleanup failure in temporary directory
        }
    }
}
