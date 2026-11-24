using System.ComponentModel;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Application.Arbiter;

/// <summary>
/// One-command Arbiter install: ensures the host config, sets <c>arbiter.enabled: true</c> non-destructively, renders
/// the target through the existing Squad install pipeline, applies the provider choice, stores the key in the OS
/// credential store, verifies, and reports the target's trust step.
/// </summary>
/// <remarks>
/// The key is never printed and never written to configuration, receipts, or deployed files; only its presence in the
/// store is reported. A second identical run is an idempotent no-op with a status line: no config rewrite, no duplicate
/// credential-store write, no re-render.
/// </remarks>
public sealed class ArbiterInstallService(
    IProcessEnvironment environment,
    IPathResolver paths,
    IUserHostConfigStore userConfig,
    IArbiterConfigResolver configs,
    IArbiterUserSettings userSettings,
    IArbiterKeyInput keyInput,
    ICredentialStore credentials,
    IWritableFileSystem files,
    ISquadStateStore stateStore,
    IArbiterInstallLifecycle lifecycle,
    ISquadGlobalRootResolver globalRoots,
    IArbiterBinaryProbe probe,
    IDocsScaffoldingService scaffolder,
    IArbiterKeyService keys) : IArbiterInstallService
{
    /// <inheritdoc />
    public ArbiterInstallPrepared Prepare(ArbiterInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ArbiterInstallEvent> events = [];
        bool isProjectScope = request.ProjectScope;
        string root = isProjectScope
            ? paths.GetFullPath(
                string.IsNullOrWhiteSpace(request.PathOption)
                    ? (string.IsNullOrWhiteSpace(request.RepositoryPath) ? "." : request.RepositoryPath)
                    : request.PathOption,
                environment.CurrentDirectory)
            : paths.GetFullPath(environment.HomeDirectory, environment.CurrentDirectory);

        ArbiterProviderKind existingKind;
        ArbiterProviderKind effectiveKind;
        KyberWeaveConfig repositoryConfig;
        ArbiterProviderConfig effectiveProvider;
        bool endpointOverridden;
        string? loadedConfigPath;
        try
        {
            (repositoryConfig, existingKind, effectiveProvider, endpointOverridden, loadedConfigPath) =
                ReadExistingProvider(root, request.ConfigOption, isProjectScope);
            effectiveKind = effectiveProvider.Kind;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return StopPrepared(1, events);
        }

        if (!ArbiterUserSettings.TryResolveEndpointOrigin(
                repositoryConfig.Arbiter.Provider,
                effectiveProvider,
                endpointOverridden,
                environment.HomeDirectory,
                loadedConfigPath,
                out string endpoint,
                out string origin,
                out string? endpointError))
        {
            events.Add(new ArbiterInstallError(endpointError));
            return StopPrepared(1, events);
        }

        bool providerOmitted = request.ProviderOption is null;
        ArbiterProviderKind providerKind = existingKind;
        if (!providerOmitted && ParseProvider(request.ProviderOption, out ArbiterProviderKind parsed))
        {
            providerKind = parsed;
        }

        string kindName = KindName(providerKind);
        events.Add(new ArbiterInstallProviderLine(
            kindName,
            providerOmitted,
            providerOmitted || providerKind == existingKind ? null : KindName(existingKind),
            effectiveKind != providerKind
                ? $" The per-user override sets {KindName(effectiveKind)}, which takes precedence."
                : null));

        if (!string.IsNullOrWhiteSpace(request.ArbiterKey))
        {
            events.Add(new ArbiterInstallArgvKeyWarning());
        }

        bool needsKey = providerKind == ArbiterProviderKind.Systemone
                        && !IsLoopbackEndpoint(endpoint);

        string? keyToStore = NullIfEmpty(request.ArbiterKey);
        if (needsKey && keyToStore is null && request.KeyStdin)
            keyToStore = NullIfEmpty(ReadStdinKey());

        if (needsKey && keyToStore is null)
        {
            if (!string.IsNullOrEmpty(ResolveStoredKey(endpoint, repositoryConfig, effectiveProvider)))
            {
                keyToStore = null;
            }
            else
            {
                string? prompted = request.KeyStdin ? null : NullIfEmpty(ReadPromptedKey());
                if (!string.IsNullOrEmpty(prompted))
                {
                    keyToStore = prompted;
                }
                else
                {
                    events.Add(new ArbiterInstallError(
                        $"{ArbiterKeyResolver.MissingKeyCode}: A remote provider is configured, " +
                        $"and no key resolves for origin '{origin}'. " +
                        "Run with --arbiter-key, --key-stdin, or export " +
                        $"{ArbiterKeyResolver.EnvVarName}."));
                    return StopPrepared(2, events);
                }
            }
        }

        string? existingStored = needsKey ? ReadStoredKey(origin) : null;
        bool keyWriteNeeded = needsKey
                              && !string.IsNullOrEmpty(keyToStore)
                              && !string.Equals(existingStored, keyToStore, StringComparison.Ordinal);

        if (isProjectScope
            && !request.DryRun
            && string.IsNullOrWhiteSpace(request.ConfigOption)
            && FindConfigPath(root) is null)
        {
            try
            {
                scaffolder.Scaffold(root);
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException)
            {
                events.Add(new ArbiterInstallError($"The documentation scaffold could not be created: {ex.Message}"));
                return StopPrepared(1, events);
            }
        }

        SquadDeploymentScope scope = isProjectScope ? SquadDeploymentScope.Project : SquadDeploymentScope.Global;
        string configPath;
        string? existingText;
        string desiredText;
        bool configChanged;

        if (isProjectScope)
        {
            configPath = ResolveConfigPath(root, request.ConfigOption);
            existingText = ReadConfigText(configPath, events);
            if (existingText is null)
                return StopPrepared(1, events);

            try
            {
                desiredText = HostConfigYaml.WithArbiterProviderKind(
                    HostConfigYaml.WithArbiterEnabled(existingText),
                    kindName);
            }
            catch (InvalidDataException ex)
            {
                events.Add(new ArbiterInstallError(ex.Message));
                return StopPrepared(1, events);
            }

            configChanged = !string.Equals(desiredText, existingText, StringComparison.Ordinal);
        }
        else
        {
            configPath = UserHostConfigPath.Of(root);
            if (files.FileExists(configPath))
            {
                existingText = files.ReadAllText(configPath);
            }
            else if (request.DryRun)
            {
                existingText = string.Empty;
            }
            else
            {
                existingText = userConfig.EnsureUserConfig(root);
            }

            List<string> globalTargets = [.. userConfig.ReadGlobalTargets(root)];
            if (!globalTargets.Any(token => string.Equals(token, request.TargetToken, StringComparison.OrdinalIgnoreCase)))
            {
                globalTargets.Add(request.TargetToken);
            }

            try
            {
                desiredText = HostConfigYaml.WithArbiterGlobalTargets(
                    HostConfigYaml.WithArbiterProviderKind(
                        HostConfigYaml.WithArbiterEnabled(existingText),
                        kindName),
                    globalTargets);
            }
            catch (InvalidDataException ex)
            {
                events.Add(new ArbiterInstallError(ex.Message));
                return StopPrepared(1, events);
            }

            configChanged = !string.Equals(desiredText, existingText, StringComparison.Ordinal);
        }

        if (!SquadTargetCatalog.TryResolve(request.TargetToken, out SquadTarget target))
        {
            events.Add(new ArbiterInstallError($"Unknown target '{request.TargetToken}'."));
            return StopPrepared(2, events);
        }

        SquadReceipt? existingReceipt;
        try
        {
            existingReceipt = stateStore.ReadReceipt(root, scope);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException or InvalidOperationException)
        {
            events.Add(new ArbiterInstallError($"The Squad deployment state for '{root}' could not be read: {ex.Message}"));
            return StopPrepared(1, events);
        }

        bool receiptMatches = existingReceipt is not null && ReceiptTargetsMatch(existingReceipt, request.TargetToken);

        if (!configChanged && !keyWriteNeeded && receiptMatches)
        {
            events.Add(new ArbiterInstallAlreadyInstalled(request.TargetToken, root));
            return StopPrepared(0, events);
        }

        return new ArbiterInstallPrepared(
            0,
            events,
            new ArbiterInstallPlan(
                root,
                scope,
                target,
                request.TargetToken,
                request.DryRun,
                configPath,
                request.ConfigOption,
                desiredText,
                configChanged,
                keyWriteNeeded,
                keyToStore,
                origin,
                needsKey,
                endpoint,
                existingReceipt));
    }

    /// <inheritdoc />
    public ArbiterInstallApplied Apply(ArbiterInstallPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<ArbiterInstallEvent> events = [];
        bool globalScope = plan.Scope == SquadDeploymentScope.Global;

        if (!plan.DryRun)
        {
            if (plan.ConfigChanged && !WriteConfigText(plan.ConfigPath, plan.DesiredConfigText, events))
                return StopApplied(1, events);

            if (plan.KeyWriteNeeded && plan.KeyToStore is not null && !WriteStoredKey(plan.Origin, plan.KeyToStore, events))
                return StopApplied(1, events);
        }

        // A dry run writes no scaffold, so on a bare root there is no file to load. Preview
        // against the configuration the real run would have written instead of failing. An
        // explicit --config keeps its normal load path, so a missing file is still reported.
        KyberWeaveConfigLoadResult configLoad;
        if (plan.DryRun
            && string.IsNullOrWhiteSpace(plan.ConfigOption)
            && !files.FileExists(plan.ConfigPath))
        {
            try
            {
                configLoad = KyberWeaveConfigLoadResultFactory.Ok(
                    configs.LoadFromYaml(plan.DesiredConfigText), plan.ConfigPath);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                configLoad = KyberWeaveConfigLoadResultFactory.Fail(ex.Message, plan.ConfigPath);
            }
        }
        else
        {
            configLoad = configs.Load(plan.Root, plan.ConfigOption, !globalScope);
        }

        if (!configLoad.Success || configLoad.Config is null)
        {
            events.Add(new ArbiterInstallError(
                $"The host configuration '{configLoad.ConfigPath ?? plan.ConfigPath}' could not be loaded: " +
                $"{configLoad.Error ?? "unknown load failure"}."));
            return StopApplied(1, events);
        }

        SquadArbiterWiring wiring = new(
            configLoad.Config.Arbiter.Enabled,
            ArbiterHookTimeout.SecondsFor(configLoad.Config.Arbiter.Provider.TimeoutMs));

        SquadLifecycleResult result;
        try
        {
            result = lifecycle.InstallAsync(plan.Root, plan.Scope, plan.Target, plan.ExistingReceipt, plan.DryRun, wiring, cancellationToken)
                .GetAwaiter().GetResult();
        }
        catch (SquadDeploymentConflictException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return StopApplied(1, events);
        }
        catch (Exception ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return StopApplied(1, events);
        }

        if (!result.Success)
        {
            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                    events.Add(new ArbiterInstallError(error));
            }
            else
            {
                events.Add(new ArbiterInstallError("The Squad render failed."));
            }

            return StopApplied(1, events);
        }

        if (plan.DryRun)
        {
            events.Add(new ArbiterInstallDryRun(result.Receipt?.Files.Count ?? 0, plan.Root));
        }
        else
        {
            events.Add(new ArbiterInstallDone(plan.TargetToken, plan.Root, globalScope));
        }

        events.Add(new ArbiterInstallTrustSteps(wiring));
        if (result.Warnings is { Count: > 0 })
        {
            events.Add(new ArbiterInstallWarnings(result.Warnings));
        }

        if (globalScope)
        {
            events.Add(ScopeTable(plan.Root));
        }

        if (!plan.DryRun && !Verify(plan, events))
            return StopApplied(1, events);

        return new ArbiterInstallApplied(0, events);
    }

    private static ArbiterInstallPrepared StopPrepared(int exitCode, List<ArbiterInstallEvent> events) =>
        new(exitCode, events, null);

    private static ArbiterInstallApplied StopApplied(int exitCode, List<ArbiterInstallEvent> events) =>
        new(exitCode, events);

    private ArbiterInstallScopeTable ScopeTable(string home)
    {
        List<ArbiterInstallScopeRow> rows = [];
        foreach (string token in userConfig.ReadGlobalTargets(home).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
            {
                rows.Add(new ArbiterInstallScopeRow(token, "enforced-global", home, "not a known harness"));
                continue;
            }

            string path = globalRoots.ResolveGlobalRoot(target);
            IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations([target], SquadDeploymentScope.Global);
            string reason = records.Count > 0 ? (records[0].Details ?? "enforced-global") : "enforced-global";
            string trust = ArbiterHookWiring.TrustSteps.TryGetValue(target, out string? text)
                ? text
                : "No trust gate is required for this harness.";
            rows.Add(new ArbiterInstallScopeRow(token, reason, path, trust));
        }

        return new ArbiterInstallScopeTable(rows);
    }

    private static string KindName(ArbiterProviderKind kind) =>
        kind == ArbiterProviderKind.Systemone ? "systemone" : "none";

    /// <summary>
    /// The repository configuration as loaded, the provider kind it configures (none when nothing
    /// is configured), and the provider in effect once the per-user override is applied over it.
    /// </summary>
    /// <remarks>
    /// One read serves all three: the endpoint and its origin come from the effective provider,
    /// while the un-merged repository configuration is what the key service needs to bind the
    /// environment key to the override origin.
    /// </remarks>
    /// <exception cref="YamlDotNet.Core.YamlException">Thrown when the per-user override is malformed.</exception>
    private (KyberWeaveConfig Repository, ArbiterProviderKind Configured, ArbiterProviderConfig Effective, bool EndpointOverridden, string? ConfigPath) ReadExistingProvider(
        string root, string? configOption, bool projectScope)
    {
        KyberWeaveConfigLoadResult loaded = configs.Load(root, configOption, projectScope);
        KyberWeaveConfig repository = loaded.Success && loaded.Config is not null
            ? loaded.Config
            : KyberWeaveConfigDefaults.Product;
        ArbiterUserProviderResolution resolution =
            userSettings.Resolve(repository.Arbiter.Provider, environment.HomeDirectory);
        return (repository, repository.Arbiter.Provider.Kind, resolution.Provider, resolution.EndpointOverridden, loaded.ConfigPath);
    }

    private static bool ParseProvider(string? raw, out ArbiterProviderKind kind)
    {
        kind = ArbiterProviderKind.None;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(trimmed, "systemone", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArbiterProviderKind.Systemone;
            return true;
        }

        return false;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private string? ReadStdinKey()
    {
        try
        {
            return keyInput.ReadFromStandardInput();
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    private string? ReadPromptedKey()
    {
        try
        {
            return keyInput.PromptHidden();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        try
        {
            return ArbiterKeyResolver.IsLoopbackEndpoint(endpoint);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string? ResolveStoredKey(string endpoint, KyberWeaveConfig repository, ArbiterProviderConfig effectiveProvider)
    {
        return keys.TryResolveKey(repository, effectiveProvider, endpoint);
    }

    private string? ReadStoredKey(string origin)
    {
        try
        {
            return credentials.Read(origin);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private bool WriteStoredKey(string origin, string key, List<ArbiterInstallEvent> events)
    {
        try
        {
            credentials.Write(origin, key);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or Win32Exception)
        {
            events.Add(new ArbiterInstallError(
                $"The credential store for origin '{origin}' is not available: {ex.Message} " +
                $"Store the key in {ArbiterKeyResolver.EnvVarName} instead."));
            return false;
        }

        events.Add(new ArbiterInstallStoredKey(origin));
        return true;
    }

    private static bool ReceiptTargetsMatch(SquadReceipt receipt, string targetToken)
    {
        HashSet<string> existing = receipt.Files
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return existing.SetEquals([targetToken]);
    }

    private string ResolveConfigPath(string root, string? configOption)
    {
        if (!string.IsNullOrWhiteSpace(configOption))
        {
            return Path.IsPathRooted(configOption)
                ? configOption
                : Path.Combine(root, configOption);
        }

        return FindConfigPath(root) ?? Path.Combine(root, ".kyber-weave", "kyber-weave.yml");
    }

    private string? FindConfigPath(string root)
    {
        string defaultPath = Path.Combine(root, ".kyber-weave", "kyber-weave.yml");
        if (files.FileExists(defaultPath))
            return defaultPath;

        string legacyPath = Path.Combine(root, "kyber-weave.yml");
        return files.FileExists(legacyPath) ? legacyPath : null;
    }

    private string? ReadConfigText(string configPath, List<ArbiterInstallEvent> events)
    {
        if (!files.FileExists(configPath))
            return string.Empty;

        try
        {
            return files.ReadAllText(configPath);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            events.Add(new ArbiterInstallError($"The host configuration '{configPath}' could not be read: {ex.Message}"));
            return null;
        }
    }

    private bool WriteConfigText(string configPath, string text, List<ArbiterInstallEvent> events)
    {
        try
        {
            string? directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
                files.CreateDirectory(directory);

            files.WriteAllText(configPath, text);
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            events.Add(new ArbiterInstallError($"The host configuration '{configPath}' could not be written: {ex.Message}"));
            return false;
        }
    }

    private bool Verify(ArbiterInstallPlan plan, List<ArbiterInstallEvent> events)
    {
        KyberWeaveConfigLoadResult loaded = configs.Load(plan.Root, plan.ConfigOption, plan.Scope == SquadDeploymentScope.Project);
        if (!loaded.Success || loaded.Config is null || !loaded.Config.Arbiter.Enabled)
        {
            events.Add(new ArbiterInstallError(
                "Verification failed: the host configuration does not enable the Arbiter " +
                $"after install: {loaded.Error ?? "arbiter.enabled is not true"}."));
            return false;
        }

        if (plan.NeedsKey)
        {
            // Read and validate the override once here, then pass the merged provider to key
            // resolution so a file modified mid-verify cannot pass validation on one endpoint
            // and bind the environment key to another.
            ArbiterUserProviderResolution effective;
            try
            {
                effective = userSettings.Resolve(loaded.Config.Arbiter.Provider, environment.HomeDirectory);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                events.Add(new ArbiterInstallError(ex.Message));
                return false;
            }

            if (!ArbiterUserSettings.TryResolveEndpointOrigin(
                    loaded.Config.Arbiter.Provider,
                    effective.Provider,
                    effective.EndpointOverridden,
                    environment.HomeDirectory,
                    loaded.ConfigPath,
                    out string effectiveEndpoint,
                    out string effectiveOrigin,
                    out string? endpointError))
            {
                events.Add(new ArbiterInstallError(endpointError));
                return false;
            }

            if (!string.Equals(effectiveEndpoint, plan.Endpoint, StringComparison.Ordinal) ||
                !string.Equals(effectiveOrigin, plan.Origin, StringComparison.Ordinal))
            {
                events.Add(new ArbiterInstallError(
                    $"Verification failed: the effective provider endpoint '{effectiveEndpoint}' " +
                    $"(origin '{effectiveOrigin}') does not match the planned endpoint '{plan.Endpoint}' " +
                    $"(origin '{plan.Origin}')."));
                return false;
            }

            string? key = keys.TryResolveKey(loaded.Config, effective.Provider, effectiveEndpoint);

            if (string.IsNullOrEmpty(key))
            {
                events.Add(new ArbiterInstallError(
                    "Verification failed: no key resolves for the configured provider " +
                    "after install. Run with --arbiter-key, --key-stdin, or export " +
                    $"{ArbiterKeyResolver.EnvVarName}."));
                return false;
            }
        }

        ProbeBinary(events);
        return true;
    }

    private void ProbeBinary(List<ArbiterInstallEvent> events)
    {
        try
        {
            ToolProbeResult result = probe.Probe();
            if (!result.IsAvailable)
            {
                events.Add(new ArbiterInstallProbeWarning(
                    $"The 'kyber-weave-arbiter' binary is not available: " +
                    $"{result.FailureReason ?? "not on PATH"}. " +
                    "Install the binary matching this CLI version so hooks can enforce."));
            }
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or Win32Exception)
        {
            events.Add(new ArbiterInstallProbeWarning(
                $"The 'kyber-weave-arbiter' binary probe failed: {ex.Message}"));
        }
    }
}
