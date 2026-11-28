using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public async Task<ArbiterInstallPrepared> PrepareAsync(
        ArbiterInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ArbiterInstallEvent> events = [];
        if (!TryBeginPreparation(request, events, out InstallContext context))
            return StopPrepared(context.ExitCode, events);

        (bool keyStop, string? keyToStore) = await ResolveKeyToStoreAsync(
            request, context.NeedsKey, context.Endpoint, context.Origin, context.RepositoryConfig, context.EffectiveProvider,
            events, cancellationToken).ConfigureAwait(false);
        if (keyStop)
            return StopPrepared(2, events);

        string? existingStored = context.NeedsKey
            ? await ReadStoredKeyAsync(context.Origin, cancellationToken).ConfigureAwait(false)
            : null;
        bool keyWriteNeeded = context.NeedsKey
                              && !string.IsNullOrEmpty(keyToStore)
                              && !string.Equals(existingStored, keyToStore, StringComparison.Ordinal);

        (bool configStop, string configPath, string desiredText, bool configChanged) =
            await PrepareConfigTextAsync(
                context.Root, request, context.IsProjectScope, context.KindName, events, cancellationToken).ConfigureAwait(false);
        if (configStop)
            return StopPrepared(1, events);

        return FinishPreparation(
            request, context, keyToStore, keyWriteNeeded, (configPath, desiredText, configChanged), events);
    }

    /// <summary>
    /// Everything both preparations decide before the key and the config text: the root, the provider choice they report,
    /// and the endpoint and origin the rest of the run follows. False means the run stops with the context's exit code.
    /// </summary>
    private bool TryBeginPreparation(
        ArbiterInstallRequest request,
        List<ArbiterInstallEvent> events,
        out InstallContext context)
    {
        bool isProjectScope = request.ProjectScope;
        string root = isProjectScope
            ? paths.GetFullPath(
                string.IsNullOrWhiteSpace(request.PathOption)
                    ? (string.IsNullOrWhiteSpace(request.RepositoryPath) ? "." : request.RepositoryPath)
                    : request.PathOption,
                environment.CurrentDirectory)
            : paths.GetFullPath(environment.HomeDirectory, environment.CurrentDirectory);

        KyberWeaveConfig repositoryConfig;
        ArbiterProviderKind existingKind;
        ArbiterProviderConfig effectiveProvider;
        bool endpointOverridden;
        string? loadedConfigPath;
        try
        {
            (repositoryConfig, existingKind, effectiveProvider, endpointOverridden, loadedConfigPath) =
                ReadExistingProvider(root, request.ConfigOption, isProjectScope);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            context = InstallContext.Stopped(1);
            return false;
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
            context = InstallContext.Stopped(1);
            return false;
        }

        ArbiterProviderKind providerKind = ReportProviderChoice(request, existingKind, effectiveProvider.Kind, events);
        context = new InstallContext(
            0,
            root,
            isProjectScope ? SquadDeploymentScope.Project : SquadDeploymentScope.Global,
            isProjectScope,
            KindName(providerKind),
            endpoint,
            origin,
            providerKind == ArbiterProviderKind.Systemone && !IsLoopbackEndpoint(endpoint),
            repositoryConfig,
            effectiveProvider);
        return true;
    }

    /// <summary>
    /// Reports the provider choice the run settled on and returns the kind it will apply: the existing kind unless
    /// <c>--provider</c> names another.
    /// </summary>
    private static ArbiterProviderKind ReportProviderChoice(
        ArbiterInstallRequest request,
        ArbiterProviderKind existingKind,
        ArbiterProviderKind effectiveKind,
        List<ArbiterInstallEvent> events)
    {
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

        return providerKind;
    }

    /// <summary>The target, the receipt already recorded and the plan, which is what the caller confirms and applies.</summary>
    private ArbiterInstallPrepared FinishPreparation(
        ArbiterInstallRequest request,
        InstallContext context,
        string? keyToStore,
        bool keyWriteNeeded,
        (string Path, string DesiredText, bool Changed) config,
        List<ArbiterInstallEvent> events)
    {
        if (!SquadTargetCatalog.TryResolve(request.TargetToken, out SquadTarget target))
        {
            events.Add(new ArbiterInstallError($"Unknown target '{request.TargetToken}'."));
            return StopPrepared(2, events);
        }

        SquadReceipt? existingReceipt;
        try
        {
            existingReceipt = stateStore.ReadReceipt(context.Root, context.Scope);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException or InvalidOperationException)
        {
            events.Add(new ArbiterInstallError($"The Squad deployment state for '{context.Root}' could not be read: {ex.Message}"));
            return StopPrepared(1, events);
        }

        bool receiptMatches = existingReceipt is not null && ReceiptTargetsMatch(existingReceipt, request.TargetToken);

        if (!config.Changed && !keyWriteNeeded && receiptMatches)
        {
            events.Add(new ArbiterInstallAlreadyInstalled(request.TargetToken, context.Root));
            return StopPrepared(0, events);
        }

        return new ArbiterInstallPrepared(
            0,
            events,
            new ArbiterInstallPlan(
                context.Root,
                context.Scope,
                target,
                request.TargetToken,
                request.DryRun,
                config.Path,
                request.ConfigOption,
                config.DesiredText,
                config.Changed,
                keyWriteNeeded,
                keyToStore,
                context.Origin,
                context.NeedsKey,
                context.Endpoint,
                existingReceipt));
    }

    /// <summary>The same key resolution without blocking on the credential store.</summary>
    private async Task<(bool Stop, string? KeyToStore)> ResolveKeyToStoreAsync(
        ArbiterInstallRequest request,
        bool needsKey,
        string endpoint,
        string origin,
        KyberWeaveConfig repositoryConfig,
        ArbiterProviderConfig effectiveProvider,
        List<ArbiterInstallEvent> events,
        CancellationToken cancellationToken)
    {
        string? keyToStore = NullIfEmpty(request.ArbiterKey);
        if (needsKey && keyToStore is null && request.KeyStdin)
            keyToStore = NullIfEmpty(ReadStdinKey());

        if (!needsKey || keyToStore is not null)
            return (false, keyToStore);

        string? stored = await keys.TryResolveKeyAsync(
            repositoryConfig, effectiveProvider, endpoint, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored))
            return (false, null);

        string? prompted = request.KeyStdin ? null : NullIfEmpty(ReadPromptedKey());
        if (!string.IsNullOrEmpty(prompted))
            return (false, prompted);

        events.Add(new ArbiterInstallError(
            $"{ArbiterKeyResolver.MissingKeyCode}: A remote provider is configured, " +
            $"and no key resolves for origin '{origin}'. " +
            "Run with --arbiter-key, --key-stdin, or export " +
            $"{ArbiterKeyResolver.EnvVarName}."));
        return (true, null);
    }

    /// <summary>The same config text without blocking on the docs scaffold.</summary>
    private async Task<(bool Stop, string ConfigPath, string DesiredText, bool ConfigChanged)> PrepareConfigTextAsync(
        string root,
        ArbiterInstallRequest request,
        bool isProjectScope,
        string kindName,
        List<ArbiterInstallEvent> events,
        CancellationToken cancellationToken)
    {
        if (isProjectScope
            && !request.DryRun
            && string.IsNullOrWhiteSpace(request.ConfigOption)
            && FindConfigPath(root) is null)
        {
            try
            {
                await scaffolder.ScaffoldAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException)
            {
                events.Add(new ArbiterInstallError($"The documentation scaffold could not be created: {ex.Message}"));
                return (true, string.Empty, string.Empty, false);
            }
        }

        return isProjectScope
            ? PrepareProjectConfigText(root, request, kindName, events)
            : PrepareGlobalConfigText(root, request, kindName, events);
    }

    /// <summary>The project-scope config text: the existing file with the Arbiter's enabled flag and provider kind set.</summary>
    private (bool Stop, string ConfigPath, string DesiredText, bool ConfigChanged) PrepareProjectConfigText(
        string root,
        ArbiterInstallRequest request,
        string kindName,
        List<ArbiterInstallEvent> events)
    {
        string configPath = ResolveConfigPath(root, request.ConfigOption);
        string? existingText = ReadConfigText(configPath, events);
        if (existingText is null)
            return (true, configPath, string.Empty, false);

        string desiredText;
        try
        {
            desiredText = HostConfigYaml.WithArbiterProviderKind(
                HostConfigYaml.WithArbiterEnabled(existingText),
                kindName);
        }
        catch (InvalidDataException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return (true, configPath, string.Empty, false);
        }

        return (false, configPath, desiredText, !string.Equals(desiredText, existingText, StringComparison.Ordinal));
    }

    /// <summary>The global-scope config text: the user config with the target added and the Arbiter's settings set.</summary>
    private (bool Stop, string ConfigPath, string DesiredText, bool ConfigChanged) PrepareGlobalConfigText(
        string root,
        ArbiterInstallRequest request,
        string kindName,
        List<ArbiterInstallEvent> events)
    {
        string configPath = UserHostConfigPath.Of(root);
        string existingText;
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

        string desiredText;
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
            return (true, configPath, string.Empty, false);
        }

        return (false, configPath, desiredText, !string.Equals(desiredText, existingText, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<ArbiterInstallApplied> ApplyAsync(
        ArbiterInstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<ArbiterInstallEvent> events = [];
        bool globalScope = plan.Scope == SquadDeploymentScope.Global;

        if (!plan.DryRun)
        {
            if (plan.ConfigChanged && !WriteConfigText(plan.ConfigPath, plan.DesiredConfigText, events))
                return StopApplied(1, events);

            if (plan.KeyWriteNeeded
                && plan.KeyToStore is not null
                && !await WriteStoredKeyAsync(plan.Origin, plan.KeyToStore, events, cancellationToken).ConfigureAwait(false))
                return StopApplied(1, events);
        }

        SquadArbiterWiring? wiring = LoadWiring(plan, globalScope, events);
        if (wiring is null)
            return StopApplied(1, events);

        SquadLifecycleResult result;
        try
        {
            result = await lifecycle.InstallAsync(
                plan.Root, plan.Scope, plan.Target, plan.ExistingReceipt, plan.DryRun, wiring, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SquadDeploymentConflictException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return StopApplied(1, events);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return StopApplied(1, events);
        }

        if (!ReportRender(plan, wiring, result, globalScope, events))
            return StopApplied(1, events);

        if (!plan.DryRun && !await VerifyAsync(plan, events, cancellationToken).ConfigureAwait(false))
            return StopApplied(1, events);

        return new ArbiterInstallApplied(0, events);
    }

    /// <summary>
    /// The Arbiter settings the deployment runs with, read from the configuration the run left on disk. Null stops the
    /// run with the load finding already recorded.
    /// </summary>
    private SquadArbiterWiring? LoadWiring(ArbiterInstallPlan plan, bool globalScope, List<ArbiterInstallEvent> events)
    {
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
            return null;
        }

        return new SquadArbiterWiring(
            configLoad.Config.Arbiter.Enabled,
            ArbiterHookTimeout.SecondsFor(configLoad.Config.Arbiter.Provider.TimeoutMs));
    }

    /// <summary>Reports the render's outcome; false means it failed and its errors are recorded.</summary>
    private bool ReportRender(
        ArbiterInstallPlan plan,
        SquadArbiterWiring wiring,
        SquadLifecycleResult result,
        bool globalScope,
        List<ArbiterInstallEvent> events)
    {
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

            return false;
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

        return true;
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

    /// <summary>The stored key without blocking on the credential store; a store failure reads as no key.</summary>
    private async Task<string?> ReadStoredKeyAsync(string origin, CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.ReadAsync(origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The key write without blocking on the credential store; false means the finding is recorded.</summary>
    private async Task<bool> WriteStoredKeyAsync(
        string origin,
        string key,
        List<ArbiterInstallEvent> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await credentials.WriteAsync(origin, key, cancellationToken).ConfigureAwait(false);
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

        return FindConfigPath(root) ?? Path.Combine(root, UserStatePaths.DirectoryName, "kyber-weave.yml");
    }

    private string? FindConfigPath(string root)
    {
        string defaultPath = Path.Combine(root, UserStatePaths.DirectoryName, "kyber-weave.yml");
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

    /// <summary>The same verification without blocking on the credential store.</summary>
    private async Task<bool> VerifyAsync(
        ArbiterInstallPlan plan,
        List<ArbiterInstallEvent> events,
        CancellationToken cancellationToken)
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
            if (!TryResolveVerifiedProvider(loaded, plan, events, out ArbiterProviderConfig? provider, out string effectiveEndpoint)
                || provider is null)
            {
                return false;
            }

            string? key = await keys.TryResolveKeyAsync(
                loaded.Config, provider, effectiveEndpoint, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(key))
            {
                events.Add(MissingVerifiedKeyError());
                return false;
            }
        }

        await ProbeBinaryAsync(events, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The effective provider the loaded configuration resolves to, once its endpoint and origin are confirmed to match
    /// the plan. False means the finding is recorded and verification stops.
    /// </summary>
    private bool TryResolveVerifiedProvider(
        KyberWeaveConfigLoadResult loaded,
        ArbiterInstallPlan plan,
        List<ArbiterInstallEvent> events,
        out ArbiterProviderConfig? provider,
        out string effectiveEndpoint)
    {
        provider = null;
        effectiveEndpoint = string.Empty;
        if (loaded.Config is not { } config)
            return false;

        // Read and validate the override once here, then pass the merged provider to key
        // resolution so a file modified mid-verify cannot pass validation on one endpoint
        // and bind the environment key to another.
        ArbiterUserProviderResolution effective;
        try
        {
            effective = userSettings.Resolve(config.Arbiter.Provider, environment.HomeDirectory);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            events.Add(new ArbiterInstallError(ex.Message));
            return false;
        }

        if (!ArbiterUserSettings.TryResolveEndpointOrigin(
                config.Arbiter.Provider,
                effective.Provider,
                effective.EndpointOverridden,
                environment.HomeDirectory,
                loaded.ConfigPath,
                out effectiveEndpoint,
                out string effectiveOrigin,
                out string? endpointError))
        {
            events.Add(new ArbiterInstallError(endpointError));
            return false;
        }

        if (!ArbiterUserSettings.EndpointsEqual(effectiveEndpoint, plan.Endpoint) ||
            !ArbiterUserSettings.EndpointsEqual(effectiveOrigin, plan.Origin))
        {
            (string displayEffectiveEndpoint, string displayPlannedEndpoint) =
                FormatMismatchPair(effectiveEndpoint, plan.Endpoint);
            (string displayEffectiveOrigin, string displayPlannedOrigin) =
                FormatMismatchPair(effectiveOrigin, plan.Origin);
            events.Add(new ArbiterInstallError(
                $"Verification failed: the effective provider endpoint '{displayEffectiveEndpoint}' " +
                $"(origin '{displayEffectiveOrigin}') does not match the planned endpoint '{displayPlannedEndpoint}' " +
                $"(origin '{displayPlannedOrigin}')."));
            return false;
        }

        provider = effective.Provider;
        return true;
    }

    /// <summary>The finding for a key that stopped resolving once the install wrote its configuration.</summary>
    private static ArbiterInstallError MissingVerifiedKeyError() =>
        new("Verification failed: no key resolves for the configured provider " +
            "after install. Run with --arbiter-key, --key-stdin, or export " +
            $"{ArbiterKeyResolver.EnvVarName}.");

    private async Task ProbeBinaryAsync(List<ArbiterInstallEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            ToolProbeResult result = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
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

    private static (string Effective, string Planned) FormatMismatchPair(string effective, string planned) =>
        MismatchDisplay.For(effective, planned);

    /// <summary>
    /// How a verification mismatch spells the two values it compared. The two are bounded to a fixed length first, so a
    /// long endpoint cannot flood the error; when that bound makes them identical, each carries where they diverge, how
    /// long it is, and a content hash, so the operator still sees two different strings.
    /// </summary>
    private static class MismatchDisplay
    {
        /// <summary>The two displays, in the order the error names them.</summary>
        /// <param name="effective">The endpoint or origin the install now resolves.</param>
        /// <param name="planned">The endpoint or origin the plan recorded.</param>
        /// <returns>The two displays, each distinguishable from the other.</returns>
        internal static (string Effective, string Planned) For(string effective, string planned)
        {
            string displayEffective = Bound(effective);
            string displayPlanned = Bound(planned);

            if (string.Equals(effective, planned, StringComparison.Ordinal) ||
                !string.Equals(displayEffective, displayPlanned, StringComparison.Ordinal))
            {
                return (displayEffective, displayPlanned);
            }

            int diffIndex = FirstDifferenceIndex(effective, planned);
            return (Annotate(displayEffective, diffIndex, effective), Annotate(displayPlanned, diffIndex, planned));
        }

        private static string Bound(string value) => ArbiterUserSettings.FormatCandidateForError(value);

        private static string Annotate(string display, int diffIndex, string value) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{display} [differs at index {diffIndex}, len {value.Length}, sha256:{ShortSha256(value)}]");

        private static int FirstDifferenceIndex(string left, string right)
        {
            int sharedLength = Math.Min(left.Length, right.Length);
            for (int i = 0; i < sharedLength; i++)
            {
                if (left[i] != right[i])
                {
                    return i;
                }
            }

            return sharedLength;
        }

        private static string ShortSha256(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }

    /// <summary>
    /// What both preparations settle on before the key and the config text, so the synchronous and asynchronous paths
    /// share every step after it.
    /// </summary>
    /// <param name="ExitCode">The code the run stops with when <see cref="Stopped"/> built it; 0 otherwise.</param>
    /// <param name="Root">The resolved deployment root.</param>
    /// <param name="Scope">Project or global.</param>
    /// <param name="IsProjectScope">Whether the root is a project rather than the user's home.</param>
    /// <param name="KindName">The provider kind's name, as the events and the config text spell it.</param>
    /// <param name="Endpoint">The provider endpoint in effect.</param>
    /// <param name="Origin">The origin the key is stored under.</param>
    /// <param name="NeedsKey">Whether the provider needs a key at all.</param>
    /// <param name="RepositoryConfig">The repository configuration before the per-user override applied.</param>
    /// <param name="EffectiveProvider">The provider in effect once the override applied.</param>
    private sealed record InstallContext(
        int ExitCode,
        string Root,
        SquadDeploymentScope Scope,
        bool IsProjectScope,
        string KindName,
        string Endpoint,
        string Origin,
        bool NeedsKey,
        KyberWeaveConfig RepositoryConfig,
        ArbiterProviderConfig EffectiveProvider)
    {
        /// <summary>A context for a run that stops before it resolves a root, carrying only the exit code.</summary>
        /// <param name="exitCode">The code the host returns.</param>
        /// <returns>The context <see cref="TryBeginPreparation"/> hands back for a stopped run.</returns>
        internal static InstallContext Stopped(int exitCode) =>
            new(exitCode, string.Empty, SquadDeploymentScope.Project, true, string.Empty, string.Empty, string.Empty, false,
                KyberWeaveConfigDefaults.Product, ArbiterProviderConfig.DefaultNone);
    }
}
